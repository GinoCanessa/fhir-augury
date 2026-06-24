using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="OwningWorkGroupResolver"/>: legacy ticket parity through
/// the <see cref="WorkGroupRef"/> seam (Phase 1), and the page chain that resolves
/// via the <c>[%wg%]</c> marker and never falls back to a ticket WG (Phase 2).
/// The registry DB path is pinned to a non-existent file so these tests are
/// independent of any cached <c>github.db</c>.
/// </summary>
public sealed class OwningWorkGroupResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _missingDbPath;

    public OwningWorkGroupResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "owningwg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _missingDbPath = Path.Combine(_tempDir, "does-not-exist.db");
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

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
    public void Resolve_artifact_without_owner_yields_unknown()
    {
        IReadOnlyList<WorkGroupRef> refs = Resolve(ArtifactUnit(), EmptyAttribution(), hint: null);

        Assert.Single(refs);
        Assert.Equal("(unknown)", refs[0].DisplayName);
        Assert.Equal(string.Empty, refs[0].Code);
        Assert.Equal("(unknown)", WorkGroupRef.JoinNames(refs));
        Assert.Equal(string.Empty, WorkGroupRef.JoinCodes(refs));
    }

    [Fact]
    public void Page_resolves_via_marker_when_registry_empty()
    {
        WritePage("security", "<td id=\"wg\"><a href=\"[%wg sec%]\">[%wgt sec%]</a> Work Group</td>");
        HydrationUnit unit = new() { Type = "Page", Name = "security", ChangedPaths = ["source/security.html"] };

        IReadOnlyList<WorkGroupRef> refs = Resolve(unit, EmptyAttribution(), hint: null);

        Assert.Single(refs);
        Assert.Equal("sec", refs[0].Code);
        // No github.db → display name falls back to the raw code.
        Assert.Equal("sec", refs[0].DisplayName);
    }

    [Fact]
    public void Page_never_falls_back_to_ticket_wg()
    {
        // No page file and a strong ticket WG present: the page must stay (unknown).
        UnitAttribution attribution = new()
        {
            Tickets = [new() { Key = "FHIR-9", WorkGroup = "Some WG", AttributionDate = Date("2026-06-01") }],
            CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(),
        };
        HydrationUnit unit = new() { Type = "Page", Name = "missing-page", ChangedPaths = ["source/missing-page.html"] };

        IReadOnlyList<WorkGroupRef> refs = Resolve(unit, attribution, hint: "Hint WG");

        Assert.Single(refs);
        Assert.Equal("(unknown)", refs[0].DisplayName);
        Assert.Equal(string.Empty, refs[0].Code);
    }

    private void WritePage(string stem, string html)
    {
        string dir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{stem}.html"), html);
    }

    private IReadOnlyList<WorkGroupRef> Resolve(
        HydrationUnit unit, UnitAttribution attribution, string? hint)
        => OwningWorkGroupResolver.Resolve(
            unit,
            clonePath: _tempDir,
            owner: "HL7",
            name: "fhir",
            attribution,
            workGroupHint: hint,
            options: new BallotNotesHydrationOptions { GitHubDbPath = _missingDbPath },
            logger: null);

    private static UnitAttribution EmptyAttribution() => new()
    {
        Tickets = [],
        CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(),
    };

    private static HydrationUnit ArtifactUnit() => new()
    {
        Type = "Artifact",
        Name = "Observation",
        ChangedPaths = ["source/observation/observation.xml"],
    };

    private static DateTimeOffset Date(string iso) => DateTimeOffset.Parse(iso);
}
