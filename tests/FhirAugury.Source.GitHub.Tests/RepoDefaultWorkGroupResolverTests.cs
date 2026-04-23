using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

public class RepoDefaultWorkGroupResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;
    private readonly WorkGroupResolver _wgResolver;

    public RepoDefaultWorkGroupResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "repo-default-wg-" + Guid.NewGuid().ToString("N")[..8]);
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

    private RepoDefaultWorkGroupResolver MakeResolver(GitHubServiceOptions opts) =>
        new(Options.Create(opts), _wgResolver, NullLogger<RepoDefaultWorkGroupResolver>.Instance);

    [Fact]
    public void ConfigOverride_ResolvesAndDropsRawWhenSameAsCode()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        GitHubServiceOptions opts = new();
        opts.RepoOverrides["HL7/fhir"] = new RepoOverrideOptions { WorkGroup = "fhir-i" };
        RepoDefaultWorkGroupResolver sut = MakeResolver(opts);

        using SqliteConnection conn = _database.OpenConnection();
        RepoDefaultResult result = sut.Resolve(conn, "HL7/fhir");

        Assert.Equal("fhir-i", result.Code);
        Assert.Null(result.Raw);
        Assert.Equal(RepoDefaultWorkGroupResolver.SourceConfig, result.Source);
    }

    [Fact]
    public void ConfigOverride_ResolvesViaName_PreservesRaw()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        GitHubServiceOptions opts = new();
        opts.RepoOverrides["HL7/fhir"] = new RepoOverrideOptions { WorkGroup = "FHIR Infrastructure" };
        RepoDefaultWorkGroupResolver sut = MakeResolver(opts);

        using SqliteConnection conn = _database.OpenConnection();
        RepoDefaultResult result = sut.Resolve(conn, "HL7/fhir");

        Assert.Equal("fhir-i", result.Code);
        Assert.Equal("FHIR Infrastructure", result.Raw);
        Assert.Equal(RepoDefaultWorkGroupResolver.SourceConfig, result.Source);
    }

    [Fact]
    public void ConfigOverride_Unresolvable_KeepsRawOnly()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"));
        GitHubServiceOptions opts = new();
        opts.RepoOverrides["HL7/fhir"] = new RepoOverrideOptions { WorkGroup = "made-up-wg" };
        RepoDefaultWorkGroupResolver sut = MakeResolver(opts);

        using SqliteConnection conn = _database.OpenConnection();
        RepoDefaultResult result = sut.Resolve(conn, "HL7/fhir");

        Assert.Null(result.Code);
        Assert.Equal("made-up-wg", result.Raw);
        Assert.Equal(RepoDefaultWorkGroupResolver.SourceConfig, result.Source);
    }

    [Fact]
    public void MajorityJiraSpec_ResolvedCodeWins()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"), ("oo", "Orders and Observations"));
        SeedJiraSpecsAndWorkgroups(
            repo: "HL7/fhir",
            workgroups:
            [
                ("fhir-i", "FHIR Infrastructure", "fhir-i"),
                ("oo", "Orders and Observations", "oo"),
            ],
            specs:
            [
                ("core", "fhir-i"),
                ("conformance", "fhir-i"),
                ("observation", "oo"),
            ]);

        RepoDefaultWorkGroupResolver sut = MakeResolver(new GitHubServiceOptions());
        using SqliteConnection conn = _database.OpenConnection();
        RepoDefaultResult result = sut.Resolve(conn, "HL7/fhir");

        Assert.Equal("fhir-i", result.Code);
        Assert.Null(result.Raw);
        Assert.Equal(RepoDefaultWorkGroupResolver.SourceMajorityJiraSpec, result.Source);
    }

    [Fact]
    public void MajorityJiraSpec_TieBrokenByCodeAscending()
    {
        SeedHl7(("fhir-i", "FHIR Infrastructure"), ("oo", "Orders and Observations"));
        SeedJiraSpecsAndWorkgroups(
            repo: "HL7/fhir",
            workgroups:
            [
                ("fhir-i", "FHIR Infrastructure", "fhir-i"),
                ("oo", "Orders and Observations", "oo"),
            ],
            specs:
            [
                ("a", "fhir-i"),
                ("b", "oo"),
            ]);

        RepoDefaultWorkGroupResolver sut = MakeResolver(new GitHubServiceOptions());
        using SqliteConnection conn = _database.OpenConnection();
        RepoDefaultResult result = sut.Resolve(conn, "HL7/fhir");

        // Tie of 1-1: ascending code → "fhir-i" wins ("fhir-i" < "oo").
        Assert.Equal("fhir-i", result.Code);
    }

    [Fact]
    public void MajorityJiraSpec_OnlyUnresolvedNames_ReturnsRawOnly()
    {
        // Hl7 codeset empty → JIRA-Spec workgroups never resolved → only Name (Raw) signal.
        SeedJiraSpecsAndWorkgroups(
            repo: "HL7/example",
            workgroups:
            [
                ("alpha", "Alpha Group", null),
                ("beta", "Beta Group", null),
            ],
            specs:
            [
                ("s1", "alpha"),
                ("s2", "alpha"),
                ("s3", "beta"),
            ]);

        RepoDefaultWorkGroupResolver sut = MakeResolver(new GitHubServiceOptions());
        using SqliteConnection conn = _database.OpenConnection();
        RepoDefaultResult result = sut.Resolve(conn, "HL7/example");

        Assert.Null(result.Code);
        Assert.Equal("Alpha Group", result.Raw);
        Assert.Equal(RepoDefaultWorkGroupResolver.SourceMajorityJiraSpec, result.Source);
    }

    [Fact]
    public void NoSignalAtAll_ReturnsEmptyMajority()
    {
        RepoDefaultWorkGroupResolver sut = MakeResolver(new GitHubServiceOptions());
        using SqliteConnection conn = _database.OpenConnection();
        RepoDefaultResult result = sut.Resolve(conn, "HL7/empty");

        Assert.Null(result.Code);
        Assert.Null(result.Raw);
        Assert.Equal(RepoDefaultWorkGroupResolver.SourceMajorityJiraSpec, result.Source);
    }

    private void SeedJiraSpecsAndWorkgroups(
        string repo,
        (string Key, string Name, string? Code)[] workgroups,
        (string SpecKey, string DefaultWorkgroupKey)[] specs)
    {
        using SqliteConnection conn = _database.OpenConnection();

        List<JiraWorkgroupRecord> wgRows = [];
        foreach ((string key, string name, string? code) in workgroups)
        {
            wgRows.Add(new JiraWorkgroupRecord
            {
                Id = JiraWorkgroupRecord.GetIndex(),
                RepoFullName = repo,
                WorkgroupKey = key,
                Name = name,
                Deprecated = false,
                WorkGroupCode = code,
            });
        }
        JiraWorkgroupRecord.Insert(conn, wgRows, ignoreDuplicates: true, insertPrimaryKey: true);

        List<JiraSpecRecord> specRows = [];
        foreach ((string specKey, string defaultWg) in specs)
        {
            specRows.Add(new JiraSpecRecord
            {
                Id = JiraSpecRecord.GetIndex(),
                RepoFullName = repo,
                FilePath = $"xml/FHIR-{specKey}.xml",
                Family = "FHIR",
                SpecKey = specKey,
                DefaultWorkgroup = defaultWg,
                DefaultVersion = "STU1",
            });
        }
        JiraSpecRecord.Insert(conn, specRows, ignoreDuplicates: true, insertPrimaryKey: true);
    }
}
