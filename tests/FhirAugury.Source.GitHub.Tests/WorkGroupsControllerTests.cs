using FhirAugury.Common.Api;
using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class WorkGroupsControllerTests : IDisposable
{
    private const string Repo = "HL7/fhir";

    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;
    private readonly WorkGroupsController _controller;

    public WorkGroupsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wg-controller-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
        _controller = new WorkGroupsController(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    private static T Unwrap<T>(IActionResult result) where T : class
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value!);
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
    }

    private void InsertCanonical(string repo, string name, string filePath, string? wg, string? raw)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubCanonicalArtifactRecord.Insert(conn, new GitHubCanonicalArtifactRecord
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = repo,
            FilePath = filePath,
            ResourceType = "Resource",
            Url = $"http://hl7.org/fhir/{name}",
            Name = name,
            WorkGroup = wg,
            WorkGroupRaw = raw,
            Format = "xml",
        });
    }

    private void InsertSpecFileMap(string repo, string artifactKey, string filePath, string mapType, string? wg, string? raw)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubSpecFileMapRecord.Insert(conn, new GitHubSpecFileMapRecord
        {
            Id = GitHubSpecFileMapRecord.GetIndex(),
            RepoFullName = repo,
            ArtifactKey = artifactKey,
            FilePath = filePath,
            MapType = mapType,
            WorkGroup = wg,
            WorkGroupRaw = raw,
        });
    }

    private void InsertRepoDefault(string repo, string? wg, string? raw, string source)
    {
        using SqliteConnection conn = _database.OpenConnection();
        GitHubRepoWorkGroupRecord.Insert(conn, new GitHubRepoWorkGroupRecord
        {
            Id = GitHubRepoWorkGroupRecord.GetIndex(),
            RepoFullName = repo,
            WorkGroup = wg,
            WorkGroupRaw = raw,
            Source = source,
            ResolvedAt = DateTimeOffset.UtcNow,
        });
    }

    [Fact]
    public void ListWorkGroups_AggregatesPerRepoFileAndArtifactCounts()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertCanonical(Repo, "Patient", "source/patient/patient.xml", "fhir-i", null);
        InsertCanonical(Repo, "Observation", "source/observation/observation.xml", "fhir-i", null);
        InsertSpecFileMap(Repo, "patient", "source/patient", "directory", "fhir-i", null);

        WorkGroupListResponse resp = Unwrap<WorkGroupListResponse>(_controller.ListWorkGroups());
        WorkGroupSummary fhirI = Assert.Single(resp.WorkGroups, w => w.Code == "fhir-i");
        Assert.Equal(1, fhirI.TotalFileCount);
        Assert.Equal(2, fhirI.TotalArtifactCount);
        Assert.Single(fhirI.Repos);
        Assert.Equal(Repo, fhirI.Repos[0].RepoFullName);
    }

    [Fact]
    public void ListFiles_ReturnsRequestedRowsWithPaging()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        for (int i = 0; i < 5; i++)
            InsertSpecFileMap(Repo, $"a{i}", $"source/a{i}", "directory", "fhir-i", null);

        IActionResult action = _controller.ListFiles(Repo, "fhir-i", limit: 2, offset: 1);
        WorkGroupFileListResponse resp = Unwrap<WorkGroupFileListResponse>(action);
        Assert.Equal(2, resp.Files.Count);
        Assert.Equal(5, resp.Page.Total);
        Assert.Equal(2, resp.Page.Limit);
        Assert.Equal(1, resp.Page.Offset);
    }

    [Fact]
    public void ListFiles_NormalizesNegativeAndOversizedLimits()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertSpecFileMap(Repo, "a", "source/a", "directory", "fhir-i", null);

        WorkGroupFileListResponse zero = Unwrap<WorkGroupFileListResponse>(
            _controller.ListFiles(Repo, "fhir-i", limit: 0, offset: -3));
        Assert.Equal(100, zero.Page.Limit);
        Assert.Equal(0, zero.Page.Offset);

        WorkGroupFileListResponse huge = Unwrap<WorkGroupFileListResponse>(
            _controller.ListFiles(Repo, "fhir-i", limit: 50_000, offset: 0));
        Assert.Equal(1000, huge.Page.Limit);
    }

    [Fact]
    public void ListFiles_RequiresRepoAndWorkgroup()
    {
        Assert.IsType<BadRequestObjectResult>(_controller.ListFiles("", "fhir-i"));
        Assert.IsType<BadRequestObjectResult>(_controller.ListFiles(Repo, ""));
    }

    [Fact]
    public void ListArtifacts_UnionsCanonicalAndStructureDefinitionRows()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertCanonical(Repo, "Patient", "source/patient/patient.xml", "fhir-i", null);

        using (SqliteConnection conn = _database.OpenConnection())
        {
            GitHubStructureDefinitionRecord.Insert(conn, new GitHubStructureDefinitionRecord
            {
                Id = GitHubStructureDefinitionRecord.GetIndex(),
                RepoFullName = Repo,
                FilePath = "source/patient/structuredefinition-Patient.xml",
                Url = "http://hl7.org/fhir/StructureDefinition/Patient",
                Name = "Patient",
                ArtifactClass = "Resource",
                Kind = "resource",
                WorkGroup = "fhir-i",
            });
        }

        WorkGroupArtifactListResponse resp = Unwrap<WorkGroupArtifactListResponse>(
            _controller.ListArtifacts(Repo, "fhir-i"));
        Assert.Equal(2, resp.Artifacts.Count);
        Assert.Contains(resp.Artifacts, a => a.Source == "canonical");
        Assert.Contains(resp.Artifacts, a => a.Source == "structure-definition");
    }

    [Fact]
    public void Resolve_ExactFileMatchHasHighestPriority()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertSpecFileMap(Repo, "patient", "source/patient/patient-introduction.md", "file", "fhir-i", null);

        WorkGroupResolveResponse resp = Unwrap<WorkGroupResolveResponse>(
            _controller.Resolve(Repo, "source/patient/patient-introduction.md"));
        Assert.Equal("fhir-i", resp.WorkGroup);
        Assert.Equal("exact-file", resp.MatchedStage);
    }

    [Fact]
    public void Resolve_DirectoryPrefixUsesLongestMatch()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"), ("pa", "Patient Administration"));
        InsertSpecFileMap(Repo, "src", "source", "directory", "fhir-i", null);
        InsertSpecFileMap(Repo, "patient", "source/patient", "directory", "pa", null);

        WorkGroupResolveResponse resp = Unwrap<WorkGroupResolveResponse>(
            _controller.Resolve(Repo, "source/patient/patient.xml"));
        Assert.Equal("pa", resp.WorkGroup);
        Assert.Equal("directory-prefix", resp.MatchedStage);
    }

    [Fact]
    public void Resolve_FallsBackToArtifactWhenNoSpecFileMapMatch()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertCanonical(Repo, "Patient", "source/patient/patient.xml", "fhir-i", null);

        WorkGroupResolveResponse resp = Unwrap<WorkGroupResolveResponse>(
            _controller.Resolve(Repo, "source/patient/patient.xml"));
        Assert.Equal("fhir-i", resp.WorkGroup);
        Assert.Equal("artifact", resp.MatchedStage);
    }

    [Fact]
    public void Resolve_FallsBackToRepoDefault()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        InsertRepoDefault(Repo, "fhir-i", null, "config-override");

        WorkGroupResolveResponse resp = Unwrap<WorkGroupResolveResponse>(
            _controller.Resolve(Repo, "source/unknown/file.txt"));
        Assert.Equal("fhir-i", resp.WorkGroup);
        Assert.Equal("repo-default", resp.MatchedStage);
    }

    [Fact]
    public void Resolve_ReturnsNoneStageWhenNothingMatches()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));

        WorkGroupResolveResponse resp = Unwrap<WorkGroupResolveResponse>(
            _controller.Resolve(Repo, "anything/at/all.txt"));
        Assert.Null(resp.WorkGroup);
        Assert.Equal("none", resp.MatchedStage);
    }

    [Fact]
    public void Resolve_RequiresRepoAndPath()
    {
        Assert.IsType<BadRequestObjectResult>(_controller.Resolve("", "x"));
        Assert.IsType<BadRequestObjectResult>(_controller.Resolve(Repo, ""));
    }

    [Fact]
    public void Unresolved_AggregatesAcrossTablesSortedByOccurrence()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        // "Mystery" appears in 3 places, "Other" in 1.
        InsertCanonical(Repo, "X", "source/x.xml", null, "Mystery");
        InsertCanonical(Repo, "Y", "source/y.xml", null, "Mystery");
        InsertSpecFileMap(Repo, "z", "source/z", "directory", null, "Mystery");
        InsertSpecFileMap(Repo, "q", "source/q", "directory", null, "Other");

        WorkGroupUnresolvedListResponse resp = Unwrap<WorkGroupUnresolvedListResponse>(
            _controller.Unresolved());
        Assert.Equal(2, resp.Page.Total);
        WorkGroupUnresolvedItem first = resp.Items[0];
        Assert.Equal("Mystery", first.WorkGroupRaw);
        Assert.Equal(3, first.OccurrenceCount);
        Assert.Equal(3, first.Examples.Count);
    }
}
