using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

public class WorkGroupResolutionPassTests : IDisposable
{
    private const string Repo = "HL7/fhir";

    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;
    private readonly WorkGroupResolver _wgResolver;

    public WorkGroupResolutionPassTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wg-resolution-pass-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
        _wgResolver = new WorkGroupResolver(_database, NullLogger<WorkGroupResolver>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

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

    private int InsertCanonical(string repo, string name, string? wg, string? raw, string? typeSpecific = null, string resourceType = "Resource")
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubCanonicalArtifactRecord rec = new()
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = repo,
            FilePath = $"source/{name}.xml",
            ResourceType = resourceType,
            Url = $"http://hl7.org/fhir/{name}",
            Name = name,
            WorkGroup = wg,
            WorkGroupRaw = raw,
            TypeSpecificData = typeSpecific,
            Format = "xml",
        };
        GitHubCanonicalArtifactRecord.Insert(conn, rec);
        return GetLastRowId(conn);
    }

    private int InsertSd(string repo, string name, string? wg, string? raw)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubStructureDefinitionRecord rec = new()
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = repo,
            FilePath = $"source/{name}.xml",
            Url = $"http://hl7.org/fhir/StructureDefinition/{name}",
            Name = name,
            ArtifactClass = "Resource",
            Kind = "resource",
            BaseDefinition = null,
            Derivation = null,
            Status = "active",
            Title = null,
            Description = null,
            Publisher = null,
            WorkGroup = wg,
            WorkGroupRaw = raw,
            FhirMaturity = null,
            StandardsStatus = null,
        };
        GitHubStructureDefinitionRecord.Insert(conn, rec);
        return GetLastRowId(conn);
    }

    private int InsertSpecFileMap(string repo, string artifactKey, string mapType, string? wg, string? raw)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubSpecFileMapRecord rec = new()
        {
            Id = GitHubSpecFileMapRecord.GetIndex(),
            RepoFullName = repo,
            ArtifactKey = artifactKey,
            FilePath = $"source/{artifactKey}.html",
            MapType = mapType,
            WorkGroup = wg,
            WorkGroupRaw = raw,
        };
        GitHubSpecFileMapRecord.Insert(conn, rec);
        return GetLastRowId(conn);
    }

    private static int GetLastRowId(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt32(cmd.ExecuteScalar());
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

    private (string? Wg, string? Raw) ReadCanonical(int id)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw FROM github_canonical_artifacts WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read(), $"No row for canonical artifact Id={id}");
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private (string? Wg, string? Raw) ReadSd(int id)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw FROM github_structure_definitions WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        r.Read();
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private (string? Wg, string? Raw) ReadFileMap(int id)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw FROM github_spec_file_map WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        r.Read();
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private WorkGroupResolutionPass MakePass(GitHubServiceOptions? opts = null)
    {
        opts ??= new GitHubServiceOptions();
        RepoDefaultWorkGroupResolver repoDefault = new(Options.Create(opts), _wgResolver,
            NullLogger<RepoDefaultWorkGroupResolver>.Instance);
        return new WorkGroupResolutionPass(_database, _wgResolver, repoDefault,
            NullLogger<WorkGroupResolutionPass>.Instance);
    }

    [Fact]
    public async Task BackfillsJiraWorkgroupCodesWhereResolvable()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        SeedJiraWorkgroup(Repo, "fhir_i", "FHIR Infrastructure");
        SeedJiraWorkgroup(Repo, "unknown", "Some Unknown WG");

        await MakePass().RunAsync([Repo], CancellationToken.None);

        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkgroupKey, WorkGroupCode FROM jira_workgroups WHERE RepoFullName = @r ORDER BY WorkgroupKey";
        cmd.Parameters.AddWithValue("@r", Repo);
        using SqliteDataReader r = cmd.ExecuteReader();
        Dictionary<string, string?> map = [];
        while (r.Read()) map[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        Assert.Equal("fhir-i", map["fhir_i"]);
        Assert.Null(map["unknown"]);
    }

    [Fact]
    public async Task NormalizesArtifactWorkGroup_ResolvedSameAsInput_ClearsRaw()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        int id = InsertCanonical(Repo, "Patient", wg: "fhir-i", raw: null);

        await MakePass().RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadCanonical(id);
        Assert.Equal("fhir-i", wg);
        Assert.Null(raw);
    }

    [Fact]
    public async Task NormalizesArtifactWorkGroup_ResolvedDifferentFromInput_PreservesRaw()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        int id = InsertCanonical(Repo, "Patient", wg: "FHIR Infrastructure", raw: null);

        await MakePass().RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadCanonical(id);
        Assert.Equal("fhir-i", wg);
        Assert.Equal("FHIR Infrastructure", raw);
    }

    [Fact]
    public async Task NormalizesArtifactWorkGroup_Unresolved_MovesToRawAndClearsWg()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        int id = InsertCanonical(Repo, "Patient", wg: "Mystery Group", raw: null);

        await MakePass().RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadCanonical(id);
        Assert.Null(wg);
        Assert.Equal("Mystery Group", raw);
    }

    [Fact]
    public async Task SearchParameterInheritsFromBaseStructureDefinition()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertSd(Repo, "Patient", wg: "fhir-i", raw: null);
        int spId = InsertCanonical(
            Repo,
            "Patient-name",
            wg: null,
            raw: null,
            typeSpecific: "{\"baseResources\":[\"Patient\"]}",
            resourceType: "SearchParameter");

        await MakePass().RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadCanonical(spId);
        Assert.Equal("fhir-i", wg);
        Assert.Null(raw);
    }

    [Fact]
    public async Task FileMapResolvesViaJiraSpecKeyIndirection()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        SeedJiraWorkgroup(Repo, "fhir_i", "FHIR Infrastructure");
        SeedJiraSpecArtifact(Repo, "patient", "fhir_i");
        int id = InsertSpecFileMap(Repo, "patient", mapType: "directory", wg: null, raw: null);

        await MakePass().RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadFileMap(id);
        Assert.Equal("fhir-i", wg);
        Assert.Equal("fhir_i", raw);
    }

    [Fact]
    public async Task FileMapForPageRowResolvesViaJiraSpecPage()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        SeedJiraWorkgroup(Repo, "fhir_i", "FHIR Infrastructure");
        SeedJiraSpecPage(Repo, "security", "fhir_i");
        int id = InsertSpecFileMap(Repo, "security", mapType: "page", wg: null, raw: null);

        await MakePass().RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadFileMap(id);
        Assert.Equal("fhir-i", wg);
        Assert.Equal("fhir_i", raw);
    }

    [Fact]
    public async Task FileMapFallsBackToArtifactWorkGroupWhenJiraSpecMissing()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertCanonical(Repo, "Observation", wg: "fhir-i", raw: null);
        int id = InsertSpecFileMap(Repo, "Observation", mapType: "directory", wg: null, raw: null);

        await MakePass().RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadFileMap(id);
        Assert.Equal("fhir-i", wg);
        Assert.Null(raw);
    }

    [Fact]
    public async Task RepoDefaultBackfillsRowsWithBothNull()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        GitHubServiceOptions opts = new();
        opts.RepoOverrides[Repo] = new RepoOverrideOptions { WorkGroup = "fhir-i" };
        int id = InsertCanonical(Repo, "Mystery", wg: null, raw: null);

        await MakePass(opts).RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadCanonical(id);
        Assert.Equal("fhir-i", wg);
        Assert.Null(raw);
    }

    [Fact]
    public async Task RepoDefaultDoesNotOverwriteUnresolvedKept()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        GitHubServiceOptions opts = new();
        opts.RepoOverrides[Repo] = new RepoOverrideOptions { WorkGroup = "fhir-i" };
        // Pre-existing raw, unresolved → must be left alone.
        int id = InsertCanonical(Repo, "Mystery", wg: null, raw: "Some Old Raw");

        await MakePass(opts).RunAsync([Repo], CancellationToken.None);

        (string? wg, string? raw) = ReadCanonical(id);
        Assert.Null(wg);
        Assert.Equal("Some Old Raw", raw);
    }

    [Fact]
    public async Task RepoDefaultRowIsUpsertedFromConfigOverride()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        GitHubServiceOptions opts = new();
        opts.RepoOverrides[Repo] = new RepoOverrideOptions { WorkGroup = "fhir-i" };

        await MakePass(opts).RunAsync([Repo], CancellationToken.None);

        using SqliteConnection conn = _database.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw, Source FROM github_repo_workgroups WHERE RepoFullName = @r";
        cmd.Parameters.AddWithValue("@r", Repo);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("fhir-i", r.GetString(0));
        Assert.True(r.IsDBNull(1));
        Assert.Equal(RepoDefaultWorkGroupResolver.SourceConfig, r.GetString(2));
    }

    [Fact]
    public async Task IsIdempotent_SecondRunLeavesValuesUnchanged()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        int idA = InsertCanonical(Repo, "Patient", wg: "FHIR Infrastructure", raw: null);
        int idB = InsertCanonical(Repo, "Mystery", wg: "Mystery Group", raw: null);

        WorkGroupResolutionPass pass = MakePass();
        await pass.RunAsync([Repo], CancellationToken.None);
        (string? wg1, string? raw1) = ReadCanonical(idA);
        (string? wg2, string? raw2) = ReadCanonical(idB);

        await pass.RunAsync([Repo], CancellationToken.None);
        (string? wg1b, string? raw1b) = ReadCanonical(idA);
        (string? wg2b, string? raw2b) = ReadCanonical(idB);

        Assert.Equal(wg1, wg1b);
        Assert.Equal(raw1, raw1b);
        Assert.Equal(wg2, wg2b);
        Assert.Equal(raw2, raw2b);
    }
}
