using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Database.Tests;

public class Fts5ZulipTests
{
    [Fact]
    public void InsertMessage_AutoPopulatesFts5()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(100, "implementers");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(1000, stream.Id, "implementers", "FHIRPath",
            content: "The FHIRPath specification defines aggregate functions.");
        ZulipMessageRecord.Insert(conn, msg);

        var results = FtsSearchService.SearchZulipMessages(conn, "FHIRPath", limit: 10);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id.StartsWith("implementers:"));
    }

    [Fact]
    public void UpdateMessage_UpdatesFts5()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(200, "terminology");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(2000, stream.Id, "terminology", "CodeSystems",
            content: "Original content about bundles.");
        ZulipMessageRecord.Insert(conn, msg);

        msg.ContentPlain = "Updated content about FHIRPath expressions.";
        ZulipMessageRecord.Update(conn, msg);

        var newResults = FtsSearchService.SearchZulipMessages(conn, "FHIRPath", limit: 10);
        Assert.NotEmpty(newResults);

        var oldResults = FtsSearchService.SearchZulipMessages(conn, "bundles", limit: 10);
        Assert.Empty(oldResults);
    }

    [Fact]
    public void DeleteMessage_RemovesFromFts5()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(300, "test-stream");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(3000, stream.Id, "test-stream", "questionnaires",
            content: "FHIR questionnaires are used for data capture.");
        ZulipMessageRecord.Insert(conn, msg);

        var beforeResults = FtsSearchService.SearchZulipMessages(conn, "questionnaires", limit: 10);
        Assert.NotEmpty(beforeResults);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM zulip_messages WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", msg.Id);
        cmd.ExecuteNonQuery();

        var afterResults = FtsSearchService.SearchZulipMessages(conn, "questionnaires", limit: 10);
        Assert.Empty(afterResults);
    }

    [Fact]
    public void FtsSearch_ReturnsSnippets()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(400, "implementers");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(4000, stream.Id, "implementers", "FHIRPath",
            content: "The FHIRPath specification defines a set of aggregate functions including count, sum, and avg.");
        ZulipMessageRecord.Insert(conn, msg);

        var results = FtsSearchService.SearchZulipMessages(conn, "aggregate", limit: 10);
        Assert.NotEmpty(results);
        Assert.NotNull(results[0].Snippet);
    }

    [Fact]
    public void FtsSearch_RanksRelevantResultsHigher()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(500, "implementers");
        ZulipStreamRecord.Insert(conn, stream);

        var relevant = TestHelper.CreateSampleMessage(5001, stream.Id, "implementers", "normative",
            content: "The normative ballot for the normative FHIRPath normative specification is ready.");
        ZulipMessageRecord.Insert(conn, relevant);

        var lessRelevant = TestHelper.CreateSampleMessage(5002, stream.Id, "implementers", "general",
            content: "A minor mention of normative once.");
        ZulipMessageRecord.Insert(conn, lessRelevant);

        var results = FtsSearchService.SearchZulipMessages(conn, "normative", limit: 10);
        Assert.True(results.Count >= 2);
        Assert.Contains("normative", results[0].Id);
    }

    [Fact]
    public void FtsSearch_FiltersByStream()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream1 = TestHelper.CreateSampleStream(600, "implementers");
        ZulipStreamRecord.Insert(conn, stream1);
        var stream2 = TestHelper.CreateSampleStream(601, "terminology");
        ZulipStreamRecord.Insert(conn, stream2);

        ZulipMessageRecord.Insert(conn, TestHelper.CreateSampleMessage(6001, stream1.Id, "implementers", "topic",
            content: "FHIRPath discussion in implementers"));
        ZulipMessageRecord.Insert(conn, TestHelper.CreateSampleMessage(6002, stream2.Id, "terminology", "topic",
            content: "FHIRPath discussion in terminology"));

        var filtered = FtsSearchService.SearchZulipMessages(conn, "FHIRPath", limit: 10, streamFilter: "implementers");
        Assert.Single(filtered);
        Assert.Contains("implementers", filtered[0].Id);
    }

    [Fact]
    public void RebuildZulipFts_RepopulatesFromContentTables()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(700, "test");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(7000, stream.Id, "test", "terminology",
            content: "Testing terminology binding for CodeSystem resources.");
        ZulipMessageRecord.Insert(conn, msg);

        FtsSetup.RebuildZulipFts(conn);

        var results = FtsSearchService.SearchZulipMessages(conn, "terminology", limit: 10);
        Assert.NotEmpty(results);
    }
}
