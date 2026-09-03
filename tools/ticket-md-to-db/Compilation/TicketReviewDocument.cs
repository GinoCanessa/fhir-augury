using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace FhirAugury.Tools.TicketMdToDb.Compilation;

public sealed record TicketReviewHeading(
    int Level,
    string Text,
    string HeadingPath,
    int StartOffset,
    int EndOffset,
    int StartLine,
    int StartColumn);

public sealed record TicketReviewLink(
    string Label,
    string Url,
    int StartOffset,
    int EndOffset,
    string HeadingPath);

public sealed record TicketReviewSection(
    TicketReviewHeading Heading,
    int FullStartOffset,
    int BodyStartOffset,
    int EndOffset)
{
    public bool Contains(int offset) => offset >= BodyStartOffset && offset <= EndOffset;
}

public sealed partial class TicketReviewDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    private readonly int[] _lineStarts;

    private TicketReviewDocument(
        DiscoveredReport source,
        string markdown,
        IReadOnlyList<TicketReviewHeading> headings,
        IReadOnlyList<TicketReviewLink> links)
    {
        Source = source;
        Markdown = markdown;
        Headings = headings;
        Links = links;
        _lineStarts = BuildLineStarts(markdown);
    }

    public DiscoveredReport Source { get; }
    public string Markdown { get; }
    public IReadOnlyList<TicketReviewHeading> Headings { get; }
    public IReadOnlyList<TicketReviewLink> Links { get; }

    public static TicketReviewDocument Parse(DiscoveredReport source)
    {
        string markdown = Encoding.UTF8.GetString(source.Bytes);
        MarkdownDocument syntax = Markdig.Markdown.Parse(markdown, Pipeline);
        int[] lineStarts = BuildLineStarts(markdown);
        List<TicketReviewHeading> headings = [];
        List<TicketReviewLink> links = [];
        List<string> headingStack = [];

        foreach (Block block in syntax)
        {
            CollectBlock(block, markdown, lineStarts, headings, links, headingStack);
        }

        return new TicketReviewDocument(source, markdown, headings, links);
    }

    public bool HasOrderedHeadings(params string[] names)
    {
        int index = -1;
        foreach (string name in names)
        {
            index = FindHeadingIndex(name, index + 1);
            if (index < 0)
            {
                return false;
            }
        }

        return true;
    }

    public TicketReviewSection? FindSection(string headingText)
    {
        int index = FindHeadingIndex(headingText, 0);
        if (index < 0)
        {
            return null;
        }

        TicketReviewHeading heading = Headings[index];
        int end = Markdown.Length - 1;
        for (int i = index + 1; i < Headings.Count; i++)
        {
            if (Headings[i].Level <= heading.Level)
            {
                end = Headings[i].StartOffset - 1;
                break;
            }
        }

        return new TicketReviewSection(
            heading,
            heading.StartOffset,
            SkipLineEnding(heading.EndOffset + 1),
            Math.Max(heading.EndOffset, end));
    }

    public TicketReviewSection? FindSectionUntil(
        string headingText,
        string? nextHeadingText)
    {
        int index = FindHeadingIndex(headingText, 0);
        if (index < 0)
        {
            return null;
        }

        TicketReviewHeading heading = Headings[index];
        int end = Markdown.Length - 1;
        if (nextHeadingText is not null)
        {
            int nextIndex = FindHeadingIndex(nextHeadingText, index + 1);
            if (nextIndex < 0)
            {
                return null;
            }

            end = Headings[nextIndex].StartOffset - 1;
        }

        return new TicketReviewSection(
            heading,
            heading.StartOffset,
            SkipLineEnding(heading.EndOffset + 1),
            Math.Max(heading.EndOffset, end));
    }

    public TicketReviewSection? FindSectionStartingWith(string prefix)
    {
        TicketReviewHeading? heading = Headings.FirstOrDefault(
            candidate => candidate.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return heading is null ? null : FindSection(heading);
    }

    public TicketReviewSection? FindSection(TicketReviewHeading heading)
    {
        int index = FindHeadingIndex(heading);
        if (index < 0)
        {
            return null;
        }

        int end = Markdown.Length - 1;
        for (int i = index + 1; i < Headings.Count; i++)
        {
            if (Headings[i].Level <= heading.Level)
            {
                end = Headings[i].StartOffset - 1;
                break;
            }
        }

        return new TicketReviewSection(
            heading,
            heading.StartOffset,
            SkipLineEnding(heading.EndOffset + 1),
            Math.Max(heading.EndOffset, end));
    }

    public string Slice(int startOffset, int endOffset)
    {
        if (Markdown.Length == 0 || endOffset < startOffset)
        {
            return string.Empty;
        }

        int start = Math.Clamp(startOffset, 0, Markdown.Length);
        int end = Math.Clamp(endOffset, -1, Markdown.Length - 1);
        return end < start
            ? string.Empty
            : Markdown[start..(end + 1)].Trim();
    }

    public string Body(TicketReviewSection? section) =>
        section is null ? string.Empty : Slice(section.BodyStartOffset, section.EndOffset);

    public SourceEvidence Evidence(
        int startOffset,
        int endOffset,
        string headingPath,
        string mappingRuleId)
    {
        int safeStart = Math.Clamp(startOffset, 0, Math.Max(0, Markdown.Length - 1));
        int safeEnd = Math.Clamp(endOffset, safeStart, Math.Max(safeStart, Markdown.Length - 1));
        (int startLine, int startColumn) = GetLineColumn(safeStart);
        (int endLine, int endColumn) = GetLineColumn(safeEnd);
        return new SourceEvidence(
            Source.RelativePath,
            Source.SourceSha256,
            headingPath,
            safeStart,
            safeEnd,
            startLine,
            startColumn,
            endLine,
            endColumn,
            mappingRuleId,
            Slice(safeStart, safeEnd));
    }

    public SourceEvidence Evidence(TicketReviewSection section, string mappingRuleId) =>
        Evidence(
            section.BodyStartOffset,
            section.EndOffset,
            section.Heading.HeadingPath,
            mappingRuleId);

    public IReadOnlyList<ClassifiedSourceRange> ClassifyTopLevelSections(
        IReadOnlyDictionary<string, (SourceRangeDisposition Disposition, string Reason)> classifications,
        string defaultReason)
    {
        List<TicketReviewHeading> topLevel = Headings.Where(heading => heading.Level == 2).ToList();
        if (Markdown.Length == 0)
        {
            return [];
        }

        List<ClassifiedSourceRange> ranges = [];
        int cursor = 0;
        foreach (TicketReviewHeading heading in topLevel)
        {
            if (heading.StartOffset > cursor)
            {
                AddRange(
                    ranges,
                    cursor,
                    heading.StartOffset - 1,
                    SourceRangeDisposition.IntentionallyUnmapped,
                    cursor == 0 ? "document-preface" : defaultReason,
                    "range-unmapped");
            }

            int headingIndex = FindHeadingIndex(heading);
            int end = Markdown.Length - 1;
            for (int i = headingIndex + 1; i < Headings.Count; i++)
            {
                if (Headings[i].Level <= heading.Level)
                {
                    end = Headings[i].StartOffset - 1;
                    break;
                }
            }

            (SourceRangeDisposition disposition, string reason) =
                classifications.TryGetValue(NormalizeHeading(heading.Text), out var configured)
                    ? configured
                    : (SourceRangeDisposition.IntentionallyUnmapped, defaultReason);
            AddRange(ranges, heading.StartOffset, end, disposition, reason, $"range-{NormalizeRule(reason)}");
            cursor = end + 1;
        }

        if (cursor < Markdown.Length)
        {
            AddRange(
                ranges,
                cursor,
                Markdown.Length - 1,
                SourceRangeDisposition.IntentionallyUnmapped,
                defaultReason,
                "range-unmapped-tail");
        }

        return MergeAdjacentRanges(ranges);
    }

    public IReadOnlyList<ClassifiedSourceRange> ClassifySentinelSections(
        IReadOnlyList<string> sentinelHeadings,
        IReadOnlyDictionary<string, (SourceRangeDisposition Disposition, string Reason)> classifications,
        string defaultReason)
    {
        List<TicketReviewHeading> sentinels = [];
        int searchStart = 0;
        foreach (string sentinel in sentinelHeadings)
        {
            int index = FindHeadingIndex(sentinel, searchStart);
            if (index < 0)
            {
                continue;
            }

            sentinels.Add(Headings[index]);
            searchStart = index + 1;
        }
        if (Markdown.Length == 0)
        {
            return [];
        }

        List<ClassifiedSourceRange> ranges = [];
        int cursor = 0;
        for (int i = 0; i < sentinels.Count; i++)
        {
            TicketReviewHeading heading = sentinels[i];
            if (heading.StartOffset > cursor)
            {
                AddRange(
                    ranges,
                    cursor,
                    heading.StartOffset - 1,
                    SourceRangeDisposition.IntentionallyUnmapped,
                    "document-preface",
                    "range-unmapped-preface");
            }

            int end = i + 1 < sentinels.Count
                ? sentinels[i + 1].StartOffset - 1
                : Markdown.Length - 1;
            (SourceRangeDisposition disposition, string reason) =
                classifications.TryGetValue(NormalizeHeading(heading.Text), out var configured)
                    ? configured
                    : (SourceRangeDisposition.IntentionallyUnmapped, defaultReason);
            AddRange(ranges, heading.StartOffset, end, disposition, reason, $"range-{NormalizeRule(reason)}");
            cursor = end + 1;
        }

        if (cursor < Markdown.Length)
        {
            AddRange(
                ranges,
                cursor,
                Markdown.Length - 1,
                SourceRangeDisposition.IntentionallyUnmapped,
                defaultReason,
                "range-unmapped-tail");
        }

        return MergeAdjacentRanges(ranges);
    }

    public static string NormalizeHeading(string value) =>
        WhitespaceRegex().Replace(value.Trim().TrimEnd(':'), " ");

    private static void CollectBlock(
        Block block,
        string markdown,
        int[] lineStarts,
        List<TicketReviewHeading> headings,
        List<TicketReviewLink> links,
        List<string> headingStack)
    {
        if (block is HeadingBlock headingBlock)
        {
            string text = ReadHeadingText(markdown, headingBlock.Span.Start, headingBlock.Span.End);
            while (headingStack.Count >= headingBlock.Level)
            {
                headingStack.RemoveAt(headingStack.Count - 1);
            }
            headingStack.Add(text);
            (int line, int column) = GetLineColumn(lineStarts, headingBlock.Span.Start);
            headings.Add(new TicketReviewHeading(
                headingBlock.Level,
                text,
                string.Join(" > ", headingStack),
                headingBlock.Span.Start,
                headingBlock.Span.End,
                line,
                column));
        }

        if (block is LeafBlock leaf && leaf.Inline is not null)
        {
            CollectInline(leaf.Inline.FirstChild, markdown, headings, links);
        }

        if (block is ContainerBlock container)
        {
            foreach (Block child in container)
            {
                CollectBlock(child, markdown, lineStarts, headings, links, headingStack);
            }
        }
    }

    private static void CollectInline(
        Inline? inline,
        string markdown,
        IReadOnlyList<TicketReviewHeading> headings,
        List<TicketReviewLink> links)
    {
        for (Inline? current = inline; current is not null; current = current.NextSibling)
        {
            if (current is LinkInline { IsImage: false, Url: not null } link)
            {
                int start = Math.Max(0, link.Span.Start);
                int end = Math.Min(markdown.Length - 1, link.Span.End);
                string source = end >= start ? markdown[start..(end + 1)] : string.Empty;
                Match labelMatch = LinkLabelRegex().Match(source);
                string label = labelMatch.Success ? labelMatch.Groups[1].Value : source;
                string headingPath = headings.LastOrDefault(heading => heading.StartOffset <= start)?.HeadingPath ?? string.Empty;
                links.Add(new TicketReviewLink(label.Trim(), link.Url, start, end, headingPath));
            }

            if (current is ContainerInline nested)
            {
                CollectInline(nested.FirstChild, markdown, headings, links);
            }
        }
    }

    private static string ReadHeadingText(string markdown, int start, int end)
    {
        string source = markdown[start..Math.Min(markdown.Length, end + 1)];
        Match match = HeadingTextRegex().Match(source);
        return match.Success ? match.Groups[1].Value.Trim() : source.Trim();
    }

    private void AddRange(
        List<ClassifiedSourceRange> ranges,
        int start,
        int end,
        SourceRangeDisposition disposition,
        string reason,
        string rule)
    {
        if (end < start)
        {
            return;
        }

        string headingPath = Headings.LastOrDefault(heading => heading.StartOffset <= start)?.HeadingPath ?? string.Empty;
        SourceEvidence evidence = Evidence(start, end, headingPath, rule);
        ranges.Add(new ClassifiedSourceRange(start, end, disposition, reason, evidence));
    }

    private static IReadOnlyList<ClassifiedSourceRange> MergeAdjacentRanges(
        IReadOnlyList<ClassifiedSourceRange> ranges)
    {
        List<ClassifiedSourceRange> merged = [];
        foreach (ClassifiedSourceRange range in ranges.OrderBy(range => range.StartOffset))
        {
            if (merged.Count > 0
                && merged[^1].EndOffset + 1 == range.StartOffset
                && merged[^1].Disposition == range.Disposition
                && string.Equals(merged[^1].Reason, range.Reason, StringComparison.Ordinal))
            {
                ClassifiedSourceRange previous = merged[^1];
                SourceEvidence combined = previous.Evidence with
                {
                    EndOffset = range.Evidence.EndOffset,
                    EndLine = range.Evidence.EndLine,
                    EndColumn = range.Evidence.EndColumn,
                    Markdown = $"{previous.Evidence.Markdown}\n{range.Evidence.Markdown}".Trim(),
                };
                merged[^1] = previous with
                {
                    EndOffset = range.EndOffset,
                    Evidence = combined,
                };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private int FindHeadingIndex(string headingText, int startIndex)
    {
        string normalized = NormalizeHeading(headingText);
        for (int i = startIndex; i < Headings.Count; i++)
        {
            if (string.Equals(
                    NormalizeHeading(Headings[i].Text),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindHeadingIndex(TicketReviewHeading heading)
    {
        for (int i = 0; i < Headings.Count; i++)
        {
            if (ReferenceEquals(Headings[i], heading) || Headings[i] == heading)
            {
                return i;
            }
        }

        return -1;
    }

    private int SkipLineEnding(int offset)
    {
        int current = Math.Clamp(offset, 0, Markdown.Length);
        while (current < Markdown.Length && (Markdown[current] == '\r' || Markdown[current] == '\n'))
        {
            current++;
        }

        return current;
    }

    private (int Line, int Column) GetLineColumn(int offset) => GetLineColumn(_lineStarts, offset);

    private static (int Line, int Column) GetLineColumn(int[] lineStarts, int offset)
    {
        int lineIndex = Array.BinarySearch(lineStarts, offset);
        if (lineIndex < 0)
        {
            lineIndex = ~lineIndex - 1;
        }

        lineIndex = Math.Max(0, lineIndex);
        return (lineIndex + 1, offset - lineStarts[lineIndex] + 1);
    }

    private static int[] BuildLineStarts(string markdown)
    {
        List<int> starts = [0];
        for (int i = 0; i < markdown.Length; i++)
        {
            if (markdown[i] == '\n' && i + 1 < markdown.Length)
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static string NormalizeRule(string value)
    {
        StringBuilder builder = new();
        foreach (char character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Singleline)]
    private static partial Regex HeadingTextRegex();

    [GeneratedRegex(@"^\[([^\]]+)\]")]
    private static partial Regex LinkLabelRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
