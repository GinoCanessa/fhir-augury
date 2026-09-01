using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Direct coverage for <see cref="JiraFieldMapper.CleanFieldValue"/>, in
/// particular the U+00A0 NBSP normalization that lets exact-match filters
/// (e.g. the planner's <c>SpecificationsToInclude</c>) match values that Jira
/// ships with <c>&amp;nbsp;</c> in their HTML payload.
/// </summary>
public class JiraFieldMapperCleanFieldValueTests
{
    [Fact]
    public void CleanFieldValue_NullInput_ReturnsNull()
        => Assert.Null(JiraFieldMapper.CleanFieldValue(null));

    [Fact]
    public void CleanFieldValue_EmptyInput_ReturnsNull()
        => Assert.Null(JiraFieldMapper.CleanFieldValue(""));

    [Fact]
    public void CleanFieldValue_WhitespaceOnly_ReturnsNull()
        => Assert.Null(JiraFieldMapper.CleanFieldValue("   "));

    [Fact]
    public void CleanFieldValue_NbspOnly_ReturnsNull()
        => Assert.Null(JiraFieldMapper.CleanFieldValue("\u00A0\u00A0"));

    [Fact]
    public void CleanFieldValue_PlainText_ReturnsTrimmedValue()
        => Assert.Equal("FHIR Core (FHIR)", JiraFieldMapper.CleanFieldValue("  FHIR Core (FHIR)  "));

    [Fact]
    public void CleanFieldValue_HtmlEntityNbsp_ReplacedWithAsciiSpace()
        => Assert.Equal(
            "FHIR R5 Subscriptions Backport (FHIR)",
            JiraFieldMapper.CleanFieldValue("FHIR&nbsp;R5&nbsp;Subscriptions&nbsp;Backport (FHIR)"));

    [Fact]
    public void CleanFieldValue_LiteralU00A0_ReplacedWithAsciiSpace()
        => Assert.Equal(
            "FHIR R5 Subscriptions Backport (FHIR)",
            JiraFieldMapper.CleanFieldValue("FHIR\u00A0R5\u00A0Subscriptions\u00A0Backport (FHIR)"));

    [Fact]
    public void CleanFieldValue_LeadingTrailingNbsp_AreTrimmed()
        => Assert.Equal(
            "FHIR Core (FHIR)",
            JiraFieldMapper.CleanFieldValue("\u00A0FHIR Core (FHIR)\u00A0"));

    [Fact]
    public void CleanFieldValue_OtherHtmlEntities_StillDecoded()
        => Assert.Equal("AT&T <value>", JiraFieldMapper.CleanFieldValue("AT&amp;T &lt;value&gt;"));
}
