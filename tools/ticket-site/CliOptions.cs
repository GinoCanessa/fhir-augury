namespace FhirAugury.Tools.TicketSite;

internal sealed record CliOptions(
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
    bool Force,
    bool Help);
