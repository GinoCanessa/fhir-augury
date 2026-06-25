using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="AppliedWorkGroupResolver"/>: a ticket's work group is
/// counted only when its attributed commit touched a unit source path (matched
/// via the <c>ChangedPaths ∪ resolution.Files</c> union the hydrator supplies),
/// codes derive from <see cref="Hl7WorkGroupNameCleaner.Clean"/>, and the coarse
/// fallback emits a warning when no commit-to-file signal is available.
/// </summary>
public sealed class AppliedWorkGroupResolverTests
{
    [Fact]
    public void Includes_wg_of_ticket_whose_commit_touched_unit_file_excludes_others()
    {
        List<WindowCommit> commits =
        [
            Commit("shaA", changed: ["source/observation/observation.xml"]),
            Commit("shaB", changed: ["source/other/unrelated.xml"]),
        ];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"], ["shaB"] = ["FHIR-2"] },
            tickets:
            [
                Ticket("FHIR-1", "Orders and Observations"),
                Ticket("FHIR-2", "Patient Administration"),
            ]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db: null, NameCache());

        Assert.Equal(string.Empty, result.WarningNote);
        WorkGroupRef one = Assert.Single(result.Refs);
        Assert.Equal(Hl7WorkGroupNameCleaner.Clean("Orders and Observations"), one.Code);
    }

    [Fact]
    public void Counts_ticket_whose_commit_touched_a_head_deleted_file_via_union()
    {
        // The deleted file is absent from resolution.Files at HEAD but present in
        // the union path set the hydrator supplies; the commit that touched it must
        // still count.
        List<WindowCommit> commits = [Commit("shaA", changed: ["source/observation/deleted.xml"])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"] },
            tickets: [Ticket("FHIR-1", "Orders and Observations")]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/deleted.xml"], db: null, NameCache());

        Assert.Equal(string.Empty, result.WarningNote);
        Assert.Equal(Hl7WorkGroupNameCleaner.Clean("Orders and Observations"), Assert.Single(result.Refs).Code);
    }

    [Fact]
    public void Applied_code_equals_cleaned_ticket_workgroup()
    {
        const string wg = "Clinical Quality Information";
        List<WindowCommit> commits = [Commit("shaA", changed: ["source/measure/measure.xml"])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-9"] },
            tickets: [Ticket("FHIR-9", wg)]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/measure/measure.xml"], db: null, NameCache());

        Assert.Equal(Hl7WorkGroupNameCleaner.Clean(wg), Assert.Single(result.Refs).Code);
    }

    [Fact]
    public void Fallback_lists_all_ticket_wgs_with_warning_when_no_changed_paths()
    {
        // Commits carry no per-file detail → no commit-to-file signal → fallback.
        List<WindowCommit> commits = [Commit("shaA", changed: []), Commit("shaB", changed: [])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"], ["shaB"] = ["FHIR-2"] },
            tickets:
            [
                Ticket("FHIR-1", "Orders and Observations"),
                Ticket("FHIR-2", "Patient Administration"),
            ]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db: null, NameCache());

        Assert.NotEqual(string.Empty, result.WarningNote);
        Assert.Equal(2, result.Refs.Count);
        Assert.Contains(result.Refs, r => r.Code == Hl7WorkGroupNameCleaner.Clean("Orders and Observations"));
        Assert.Contains(result.Refs, r => r.Code == Hl7WorkGroupNameCleaner.Clean("Patient Administration"));
    }

    [Fact]
    public void Skips_blank_work_groups_and_dedupes_by_code()
    {
        List<WindowCommit> commits =
        [
            Commit("shaA", changed: ["source/observation/observation.xml"]),
            Commit("shaB", changed: ["source/observation/observation.xml"]),
            Commit("shaC", changed: ["source/observation/observation.xml"]),
        ];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"], ["shaB"] = ["FHIR-2"], ["shaC"] = ["FHIR-3"] },
            tickets:
            [
                Ticket("FHIR-1", "Orders and Observations"),
                Ticket("FHIR-2", "Orders and Observations"), // duplicate WG
                Ticket("FHIR-3", ""),                          // blank WG → skipped
            ]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db: null, NameCache());

        Assert.Equal(Hl7WorkGroupNameCleaner.Clean("Orders and Observations"), Assert.Single(result.Refs).Code);
    }

    private static WindowCommit Commit(string sha, IReadOnlyList<string> changed)
        => new() { Sha = sha, ShortSha = sha, ChangedPaths = changed };

    private static AttributedTicket Ticket(string key, string workGroup)
        => new() { Key = key, WorkGroup = workGroup };

    private static UnitAttribution Attribution(
        Dictionary<string, IReadOnlyList<string>> commitKeys,
        IReadOnlyList<AttributedTicket> tickets)
        => new() { Tickets = tickets, CommitTicketKeys = commitKeys };

    private static Dictionary<string, string> NameCache()
        => new(StringComparer.OrdinalIgnoreCase);
}
