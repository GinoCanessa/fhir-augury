namespace FhirAugury.Tools.PreparerSite;

internal sealed record CliOptions(
    string? DbPath,
    string? OutPath,
    string Title,
    string? FilterSpec,
    string? FilterProject,
    string? FilterWorkGroup,
    string? JiraSourceUrl,
    string? JiraSourceDbPath,
    string? OrchestratorAddress,
    bool NoHydrate,
    bool Force,
    bool BackfillSpec,
    bool Help);
