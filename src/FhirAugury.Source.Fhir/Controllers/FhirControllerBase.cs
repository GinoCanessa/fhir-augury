using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

/// <summary>
/// Base controller for release-scoped endpoints. Resolves the <c>{release}</c>
/// route token (a blank token resolves to the default release), returns 404 for
/// unknown releases / artifacts, and wraps results in a
/// <see cref="FhirReleaseResponse{T}"/> that echoes the resolved release.
/// </summary>
public abstract class FhirControllerBase(FhirReleaseResolver resolver) : ControllerBase
{
    /// <summary>The release resolver, shared with derived controllers.</summary>
    protected FhirReleaseResolver Resolver { get; } = resolver;

    /// <summary>Resolves the release and wraps a never-null result (e.g. a list).</summary>
    protected IActionResult ResolvedList<T>(string release, Func<int, T> produce)
    {
        if (!Resolver.TryResolve(release, out int packageKey, out ReleaseInfo? info, out string? error))
        {
            return NotFound(new { error });
        }
        return Ok(new FhirReleaseResponse<T>(info!, produce(packageKey)));
    }

    /// <summary>Resolves the release and wraps a possibly-null result (404 when null).</summary>
    protected IActionResult ResolvedItem<T>(string release, Func<int, T?> produce, string notFound)
        where T : class
    {
        if (!Resolver.TryResolve(release, out int packageKey, out ReleaseInfo? info, out string? error))
        {
            return NotFound(new { error });
        }
        T? result = produce(packageKey);
        return result is null
            ? NotFound(new { error = notFound })
            : Ok(new FhirReleaseResponse<T>(info!, result));
    }
}
