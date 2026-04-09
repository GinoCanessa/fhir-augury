namespace FhirAugury.Parsing.Fsh.Tests;

public class SushiConfigParserTests
{
    [Fact]
    public void TryParseContent_ValidConfig_ReturnsAllFields()
    {
        string yaml = """
            id: hl7.fhir.uv.application-feature
            canonical: http://hl7.org/fhir/uv/application-feature
            name: ApplicationFeature
            title: "Application Feature Framework"
            fhirVersion: 5.0.0
            status: draft
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal("hl7.fhir.uv.application-feature", config.Id);
        Assert.Equal("http://hl7.org/fhir/uv/application-feature", config.Canonical);
        Assert.Equal("ApplicationFeature", config.Name);
        Assert.Equal("Application Feature Framework", config.Title);
        Assert.Equal("5.0.0", config.FhirVersion);
        Assert.Equal("draft", config.Status);
    }

    [Fact]
    public void TryParseContent_PathResource_ParsesList()
    {
        string yaml = """
            id: test.ig
            canonical: http://example.org/fhir
            name: TestIG
            path-resource:
              - input/resources
              - input/vocabulary
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal(2, config.PathResource.Count);
        Assert.Equal("input/resources", config.PathResource[0]);
        Assert.Equal("input/vocabulary", config.PathResource[1]);
    }

    [Fact]
    public void TryParseContent_InlineList_ParsesCorrectly()
    {
        string yaml = """
            id: test.ig
            canonical: http://example.org/fhir
            name: TestIG
            path-resource: [input/resources, input/vocabulary]
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal(2, config.PathResource.Count);
        Assert.Equal("input/resources", config.PathResource[0]);
        Assert.Equal("input/vocabulary", config.PathResource[1]);
    }

    [Fact]
    public void TryParseContent_QuotedTitle_UnquotesCorrectly()
    {
        string yaml = """
            id: test.ig
            canonical: http://example.org/fhir
            name: TestIG
            title: "My IG Title"
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal("My IG Title", config.Title);
    }

    [Fact]
    public void TryParseContent_SingleQuotedTitle_UnquotesCorrectly()
    {
        string yaml = """
            id: test.ig
            canonical: http://example.org/fhir
            name: TestIG
            title: 'My IG Title'
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal("My IG Title", config.Title);
    }

    [Fact]
    public void TryParseContent_MissingOptionalFields_ReturnsNulls()
    {
        string yaml = """
            id: test.ig
            canonical: http://example.org/fhir
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal("test.ig", config.Id);
        Assert.Equal("http://example.org/fhir", config.Canonical);
        Assert.Null(config.Name);
        Assert.Null(config.Title);
        Assert.Null(config.FhirVersion);
        Assert.Null(config.Status);
        Assert.Empty(config.PathResource);
        Assert.Empty(config.AdditionalResource);
    }

    [Fact]
    public void TryParseContent_CommentsSkipped()
    {
        string yaml = """
            # This is a comment
            id: test.ig
            # Another comment
            canonical: http://example.org/fhir
            name: TestIG
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal("test.ig", config.Id);
        Assert.Equal("http://example.org/fhir", config.Canonical);
    }

    [Fact]
    public void TryParseContent_EmptyContent_ReturnsEmptyConfig()
    {
        SushiConfig? config = SushiConfigParser.TryParseContent("");

        Assert.NotNull(config);
        Assert.Null(config.Id);
        Assert.Null(config.Canonical);
    }

    [Fact]
    public void TryParseContent_FhirVersionList_ParsesFirst()
    {
        string yaml = """
            id: test.ig
            canonical: http://example.org/fhir
            name: TestIG
            fhirVersion:
              - 5.0.0
              - 4.0.1
            """;

        SushiConfig? config = SushiConfigParser.TryParseContent(yaml);

        Assert.NotNull(config);
        Assert.Equal("5.0.0", config.FhirVersion);
    }
}
