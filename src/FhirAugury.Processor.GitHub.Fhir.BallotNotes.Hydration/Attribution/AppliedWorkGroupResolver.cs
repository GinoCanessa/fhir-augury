using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;

/// <summary>
/// The <em>applied-by</em> work groups for a unit plus an optional imprecision
/// warning. <see cref="Refs"/> is the distinct set of work groups that actually
/// moved the unit in the window; <see cref="WarningNote"/> is non-empty only when
/// the coarse fallback was used (no commit-to-file granularity available).
/// </summary>
public readonly record struct AppliedWorkGroupResolution(
    IReadOnlyList<WorkGroupRef> Refs,
    string WarningNote);

/// <summary>
/// Derives the set of work groups that <em>applied changes</em> to a unit in the
/// window: the distinct work groups of attributed tickets whose window commit
/// touched one of the unit's source files. Work-group codes are derived on the
/// same canonical basis as the owning/Listed/Index lineages
/// (<see cref="Hl7WorkGroupNameCleaner.Clean"/> of the ticket's work-group name),
/// so cross-lineage code comparison in the SPA is valid.
/// </summary>
public static class AppliedWorkGroupResolver
{
    /// <summary>
    /// Resolves applied-by work groups for the unit. <paramref name="unitSourcePaths"/>
    /// is the union of the unit's window-changed paths and its resolved HEAD source
    /// files (so a ticket whose commit touched a HEAD-deleted file is still
    /// counted). Falls back to the distinct work groups of <em>all</em> attributed
    /// tickets — with a non-empty <see cref="AppliedWorkGroupResolution.WarningNote"/>
    /// — when no commit-to-file signal is available.
    /// </summary>
    public static AppliedWorkGroupResolution Resolve(
        IReadOnlyList<WindowCommit> commits,
        UnitAttribution attribution,
        IReadOnlyCollection<string> unitSourcePaths,
        SqliteConnection? db,
        IDictionary<string, string> nameCache)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(unitSourcePaths);
        ArgumentNullException.ThrowIfNull(nameCache);

        bool hasPathSignal = unitSourcePaths.Count > 0 && commits.Any(static c => c.ChangedPaths.Count > 0);
        if (!hasPathSignal)
        {
            return new AppliedWorkGroupResolution(
                BuildRefs(attribution.Tickets, db, nameCache),
                attribution.Tickets.Count == 0
                    ? string.Empty
                    : "Applied-by work groups are approximate: no commit-to-file detail was available, "
                      + "so all attributed tickets' work groups are listed.");
        }

        HashSet<string> pathSet = new(unitSourcePaths, StringComparer.OrdinalIgnoreCase);

        // Collect the keys of tickets whose attributed commit touched a unit path.
        HashSet<string> appliedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (WindowCommit commit in commits)
        {
            bool touchesUnit = commit.ChangedPaths.Any(p => pathSet.Contains(p));
            if (!touchesUnit) continue;

            if (attribution.CommitTicketKeys.TryGetValue(commit.Sha, out IReadOnlyList<string>? keys))
            {
                foreach (string key in keys) appliedKeys.Add(key);
            }
        }

        List<AttributedTicket> applied = [];
        foreach (AttributedTicket ticket in attribution.Tickets)
        {
            if (appliedKeys.Contains(ticket.Key)) applied.Add(ticket);
        }

        return new AppliedWorkGroupResolution(BuildRefs(applied, db, nameCache), string.Empty);
    }

    private static IReadOnlyList<WorkGroupRef> BuildRefs(
        IReadOnlyList<AttributedTicket> tickets,
        SqliteConnection? db,
        IDictionary<string, string> nameCache)
    {
        List<WorkGroupRef> refs = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (AttributedTicket ticket in tickets)
        {
            if (string.IsNullOrWhiteSpace(ticket.WorkGroup)) continue;

            string code = Hl7WorkGroupNameCleaner.Clean(ticket.WorkGroup);
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (!seen.Add(code)) continue;

            string display = db is not null
                ? WorkGroupNameResolver.Resolve(db, code, nameCache)
                : ticket.WorkGroup;
            if (string.IsNullOrWhiteSpace(display)) display = ticket.WorkGroup;

            refs.Add(new WorkGroupRef(code, display));
        }

        return refs;
    }
}
