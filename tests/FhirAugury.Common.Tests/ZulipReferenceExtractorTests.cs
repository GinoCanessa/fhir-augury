using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;

namespace FhirAugury.Common.Tests;

public class ZulipReferenceExtractorTests
{
    [Fact]
    public void ExtractsStreamAndTopicUrl()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "See https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Coverage for details");
        Assert.Single(refs);
        Assert.Equal(179166, refs[0].StreamId);
        Assert.Equal("implementers", refs[0].StreamName);
        Assert.Equal("Coverage", refs[0].TopicName);
        Assert.Null(refs[0].MessageId);
    }

    [Fact]
    public void ExtractsChannelVariant()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "https://chat.fhir.org/#narrow/channel/179166-implementers/topic/Coverage");
        Assert.Single(refs);
        Assert.Equal(179166, refs[0].StreamId);
        Assert.Equal("Coverage", refs[0].TopicName);
    }

    [Fact]
    public void ExtractsUrlEncodedTopic()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Foo%20Bar%20Baz");
        Assert.Single(refs);
        Assert.Equal("Foo Bar Baz", refs[0].TopicName);
    }

    [Fact]
    public void ExtractsStreamOnlyUrl()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "See https://chat.fhir.org/#narrow/stream/179166-implementers for info");
        Assert.Single(refs);
        Assert.Equal(179166, refs[0].StreamId);
        Assert.Null(refs[0].TopicName);
    }

    [Fact]
    public void ExtractsWithMessageId()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Coverage/near/123456");
        Assert.Single(refs);
        Assert.Equal(123456, refs[0].MessageId);
    }

    [Fact]
    public void ExtractsWithVariant()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Coverage/with/123456");
        Assert.Single(refs);
        Assert.Equal(123456, refs[0].MessageId);
    }

    [Fact]
    public void DeduplicatesSameUrl()
    {
        string url = "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Coverage";
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1", $"{url} and again {url}");
        Assert.Single(refs);
    }

    [Fact]
    public void TargetIdWithTopic()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Coverage");
        Assert.Equal("179166:Coverage", refs[0].TargetId);
    }

    [Fact]
    public void TargetIdStreamOnly()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "https://chat.fhir.org/#narrow/stream/179166-implementers");
        Assert.Equal("179166", refs[0].TargetId);
    }

    [Fact]
    public void TargetTypeIsZulip()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences(
            "issue", "1",
            "https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Coverage");
        Assert.Equal(SourceSystems.Zulip, refs[0].TargetType);
    }

    [Fact]
    public void EmptyTextReturnsEmpty()
    {
        List<ZulipXRefRecord> refs = ZulipReferenceExtractor.GetReferences("issue", "1", "");
        Assert.Empty(refs);
    }
}
