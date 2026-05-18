namespace FhirAugury.Cli.Safety;

/// <summary>
/// Result of <see cref="DegradedCatalogSafetyNet.Evaluate(bool, bool, bool, bool)"/>.
/// </summary>
public enum DegradedCatalogSafetyOutcome
{
    /// <summary>Proceed; the requested action does not match the
    /// degraded-catalog wipe-all combination, or the caller already
    /// supplied an explicit override.</summary>
    Proceed,

    /// <summary>The action matches the degraded-catalog wipe-all
    /// combination but the override field was not set. CLI callers must
    /// surface a hard error suggesting the override; interactive callers
    /// must prompt for confirmation.</summary>
    RequiresOverride,
}

/// <summary>
/// Implements D4 of the work-group selector unification feature: the
/// only combination of selector + replace-mode + catalog state that
/// requires an explicit "yes, I know" override is
/// <c>selector=all</c> + <c>replaceMode=wipe-first</c> when the
/// upstream <c>list-jira-workgroups</c> envelope reported
/// <c>catalogJoinDegraded=true</c>. Every other combination proceeds
/// with a single warning banner.
/// </summary>
public static class DegradedCatalogSafetyNet
{
    public const string OverrideFieldName = "allowDegradedWipeAll";

    public static DegradedCatalogSafetyOutcome Evaluate(
        bool catalogJoinDegraded,
        bool selectorIsAll,
        bool replaceModeIsWipeFirst,
        bool allowDegradedWipeAll)
    {
        if (!catalogJoinDegraded) return DegradedCatalogSafetyOutcome.Proceed;
        if (!selectorIsAll) return DegradedCatalogSafetyOutcome.Proceed;
        if (!replaceModeIsWipeFirst) return DegradedCatalogSafetyOutcome.Proceed;
        return allowDegradedWipeAll
            ? DegradedCatalogSafetyOutcome.Proceed
            : DegradedCatalogSafetyOutcome.RequiresOverride;
    }

    /// <summary>
    /// Returns a structured error payload suitable for surfacing back to
    /// the JSON-mode CLI caller when
    /// <see cref="Evaluate(bool, bool, bool, bool)"/> returns
    /// <see cref="DegradedCatalogSafetyOutcome.RequiresOverride"/>.
    /// </summary>
    public static object BuildRequiresOverrideError() => new
    {
        error = "requires-override",
        message =
            "The HL7 work-group catalog is degraded (catalogJoinDegraded=true) " +
            "and the requested action would wipe every work group. " +
            "Re-issue with " + OverrideFieldName + "=true to confirm, or " +
            "narrow the selector to a single work group, or re-run after " +
            "the catalog has been ingested.",
        overrideField = OverrideFieldName,
    };
}
