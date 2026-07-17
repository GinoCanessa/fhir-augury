using FhirAugury.Common.Api;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Composite end-to-end coverage of <see cref="WorkGroupResolutionPass"/>
/// and the <see cref="WorkGroupsController"/> HTTP surface in a single
/// realistic scenario that exercises every rung of the resolution chain
/// concurrently.
/// </summary>
/// <remarks>
/// Plan-step 7.1 prescribes driving a synthetic clone tree through
/// <c>GitHubIngestionPipeline.PostIngestionAsync</c>. That requires
/// constructing all 19 pipeline collaborators (cloners, indexers,
/// extractors, HTTP factories, …); the resulting test would dwarf the
/// behavior under test. This composite instead seeds the post-ingest
/// database state directly and runs the resolution pass plus the
/// controller atop it, which is the same surface area
/// <c>PostIngestionAsync</c> would exercise after its earlier stages
/// completed. Documented as a deviation in <c>plan.md</c>.
/// </remarks>
public class WorkGroupResolutionEndToEndTests : IDisposable
{
    private const string Repo = "HL7/fhir";

    private readonly string _tempDir;
    private readonly GitHubDatabase _database;
    private readonly WorkGroupResolver _wgResolver;

    public WorkGroupResolutionEndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wg-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _database = new GitHubDatabase(Path.Combine(_tempDir, "test.db"), NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
        _wgResolver = new WorkGroupResolver(_database, NullLogger<WorkGroupResolver>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    [Fact]
    public async Task FullChainResolves_AllRungsCoExist_ControllerReportsCanonicalCounts()
    {
        // ── 1. Authoritative HL7 codeset ─────────────────────────────
        SeedHl7(("fhir-i", "FHIR Infrastructure"), ("pa", "Patient Administration"));

        // ── 2. JIRA-Spec workgroup keys (free-text → canonical lookup) ─
        SeedJiraWorkgroup(Repo, "fhir_i", "FHIR Infrastructure");
        SeedJiraWorkgroup(Repo, "pa_admin", "Patient Administration");

        // ── 3. JIRA-Spec artifact + page rows that supply WG by key ──
        SeedJiraSpecArtifact(Repo, "patient", "pa_admin");
        SeedJiraSpecPage(Repo, "extensibility", "fhir_i");

        // ── 4. Spec file map rows (one indirected via JIRA-Spec key,
        //      one already-canonical, one unresolvable) ───────────────
        int fmIndirect = InsertSpecFileMap(Repo, "patient", "directory", null, "pa_admin", filePath: "source/patient");
        int fmCanonical = InsertSpecFileMap(Repo, "extensibility", "page", "fhir-i", null, filePath: "source/extensibility.html");
        int fmUnresolved = InsertSpecFileMap(Repo, "mystery", "directory", null, "Mystery Group", filePath: "source/mystery");

        // ── 5. Canonical artifacts: name-clean match + already-canonical
        //      + unresolved leftover ───────────────────────────────────
        int caResolvedByName = InsertCanonical(Repo, "Observation", "FHIR Infrastructure", null);
        int caAlreadyCanonical = InsertCanonical(Repo, "Patient", "pa", null);
        int caUnresolved = InsertCanonical(Repo, "Foo", "Some Other Group", null);

        // ── 6. StructureDefinition with a free-text WG to canonicalize ─
        int sdResolved = InsertSd(Repo, "Account", "Patient Administration", null);

        // ── 7. Repo default fed by config override ────────────────────
        GitHubServiceOptions opts = new();
        opts.RepoOverrides[Repo] = new RepoOverrideOptions { WorkGroup = "fhir-i" };

        // ── Act: single resolution pass ──────────────────────────────
        WorkGroupResolutionPass pass = MakePass(opts);
        await pass.RunAsync([Repo], CancellationToken.None);

        // ── Assert per-rung outcomes ─────────────────────────────────
        Assert.Equal(("pa", "pa_admin"), ReadFileMap(fmIndirect));
        Assert.Equal(("fhir-i", "fhir_i"), ReadFileMap(fmCanonical));
        Assert.Equal(((string?)null, "Mystery Group"), ReadFileMap(fmUnresolved));

        Assert.Equal(("fhir-i", "FHIR Infrastructure"), ReadCanonical(caResolvedByName));
        Assert.Equal(("pa", (string?)null), ReadCanonical(caAlreadyCanonical));
        Assert.Equal(((string?)null, "Some Other Group"), ReadCanonical(caUnresolved));

        Assert.Equal(("pa", "Patient Administration"), ReadSd(sdResolved));

        (string? rdWg, string? rdRaw, string source) = ReadRepoDefault(Repo);
        Assert.Equal("fhir-i", rdWg);
        Assert.Null(rdRaw);
        Assert.Equal("config", source);

        // Idempotency: a second pass over the same data must not change anything.
        await pass.RunAsync([Repo], CancellationToken.None);
        Assert.Equal(("pa", "pa_admin"), ReadFileMap(fmIndirect));
        Assert.Equal(((string?)null, "Mystery Group"), ReadFileMap(fmUnresolved));
        Assert.Equal(("fhir-i", "FHIR Infrastructure"), ReadCanonical(caResolvedByName));

        // ── HTTP surface: list/resolve/unresolved over the same DB ───
        WorkGroupsController controller = new(_database);
        WorkGroupListResponse list = Unwrap<WorkGroupListResponse>(controller.ListWorkGroups());
        WorkGroupSummary fhirI = Assert.Single(list.WorkGroups, w => w.Code == "fhir-i");
        WorkGroupSummary pa = Assert.Single(list.WorkGroups, w => w.Code == "pa");
        Assert.Equal(1, fhirI.TotalFileCount);
        Assert.Equal(1, fhirI.TotalArtifactCount);
        Assert.Equal(1, pa.TotalFileCount);
        Assert.Equal(2, pa.TotalArtifactCount); // Observation? no, Patient(canonical)+Account(SD)

        WorkGroupResolveResponse exact = Unwrap<WorkGroupResolveResponse>(
            controller.Resolve(Repo, "source/extensibility.html"));
        Assert.Equal("fhir-i", exact.WorkGroup);
        Assert.Equal("exact-file", exact.MatchedStage);

        WorkGroupResolveResponse dir = Unwrap<WorkGroupResolveResponse>(
            controller.Resolve(Repo, "source/patient/something-else.html"));
        Assert.Equal("pa", dir.WorkGroup);
        Assert.Equal("directory-prefix", dir.MatchedStage);

        WorkGroupResolveResponse fallback = Unwrap<WorkGroupResolveResponse>(
            controller.Resolve(Repo, "totally/unknown.txt"));
        Assert.Equal("fhir-i", fallback.WorkGroup);
        Assert.Equal("repo-default", fallback.MatchedStage);

        WorkGroupUnresolvedListResponse unresolved = Unwrap<WorkGroupUnresolvedListResponse>(
            controller.Unresolved());
        Assert.Contains(unresolved.Items, i => i.WorkGroupRaw == "Mystery Group");
        Assert.Contains(unresolved.Items, i => i.WorkGroupRaw == "Some Other Group");
    }

    // ── Helpers (mirror WorkGroupResolutionPassTests for consistency) ──

    private void SeedHl7(params (string Code, string Name)[] groups)
    {
        using SqliteConnection conn = _database.OpenConnection();
        List<Hl7WorkGroupRecord> rows = [];
        foreach ((string code, string name) in groups)
        {
            rows.Add(new Hl7WorkGroupRecord
            {
                Id = Hl7WorkGroupRecord.GetIndex(),
                Code = code,
                Name = name,
                Definition = null,
                Retired = false,
                NameClean = FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(name),
            });
        }
        Hl7WorkGroupRecord.Insert(conn, rows, ignoreDuplicates: true, insertPrimaryKey: true);
        _wgResolver.Reload();
    }

    private void SeedJiraWorkgroup(string repo, string key, string name)
    {
        using SqliteConnection conn = _database.OpenConnection();
        JiraWorkgroupRecord.Insert(conn, new JiraWorkgroupRecord
        {
            Id = JiraWorkgroupRecord.GetIndex(),
            RepoFullName = repo,
            WorkgroupKey = key,
            Name = name,
            Webcode = null,
            Listserv = null,
            Deprecated = false,
            WorkGroupCode = null,
        });
    }

    private void SeedJiraSpecArtifact(string repo, string artifactKey, string workgroupKey)
    {
        using SqliteConnection conn = _database.OpenConnection();
        JiraSpecArtifactRecord.Insert(conn, new JiraSpecArtifactRecord
        {
            Id = JiraSpecArtifactRecord.GetIndex(),
            RepoFullName = repo,
            SpecKey = "spec1",
            JiraSpecId = 1,
            ArtifactKey = artifactKey,
            Name = artifactKey,
            ArtifactId = null,
            ResourceType = null,
            Workgroup = workgroupKey,
            Deprecated = false,
            OtherArtifactIds = null,
        });
    }

    private void SeedJiraSpecPage(string repo, string pageKey, string workgroupKey)
    {
        using SqliteConnection conn = _database.OpenConnection();
        JiraSpecPageRecord.Insert(conn, new JiraSpecPageRecord
        {
            Id = JiraSpecPageRecord.GetIndex(),
            RepoFullName = repo,
            SpecKey = "spec1",
            JiraSpecId = 1,
            PageKey = pageKey,
            Name = pageKey,
            Url = null,
            Workgroup = workgroupKey,
            Deprecated = false,
            OtherPageUrls = null,
        });
    }

    private int InsertCanonical(string repo, string name, string? wg, string? raw)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubCanonicalArtifactRecord.Insert(conn, new GitHubCanonicalArtifactRecord
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = repo,
            FilePath = $"source/{name}.xml",
            ResourceType = "Resource",
            Url = $"http://hl7.org/fhir/{name}",
            Name = name,
            WorkGroup = wg,
            WorkGroupRaw = raw,
            Format = "xml",
        });
        return GetLastRowId(conn);
    }

    private int InsertSd(string repo, string name, string? wg, string? raw)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubStructureDefinitionRecord.Insert(conn, new GitHubStructureDefinitionRecord
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = repo,
            FilePath = $"source/{name}.xml",
            Url = $"http://hl7.org/fhir/StructureDefinition/{name}",
            Name = name,
            ArtifactClass = "Resource",
            Kind = "resource",
            WorkGroup = wg,
            WorkGroupRaw = raw,
        });
        return GetLastRowId(conn);
    }

    private int InsertSpecFileMap(string repo, string artifactKey, string mapType, string? wg, string? raw, string? filePath = null)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubSpecFileMapRecord.Insert(conn, new GitHubSpecFileMapRecord
        {
            Id = GitHubSpecFileMapRecord.GetIndex(),
            RepoFullName = repo,
            ArtifactKey = artifactKey,
            FilePath = filePath ?? $"source/{artifactKey}.html",
            MapType = mapType,
            WorkGroup = wg,
            WorkGroupRaw = raw,
        });
        return GetLastRowId(conn);
    }

    private static int GetLastRowId(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private (string? Wg, string? Raw) ReadCanonical(int id)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw FROM github_canonical_artifacts WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private (string? Wg, string? Raw) ReadSd(int id)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw FROM github_structure_definitions WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private (string? Wg, string? Raw) ReadFileMap(int id)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw FROM github_spec_file_map WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private (string? Wg, string? Raw, string Source) ReadRepoDefault(string repo)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw, Source FROM github_repo_workgroups WHERE RepoFullName = @r";
        cmd.Parameters.AddWithValue("@r", repo);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2));
    }

    private static T Unwrap<T>(IActionResult result) where T : class
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value!);
    }

    private WorkGroupResolutionPass MakePass(GitHubServiceOptions opts)
    {
        RepoDefaultWorkGroupResolver repoDefault = new(Options.Create(opts), _wgResolver,
            NullLogger<RepoDefaultWorkGroupResolver>.Instance);
        return new WorkGroupResolutionPass(_database, _wgResolver, repoDefault,
            NullLogger<WorkGroupResolutionPass>.Instance);
    }
}
