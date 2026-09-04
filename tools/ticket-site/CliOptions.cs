namespace FhirAugury.Tools.TicketSite;

internal sealed record CliOptions(
    string? PreparerDbPath,
    bool PreparerDbSupplied,
    string? PlannerDbPath,
    bool PlannerDbSupplied,
    string? PreparerSnapshotPath,
    bool PreparerSnapshotSupplied,
    string? PlannerSnapshotPath,
    bool PlannerSnapshotSupplied,
    string? SnapshotDescriptorPath,
    string? OutPath,
    string Title,
    string? FilterSpec,
    string? FilterProject,
    string? FilterWorkGroup,
    string? JiraSourceUrl,
    string? JiraSourceDbPath,
    bool Force,
    bool Help)
{
    public bool SnapshotMode =>
        PreparerSnapshotSupplied || PlannerSnapshotSupplied;
}
