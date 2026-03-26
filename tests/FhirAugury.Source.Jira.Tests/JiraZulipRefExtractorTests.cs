using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;

namespace FhirAugury.Source.Jira.Tests;

public class JiraZulipRefExtractorTests
{
    [Fact]
    public void ExtractReferences_MatchesChannelUrl_WithTopicAndMessage()
    {
        string text = "See https://chat.fhir.org/#narrow/channel/179166-implementers/topic/Exclusive%20Observation%2EreferenceRange/with/290861524 for details.";

        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("issue", "FHIR-12345", text);

        Assert.Single(results);
        ZulipXRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("Exclusive Observation.referenceRange", record.TopicName);
        Assert.Equal(290861524, record.MessageId);
        Assert.Equal("FHIR-12345", record.SourceId);
        Assert.Equal("issue", record.SourceType);
    }

    [Fact]
    public void ExtractReferences_MatchesChannelUrl_WithNear()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/TestTopic/near/540385278";

        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("comment", "FHIR-100", text);

        Assert.Single(results);
        ZulipXRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("TestTopic", record.TopicName);
        Assert.Equal(540385278, record.MessageId);
    }

    [Fact]
    public void ExtractReferences_MatchesChannelUrl_TopicOnly()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/SomeTopic";

        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("issue", "FHIR-200", text);

        Assert.Single(results);
        ZulipXRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("SomeTopic", record.TopicName);
        Assert.Null(record.MessageId);
    }

    [Fact]
    public void ExtractReferences_MatchesChannelUrl_StreamOnly()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers";

        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("issue", "FHIR-300", text);

        Assert.Single(results);
        ZulipXRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Null(record.TopicName);
        Assert.Null(record.MessageId);
    }

    [Fact]
    public void ExtractReferences_MatchesStreamUrl()
    {
        string text = "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/OldTopic/with/123456";

        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("issue", "FHIR-400", text);

        Assert.Single(results);
        ZulipXRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("OldTopic", record.TopicName);
        Assert.Equal(123456, record.MessageId);
    }

    [Fact]
    public void ExtractReferences_DecodesUrlEncodedTopicNames()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/Hello%20World%2Efoo";

        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("issue", "FHIR-500", text);

        Assert.Single(results);
        Assert.Equal("Hello World.foo", results[0].TopicName);
    }

    [Fact]
    public void ExtractReferences_DeduplicatesSameUrl()
    {
        string url = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/TestTopic/with/999";
        string text = $"First ref: {url} and again: {url}";

        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("issue", "FHIR-600", text);

        Assert.Single(results);
    }

    [Fact]
    public void ExtractReferences_EmptyText_ReturnsEmpty()
    {
        List<ZulipXRefRecord> results = ZulipReferenceExtractor.GetReferences("issue", "FHIR-700", "");

        Assert.Empty(results);
    }
}
