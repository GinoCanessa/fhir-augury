using FhirAugury.Common;
using FhirAugury.Common.Api;

namespace FhirAugury.Orchestrator.Tests;

/// <summary>
/// Verifies the cross-reference API contract types expose expected fields
/// and the HTTP API signature accepts targetSources filtering.
/// </summary>
public class CrossReferenceContractTests
{
    [Fact]
    public void SourceCrossReference_HasTargetTitleField()
    {
        SourceCrossReference xref = new SourceCrossReference(
            SourceType: SourceSystems.Confluence,
            SourceId: "page-1",
            TargetType: SourceSystems.Jira,
            TargetId: "FHIR-123",
            LinkType: "mentions",
            Context: null,
            SourceContentType: null,
            TargetTitle: "Example Title",
            TargetUrl: null);

        Assert.Equal("Example Title", xref.TargetTitle);
    }

    [Fact]
    public void SourceCrossReference_HasTargetUrlField()
    {
        SourceCrossReference xref = new SourceCrossReference(
            SourceType: SourceSystems.Confluence,
            SourceId: "page-1",
            TargetType: SourceSystems.Jira,
            TargetId: "FHIR-123",
            LinkType: "mentions",
            Context: null,
            SourceContentType: null,
            TargetTitle: null,
            TargetUrl: "https://example.com/item/1");

        Assert.Equal("https://example.com/item/1", xref.TargetUrl);
    }

    [Fact]
    public void SourceCrossReference_TargetFieldsDefaultToNull()
    {
        SourceCrossReference xref = new SourceCrossReference(
            SourceType: "",
            SourceId: "",
            TargetType: "",
            TargetId: "",
            LinkType: "",
            Context: null,
            SourceContentType: null,
            TargetTitle: null,
            TargetUrl: null);

        Assert.Null(xref.TargetTitle);
        Assert.Null(xref.TargetUrl);
    }

    [Fact]
    public void SourceCrossReference_AllFieldsRoundTrip()
    {
        SourceCrossReference xref = new SourceCrossReference(
            SourceType: SourceSystems.GitHub,
            SourceId: "HL7/fhir#4006",
            TargetType: SourceSystems.Jira,
            TargetId: "FHIR-55001",
            LinkType: "mentions",
            Context: "Referenced in PR",
            SourceContentType: "pull_request",
            TargetTitle: "Fix patient resource validation",
            TargetUrl: "https://jira.example.com/browse/FHIR-123");

        Assert.Equal(SourceSystems.GitHub, xref.SourceType);
        Assert.Equal("HL7/fhir#4006", xref.SourceId);
        Assert.Equal(SourceSystems.Jira, xref.TargetType);
        Assert.Equal("FHIR-55001", xref.TargetId);
        Assert.Equal("mentions", xref.LinkType);
        Assert.Equal("Referenced in PR", xref.Context);
        Assert.Equal("pull_request", xref.SourceContentType);
        Assert.Equal("Fix patient resource validation", xref.TargetTitle);
        Assert.Equal("https://jira.example.com/browse/FHIR-123", xref.TargetUrl);
    }

    [Fact]
    public void HttpRelatedEndpoint_CsvParserParseSourceList_ParsesCommaSeparatedSources()
    {
        List<string>? result = FhirAugury.Common.Text.CsvParser.ParseSourceList($"{SourceSystems.Jira},{SourceSystems.Confluence},{SourceSystems.Zulip}");

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Contains(SourceSystems.Jira, result);
        Assert.Contains(SourceSystems.Confluence, result);
        Assert.Contains(SourceSystems.Zulip, result);
    }

    [Fact]
    public void HttpRelatedEndpoint_CsvParserParseSourceList_ReturnsNullForEmpty()
    {
        Assert.Null(FhirAugury.Common.Text.CsvParser.ParseSourceList(null));
        Assert.Null(FhirAugury.Common.Text.CsvParser.ParseSourceList(""));
        Assert.Null(FhirAugury.Common.Text.CsvParser.ParseSourceList("   "));
    }

    [Fact]
    public void CrossReferenceResponse_AllFieldsRoundTrip()
    {
        CrossReferenceResponse response = new CrossReferenceResponse(
            Source: SourceSystems.Jira,
            Id: "FHIR-55001",
            Direction: "both",
            References: [
                new SourceCrossReference(
                    SourceType: SourceSystems.GitHub,
                    SourceId: "HL7/fhir#4006",
                    TargetType: SourceSystems.Jira,
                    TargetId: "FHIR-55001",
                    LinkType: "mentions",
                    Context: "Referenced in PR",
                    SourceContentType: null,
                    TargetTitle: "Fix validation",
                    TargetUrl: "https://github.com/HL7/fhir/pull/4006")
            ]);

        Assert.Equal(SourceSystems.Jira, response.Source);
        Assert.Equal("FHIR-55001", response.Id);
        Assert.Equal("both", response.Direction);
        Assert.Single(response.References);
    }

    [Fact]
    public void FindRelatedResponse_HasSeedFields()
    {
        FindRelatedResponse response = new FindRelatedResponse(
            SeedSource: SourceSystems.Jira,
            SeedId: "FHIR-55001",
            SeedTitle: "Test",
            Items: []);

        Assert.Equal(SourceSystems.Jira, response.SeedSource);
        Assert.Equal("FHIR-55001", response.SeedId);
    }

    [Fact]
    public void PeerIngestionAck_HasAcknowledgedField()
    {
        PeerIngestionAck ack = new PeerIngestionAck(Acknowledged: true);
        Assert.True(ack.Acknowledged);
    }
}
