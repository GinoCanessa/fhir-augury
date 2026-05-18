using FhirAugury.Common.WorkGroups;

namespace FhirAugury.Common.Tests.WorkGroups;

public class WorkGroupResolverTests
{
    private static readonly Hl7WorkGroupDto OrdersObs = new Hl7WorkGroupDto(
        Code: "oo",
        Name: "Orders & Observations",
        Definition: null,
        Retired: false,
        NameClean: "OrdersAndObservations");

    private static readonly Hl7WorkGroupDto PatientCare = new Hl7WorkGroupDto(
        Code: "pc",
        Name: "Patient Care",
        Definition: null,
        Retired: false,
        NameClean: "PatientCare");

    private static readonly Hl7WorkGroupDto FhirInfra = new Hl7WorkGroupDto(
        Code: "fhir",
        Name: "FHIR Infrastructure",
        Definition: null,
        Retired: false,
        NameClean: "FHIRInfrastructure");

    private static readonly Hl7WorkGroupDto RetiredGroup = new Hl7WorkGroupDto(
        Code: "old",
        Name: "Old Retired Group",
        Definition: null,
        Retired: true,
        NameClean: "OldRetiredGroup");

    private static IReadOnlyList<Hl7WorkGroupDto> Snapshot() =>
        new[] { OrdersObs, PatientCare, FhirInfra, RetiredGroup };

    [Fact]
    public void ExactCode_Resolves()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("oo");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(OrdersObs, result.Match);
        Assert.Equal(WorkGroupResolveMatchKind.ExactCode, result.MatchKind);
        Assert.Null(result.Score);
    }

    [Fact]
    public void ExactCode_IsCaseInsensitive()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("OO");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(OrdersObs, result.Match);
        Assert.Equal(WorkGroupResolveMatchKind.ExactCode, result.MatchKind);
    }

    [Fact]
    public void ExactNameClean_Resolves()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("OrdersAndObservations");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(OrdersObs, result.Match);
        Assert.Equal(WorkGroupResolveMatchKind.ExactNameClean, result.MatchKind);
    }

    [Fact]
    public void ExactName_Resolves()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("Orders & Observations");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(OrdersObs, result.Match);
        Assert.Equal(WorkGroupResolveMatchKind.ExactName, result.MatchKind);
    }

    [Fact]
    public void LegacyPreparerSlug_ResolvesAsExactName()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("Orders&Observations");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(OrdersObs, result.Match);
        Assert.Equal(WorkGroupResolveMatchKind.ExactName, result.MatchKind);
    }

    [Fact]
    public void NormalizedName_Resolves()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("orders and observations");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(OrdersObs, result.Match);
        Assert.Equal(WorkGroupResolveMatchKind.NormalizedName, result.MatchKind);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public void FuzzyName_AboveThreshold_Resolves()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("Orders & Observation");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(OrdersObs, result.Match);
        Assert.Equal(WorkGroupResolveMatchKind.FuzzyName, result.MatchKind);
        Assert.NotNull(result.Score);
        Assert.True(result.Score >= 0.92, $"Expected score >= 0.92 but was {result.Score}.");
    }

    [Fact]
    public void NotFound_ReturnsDidYouMeanCandidates()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("Orders");

        Assert.Equal(WorkGroupResolveOutcome.NotFound, result.Outcome);
        Assert.Null(result.Match);
        Assert.NotEmpty(result.Candidates);
        Assert.True(result.Candidates.Count <= 3);
    }

    [Fact]
    public void Ambiguous_ReturnsTiedCandidates()
    {
        Hl7WorkGroupDto a = new Hl7WorkGroupDto("a", "Acme Workimg Group", null, false, "AcmeWorkimgGroup");
        Hl7WorkGroupDto b = new Hl7WorkGroupDto("b", "Acme Workirg Group", null, false, "AcmeWorkirgGroup");
        WorkGroupResolver resolver = new WorkGroupResolver(new[] { a, b });

        WorkGroupResolveResult result = resolver.Resolve("Acme Working Group");

        Assert.Equal(WorkGroupResolveOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Match);
        Assert.True(result.Candidates.Count >= 2);
    }

    [Fact]
    public void RetiredRows_ExcludedFromFuzzyByDefault()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("Old Retired Grup");

        Assert.NotEqual(WorkGroupResolveOutcome.Found, result.Outcome);
    }

    [Fact]
    public void RetiredRows_StillHitOnExactCode()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve("old");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(RetiredGroup, result.Match);
    }

    [Fact]
    public void RetiredRows_IncludedFuzzy_WhenOptionSet()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(
            Snapshot(),
            new WorkGroupResolverOptions(IncludeRetired: true));

        WorkGroupResolveResult result = resolver.Resolve("Old Retired Grup");

        Assert.Equal(WorkGroupResolveOutcome.Found, result.Outcome);
        Assert.Equal(RetiredGroup, result.Match);
    }

    [Fact]
    public void EmptySnapshot_FlagsDegraded()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Array.Empty<Hl7WorkGroupDto>());
        Assert.True(resolver.CatalogJoinDegraded);
    }

    [Fact]
    public void RowWithNullCode_FlagsDegraded()
    {
        Hl7WorkGroupDto missingCode = new Hl7WorkGroupDto(
            Code: null!,
            Name: "Some Group",
            Definition: null,
            Retired: false,
            NameClean: "SomeGroup");
        WorkGroupResolver resolver = new WorkGroupResolver(new[] { missingCode });
        Assert.True(resolver.CatalogJoinDegraded);
    }

    [Fact]
    public void HealthySnapshot_NotDegraded()
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());
        Assert.False(resolver.CatalogJoinDegraded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespaceInput_ReturnsNotFound(string? input)
    {
        WorkGroupResolver resolver = new WorkGroupResolver(Snapshot());

        WorkGroupResolveResult result = resolver.Resolve(input);

        Assert.Equal(WorkGroupResolveOutcome.NotFound, result.Outcome);
        Assert.Null(result.Match);
    }
}
