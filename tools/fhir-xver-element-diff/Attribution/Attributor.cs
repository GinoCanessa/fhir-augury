using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FhirAugury.Common.Text;
using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Readers;
using FhirAugury.Tools.FhirXverElementDiff.Report;

namespace FhirAugury.Tools.FhirXverElementDiff.Attribution;

/// <summary>
/// Structure-window attribution: for each changed structure, walk the git window over its
/// source file(s), extract the FHIR tickets its authoring commits cite, and stamp that
/// shared <see cref="ElementChangeRecord"/> onto every one of the structure's changed rows
/// (the request's structure-window fallback; Phase 6 refines to per-element where a commit
/// isolates one element). Ticket links are preferred; a bare commit-hash list is the
/// fallback when no allowlisted ticket resolves.
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

    // Bound the auxiliary merge-harvest so a large structure window does not spawn one
    // nearest-merge lookup per authoring commit; the first handful of merges is plenty to
    // recover a branch-encoded ticket.
    private const int MergeHarvestBudget = 6;
    private const int HashFallbackCount = 3;
    private const int MaxParallelism = 8;

    /// <summary>
    /// Returns a copy of <paramref name="model"/> with every changed structure's rows
    /// stamped with its structure-window change record. A no-op (returns the input) when the
    /// clone is unavailable, the allowlist is empty, or neither endpoint tree loads.
    /// </summary>
    public static async Task<ReportModel> AttributeAsync(
        ReportModel model, GitLog git, string since, string until,
        FhirKeyAllowlist allowlist, CancellationToken ct = default)
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

        ConcurrentDictionary<string, ElementChangeRecord?> byStructure = new(StringComparer.Ordinal);
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
                        byStructure[key] = await BuildStructureRecordAsync(
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
            byStructure.TryGetValue("M:" + report.Pair.Later.Name, out ElementChangeRecord? record);
            mapped.Add(new MappedStructureReport(report.Pair, ApplyRecord(report.Rows, record)));
        }
        List<StructureElementReport> removed = [];
        foreach (StructureElementReport report in model.Removed)
        {
            byStructure.TryGetValue("R:" + report.Structure.Name, out ElementChangeRecord? record);
            removed.Add(new StructureElementReport(report.Structure, ApplyRecord(report.Rows, record)));
        }
        List<StructureElementReport> added = [];
        foreach (StructureElementReport report in model.Added)
        {
            byStructure.TryGetValue("A:" + report.Structure.Name, out ElementChangeRecord? record);
            added.Add(new StructureElementReport(report.Structure, ApplyRecord(report.Rows, record)));
        }

        return model with { Mapped = mapped, Removed = removed, Added = added };
    }

    private static async Task<ElementChangeRecord?> BuildStructureRecordAsync(
        GitLog git, SourceFileResolver resolver, StructureModel structure, string? oldName,
        string since, string until, FhirKeyAllowlist allowlist, CancellationToken ct)
    {
        IReadOnlyList<string> files = resolver.Resolve(structure, oldName);
        if (files.Count == 0)
        {
            return null;
        }

        IReadOnlyList<CommitInfo> commits = await git.LogAsync(since, until, files, ct).ConfigureAwait(false);
        if (commits.Count == 0)
        {
            return null;
        }

        // Tier 1: tickets cited directly in authoring commits.
        List<int> tickets = ExtractAuthoringTickets(commits, allowlist);

        // Tier 2: the enclosing PR-merge subject/branch (only when tier 1 is empty).
        if (tickets.Count == 0)
        {
            tickets = await HarvestMergeTicketsAsync(git, commits, until, allowlist, ct).ConfigureAwait(false);
        }

        if (tickets.Count > 0)
        {
            List<string> keys = [.. tickets.Select(n => "FHIR-" + n)];
            return new ElementChangeRecord(keys, []);
        }

        // Tier 3: bare commit-hash fallback (newest few).
        List<string> shas = [.. commits.Take(HashFallbackCount).Select(c => c.ShortSha)];
        return new ElementChangeRecord([], shas);
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

    private static IReadOnlyList<ElementRow> ApplyRecord(IReadOnlyList<ElementRow> rows, ElementChangeRecord? record)
    {
        if (record is null)
        {
            return rows;
        }
        List<ElementRow> updated = new(rows.Count);
        foreach (ElementRow row in rows)
        {
            updated.Add(row with { ChangeRecord = record });
        }
        return updated;
    }

    private static bool TryFhirNumber(string jiraKey, out int number)
    {
        number = 0;
        return jiraKey.StartsWith("FHIR-", StringComparison.Ordinal)
            && int.TryParse(jiraKey.AsSpan(5), out number);
    }
}
