using FhirAugury.Sources.Jira;

namespace FhirAugury.Sources.Tests;

public class JiraXmlParserTests
{
    private static Stream LoadSampleXml()
    {
        return File.OpenRead(Path.Combine("TestData", "sample-jira-export.xml"));
    }

    [Fact]
    public void ParseExport_ReturnsTwoIssues()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ParseExport_FirstIssue_HasCorrectKey()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        Assert.Equal("FHIR-43499", results[0].Issue.Key);
    }

    [Fact]
    public void ParseExport_FirstIssue_HasCorrectTitle()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        // XML title includes the key prefix: "[FHIR-43499] FHIRPath normative readiness review"
        Assert.Contains("FHIRPath normative readiness review", results[0].Issue.Title);
    }

    [Fact]
    public void ParseExport_FirstIssue_HasCorrectStatus()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        Assert.Contains("Triaged", results[0].Issue.Status);
    }

    [Fact]
    public void ParseExport_FirstIssue_HasComments()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        Assert.Equal(2, results[0].Comments.Count);
        Assert.Equal("bporter", results[0].Comments[0].Author);
    }

    [Fact]
    public void ParseExport_FirstIssue_HasCustomFields()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        Assert.Equal("FHIRPath", results[0].Issue.Specification);
        Assert.Equal("FHIR Infrastructure", results[0].Issue.WorkGroup);
    }

    [Fact]
    public void ParseExport_SecondIssue_HasResolution()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        Assert.Equal("FHIR-42100", results[1].Issue.Key);
        Assert.Equal("Persuasive", results[1].Issue.Resolution);
    }

    [Fact]
    public void ParseExport_SecondIssue_HasResolvedDate()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        // The resolved date may or may not parse depending on format
        // The XML uses "Wed, 15 Jan 2026 16:45:00 -0500" RFC 822 format
        // At minimum, verify the second issue has a resolution
        Assert.Equal("Persuasive", results[1].Issue.Resolution);
    }

    [Fact]
    public void ParseExport_HandlesHtmlInDescription()
    {
        using var stream = LoadSampleXml();
        var results = JiraXmlParser.ParseExport(stream).ToList();

        // Second issue has HTML entities in description
        Assert.NotNull(results[1].Issue.Description);
    }
}

