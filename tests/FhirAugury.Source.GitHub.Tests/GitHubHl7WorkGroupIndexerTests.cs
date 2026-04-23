using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Tests for <see cref="GitHubHl7WorkGroupIndexer"/> — verifies the GitHub
/// store-backed indexer ingests a synthetic <c>CodeSystem-hl7-work-group</c>
/// XML into the local <c>hl7_workgroups</c> table.
/// </summary>
public sealed class GitHubHl7WorkGroupIndexerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly string _workDir;

    public GitHubHl7WorkGroupIndexerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gh_hl7wg_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _workDir = Path.Combine(Path.GetTempPath(), $"gh_hl7wg_xml_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private string WriteFixture(string body)
    {
        string xml = $$"""
            <CodeSystem xmlns="http://hl7.org/fhir">
              {{body}}
            </CodeSystem>
            """;
        string path = Path.Combine(_workDir, $"cs_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void Rebuild_NullPath_NoOp_ReturnsZero()
    {
        GitHubHl7WorkGroupIndexer ix = new(_db, NullLogger<GitHubHl7WorkGroupIndexer>.Instance);
        int total = ix.Rebuild(null);
        Assert.Equal(0, total);
    }

    [Fact]
    public void Rebuild_HappyPath_InsertsRows()
    {
        string xml = WriteFixture("""
              <concept>
                <code value="fhir"/>
                <display value="FHIR Infrastructure"/>
                <definition value="The FHIR-I work group."/>
              </concept>
              <concept>
                <code value="oo"/>
                <display value="Orders &amp; Observations"/>
              </concept>
            """);

        GitHubHl7WorkGroupIndexer ix = new(_db, NullLogger<GitHubHl7WorkGroupIndexer>.Instance);
        int total = ix.Rebuild(xml);

        Assert.Equal(2, total);
        using SqliteConnection conn = _db.OpenConnection();
        List<Hl7WorkGroupRecord> rows = Hl7WorkGroupRecord.SelectList(conn);
        Assert.Equal(2, rows.Count);
        Hl7WorkGroupRecord fhir = rows.Single(r => r.Code == "fhir");
        Assert.Equal("FHIR Infrastructure", fhir.Name);
        Assert.False(fhir.Retired);
        Assert.NotEmpty(fhir.NameClean);
    }

    [Fact]
    public void Rebuild_RetiresMissingRowsOnSecondPass()
    {
        string firstXml = WriteFixture("""
              <concept><code value="fhir"/><display value="FHIR Infrastructure"/></concept>
              <concept><code value="oo"/><display value="Orders and Observations"/></concept>
            """);
        GitHubHl7WorkGroupIndexer ix = new(_db, NullLogger<GitHubHl7WorkGroupIndexer>.Instance);
        ix.Rebuild(firstXml);

        // Second XML drops "oo" — it must be retired, not deleted.
        string secondXml = WriteFixture("""
              <concept><code value="fhir"/><display value="FHIR Infrastructure"/></concept>
            """);
        int total = ix.Rebuild(secondXml);

        Assert.Equal(2, total);
        using SqliteConnection conn = _db.OpenConnection();
        List<Hl7WorkGroupRecord> rows = Hl7WorkGroupRecord.SelectList(conn);
        Assert.True(rows.Single(r => r.Code == "oo").Retired);
        Assert.False(rows.Single(r => r.Code == "fhir").Retired);
    }
}
