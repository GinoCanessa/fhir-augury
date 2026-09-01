using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>A Jira ticket reference extracted from text.</summary>
/// <param name="JiraKey">Normalized Jira key (e.g. FHIR-N, BALLOT-N, PSS-N, UP-N).</param>
/// <param name="OriginalLiteral">The literal as it appeared in the source text.</param>
/// <param name="Context">Surrounding text for context.</param>
public record JiraTicketMatch(string JiraKey, string OriginalLiteral, string Context);

/// <summary>
/// Repo-scoped project scopes used by the bare-integer resolution pass. Only
/// supplied by callers that know the repository's owning project(s) and the
/// numeric range(s) a bare ticket number can legitimately fall in.
/// </summary>
/// <param name="Projects">Ordered project scopes; first matching range wins.</param>
public record RepoJiraScope(IReadOnlyList<RepoJiraProjectScope> Projects);

/// <summary>
/// A single project's bare-number window: <paramref name="ProjectKey"/> resolves
/// any standalone integer in the inclusive range [<paramref name="Lower"/>,
/// <paramref name="Upper"/>] to <c>PROJECTKEY-N</c>.
/// </summary>
public record RepoJiraProjectScope(string ProjectKey, int Lower, int Upper);

/// <summary>
/// Extracts Jira ticket references from arbitrary text using multiple patterns.
/// Source-agnostic — returns normalized keys that any source can map to its own records.
/// </summary>
public static partial class JiraTicketExtractor
{
    /// <summary>
    /// Unified key/hash pattern. Named groups identify the canonical project.
    /// Matches: 
    ///     FHIR-N, JF-N, GF-N, J-N (→ FHIR-N) 
    ///     | BALDEF-N 
    ///     | BALLOT-N 
    ///     | GCR-N
    ///     | HTA-N
    ///     | PSS-N 
    ///     | TSC-N
    ///     | UP-N
    ///     | UPSM-N
    /// Also matches hash variants: FHIR#N, JF#N, GF#N, J#N → FHIR-N, etc.
    /// </summary>
    [GeneratedRegex(@"(?<!/)\b(?:(?<fhir>FHIR|JF|GF|J)|(?<baldef>BALDEF)|(?<ballot>BALLOT)|(?<gom>GCR)|(?<termauth>HTA)|(?<pss>PSS)|(?<tsc>TSC)|(?<upsm>UPSM)|(?<up>UP))[-#](?<num>\d+)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnifiedKeyHashPattern();

    /// <summary>
    /// Combined URL pattern covering /browse/ and /projects/.../issues/ formats
    /// for all supported Jira projects.
    /// </summary>
    [GeneratedRegex(@"https?://jira\.hl7\.org/(?:browse/|projects/(?:FHIR|BALDEF|BALLOT|GCR|HTA|PSS|TSC|UP|UPSM)/issues/)((?:FHIR|BALDEF|BALLOT|GCR|HTA|PSS|TSC|UP|UPSM)-\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnifiedUrlPattern();

    /// <summary>
    /// Matches a standalone integer that is NOT part of a GitHub ref (<c>#123</c>,
    /// <c>pull/123</c>), a path, a URL, a date (<c>2026-06-25</c>), or a dotted
    /// version (<c>1.2.3</c>). Used only by the repo-scoped bare-number pass.
    /// </summary>
    [GeneratedRegex(@"(?<![\w#/.\-])\d+(?![\w./\-])")]
    private static partial Regex BareNumberPattern();

    /// <summary>
    /// Extracts all Jira ticket references from the given text.
    /// Deduplicates by JiraKey. Returns context (~160 chars) around each match.
    /// </summary>
    public static List<JiraTicketMatch> ExtractTickets(string text)
        => ExtractTickets(text, validJiraNumbers: null, repoScope: null);

    /// <summary>
    /// Extracts all Jira ticket references, filtering FHIR hash-alias patterns
    /// against an optional allowlist of valid Jira issue numbers.
    /// </summary>
    public static List<JiraTicketMatch> ExtractTickets(string text, HashSet<int>? validJiraNumbers)
        => ExtractTickets(text, validJiraNumbers, repoScope: null);

    /// <summary>
    /// Extracts all Jira ticket references. In addition to the prefixed/hashed
    /// and URL patterns, when <paramref name="repoScope"/> is supplied a
    /// repo-scoped pass resolves standalone integers (e.g. <c>54873</c>) to
    /// <c>PROJECT-N</c> for the first project whose numeric range contains them.
    /// A bare value already named by any prefixed key in the same text is never
    /// re-guessed.
    /// </summary>
    public static List<JiraTicketMatch> ExtractTickets(string text, HashSet<int>? validJiraNumbers, RepoJiraScope? repoScope)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        List<JiraTicketMatch> results = [];
        HashSet<string> seen = [];
        HashSet<int> seenNumbers = [];

        // Pass 1: URL matches (canonical PREFIX-N already in capture group)
        foreach (Match match in UnifiedUrlPattern().Matches(text))
        {
            string jiraKey = match.Groups[1].Value.ToUpperInvariant();
            RecordNumber(jiraKey, seenNumbers);
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, jiraKey, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        // Pass 2: Key/hash matches
        foreach (Match match in UnifiedKeyHashPattern().Matches(text))
        {
            string? canonicalPrefix = GetCanonicalPrefix(match);
            if (canonicalPrefix is null) continue;

            string number = match.Groups["num"].Value;
            bool isHash = match.Value.Contains('#');

            // Validation filter: only FHIR hash aliases are filtered
            if (canonicalPrefix == "FHIR" && isHash
                && validJiraNumbers is not null
                && int.TryParse(number, out int n)
                && !validJiraNumbers.Contains(n))
            {
                continue;
            }

            if (int.TryParse(number, out int parsedNumber))
                seenNumbers.Add(parsedNumber);

            string jiraKey = $"{canonicalPrefix}-{number}";
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, match.Value, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        // Pass 3: Repo-scoped bare integers (only when a scope is supplied)
        if (repoScope is { Projects.Count: > 0 })
        {
            foreach (Match match in BareNumberPattern().Matches(text))
            {
                if (!int.TryParse(match.Value, out int value)) continue;

                // A number already named by a prefixed/URL key is never re-guessed.
                if (seenNumbers.Contains(value)) continue;

                // Belt-and-suspenders GitHub-ref context guard.
                if (HasGitHubRefContext(text, match.Index)) continue;

                foreach (RepoJiraProjectScope project in repoScope.Projects)
                {
                    if (value < project.Lower || value > project.Upper) continue;

                    string jiraKey = $"{project.ProjectKey.ToUpperInvariant()}-{value}";
                    if (seen.Add(jiraKey))
                        results.Add(new JiraTicketMatch(jiraKey, match.Value, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>Records the numeric suffix of a canonical key into the seen set.</summary>
    private static void RecordNumber(string jiraKey, HashSet<int> seenNumbers)
    {
        int dash = jiraKey.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(jiraKey.AsSpan(dash + 1), out int n))
            seenNumbers.Add(n);
    }

    /// <summary>
    /// True when the ~12 chars immediately preceding the candidate integer look
    /// like a GitHub reference (<c>#</c>, <c>pull/</c>, <c>issues/</c>, <c>PR </c>).
    /// </summary>
    private static bool HasGitHubRefContext(string text, int index)
    {
        int start = Math.Max(0, index - 12);
        string left = text[start..index];
        if (left.EndsWith('#')) return true;
        return left.Contains("pull/", StringComparison.OrdinalIgnoreCase)
            || left.Contains("issues/", StringComparison.OrdinalIgnoreCase)
            || left.Contains("PR ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCanonicalPrefix(Match match)
    {
        if (match.Groups["fhir"].Success) return "FHIR";
        if (match.Groups["baldef"].Success) return "BALDEF";
        if (match.Groups["ballot"].Success) return "BALLOT";
        if (match.Groups["gom"].Success) return "GCR";
        if (match.Groups["termauth"].Success) return "HTA";
        if (match.Groups["pss"].Success) return "PSS";
        if (match.Groups["tsc"].Success) return "TSC";
        if (match.Groups["upsm"].Success) return "UPSM";
        if (match.Groups["up"].Success) return "UP";
        return null;
    }
}
