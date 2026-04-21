using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class Hl7WorkGroupIndexerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly Hl7WorkGroupIndexer _indexer;
    private readonly string _workDir;

    public Hl7WorkGroupIndexerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_hl7wg_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _indexer = new Hl7WorkGroupIndexer(_db, NullLogger<Hl7WorkGroupIndexer>.Instance);
        _workDir = Path.Combine(Path.GetTempPath(), $"jira_hl7wg_xml_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static string SampleXmlPath()
    {
        string baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "TestData", "CodeSystem-hl7-work-group-sample.xml");
    }

    private string WriteXml(string contents)
    {
        string path = Path.Combine(_workDir, $"cs_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Rebuild_HappyPath_ParsesAllConcepts()
    {
        int total = _indexer.Rebuild(SampleXmlPath());

        Assert.Equal(7, total);

        using SqliteConnection conn = _db.OpenConnection();
        List<Hl7WorkGroupRecord> rows = Hl7WorkGroupRecord.SelectList(conn);

        Hl7WorkGroupRecord fhir = rows.Single(r => r.Code == "fhir");
        Assert.Equal("FHIR Infrastructure", fhir.Name);
        Assert.Equal("FHIRInfrastructure", fhir.NameClean);
        Assert.False(fhir.Retired);
        Assert.Equal("The FHIR Infrastructure Work Group.", fhir.Definition);

        Hl7WorkGroupRecord nested = rows.Single(r => r.Code == "fhir-i-sub");
        Assert.Equal("FHIR Infrastructure Sub Group", nested.Name);
        Assert.Equal("FHIRInfrastructureSubGroup", nested.NameClean);

        Hl7WorkGroupRecord oo = rows.Single(r => r.Code == "oo");
        Assert.Equal("OrdersAndObservations", oo.NameClean);
    }

    [Fact]
    public void Rebuild_DetectsRetiredProperty()
    {
        _indexer.Rebuild(SampleXmlPath());

        using SqliteConnection conn = _db.OpenConnection();
        List<Hl7WorkGroupRecord> rows = Hl7WorkGroupRecord.SelectList(conn);

        Assert.True(rows.Single(r => r.Code == "oldwg").Retired);
        Assert.False(rows.Single(r => r.Code == "pc").Retired);
    }

    [Fact]
    public void Rebuild_MissingDisplay_FallsBackToCode()
    {
        _indexer.Rebuild(SampleXmlPath());

        using SqliteConnection conn = _db.OpenConnection();
        Hl7WorkGroupRecord row = Hl7WorkGroupRecord.SelectList(conn)
            .Single(r => r.Code == "nodisplay");
        Assert.Equal("nodisplay", row.Name);
        Assert.Equal("Nodisplay", row.NameClean);
    }

    [Fact]
    public void Rebuild_PreservesIdsOnReload()
    {
        _indexer.Rebuild(SampleXmlPath());

        Dictionary<string, int> idsAfterFirst;
        using (SqliteConnection conn = _db.OpenConnection())
        {
            idsAfterFirst = Hl7WorkGroupRecord.SelectList(conn)
                .ToDictionary(r => r.Code, r => r.Id);
        }

        _indexer.Rebuild(SampleXmlPath());

        using SqliteConnection conn2 = _db.OpenConnection();
        Dictionary<string, int> idsAfterSecond = Hl7WorkGroupRecord.SelectList(conn2)
            .ToDictionary(r => r.Code, r => r.Id);

        Assert.Equal(idsAfterFirst.Count, idsAfterSecond.Count);
        foreach (KeyValuePair<string, int> kv in idsAfterFirst)
            Assert.Equal(kv.Value, idsAfterSecond[kv.Key]);
    }

    [Fact]
    public void Rebuild_NewCodeAdded_GetsNewIdOthersUnchanged()
    {
        _indexer.Rebuild(SampleXmlPath());

        Dictionary<string, int> ids;
        using (SqliteConnection conn = _db.OpenConnection())
        {
            ids = Hl7WorkGroupRecord.SelectList(conn)
                .ToDictionary(r => r.Code, r => r.Id);
        }

        string xml2 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CodeSystem xmlns="http://hl7.org/fhir">
              <concept>
                <code value="fhir"/>
                <display value="FHIR Infrastructure"/>
              </concept>
              <concept>
                <code value="pc"/>
                <display value="Patient Care"/>
              </concept>
              <concept>
                <code value="oo"/>
                <display value="Orders &amp; Observations"/>
              </concept>
              <concept>
                <code value="cds"/>
                <display value="CDS (Clinical Decision Support)"/>
              </concept>
              <concept>
                <code value="oldwg"/>
                <display value="Retired Group"/>
                <property>
                  <code value="status"/>
                  <valueCode value="retired"/>
                </property>
              </concept>
              <concept>
                <code value="nodisplay"/>
              </concept>
              <concept>
                <code value="fhir-i-sub"/>
                <display value="FHIR Infrastructure Sub Group"/>
              </concept>
              <concept>
                <code value="newgrp"/>
                <display value="New Group"/>
              </concept>
            </CodeSystem>
            """;
        _indexer.Rebuild(WriteXml(xml2));

        using SqliteConnection conn2 = _db.OpenConnection();
        List<Hl7WorkGroupRecord> rows = Hl7WorkGroupRecord.SelectList(conn2);

        Hl7WorkGroupRecord ng = rows.Single(r => r.Code == "newgrp");
        Assert.False(ids.ContainsKey("newgrp"));
        Assert.True(ng.Id > 0);

        foreach (KeyValuePair<string, int> kv in ids)
        {
            Hl7WorkGroupRecord match = rows.Single(r => r.Code == kv.Key);
            Assert.Equal(kv.Value, match.Id);
        }
    }

    [Fact]
    public void Rebuild_CodeDroppedInSecondXml_KeepsRowAndMarksRetired()
    {
        _indexer.Rebuild(SampleXmlPath());

        int pcId;
        using (SqliteConnection conn = _db.OpenConnection())
        {
            pcId = Hl7WorkGroupRecord.SelectList(conn).Single(r => r.Code == "pc").Id;
        }

        string xml2 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CodeSystem xmlns="http://hl7.org/fhir">
              <concept>
                <code value="fhir"/>
                <display value="FHIR Infrastructure"/>
              </concept>
            </CodeSystem>
            """;
        _indexer.Rebuild(WriteXml(xml2));

        using SqliteConnection conn2 = _db.OpenConnection();
        Hl7WorkGroupRecord pc = Hl7WorkGroupRecord.SelectList(conn2).Single(r => r.Code == "pc");
        Assert.Equal(pcId, pc.Id);
        Assert.True(pc.Retired);
    }

    [Fact]
    public void Rebuild_MissingFile_ReturnsZeroAndDoesNotThrow()
    {
        int total = _indexer.Rebuild(Path.Combine(_workDir, "does-not-exist.xml"));
        Assert.Equal(0, total);
    }

    [Fact]
    public void Rebuild_NullPath_ReturnsZero()
    {
        int total = _indexer.Rebuild(null);
        Assert.Equal(0, total);
    }
}
