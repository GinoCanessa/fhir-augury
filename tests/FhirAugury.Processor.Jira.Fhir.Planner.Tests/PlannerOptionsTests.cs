using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerOptionsTests
{
    [Fact]
    public void Defaults_NoRepoRestriction()
    {
        PlannerOptions options = new();

        Assert.Null(options.RepoFilters);
        Assert.Equal("[]", PlannerRepoFilters.RenderJson(options.RepoFilters));
    }

    [Fact]
    public void EmptyRepoFilters_RendersNoRestriction()
    {
        PlannerOptions options = new() { RepoFilters = [] };

        Assert.Empty(PlannerRepoFilters.Validate(options));
        Assert.Equal("[]", PlannerRepoFilters.RenderJson(options.RepoFilters));
    }

    [Fact]
    public void NonEmptyRepoFilters_ValidateAndDeduplicateCaseInsensitively()
    {
        PlannerOptions options = new() { RepoFilters = ["HL7/fhir", "hl7/FHIR", "HL7/fhir-ig-publisher"] };

        Assert.Empty(PlannerRepoFilters.Validate(options));
        Assert.Equal(["HL7/fhir", "HL7/fhir-ig-publisher"], PlannerRepoFilters.Normalize(options.RepoFilters));
        Assert.Equal("[\\\"HL7/fhir\\\",\\\"HL7/fhir-ig-publisher\\\"]", PlannerRepoFilters.RenderJson(options.RepoFilters));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HL7")]
    [InlineData("HL7/")]
    [InlineData("/fhir")]
    [InlineData("HL7/fhir/core")]
    [InlineData("HL7/*")]
    [InlineData("HL7/fhir?")]
    public void InvalidRepoFilters_AreRejected(string value)
    {
        PlannerOptions options = new() { RepoFilters = [value] };

        Assert.NotEmpty(PlannerRepoFilters.Validate(options));
    }

    [Fact]
    public void MatchesRepositoryFullName_UsesSharedFullNameShape()
    {
        PlannerOptions options = new() { RepoFilters = ["HL7/fhir"] };

        Assert.True(PlannerRepoFilters.MatchesRepositoryFullName("hl7/FHIR", options.RepoFilters));
        Assert.False(PlannerRepoFilters.MatchesRepositoryFullName("HL7/fhir-ig-publisher", options.RepoFilters));
    }
}
