using System.Text.RegularExpressions;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

/// <summary>
/// Captures the current ballot-note <c>&lt;blockquote class="ballot-note"&gt;</c>
/// block at HEAD for an artifact/page intro file via <c>git show</c>, using a
/// string/regex scan (no DOM parse). Returns empty when the file or block is
/// absent.
/// </summary>
public static partial class BallotNoteHtmlExtractor
{
    [GeneratedRegex(
        "<blockquote[^>]*\\bballot-note\\b[^>]*>.*?</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BallotNoteBlock();

    /// <summary>Extracts the first ballot-note blockquote from HTML, or empty if none.</summary>
    public static string Extract(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        Match match = BallotNoteBlock().Match(html);
        return match.Success ? match.Value.Trim() : string.Empty;
    }

    /// <summary>
    /// Reads <paramref name="introFile"/> at HEAD and extracts its ballot-note
    /// block. Returns empty when the file does not exist at HEAD.
    /// </summary>
    public static async Task<string> ExtractAtHeadAsync(
        string clonePath,
        string introFile,
        CancellationToken ct = default)
    {
        GitRunner.GitResult result = await GitRunner.TryRunAsync(
            clonePath,
            ["show", $"HEAD:{introFile}"],
            ct).ConfigureAwait(false);

        return result.ExitCode == 0 ? Extract(result.StdOut) : string.Empty;
    }
}
