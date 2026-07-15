using System.Text;
using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Report;

/// <summary>
/// Renders a <see cref="ReportModel"/> to the markdown report shape from the feature
/// request: a title, a resolved header block, then <c>## Mapped</c> / <c>## Removed</c> /
/// <c>## Added</c>, each split into <c>### Primitive types</c> / <c>### Complex types</c> /
/// <c>### Resources</c>, each structure a <c>####</c> heading (rename-aware) followed by its
/// ten-column element table. Pure string rendering — the only I/O is <see cref="WriteAsync"/>.
/// </summary>
internal static class MarkdownReportWriter
{
    internal const string TableHeader =
        "| Source Element | Target Element | Added | Removed | Renamed | Cardinality | Type | Target | Summary | Change record |";

    private const string TableSeparator =
        "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |";

    private static readonly StructureGroup[] Groups =
        [StructureGroup.PrimitiveType, StructureGroup.ComplexType, StructureGroup.Resource];

    public static async Task WriteAsync(ReportModel model, string path)
    {
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(path, Render(model), new UTF8Encoding(false)).ConfigureAwait(false);
    }

    public static string Render(ReportModel model)
    {
        StringBuilder sb = new();
        ReportHeader header = model.Header;

        sb.Append("# FHIR element changes: ").Append(header.EarlierLabel)
            .Append(" → ").Append(header.LaterLabel).Append("\n\n");

        WriteHeaderBlock(sb, header);

        WriteMappedSection(sb, model);
        WriteBucketSection(sb, "Removed", model.Removed);
        WriteBucketSection(sb, "Added", model.Added);

        return sb.ToString();
    }

    private static void WriteHeaderBlock(StringBuilder sb, ReportHeader header)
    {
        sb.Append("_Generated ")
            .Append(header.GeneratedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"))
            .Append(" UTC._\n\n");

        sb.Append("| Endpoint | Package version | Built |\n");
        sb.Append("| --- | --- | --- |\n");
        sb.Append("| Earlier — ").Append(header.EarlierLabel).Append(" | ")
            .Append(header.EarlierVersion).Append(" | ").Append(BuiltDate(header.EarlierBuilt)).Append(" |\n");
        sb.Append("| Later — ").Append(header.LaterLabel).Append(" | ")
            .Append(header.LaterVersion).Append(" | ").Append(BuiltDate(header.LaterBuilt)).Append(" |\n\n");

        sb.Append("- **Git window:** `").Append(header.SinceSha).Append("`..`").Append(header.UntilSha).Append('`');
        if (header.CloneHead is not null)
        {
            sb.Append(" (clone HEAD `").Append(header.CloneHead).Append("`)");
        }
        sb.Append('\n');

        sb.Append("- **Attribution:** ")
            .Append(header.AttributionEnabled ? "enabled" : "disabled (`--no-attribution`)")
            .Append('\n');

        if (header.HeaderNote is not null)
        {
            sb.Append("- **Note:** ").Append(header.HeaderNote).Append('\n');
        }
        sb.Append('\n');
    }

    private static void WriteMappedSection(StringBuilder sb, ReportModel model)
    {
        sb.Append("## Mapped\n\n");
        if (model.Mapped.Count == 0)
        {
            sb.Append("_No mapped structures changed._\n\n");
            return;
        }

        foreach (StructureGroup group in Groups)
        {
            List<MappedStructureReport> inGroup =
                [.. model.Mapped.Where(m => m.Pair.Group == group)];
            if (inGroup.Count == 0)
            {
                continue;
            }

            sb.Append("### ").Append(group.Heading()).Append("\n\n");
            foreach (MappedStructureReport report in inGroup)
            {
                sb.Append("#### ").Append(MappedHeading(report.Pair)).Append("\n\n");
                WriteTable(sb, report.Rows);
            }
        }
    }

    private static void WriteBucketSection(
        StringBuilder sb, string title, IReadOnlyList<StructureElementReport> bucket)
    {
        sb.Append("## ").Append(title).Append("\n\n");
        if (bucket.Count == 0)
        {
            sb.Append("_None._\n\n");
            return;
        }

        foreach (StructureGroup group in Groups)
        {
            List<StructureElementReport> inGroup =
                [.. bucket.Where(r => r.Structure.Group == group)];
            if (inGroup.Count == 0)
            {
                continue;
            }

            sb.Append("### ").Append(group.Heading()).Append("\n\n");
            foreach (StructureElementReport report in inGroup)
            {
                sb.Append("#### ").Append(report.Structure.Name).Append("\n\n");
                WriteTable(sb, report.Rows);
            }
        }
    }

    private static void WriteTable(StringBuilder sb, IReadOnlyList<ElementRow> rows)
    {
        if (rows.Count == 0)
        {
            sb.Append("_(no locally-defined elements)_\n\n");
            return;
        }

        sb.Append(TableHeader).Append('\n');
        sb.Append(TableSeparator).Append('\n');
        foreach (ElementRow row in rows)
        {
            sb.Append("| ").Append(Cell(row.SourcePath))
                .Append(" | ").Append(Cell(row.TargetPath))
                .Append(" | ").Append(Flag(row.Flags.Added))
                .Append(" | ").Append(Flag(row.Flags.Removed))
                .Append(" | ").Append(RenamedCell(row.Flags.Renamed))
                .Append(" | ").Append(Flag(row.Flags.Cardinality))
                .Append(" | ").Append(Flag(row.Flags.Type))
                .Append(" | ").Append(Flag(row.Flags.Target))
                .Append(" | ").Append(Cell(SummaryText(row)))
                .Append(" | ").Append(ChangeRecordCell(row.ChangeRecord))
                .Append(" |\n");
        }
        sb.Append('\n');
    }

    private static string MappedHeading(StructurePair pair) => pair.RenameKind switch
    {
        RenameKind.Confirmed => $"{pair.DisplayName} (renamed from {pair.OldName})",
        RenameKind.Suspected => $"{pair.DisplayName} (suspected rename from {pair.OldName})",
        _ => pair.DisplayName,
    };

    private static string SummaryText(ElementRow row)
    {
        string summary = row.Summary;
        if (row.Flags.Renamed == RenameKind.Suspected)
        {
            summary = summary.Length == 0 ? "⚠ suspected" : $"{summary} ⚠ suspected";
        }
        return summary;
    }

    private static string Flag(bool value) => value ? "Y" : string.Empty;

    /// <summary>Trims a DB <c>ProcessDate</c> timestamp to its date portion for the header.</summary>
    private static string BuiltDate(string? built)
    {
        if (string.IsNullOrEmpty(built))
        {
            return "n/a";
        }
        int cut = built.IndexOfAny([' ', 'T']);
        return cut < 0 ? built : built[..cut];
    }

    private static string RenamedCell(RenameKind kind) => kind switch
    {
        RenameKind.Confirmed => "Y",
        RenameKind.Suspected => "Y?",
        _ => string.Empty,
    };

    private static string ChangeRecordCell(ElementChangeRecord? record)
    {
        if (record is null || (record.TicketKeys.Count == 0 && record.CommitShas.Count == 0))
        {
            return "—";
        }
        if (record.TicketKeys.Count > 0)
        {
            return string.Join(", ", record.TicketKeys.Select(TicketLink));
        }
        return string.Join(", ", record.CommitShas.Select(CommitLink));
    }

    /// <summary>A <c>FHIR-N</c> ticket rendered as a link to its Jira browse page.</summary>
    private static string TicketLink(string key) => $"[{key}](https://jira.hl7.org/browse/{key})";

    /// <summary>A commit short-hash rendered as a monospaced link to its GitHub commit.</summary>
    private static string CommitLink(string sha) => $"[`{sha}`](https://github.com/HL7/fhir/commit/{sha})";

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }
        return value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
