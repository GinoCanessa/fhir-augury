using Hl7.Fhir.Model;
using System.Runtime.CompilerServices;

namespace FhirAugury.Server.Terminology.Ingestion;

/// <summary>
/// One enumeration of one downloaded FHIR NPM package.
/// </summary>
/// <remarks>
/// Carries the inputs the ingestion pipeline needs to decide
/// idempotent / replace / no-op behavior (<see cref="RequestedTag"/>
/// vs <see cref="ResolvedVersion"/>), the FHIR major version the
/// pipeline should parse the resources under
/// (<see cref="FhirVersion"/>), and the per-resource stream from
/// fhir-pkg-lib's resource index.
/// </remarks>
public sealed class PackageIngestSnapshot
{
    public required string PackageId { get; init; }

    /// <summary>Directive the operator configured (e.g. <c>"latest"</c>).</summary>
    public required string RequestedTag { get; init; }

    /// <summary>Concrete semver the SDK resolved <see cref="RequestedTag"/> to.</summary>
    public required string ResolvedVersion { get; init; }

    public required FhirMajorVersion FhirVersion { get; init; }

    /// <summary>
    /// Streams the package's CodeSystem and ValueSet resources as
    /// pre-parsed Firely POCOs, one at a time.
    /// </summary>
    public required IAsyncEnumerable<TerminologyResource> Resources { get; init; }
}

/// <summary>
/// A single Firely-parsed terminology resource (CodeSystem or ValueSet)
/// streamed out of a package by <see cref="FhirPackageSource"/>.
/// </summary>
/// <param name="Filename">
/// Source filename inside the npm package (e.g. <c>CodeSystem-foo.json</c>).
/// Stored for diagnostics; ingestion does not use it as a key.
/// </param>
/// <param name="Resource">
/// The parsed FHIR POCO. <c>CodeSystem</c> and <c>ValueSet</c> live in the
/// shared <c>Hl7.Fhir.Conformance</c> assembly and have a single
/// representation that works across both R4 and R5; only the parser
/// differs by version.
/// </param>
/// <param name="Json">
/// Original on-disk JSON. Passed through verbatim so it can be stored in
/// <c>terminology_artifacts.Json</c> without re-serialization (which
/// would require resolving the FhirJsonSerializer / extern-alias mess).
/// </param>
public readonly record struct TerminologyResource(string Filename, Resource Resource, string Json);

/// <summary>
/// Async-enumerable adapter so the pipeline can <c>await foreach</c>
/// over a synchronous yield-return source.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsync<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (T item in source)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
