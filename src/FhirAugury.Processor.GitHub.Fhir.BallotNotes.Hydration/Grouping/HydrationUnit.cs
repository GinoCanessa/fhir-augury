namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

/// <summary>
/// One hydration unit: an artifact, a narrative page, or the consolidated
/// datatypes surface, together with the window-changed paths that landed in it.
/// </summary>
public sealed record HydrationUnit
{
    /// <summary>Unit kind: <c>Artifact</c>, <c>Page</c>, or <c>DataType</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Unit name (e.g. <c>observation</c>, <c>security</c>, <c>datatypes</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Window-changed paths classified into this unit (clone-root-relative).</summary>
    public required IReadOnlyList<string> ChangedPaths { get; init; }
}
