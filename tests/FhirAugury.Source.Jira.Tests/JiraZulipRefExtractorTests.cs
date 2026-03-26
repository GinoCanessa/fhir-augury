using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

public class JiraZulipRefExtractorTests
{
    [Fact]
    public void ExtractReferences_MatchesChannelUrl_WithTopicAndMessage()
    {
        string text = "See https://chat.fhir.org/#narrow/channel/179166-implementers/topic/Exclusive%20Observation%2EreferenceRange/with/290861524 for details.";

        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(text, "FHIR-12345", "issue");

        Assert.Single(results);
        JiraZulipRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("Exclusive Observation.referenceRange", record.TopicName);
        Assert.Equal(290861524, record.MessageId);
        Assert.Equal("FHIR-12345", record.IssueKey);
        Assert.Equal("issue", record.SourceType);
    }

    [Fact]
    public void ExtractReferences_MatchesChannelUrl_WithNear()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/TestTopic/near/540385278";

        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(text, "FHIR-100", "comment");

        Assert.Single(results);
        JiraZulipRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("TestTopic", record.TopicName);
        Assert.Equal(540385278, record.MessageId);
    }

    [Fact]
    public void ExtractReferences_MatchesChannelUrl_TopicOnly()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/SomeTopic";

        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(text, "FHIR-200", "issue");

        Assert.Single(results);
        JiraZulipRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("SomeTopic", record.TopicName);
        Assert.Null(record.MessageId);
    }

    [Fact]
    public void ExtractReferences_MatchesChannelUrl_StreamOnly()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers";

        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(text, "FHIR-300", "issue");

        Assert.Single(results);
        JiraZulipRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Null(record.TopicName);
        Assert.Null(record.MessageId);
    }

    [Fact]
    public void ExtractReferences_MatchesStreamUrl()
    {
        string text = "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/OldTopic/with/123456";

        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(text, "FHIR-400", "issue");

        Assert.Single(results);
        JiraZulipRefRecord record = results[0];
        Assert.Equal(179166, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("OldTopic", record.TopicName);
        Assert.Equal(123456, record.MessageId);
    }

    [Fact]
    public void ExtractReferences_DecodesUrlEncodedTopicNames()
    {
        string text = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/Hello%20World%2Efoo";

        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(text, "FHIR-500", "issue");

        Assert.Single(results);
        Assert.Equal("Hello World.foo", results[0].TopicName);
    }

    [Fact]
    public void ExtractReferences_DeduplicatesSameUrl()
    {
        string url = "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/TestTopic/with/999";
        string text = $"First ref: {url} and again: {url}";

        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(text, "FHIR-600", "issue");

        Assert.Single(results);
    }

    [Fact]
    public void ExtractReferences_EmptyText_ReturnsEmpty()
    {
        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences("", "FHIR-700", "issue");

        Assert.Empty(results);
    }

    [Fact]
    public void ExtractReferences_NullText_ReturnsEmpty()
    {
        List<JiraZulipRefRecord> results = JiraZulipRefExtractor.ExtractReferences(null, "FHIR-800", "issue");

        Assert.Empty(results);
    }
}
