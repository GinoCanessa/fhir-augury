namespace FhirAugury.Tools.TicketSite;

internal sealed record CliOptions(
    string? PreparerDbPath,
    bool PreparerDbSupplied,
    string? PlannerDbPath,
    bool PlannerDbSupplied,
    string? OutPath,
    string Title,
    string? FilterSpec,
    string? FilterProject,
    string? FilterWorkGroup,
    string? JiraSourceUrl,
    string? JiraSourceDbPath,
    bool Force,
    bool Help);
