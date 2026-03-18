using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class Fts5ConfluenceTests
{
    [Fact]
    public void Insert_AutoPopulatesFts()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var page = TestHelper.CreateSamplePage("1", "Patient Resource Guide", bodyPlain: "Guide to using the Patient FHIR resource");
        ConfluencePageRecord.Insert(conn, page);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM confluence_pages_fts WHERE confluence_pages_fts MATCH '\"Patient\"'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Update_UpdatesFts()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var page = TestHelper.CreateSamplePage("1", "Original Title", bodyPlain: "Original content");
        ConfluencePageRecord.Insert(conn, page);

        page.Title = "Updated Title about Observation";
        page.BodyPlain = "Updated content about Observation resource";
        ConfluencePageRecord.Update(conn, page);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM confluence_pages_fts WHERE confluence_pages_fts MATCH '\"Observation\"'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Delete_RemovesFromFts()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var page = TestHelper.CreateSamplePage("1", "To Delete", bodyPlain: "Content to delete");
        ConfluencePageRecord.Insert(conn, page);

        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM confluence_pages WHERE Id = @id";
        delCmd.Parameters.AddWithValue("@id", page.Id);
        delCmd.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM confluence_pages_fts WHERE confluence_pages_fts MATCH '\"Delete\"'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(0, count);
    }
}
