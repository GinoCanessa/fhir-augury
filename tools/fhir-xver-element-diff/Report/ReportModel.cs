using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Report;

/// <summary>
/// The resolved header metadata rendered at the top of every report: generation time,
/// both package versions + build dates, the git window actually used, the clone HEAD, and
/// (for R5→R6) the ballot4-gap note. All values are pre-resolved so the writer is pure.
/// </summary>
internal sealed record ReportHeader(
    DateTimeOffset GeneratedUtc,
    string EarlierLabel,
    string LaterLabel,
    string EarlierVersion,
    string LaterVersion,
    string? EarlierBuilt,
    string? LaterBuilt,
    string SinceSha,
    string UntilSha,
    string? CloneHead,
    bool AttributionEnabled,
    string? HeaderNote);

/// <summary>A mapped structure (possibly renamed) plus its emitted element-diff rows.</summary>
internal sealed record MappedStructureReport(StructurePair Pair, IReadOnlyList<ElementRow> Rows);

/// <summary>A removed or added structure plus its all-Removed / all-Added element rows.</summary>
internal sealed record StructureElementReport(StructureModel Structure, IReadOnlyList<ElementRow> Rows);

/// <summary>
/// The full data model for one increment's report: header + the three classified buckets,
/// each already reduced to the rows the writer renders. Built by <see cref="ReportBuilder"/>.
/// </summary>
internal sealed record ReportModel(
    IncrementDefinition Increment,
    ReportHeader Header,
    IReadOnlyList<MappedStructureReport> Mapped,
    IReadOnlyList<StructureElementReport> Removed,
    IReadOnlyList<StructureElementReport> Added);
