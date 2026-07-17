using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Diff;

/// <summary>
/// The six per-element change flags. <see cref="Renamed"/> is tri-state (reusing
/// <see cref="RenameKind"/>) so a suspected element rename renders <c>Y?</c>; the other
/// five are plain booleans rendered <c>Y</c>/blank.
/// </summary>
internal readonly record struct ElementFlags(
    bool Added,
    bool Removed,
    RenameKind Renamed,
    bool Cardinality,
    bool Type,
    bool Target)
{
    /// <summary>True when at least one flag is set — the emit gate (decision #7).</summary>
    public bool Any =>
        Added || Removed || Renamed != RenameKind.None || Cardinality || Type || Target;
}

/// <summary>
/// Attribution for one element change: the Jira tickets and commits that produced it.
/// Populated in Phases 5–6; null until then (rendered as <c>—</c>).
/// </summary>
internal sealed record ElementChangeRecord(
    IReadOnlyList<string> TicketKeys,
    IReadOnlyList<string> CommitShas);

/// <summary>
/// One emitted element-diff row. <see cref="SourcePath"/> is the earlier raw path (null
/// when Added); <see cref="TargetPath"/> is the later raw path (null when Removed). A row
/// is emitted only when <see cref="ElementFlags.Any"/> is true.
/// </summary>
internal sealed record ElementRow(
    string? SourcePath,
    string? TargetPath,
    ElementFlags Flags,
    string Summary)
{
    /// <summary>Attribution, filled by Phases 5–6.</summary>
    public ElementChangeRecord? ChangeRecord { get; init; }

    /// <summary>Path used to order rows within a structure (target preferred, else source).</summary>
    public string SortPath => TargetPath ?? SourcePath ?? string.Empty;
}
