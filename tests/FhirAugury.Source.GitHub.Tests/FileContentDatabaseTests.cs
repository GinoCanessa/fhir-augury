using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class FileContentDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public FileContentDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_fc_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Initialize_CreatesFileContentTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("github_file_contents", tables);
        Assert.Contains("github_file_contents_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_FileContent_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        GitHubFileContentRecord record = new GitHubFileContentRecord
        {
            Id = GitHubFileContentRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FilePath = "source/patient/Patient.xml",
            FileExtension = ".xml",
            ParserType = "xml",
            ContentText = "Patient resource definition with demographics",
            ContentLength = 1024,
            ExtractedLength = 47,
            LastCommitSha = "abc123",
            LastModifiedAt = "2025-01-15T10:30:00Z",
        };

        GitHubFileContentRecord.Insert(conn, record);
        List<GitHubFileContentRecord> results = GitHubFileContentRecord.SelectList(conn,
            RepoFullName: "HL7/fhir", FilePath: "source/patient/Patient.xml");

        Assert.Single(results);
        GitHubFileContentRecord retrieved = results[0];
        Assert.Equal("HL7/fhir", retrieved.RepoFullName);
        Assert.Equal("source/patient/Patient.xml", retrieved.FilePath);
        Assert.Equal(".xml", retrieved.FileExtension);
        Assert.Equal("xml", retrieved.ParserType);
        Assert.Equal("Patient resource definition with demographics", retrieved.ContentText);
        Assert.Equal(1024, retrieved.ContentLength);
        Assert.Equal(47, retrieved.ExtractedLength);
        Assert.Equal("abc123", retrieved.LastCommitSha);
    }

    [Fact]
    public void SelectList_ByParserType_FiltersCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubFileContentRecord.Insert(conn, CreateFileRecord("HL7/fhir", "file.xml", ".xml", "xml", "xml content"));
        GitHubFileContentRecord.Insert(conn, CreateFileRecord("HL7/fhir", "file.json", ".json", "json", "json content"));
        GitHubFileContentRecord.Insert(conn, CreateFileRecord("HL7/fhir", "file.md", ".md", "markdown", "md content"));

        List<GitHubFileContentRecord> xmlFiles = GitHubFileContentRecord.SelectList(conn, ParserType: "xml");
        List<GitHubFileContentRecord> jsonFiles = GitHubFileContentRecord.SelectList(conn, ParserType: "json");

        Assert.Single(xmlFiles);
        Assert.Single(jsonFiles);
    }

    [Fact]
    public void Fts5_IndexesFileContents()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubFileContentRecord.Insert(conn, CreateFileRecord("HL7/fhir", "patient.xml", ".xml", "xml",
            "Patient resource with demographics and administrative data"));
        GitHubFileContentRecord.Insert(conn, CreateFileRecord("HL7/fhir", "observation.xml", ".xml", "xml",
            "Observation resource for clinical measurements"));

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fc.FilePath FROM github_file_contents fc
            WHERE fc.Id IN (SELECT rowid FROM github_file_contents_fts WHERE github_file_contents_fts MATCH '"Patient"')
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<string> paths = [];
        while (reader.Read()) paths.Add(reader.GetString(0));

        Assert.Single(paths);
        Assert.Equal("patient.xml", paths[0]);
    }

    [Fact]
    public void ResetDatabase_DropsAndRecreatesFileContentTable()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubFileContentRecord.Insert(conn, CreateFileRecord("HL7/fhir", "file.xml", ".xml", "xml", "content"));
        Assert.Equal(1, GitHubFileContentRecord.SelectCount(conn));

        _db.ResetDatabase();

        using SqliteConnection conn2 = _db.OpenConnection();
        Assert.Equal(0, GitHubFileContentRecord.SelectCount(conn2));

        // Table should still exist after reset
        List<string> tables = GetTableNames(conn2);
        Assert.Contains("github_file_contents", tables);
        Assert.Contains("github_file_contents_fts", tables);
    }

    [Fact]
    public void RebuildFtsIndexes_IncludesFileContents()
    {
        using SqliteConnection conn = _db.OpenConnection();
        GitHubFileContentRecord.Insert(conn, CreateFileRecord("HL7/fhir", "test.xml", ".xml", "xml", "searchable content"));

        // Should not throw
        _db.RebuildFtsIndexes();

        // Verify FTS still works after rebuild
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM github_file_contents_fts WHERE github_file_contents_fts MATCH '"searchable"'
            """;
        int count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(1, count);
    }

    private static GitHubFileContentRecord CreateFileRecord(
        string repo, string path, string ext, string parser, string? content) => new()
    {
        Id = GitHubFileContentRecord.GetIndex(),
        RepoFullName = repo,
        FilePath = path,
        FileExtension = ext,
        ParserType = parser,
        ContentText = content,
        ContentLength = content?.Length ?? 0,
        ExtractedLength = content?.Length ?? 0,
    };

    private static List<string> GetTableNames(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'trigger') ORDER BY name";
        using SqliteDataReader reader = cmd.ExecuteReader();
        List<string> names = [];
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
