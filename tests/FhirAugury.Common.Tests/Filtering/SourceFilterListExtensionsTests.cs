using FhirAugury.Common.Filtering;

namespace FhirAugury.Common.Tests.Filtering;

public class SourceFilterListExtensionsTests
{
    [Fact]
    public void IsDefaultFilter_ReturnsTrueOnlyForNull()
    {
        IReadOnlyCollection<string>? values = null;

        Assert.True(values.IsDefaultFilter());
        Assert.False(Array.Empty<string>().IsDefaultFilter());
        Assert.False(new[] { "jira" }.IsDefaultFilter());
        Assert.False(new[] { "jira", "github" }.IsDefaultFilter());
    }

    [Fact]
    public void IsExplicitNoRestriction_ReturnsTrueOnlyForEmptyList()
    {
        IReadOnlyCollection<string>? values = null;

        Assert.False(values.IsExplicitNoRestriction());
        Assert.True(Array.Empty<string>().IsExplicitNoRestriction());
        Assert.False(new[] { "jira" }.IsExplicitNoRestriction());
        Assert.False(new[] { "jira", "github" }.IsExplicitNoRestriction());
    }

    [Fact]
    public void HasExplicitRestriction_ReturnsTrueOnlyForNonEmptyList()
    {
        IReadOnlyCollection<string>? values = null;

        Assert.False(values.HasExplicitRestriction());
        Assert.False(Array.Empty<string>().HasExplicitRestriction());
        Assert.True(new[] { "jira" }.HasExplicitRestriction());
        Assert.True(new[] { "jira", "github" }.HasExplicitRestriction());
    }

    [Fact]
    public void OrEmpty_ReturnsEmptyForNullAndOriginalValuesForNonNull()
    {
        IReadOnlyCollection<string>? nullValues = null;
        string[] emptyValues = [];
        string[] singleValue = ["jira"];
        string[] multipleValues = ["jira", "github"];

        Assert.Empty(nullValues.OrEmpty());
        Assert.Same(emptyValues, emptyValues.OrEmpty());
        Assert.Same(singleValue, singleValue.OrEmpty());
        Assert.Same(multipleValues, multipleValues.OrEmpty());
    }
}
