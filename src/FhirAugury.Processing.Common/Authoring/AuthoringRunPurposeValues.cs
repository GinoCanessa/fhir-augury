namespace FhirAugury.Processing.Common.Authoring;

public static class AuthoringRunPurposeValues
{
    public const string Authoring = "authoring";
    public const string InitialRevalidation = "initial-revalidation";
    public const string GroupingMaintenance = "grouping-maintenance";
    public const string PublicationRefresh = "publication-refresh";

    public static bool IsValid(string? purpose)
        => purpose is
            Authoring or
            InitialRevalidation or
            GroupingMaintenance or
            PublicationRefresh;

    public static bool IsMaintenance(string? purpose)
        => purpose is GroupingMaintenance or PublicationRefresh;

    public static void EnsureValid(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (!IsValid(purpose))
        {
            throw new ArgumentException(
                $"Unknown authoring run purpose '{purpose}'.",
                nameof(purpose));
        }
    }
}
