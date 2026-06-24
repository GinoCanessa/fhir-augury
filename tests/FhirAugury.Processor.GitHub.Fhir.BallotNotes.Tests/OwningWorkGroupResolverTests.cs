using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="OwningWorkGroupResolver"/>. Phase 1 asserts the resolver
/// reproduces the legacy ticket-recency owner exactly through the new
/// <see cref="WorkGroupRef"/> seam.
/// </summary>
public sealed class OwningWorkGroupResolverTests
{
    [Fact]
    public void Resolve_with_only_tickets_matches_legacy_behavior()
    {
        UnitAttribution attribution = new()
        {
            Tickets =
            [
                new() { Key = "FHIR-1", WorkGroup = "Old WG", AttributionDate = Date("2026-01-01") },
                new() { Key = "FHIR-2", WorkGroup = "Newest WG", AttributionDate = Date("2026-06-01") },
            ],
            CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(),
        };

        IReadOnlyList<WorkGroupRef> refs = Resolve(ArtifactUnit(), attribution, hint: null);

        (string legacyWg, string legacyCode) =
            TicketAttributor.SelectOwningWorkGroup(attribution.Tickets, hint: null);

        Assert.Single(refs);
        Assert.Equal(legacyWg, refs[0].DisplayName);
        Assert.Equal(legacyCode, refs[0].Code);
        Assert.Equal("Newest WG", refs[0].DisplayName);
        Assert.Equal("Newest WG", WorkGroupRef.JoinNames(refs));
        Assert.Equal(legacyCode, WorkGroupRef.JoinCodes(refs));
    }

    [Fact]
    public void Resolve_without_tickets_or_hint_yields_empty_primary()
    {
        UnitAttribution attribution = new()
        {
            Tickets = [],
            CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(),
        };

        IReadOnlyList<WorkGroupRef> refs = Resolve(ArtifactUnit(), attribution, hint: null);

        Assert.Single(refs);
        Assert.Equal(string.Empty, refs[0].DisplayName);
        Assert.Equal(string.Empty, refs[0].Code);
        Assert.Equal(string.Empty, WorkGroupRef.JoinNames(refs));
        Assert.Equal(string.Empty, WorkGroupRef.JoinCodes(refs));
    }

    private static IReadOnlyList<WorkGroupRef> Resolve(
        HydrationUnit unit, UnitAttribution attribution, string? hint)
        => OwningWorkGroupResolver.Resolve(
            unit,
            clonePath: "C:/nonexistent/clone",
            owner: "HL7",
            name: "fhir",
            attribution,
            workGroupHint: hint,
            options: new BallotNotesHydrationOptions(),
            logger: null);

    private static HydrationUnit ArtifactUnit() => new()
    {
        Type = "Artifact",
        Name = "Observation",
        ChangedPaths = ["source/observation/observation.xml"],
    };

    private static DateTimeOffset Date(string iso) => DateTimeOffset.Parse(iso);
}
