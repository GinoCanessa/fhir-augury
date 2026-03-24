using Fhiraugury;

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
            SourceType = "confluence",
            SourceId = "page-1",
            TargetType = "jira",
            TargetId = "FHIR-123",
            LinkType = "mentions",
            Context = "See ticket FHIR-123",
            TargetTitle = "Fix patient resource validation",
            TargetUrl = "https://jira.example.com/browse/FHIR-123",
        };

        Assert.Equal("confluence", xref.SourceType);
        Assert.Equal("page-1", xref.SourceId);
        Assert.Equal("jira", xref.TargetType);
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
        List<string>? result = FhirAugury.Common.Text.CsvParser.ParseSourceList("jira,confluence,zulip");

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Contains("jira", result);
        Assert.Contains("confluence", result);
        Assert.Contains("zulip", result);
    }

    [Fact]
    public void HttpRelatedEndpoint_CsvParserParseSourceList_ReturnsNullForEmpty()
    {
        Assert.Null(FhirAugury.Common.Text.CsvParser.ParseSourceList(null));
        Assert.Null(FhirAugury.Common.Text.CsvParser.ParseSourceList(""));
        Assert.Null(FhirAugury.Common.Text.CsvParser.ParseSourceList("   "));
    }
}
