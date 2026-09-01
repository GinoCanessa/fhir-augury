using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="SpecArtifactWorkGroupResolver"/> over a seeded throwaway
/// <c>github.db</c>: registry-primary owner lookup, <c>WorkgroupKey</c> →
/// canonical <c>WorkGroupCode</c> mapping, case-insensitive name matching, and
/// the cross-spec ambiguity guard (fall through, not an arbitrary hit).
/// </summary>
public sealed class SpecArtifactWorkGroupResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    private const string Registry = "HL7/JIRA-Spec-Artifacts";

    public SpecArtifactWorkGroupResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "specartwg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "github.db");
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void Resolves_artifact_owner_regardless_of_ticket_wg()
    {
        using SqliteConnection conn = Seed();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        SeedArtifact(conn, "R5", name: "Observation", workgroupKey: "oo-key");
        SeedWorkgroup(conn, "oo-key", code: "oo");
        SeedHl7(conn, "oo", "Orders and Observations (OO)");

        string? code = SpecArtifactWorkGroupResolver.Resolve(conn, "HL7", "fhir", "Artifact", "Observation");

        Assert.Equal("oo", code);
    }

    [Fact]
    public void Maps_workgroupkey_through_jira_workgroups_to_canonical_code()
    {
        using SqliteConnection conn = Seed();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        // The registry stores a WorkgroupKey ("pafm") that is NOT the canonical code.
        SeedArtifact(conn, "R5", name: "Account", workgroupKey: "pafm");
        SeedWorkgroup(conn, "pafm", code: "fm");
        SeedHl7(conn, "fm", "Financial Management (FM)");

        string? code = SpecArtifactWorkGroupResolver.Resolve(conn, "HL7", "fhir", "Artifact", "Account");

        Assert.Equal("fm", code);
        Assert.NotEqual("pafm", code);
    }

    [Fact]
    public void Matches_lowercase_unit_name_against_titlecased_registry_name()
    {
        using SqliteConnection conn = Seed();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        SeedArtifact(conn, "R5", name: "Observation", workgroupKey: "oo");
        SeedWorkgroup(conn, "oo", code: "oo");

        // Unit name comes from a lowercase path; registry name is title-cased.
        string? code = SpecArtifactWorkGroupResolver.Resolve(conn, "HL7", "fhir", "Artifact", "observation");

        Assert.Equal("oo", code);
    }

    [Fact]
    public void Falls_through_when_same_name_owned_by_two_specs_with_repo_unmatched()
    {
        using SqliteConnection conn = Seed();
        // Two specs, neither GitUrl matching the hydrated HL7/fhir repo.
        SeedSpec(conn, "A", "https://github.com/HL7/other-a");
        SeedSpec(conn, "B", "https://github.com/HL7/other-b");
        SeedArtifact(conn, "A", name: "Observation", workgroupKey: "oo");
        SeedArtifact(conn, "B", name: "Observation", workgroupKey: "pa");
        SeedWorkgroup(conn, "oo", code: "oo");
        SeedWorkgroup(conn, "pa", code: "pa");

        string? code = SpecArtifactWorkGroupResolver.Resolve(conn, "HL7", "fhir", "Artifact", "Observation");

        Assert.Null(code);
    }

    [Fact]
    public void Resolves_page_owner_from_jira_spec_pages()
    {
        using SqliteConnection conn = Seed();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        SeedPage(conn, "R5", name: "security", pageKey: "security", workgroupKey: "sec");
        SeedWorkgroup(conn, "sec", code: "sec");
        SeedHl7(conn, "sec", "Security (SEC)");

        string? code = SpecArtifactWorkGroupResolver.Resolve(conn, "HL7", "fhir", "Page", "security");

        Assert.Equal("sec", code);
    }

    private SqliteConnection Seed()
    {
        SqliteConnection conn = new($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        Exec(conn,
            "CREATE TABLE jira_specs (Id INTEGER PRIMARY KEY, RepoFullName TEXT, SpecKey TEXT, GitUrl TEXT)");
        Exec(conn,
            "CREATE TABLE jira_spec_artifacts (Id INTEGER PRIMARY KEY, RepoFullName TEXT, SpecKey TEXT, " +
            "Name TEXT, ArtifactId TEXT, ResourceType TEXT, Workgroup TEXT, Deprecated INTEGER)");
        Exec(conn,
            "CREATE TABLE jira_spec_pages (Id INTEGER PRIMARY KEY, RepoFullName TEXT, SpecKey TEXT, " +
            "PageKey TEXT, Name TEXT, Workgroup TEXT, Deprecated INTEGER)");
        Exec(conn,
            "CREATE TABLE jira_workgroups (Id INTEGER PRIMARY KEY, RepoFullName TEXT, WorkgroupKey TEXT, " +
            "Name TEXT, WorkGroupCode TEXT)");
        Exec(conn, "CREATE TABLE hl7_workgroups (Id INTEGER PRIMARY KEY, Code TEXT, Name TEXT)");
        return conn;
    }

    private static void SeedSpec(SqliteConnection conn, string specKey, string gitUrl)
        => Exec(conn,
            "INSERT INTO jira_specs (RepoFullName, SpecKey, GitUrl) " +
            $"VALUES ('{Registry}', '{specKey}', '{gitUrl}')");

    private static void SeedArtifact(SqliteConnection conn, string specKey, string name, string workgroupKey)
        => Exec(conn,
            "INSERT INTO jira_spec_artifacts (RepoFullName, SpecKey, Name, ArtifactId, ResourceType, Workgroup, Deprecated) " +
            $"VALUES ('{Registry}', '{specKey}', '{name}', '{name}', '{name}', '{workgroupKey}', 0)");

    private static void SeedPage(SqliteConnection conn, string specKey, string name, string pageKey, string workgroupKey)
        => Exec(conn,
            "INSERT INTO jira_spec_pages (RepoFullName, SpecKey, PageKey, Name, Workgroup, Deprecated) " +
            $"VALUES ('{Registry}', '{specKey}', '{pageKey}', '{name}', '{workgroupKey}', 0)");

    private static void SeedWorkgroup(SqliteConnection conn, string workgroupKey, string code)
        => Exec(conn,
            "INSERT INTO jira_workgroups (RepoFullName, WorkgroupKey, Name, WorkGroupCode) " +
            $"VALUES ('{Registry}', '{workgroupKey}', '{workgroupKey}', '{code}')");

    private static void SeedHl7(SqliteConnection conn, string code, string name)
        => Exec(conn, $"INSERT INTO hl7_workgroups (Code, Name) VALUES ('{code}', '{name}')");

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
