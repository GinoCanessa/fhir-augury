using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FhirAugury.Common.Text;
using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Readers;
using FhirAugury.Tools.FhirXverElementDiff.Report;

namespace FhirAugury.Tools.FhirXverElementDiff.Attribution;

/// <summary>
/// Two-tier attribution for each changed structure. First the <b>structure window</b>: walk the
/// git window over the structure's source file(s), extract the FHIR tickets its authoring
/// commits cite, and use that shared <see cref="ElementChangeRecord"/> as the default for every
/// changed row (the request's structure-window fallback). Then the <b>per-element</b> refinement
/// (Phase 6 hybrid): parse the same commits' diffs and, when a commit cleanly isolates one
/// element and changes a parseable facet (cardinality, or a structural add/remove), attribute
/// that element's row to that commit's ticket(s) instead — strictly sharpening precision, and
/// only ever replacing the window record with a <em>ticket</em> so a good structure-window
/// ticket is never downgraded to a bare hash. For the R5→R6 increment the winning commit's
/// post-change cardinality is verified against the DB value so an edit that landed after the
/// ballot4 snapshot is rejected. Ticket links are preferred throughout; a bare commit-hash list
/// is the window fallback when no allowlisted ticket resolves.
/// </summary>
internal static partial class Attributor
{
    // HL7/fhir authoring commits commonly cite a Jira ticket as a bare "#27849".
    // JiraTicketExtractor's bare-number pass deliberately EXCLUDES the "#N" form (it treats
    // "#N" as a GitHub ref), so we add this pass ourselves — allowlist-validated below.
    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex HashNumberPattern();

    // HL7 PR branches frequently encode the ticket as "Branch_<n>" (→ FHIR-<n>).
    [GeneratedRegex(@"Branch[_-](\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BranchTicketPattern();

    // The later ("→"-side) cardinality in an element-summary clause, e.g. "0..1 → 0..0" → 0, 0.
    // Restricted to the "digits..token" shape so a type/target arrow in the same summary
    // (e.g. "Quantity → Money") never matches.
    [GeneratedRegex(@"(\d+)\.\.([^\s;]+)\s*(?:→|->)\s*(\d+)\.\.([^\s;]+)")]
    private static partial Regex CardinalityArrowPattern();

    // Bound the auxiliary merge-harvest so a large structure window does not spawn one
    // nearest-merge lookup per authoring commit; the first handful of merges is plenty to
    // recover a branch-encoded ticket.
    private const int MergeHarvestBudget = 6;
    private const int HashFallbackCount = 3;
    private const int MaxParallelism = 8;

    // A commit that touches more than this many distinct element paths is a broad sweep, not
    // an element-isolating change; its diff is not used for per-element attribution.
    private const int IsolationLimit = 4;

    /// <summary>A structure's default window record plus its per-element (path → touches) index.</summary>
    internal sealed record StructureAttribution(
        ElementChangeRecord? WindowRecord,
        IReadOnlyDictionary<string, List<PathTouch>> ByPath);

    /// <summary>
    /// One qualifying (element-isolating, ticket-bearing) touch of an element path by a single
    /// commit. Stored newest-first; <see cref="NewMin"/>/<see cref="NewMax"/> carry the post-change
    /// cardinality value for the R6 snapshot gate.
    /// </summary>
    internal sealed record PathTouch(ElementFacet Facet, IReadOnlyList<string> Tickets, string? NewMin, string? NewMax);


    /// <summary>
    /// Returns a copy of <paramref name="model"/> with every changed structure's rows attributed:
    /// each row gets its structure-window record by default, refined to a per-element ticket where
    /// a commit isolates that element. A no-op (returns the input) when the clone is unavailable,
    /// the allowlist is empty, or neither endpoint tree loads. <paramref name="isR6Target"/> turns
    /// on the ballot4 snapshot cardinality gate for the R5→R6 increment.
    /// </summary>
    public static async Task<ReportModel> AttributeAsync(
        ReportModel model, GitLog git, string since, string until,
        FhirKeyAllowlist allowlist, bool isR6Target = false, CancellationToken ct = default)
    {
        if (!git.CloneAvailable || allowlist.IsEmpty)
        {
            return model;
        }

        IReadOnlyList<string> sinceFiles = await git.ListSourceFilesAsync(since, ct).ConfigureAwait(false);
        IReadOnlyList<string> untilFiles = await git.ListSourceFilesAsync(until, ct).ConfigureAwait(false);
        SourceFileResolver resolver = new(sinceFiles, untilFiles);
        if (!resolver.Any)
        {
            return model;
        }

        List<(string Key, StructureModel Structure, string? OldName)> jobs = [];
        foreach (MappedStructureReport report in model.Mapped)
        {
            jobs.Add(("M:" + report.Pair.Later.Name, report.Pair.Later, report.Pair.OldName));
        }
        foreach (StructureElementReport report in model.Removed)
        {
            jobs.Add(("R:" + report.Structure.Name, report.Structure, null));
        }
        foreach (StructureElementReport report in model.Added)
        {
            jobs.Add(("A:" + report.Structure.Name, report.Structure, null));
        }

        ConcurrentDictionary<string, StructureAttribution?> byStructure = new(StringComparer.Ordinal);
        using SemaphoreSlim gate = new(MaxParallelism);
        List<Task> tasks = [];
        foreach ((string key, StructureModel structure, string? oldName) in jobs)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(
                async () =>
                {
                    try
                    {
                        byStructure[key] = await BuildStructureAttributionAsync(
                            git, resolver, structure, oldName, since, until, allowlist, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                },
                ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        List<MappedStructureReport> mapped = [];
        foreach (MappedStructureReport report in model.Mapped)
        {
            byStructure.TryGetValue("M:" + report.Pair.Later.Name, out StructureAttribution? attr);
            mapped.Add(new MappedStructureReport(report.Pair, ApplyReport(report.Rows, attr, isR6Target)));
        }
        List<StructureElementReport> removed = [];
        foreach (StructureElementReport report in model.Removed)
        {
            byStructure.TryGetValue("R:" + report.Structure.Name, out StructureAttribution? attr);
            removed.Add(new StructureElementReport(report.Structure, ApplyReport(report.Rows, attr, isR6Target)));
        }
        List<StructureElementReport> added = [];
        foreach (StructureElementReport report in model.Added)
        {
            byStructure.TryGetValue("A:" + report.Structure.Name, out StructureAttribution? attr);
            added.Add(new StructureElementReport(report.Structure, ApplyReport(report.Rows, attr, isR6Target)));
        }

        return model with { Mapped = mapped, Removed = removed, Added = added };
    }

    private static async Task<StructureAttribution?> BuildStructureAttributionAsync(
        GitLog git, SourceFileResolver resolver, StructureModel structure, string? oldName,
        string since, string until, FhirKeyAllowlist allowlist, CancellationToken ct)
    {
        IReadOnlyList<string> files = resolver.Resolve(structure, oldName);
        if (files.Count == 0)
        {
            return null;
        }

        IReadOnlyList<CommitPatch> commitPatches =
            await git.LogWithPatchesAsync(since, until, files, ct: ct).ConfigureAwait(false);
        if (commitPatches.Count == 0)
        {
            return null;
        }

        List<CommitInfo> commits = [.. commitPatches.Select(cp => cp.Commit)];
        ElementChangeRecord? windowRecord =
            await BuildWindowRecordAsync(git, commits, until, allowlist, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, List<PathTouch>> byPath = BuildElementIndex(commitPatches, allowlist);

        return new StructureAttribution(windowRecord, byPath);
    }

    /// <summary>
    /// The shared structure-window record: tickets cited by the authoring commits (tier 1), else
    /// the enclosing PR-merge (tier 2), else the newest few commit short-hashes (tier 3).
    /// </summary>
    private static async Task<ElementChangeRecord?> BuildWindowRecordAsync(
        GitLog git, IReadOnlyList<CommitInfo> commits, string until, FhirKeyAllowlist allowlist, CancellationToken ct)
    {
        List<int> tickets = ExtractAuthoringTickets(commits, allowlist);
        if (tickets.Count == 0)
        {
            tickets = await HarvestMergeTicketsAsync(git, commits, until, allowlist, ct).ConfigureAwait(false);
        }
        if (tickets.Count > 0)
        {
            return new ElementChangeRecord([.. tickets.Select(n => "FHIR-" + n)], []);
        }
        List<string> shas = [.. commits.Take(HashFallbackCount).Select(c => c.ShortSha)];
        return new ElementChangeRecord([], shas);
    }

    /// <summary>
    /// Builds the per-element index: for each element-isolating, ticket-bearing commit (newest
    /// first), the set of <c>(path, facet)</c> touches it made. Broad sweeps (more than
    /// <see cref="IsolationLimit"/> distinct paths) and ticket-less commits are excluded, since a
    /// per-element override only ever <em>strengthens</em> a row and must resolve to a ticket.
    /// </summary>
    internal static Dictionary<string, List<PathTouch>> BuildElementIndex(
        IReadOnlyList<CommitPatch> commitPatches, FhirKeyAllowlist allowlist)
    {
        Dictionary<string, List<PathTouch>> byPath = new(StringComparer.Ordinal);

        foreach (CommitPatch cp in commitPatches)
        {
            IReadOnlyList<ElementTouch> touches = PatchParser.Parse(cp.Patch);
            if (touches.Count == 0)
            {
                continue;
            }
            if (touches.Select(t => t.Path).Distinct(StringComparer.Ordinal).Count() > IsolationLimit)
            {
                continue;
            }

            List<int> ticketNumbers = ExtractAuthoringTickets([cp.Commit], allowlist);
            if (ticketNumbers.Count == 0)
            {
                continue;
            }
            IReadOnlyList<string> tickets = [.. ticketNumbers.Select(n => "FHIR-" + n)];

            foreach (IGrouping<(string Path, ElementFacet Facet), ElementTouch> group in
                touches.GroupBy(t => (t.Path, t.Facet)))
            {
                string? newMin = group.Select(t => t.NewMin).FirstOrDefault(v => v is not null);
                string? newMax = group.Select(t => t.NewMax).FirstOrDefault(v => v is not null);
                if (!byPath.TryGetValue(group.Key.Path, out List<PathTouch>? list))
                {
                    list = [];
                    byPath[group.Key.Path] = list;
                }
                list.Add(new PathTouch(group.Key.Facet, tickets, newMin, newMax));
            }
        }

        return byPath;
    }

    /// <summary>
    /// Tickets cited by a structure's authoring commits, ascending and de-duplicated by
    /// number. Combines <see cref="JiraTicketExtractor"/> (prefixed <c>FHIR-N</c>,
    /// <c>J#N</c>/<c>FHIR#N</c> aliases, and <c>/browse/</c> URLs) with the custom bare-#N
    /// pass, and rejects every number not in the allowlist (drops bogus <c>FHIR-999999</c>).
    /// </summary>
    internal static List<int> ExtractAuthoringTickets(IReadOnlyList<CommitInfo> commits, FhirKeyAllowlist allowlist)
    {
        SortedSet<int> found = [];
        foreach (CommitInfo commit in commits)
        {
            string message = commit.Message;

            // The extractor's validJiraNumbers only filters hash aliases, so we re-check
            // every returned FHIR key against the allowlist ourselves. repoScope is left
            // null on purpose: its bare-integer pass would attribute coincidental in-range
            // numbers (counts, sizes) as tickets, and the real bare form here is "#N",
            // handled by the pass below.
            foreach (JiraTicketMatch match in JiraTicketExtractor.ExtractTickets(message, allowlist.Numbers))
            {
                if (TryFhirNumber(match.JiraKey, out int number) && allowlist.Numbers.Contains(number))
                {
                    found.Add(number);
                }
            }

            foreach (Match hash in HashNumberPattern().Matches(message))
            {
                if (int.TryParse(hash.Groups[1].Value, out int number) && allowlist.Numbers.Contains(number))
                {
                    found.Add(number);
                }
            }
        }
        return [.. found];
    }

    private static async Task<List<int>> HarvestMergeTicketsAsync(
        GitLog git, IReadOnlyList<CommitInfo> commits, string until, FhirKeyAllowlist allowlist, CancellationToken ct)
    {
        SortedSet<int> found = [];
        HashSet<string> seenMerges = new(StringComparer.Ordinal);
        int budget = MergeHarvestBudget;

        foreach (CommitInfo commit in commits)
        {
            if (budget <= 0)
            {
                break;
            }
            budget--;

            CommitInfo? merge = await git.NearestMergeAsync(commit.Sha, until, ct).ConfigureAwait(false);
            if (merge is null || !seenMerges.Add(merge.Sha))
            {
                continue;
            }

            string message = merge.Message;

            // Prefixed FHIR-N + branch-encoded tickets only. A bare "#NNNN" in a merge
            // subject is the *PR number*, not a ticket, so the #N pass is intentionally
            // skipped here.
            foreach (JiraTicketMatch match in JiraTicketExtractor.ExtractTickets(message, allowlist.Numbers))
            {
                if (TryFhirNumber(match.JiraKey, out int number) && allowlist.Numbers.Contains(number))
                {
                    found.Add(number);
                }
            }
            foreach (Match branch in BranchTicketPattern().Matches(message))
            {
                if (int.TryParse(branch.Groups[1].Value, out int number) && allowlist.Numbers.Contains(number))
                {
                    found.Add(number);
                }
            }
        }
        return [.. found];
    }

    private static IReadOnlyList<ElementRow> ApplyReport(
        IReadOnlyList<ElementRow> rows, StructureAttribution? attr, bool isR6Target)
    {
        if (attr is null)
        {
            return rows;
        }
        List<ElementRow> updated = new(rows.Count);
        foreach (ElementRow row in rows)
        {
            ElementChangeRecord? record = ResolvePerElement(row, attr, isR6Target) ?? attr.WindowRecord;
            updated.Add(record is null ? row : row with { ChangeRecord = record });
        }
        return updated;
    }

    /// <summary>
    /// The per-element ticket record for a row when an isolating commit changed the matching
    /// facet, or null to fall back to the structure-window record. Only cardinality and
    /// structural add/remove facets are refined (the reliably-parseable ones); type/target-only
    /// rows always keep the window record. For R6 the winning commit's post-change cardinality
    /// must equal the row's DB target value (the newest commit at/under the ballot4 snapshot).
    /// </summary>
    internal static ElementChangeRecord? ResolvePerElement(ElementRow row, StructureAttribution attr, bool isR6Target)
    {
        bool wantCardinality = row.Flags.Cardinality;
        bool wantStructural = row.Flags.Added || row.Flags.Removed;
        if (!wantCardinality && !wantStructural)
        {
            return null;
        }

        string? targetMin = null;
        string? targetMax = null;
        if (isR6Target && wantCardinality)
        {
            (targetMin, targetMax) = ParseTargetCardinality(row.Summary);
        }

        foreach (string? path in new[] { row.TargetPath, row.SourcePath })
        {
            if (path is null || !attr.ByPath.TryGetValue(path, out List<PathTouch>? list))
            {
                continue;
            }
            foreach (PathTouch touch in list) // newest first
            {
                bool facetMatches =
                    (touch.Facet == ElementFacet.Cardinality && wantCardinality)
                    || (touch.Facet == ElementFacet.Structural && wantStructural);
                if (!facetMatches)
                {
                    continue;
                }

                if (isR6Target && touch.Facet == ElementFacet.Cardinality
                    && !CardinalityMatchesTarget(touch, targetMin, targetMax))
                {
                    continue;
                }

                return new ElementChangeRecord(touch.Tickets, []);
            }
        }

        return null;
    }

    /// <summary>
    /// The R6 ballot4 snapshot gate: the commit's post-change cardinality must equal the row's
    /// DB target value, so a post-snapshot over-write is rejected. Conservative when the target
    /// cannot be parsed (returns false → keep the window record).
    /// </summary>
    private static bool CardinalityMatchesTarget(PathTouch touch, string? targetMin, string? targetMax)
    {
        if (targetMin is null && targetMax is null)
        {
            return false;
        }
        if (touch.NewMax is not null && !string.Equals(touch.NewMax, targetMax, StringComparison.Ordinal))
        {
            return false;
        }
        if (touch.NewMin is not null && !string.Equals(touch.NewMin, targetMin, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static (string? Min, string? Max) ParseTargetCardinality(string summary)
    {
        Match match = CardinalityArrowPattern().Match(summary);
        return match.Success ? (match.Groups[3].Value, match.Groups[4].Value) : (null, null);
    }

    private static bool TryFhirNumber(string jiraKey, out int number)
    {
        number = 0;
        return jiraKey.StartsWith("FHIR-", StringComparison.Ordinal)
            && int.TryParse(jiraKey.AsSpan(5), out number);
    }
}
