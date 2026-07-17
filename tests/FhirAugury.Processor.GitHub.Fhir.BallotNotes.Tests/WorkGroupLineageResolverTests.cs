using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="WorkGroupLineageResolver"/>: the Listed lineage is the
/// repo-read of the artifact/page definition (never the registry), the Index
/// lineage is the registry only, the two stay distinct when they disagree, and
/// the datatypes surface resolves per-datatype with the FHIR-Infrastructure
/// Listed fallback. Registry rows live in a seeded throwaway <c>github.db</c>.
/// </summary>
public sealed class WorkGroupLineageResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    private const string Registry = "HL7/JIRA-Spec-Artifacts";

    public WorkGroupLineageResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wglineage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "github.db");
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void Artifact_listed_is_repo_read_and_index_is_registry_when_they_disagree()
    {
        using SqliteConnection conn = SeedRegistry();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        SeedArtifact(conn, "R5", name: "Observation", workgroupKey: "oo");
        SeedWorkgroup(conn, "oo", code: "oo");
        SeedHl7(conn, "oo", "Orders and Observations (OO)");

        // The SD declares a *different* WG than the registry.
        string rel = WriteSd("observation", "structuredefinition-Observation.xml", workGroup: "fhir");
        HydrationUnit unit = ArtifactUnit("observation", rel);

        WorkGroupLineages lineages = Resolve(unit, [Sd(rel)], conn);

        Assert.Equal("fhir", Assert.Single(lineages.Listed).Code);   // repo-read only
        Assert.Equal("oo", Assert.Single(lineages.Index).Code);      // registry only
    }

    [Fact]
    public void Artifact_listed_is_unknown_when_sd_declares_no_wg_even_if_registry_has_one()
    {
        using SqliteConnection conn = SeedRegistry();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        SeedArtifact(conn, "R5", name: "Observation", workgroupKey: "oo");
        SeedWorkgroup(conn, "oo", code: "oo");

        string rel = WriteSd("observation", "structuredefinition-Observation.xml", workGroup: null);
        HydrationUnit unit = ArtifactUnit("observation", rel);

        WorkGroupLineages lineages = Resolve(unit, [Sd(rel)], conn);

        // Listed never borrows the index: (unknown), empty code.
        WorkGroupRef listed = Assert.Single(lineages.Listed);
        Assert.Equal("(unknown)", listed.DisplayName);
        Assert.Equal(string.Empty, listed.Code);
        // Index still resolves from the registry.
        Assert.Equal("oo", Assert.Single(lineages.Index).Code);
    }

    [Fact]
    public void Page_listed_reads_marker_and_index_reads_registry()
    {
        using SqliteConnection conn = SeedRegistry();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        SeedPage(conn, "R5", name: "security", pageKey: "security", workgroupKey: "pa");
        SeedWorkgroup(conn, "pa", code: "pa");

        WritePage("security", "<td id=\"wg\"><a href=\"[%wg sec%]\">[%wgt sec%]</a> Work Group</td>");
        HydrationUnit unit = new() { Type = "Page", Name = "security", ChangedPaths = ["source/security.html"] };

        WorkGroupLineages lineages = Resolve(unit, [], conn);

        Assert.Equal("sec", Assert.Single(lineages.Listed).Code);  // page marker
        Assert.Equal("pa", Assert.Single(lineages.Index).Code);    // registry
    }

    [Fact]
    public void Datatype_listed_falls_back_to_fhir_infrastructure_when_no_in_file_wg()
    {
        using SqliteConnection conn = SeedRegistry();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");
        SeedArtifact(conn, "R5", name: "Quantity", workgroupKey: "oo");
        SeedWorkgroup(conn, "oo", code: "oo");

        HydrationUnit unit = new()
        {
            Type = "DataType",
            Name = "datatypes",
            ChangedPaths = ["source/datatypes/Quantity.xml"],
        };

        // No resolved SD files → per-datatype repo-read is empty → FHIR Infra fallback.
        WorkGroupLineages lineages = Resolve(unit, [], conn);

        Assert.Equal("fhir", Assert.Single(lineages.Listed).Code);   // datatype-only fallback
        Assert.Equal("oo", Assert.Single(lineages.Index).Code);      // registry per datatype
    }

    [Fact]
    public void Datatype_listed_fallback_does_not_apply_to_artifacts()
    {
        using SqliteConnection conn = SeedRegistry();
        SeedSpec(conn, "R5", "https://github.com/HL7/fhir");

        // Artifact with no SD WG and no registry entry: Listed stays (unknown),
        // never the datatype FHIR-Infrastructure fallback.
        string rel = WriteSd("observation", "structuredefinition-Observation.xml", workGroup: null);
        HydrationUnit unit = ArtifactUnit("observation", rel);

        WorkGroupLineages lineages = Resolve(unit, [Sd(rel)], conn);

        Assert.Equal("(unknown)", Assert.Single(lineages.Listed).DisplayName);
        Assert.NotEqual("fhir", lineages.Listed[0].Code);
    }

    private WorkGroupLineages Resolve(
        HydrationUnit unit, IReadOnlyList<ResolvedSourceFile> files, SqliteConnection? db)
        => WorkGroupLineageResolver.Resolve(
            unit,
            clonePath: _tempDir,
            owner: "HL7",
            name: "fhir",
            resolvedFiles: files,
            headDatatypeNames: [],
            db: db,
            nameCache: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            options: new BallotNotesHydrationOptions(),
            logger: null);

    private static HydrationUnit ArtifactUnit(string name, string changedPath)
        => new() { Type = "Artifact", Name = name, ChangedPaths = [changedPath] };

    private static ResolvedSourceFile Sd(string path) => new() { Path = path, Role = "StructureDefinition" };

    private string WriteSd(string folder, string fileName, string? workGroup)
    {
        string dir = Path.Combine(_tempDir, "source", folder);
        Directory.CreateDirectory(dir);

        string name = Path.GetFileNameWithoutExtension(fileName).Replace("structuredefinition-", string.Empty);
        string wgXml = workGroup is null
            ? string.Empty
            : $"<extension url=\"http://hl7.org/fhir/StructureDefinition/structuredefinition-wg\"><valueCode value=\"{workGroup}\"/></extension>";

        string xml =
            "<StructureDefinition xmlns=\"http://hl7.org/fhir\">" +
            $"<url value=\"http://hl7.org/fhir/StructureDefinition/{name}\"/>" +
            $"<name value=\"{name}\"/>" +
            "<status value=\"active\"/>" +
            "<kind value=\"resource\"/>" +
            "<abstract value=\"false\"/>" +
            $"<type value=\"{name}\"/>" +
            wgXml +
            "</StructureDefinition>";

        string fullPath = Path.Combine(dir, fileName);
        File.WriteAllText(fullPath, xml);
        return Path.GetRelativePath(_tempDir, fullPath).Replace('\\', '/');
    }

    private void WritePage(string stem, string html)
    {
        string dir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{stem}.html"), html);
    }

    private SqliteConnection SeedRegistry()
    {
        SqliteConnection conn = new($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        Exec(conn, "CREATE TABLE jira_specs (Id INTEGER PRIMARY KEY, RepoFullName TEXT, SpecKey TEXT, GitUrl TEXT)");
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
