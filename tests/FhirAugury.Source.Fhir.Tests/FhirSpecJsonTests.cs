using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class FhirSpecJsonTests
{
    [Fact]
    public void ParseDesignations_ExtractsUseAndValue()
    {
        const string json = """[{"use":{"system":"http://acme/x","code":"label"},"value":"Final result"}]""";

        List<ConceptDesignation> designations = FhirSpecJson.ParseDesignations(json);

        ConceptDesignation d = Assert.Single(designations);
        Assert.Equal("label", d.Use);
        Assert.Equal("Final result", d.Value);
        Assert.Null(d.Language);
    }

    [Fact]
    public void ParseDesignations_WithLanguage()
    {
        const string json = """[{"language":"de","value":"Endgültig"}]""";

        ConceptDesignation d = Assert.Single(FhirSpecJson.ParseDesignations(json));
        Assert.Equal("de", d.Language);
        Assert.Equal("Endgültig", d.Value);
        Assert.Null(d.Use);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    public void ParseDesignations_EmptyOrMalformed_ReturnsEmpty(string? json)
    {
        Assert.Empty(FhirSpecJson.ParseDesignations(json));
    }

    [Fact]
    public void ParseConceptProperties_BooleanProperty()
    {
        ConceptProperty p = Assert.Single(
            FhirSpecJson.ParseConceptProperties("""[{"code":"notSelectable","valueBoolean":true}]"""));
        Assert.Equal("notSelectable", p.Code);
        Assert.Equal("boolean", p.Type);
        Assert.Equal("true", p.Value);
    }

    [Fact]
    public void ParseConceptProperties_CodeProperty()
    {
        ConceptProperty p = Assert.Single(
            FhirSpecJson.ParseConceptProperties("""[{"code":"status","valueCode":"active"}]"""));
        Assert.Equal("status", p.Code);
        Assert.Equal("code", p.Type);
        Assert.Equal("active", p.Value);
    }

    [Fact]
    public void ParseCompose_IncludeWithSystem()
    {
        const string json = """{"include":[{"system":"http://hl7.org/fhir/observation-status"}]}""";

        ComposeRule rule = Assert.Single(FhirSpecJson.ParseCompose(json));
        Assert.Equal("include", rule.Mode);
        Assert.Equal("http://hl7.org/fhir/observation-status", rule.System);
    }

    [Fact]
    public void ParseCompose_IncludeWithConceptsFiltersAndExclude()
    {
        const string json = """
            {
              "include":[{"system":"http://loinc.org","concept":[{"code":"1234-5","display":"Test"}],
                         "filter":[{"property":"concept","op":"is-a","value":"x"}]}],
              "exclude":[{"system":"http://loinc.org","valueSet":["http://example/vs"]}]
            }
            """;

        List<ComposeRule> rules = FhirSpecJson.ParseCompose(json);
        Assert.Equal(2, rules.Count);

        ComposeRule include = rules[0];
        Assert.Equal("include", include.Mode);
        Assert.Equal("1234-5", Assert.Single(include.Concepts).Code);
        Assert.Equal("is-a", Assert.Single(include.Filters).Op);

        ComposeRule exclude = rules[1];
        Assert.Equal("exclude", exclude.Mode);
        Assert.Equal("http://example/vs", Assert.Single(exclude.ValueSets));
    }

    [Fact]
    public void ParseCompose_Malformed_ReturnsEmpty()
    {
        Assert.Empty(FhirSpecJson.ParseCompose("nonsense"));
    }
}
