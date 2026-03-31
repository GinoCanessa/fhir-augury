using Fhiraugury;
using FhirAugury.Common;

namespace FhirAugury.Orchestrator.Tests;

/// <summary>
/// Verifies the cross-reference protobuf contract exposes expected fields
/// and the HTTP API signature accepts targetSources filtering.
/// </summary>
public class CrossReferenceContractTests
{
    [Fact]
    public void CrossReference_HasTargetTitleField()
    {
        CrossReference xref = new CrossReference { TargetTitle = "Example Title" };

        Assert.Equal("Example Title", xref.TargetTitle);
    }

    [Fact]
    public void CrossReference_HasTargetUrlField()
    {
        CrossReference xref = new CrossReference { TargetUrl = "https://example.com/item/1" };

        Assert.Equal("https://example.com/item/1", xref.TargetUrl);
    }

    [Fact]
    public void CrossReference_TargetFieldsDefaultToEmpty()
    {
        CrossReference xref = new CrossReference();

        Assert.Equal("", xref.TargetTitle);
        Assert.Equal("", xref.TargetUrl);
    }

    [Fact]
    public void CrossReference_AllFieldsRoundTrip()
    {
        CrossReference xref = new CrossReference
        {
            SourceType = SourceSystems.Confluence,
            SourceId = "page-1",
            TargetType = SourceSystems.Jira,
            TargetId = "FHIR-123",
            LinkType = "mentions",
            Context = "See ticket FHIR-123",
            TargetTitle = "Fix patient resource validation",
            TargetUrl = "https://jira.example.com/browse/FHIR-123",
        };

        Assert.Equal(SourceSystems.Confluence, xref.SourceType);
        Assert.Equal("page-1", xref.SourceId);
        Assert.Equal(SourceSystems.Jira, xref.TargetType);
        Assert.Equal("FHIR-123", xref.TargetId);
        Assert.Equal("mentions", xref.LinkType);
        Assert.Equal("See ticket FHIR-123", xref.Context);
        Assert.Equal("Fix patient resource validation", xref.TargetTitle);
        Assert.Equal("https://jira.example.com/browse/FHIR-123", xref.TargetUrl);
    }

    [Fact]
    public void HttpRelatedEndpoint_CsvParserParseSourceList_ParsesCommaSeparatedSources()
    {
        // Verify the CsvParser.ParseSourceList method used by the HTTP endpoint
        // correctly parses comma-separated source names.
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
    public void SourceCrossReference_AllFieldsRoundTrip()
    {
        SourceCrossReference xref = new SourceCrossReference
        {
            SourceType = SourceSystems.GitHub,
            SourceId = "HL7/fhir#4006",
            TargetType = SourceSystems.Jira,
            TargetId = "FHIR-55001",
            LinkType = "mentions",
            Context = "Referenced in PR",
            SourceTitle = "Fix validation",
            SourceUrl = "https://github.com/HL7/fhir/pull/4006",
        };

        Assert.Equal(SourceSystems.GitHub, xref.SourceType);
        Assert.Equal("HL7/fhir#4006", xref.SourceId);
        Assert.Equal(SourceSystems.Jira, xref.TargetType);
        Assert.Equal("FHIR-55001", xref.TargetId);
        Assert.Equal("mentions", xref.LinkType);
        Assert.Equal("Referenced in PR", xref.Context);
        Assert.Equal("Fix validation", xref.SourceTitle);
        Assert.Equal("https://github.com/HL7/fhir/pull/4006", xref.SourceUrl);
    }

    [Fact]
    public void GetItemXRefRequest_FieldsRoundTrip()
    {
        GetItemXRefRequest request = new GetItemXRefRequest
        {
            Source = SourceSystems.Jira,
            Id = "FHIR-55001",
            Direction = "both",
        };

        Assert.Equal(SourceSystems.Jira, request.Source);
        Assert.Equal("FHIR-55001", request.Id);
        Assert.Equal("both", request.Direction);
    }

    [Fact]
    public void GetRelatedRequest_HasSeedFields()
    {
        GetRelatedRequest request = new GetRelatedRequest
        {
            Id = "FHIR-55001",
            Limit = 20,
            SeedSource = SourceSystems.Jira,
            SeedId = "FHIR-55001",
        };

        Assert.Equal(SourceSystems.Jira, request.SeedSource);
        Assert.Equal("FHIR-55001", request.SeedId);
    }

    [Fact]
    public void IngestionCompleteAck_HasAcknowledgedField()
    {
        IngestionCompleteAck ack = new IngestionCompleteAck { Acknowledged = true };
        Assert.True(ack.Acknowledged);
    }
}
