using FhirAugury.Common.WorkGroups;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Pure decision logic for the "configured HL7 work-group refresh produced
/// zero rows" data-integrity guard, extracted from
/// <see cref="GitHubIngestionPipeline.EnsureWorkGroupsRefreshedAsync"/> so the
/// predicate and its throw behavior are unit-testable without standing up the
/// full ingestion pipeline.
/// </summary>
internal static class WorkGroupRefreshIntegrity
{
    /// <summary>
    /// A WG refresh is "configured" — and therefore expected to produce rows —
    /// when either a <see cref="WorkGroupSourceXmlOptions.LocalFile"/> or a
    /// <see cref="WorkGroupSourceXmlOptions.Url"/> is set. An unconfigured
    /// source (both null/blank) is host-friendly and never fails on empty.
    /// </summary>
    public static bool IsConfigured(WorkGroupSourceXmlOptions cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return !string.IsNullOrWhiteSpace(cfg.LocalFile)
            || !string.IsNullOrWhiteSpace(cfg.Url);
    }

    /// <summary>
    /// Throws <see cref="IngestionDataIntegrityException"/> when the refresh is
    /// configured yet materialized zero rows. No-ops for unconfigured sources
    /// or non-empty refreshes.
    /// </summary>
    public static void ThrowIfConfiguredButEmpty(WorkGroupSourceXmlOptions cfg, int total, string? xmlPath)
    {
        if (IsConfigured(cfg) && total == 0)
        {
            throw new IngestionDataIntegrityException(
                "HL7 work-group refresh is configured (Hl7WorkGroupSourceXml) but produced zero rows; " +
                $"hl7_workgroups would be empty (xml={xmlPath ?? "<none>"}).");
        }
    }
}
