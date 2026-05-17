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
    bool Force,
    bool Help);
