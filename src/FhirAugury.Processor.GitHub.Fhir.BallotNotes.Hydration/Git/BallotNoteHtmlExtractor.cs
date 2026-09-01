using System.Text.RegularExpressions;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

/// <summary>
/// One note-like <c>&lt;blockquote&gt;</c> block captured from an intro/page
/// file, classified by whether the tool generated it (carries the
/// <c>data-augury-generated="true"</c> marker on its open tag).
/// </summary>
public sealed record ClassifiedNoteBlock(string Html, bool IsAuguryGenerated);

/// <summary>
/// Captures current ballot-note blocks at HEAD for an artifact/page intro file
/// via <c>git show</c>, using a string/regex scan (no DOM parse). Matches both
/// <c>class="ballot-note"</c> and hand-authored <c>class="stu-note"</c> blocks,
/// and classifies each by the tool's <c>data-augury-generated</c> marker so a
/// regenerated note can replace only the prior tool-generated block while
/// preserving hand-authored notes. Returns empty when the file/block is absent.
/// </summary>
public static partial class BallotNoteHtmlExtractor
{
    [GeneratedRegex(
        "<blockquote([^>]*\\b(?:ballot-note|stu-note)\\b[^>]*)>(?:.*?)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NoteBlock();

    [GeneratedRegex(
        "data-augury-generated\\s*=\\s*\"true\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex AuguryMarker();

    /// <summary>Extracts the first note blockquote from HTML, or empty if none.</summary>
    public static string Extract(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        Match match = NoteBlock().Match(html);
        return match.Success ? match.Value.Trim() : string.Empty;
    }

    /// <summary>
    /// Extracts every note-like block from <paramref name="html"/>, in document
    /// order, each flagged with whether it is augury-generated.
    /// </summary>
    public static IReadOnlyList<ClassifiedNoteBlock> ExtractClassified(string html)
    {
        if (string.IsNullOrEmpty(html)) return [];

        List<ClassifiedNoteBlock> blocks = [];
        foreach (Match match in NoteBlock().Matches(html))
        {
            string openTagAttributes = match.Groups[1].Value;
            blocks.Add(new ClassifiedNoteBlock(match.Value.Trim(), AuguryMarker().IsMatch(openTagAttributes)));
        }
        return blocks;
    }

    /// <summary>
    /// Reads <paramref name="introFile"/> at HEAD and extracts its first note
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

    /// <summary>
    /// Reads <paramref name="introFile"/> at HEAD and extracts every classified
    /// note block. Returns empty when the file does not exist at HEAD.
    /// </summary>
    public static async Task<IReadOnlyList<ClassifiedNoteBlock>> ExtractClassifiedAtHeadAsync(
        string clonePath,
        string introFile,
        CancellationToken ct = default)
    {
        GitRunner.GitResult result = await GitRunner.TryRunAsync(
            clonePath,
            ["show", $"HEAD:{introFile}"],
            ct).ConfigureAwait(false);

        return result.ExitCode == 0 ? ExtractClassified(result.StdOut) : [];
    }
}
