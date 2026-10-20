using FhirAugury.Server.Terminology.Configuration;
using FhirPkg;
using FhirPkg.Indexing;
using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Server.Terminology.Ingestion;

/// <summary>
/// Wraps <see cref="IFhirPackageManager"/> to acquire a configured THO
/// package and stream its CodeSystem / ValueSet resources back to the
/// pipeline as parsed Firely POCOs.
/// </summary>
/// <remarks>
/// <para>
/// The plan's "no custom HTTP / tarball / gzip" constraint is enforced
/// here: this class talks only to <c>fhir-pkg-lib</c>. The SDK owns
/// the registry chain (with <c>packages.fhir.org</c> + npm + HL7
/// fallback), checksum verification, cache layout, and version
/// resolution for <c>"latest"</c> and dist-tags.
/// </para>
/// <para>
/// For each indexed CodeSystem / ValueSet entry the wrapper reads the
/// raw JSON off disk from <see cref="PackageRecord.ContentPath"/> and
/// hands it to the version-specific
/// <see cref="TerminologyResourceParser"/>. Entries that fail to parse
/// are logged and skipped — one corrupt resource must not abort an
/// entire package ingest.
/// </para>
/// </remarks>
public class FhirPackageSource
{
    private readonly IFhirPackageManager _packages;
    private readonly TerminologyResourceParser _parser;
    private readonly ILogger<FhirPackageSource> _logger;

    public FhirPackageSource(
        IFhirPackageManager packages,
        TerminologyResourceParser parser,
        ILogger<FhirPackageSource> logger)
    {
        _packages = packages;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>
    /// Resolves + downloads (if missing) a single configured package and
    /// returns a snapshot whose <see cref="PackageIngestSnapshot.Resources"/>
    /// stream yields one <see cref="TerminologyResource"/> per
    /// CodeSystem / ValueSet in the package.
    /// </summary>
    public async Task<PackageIngestSnapshot> AcquireAsync(PackageOptions pkg, CancellationToken ct)
    {
        if (!FhirMajorVersionParser.TryParse(pkg.FhirVersion, out FhirMajorVersion version))
        {
            throw new InvalidOperationException(
                $"Package '{pkg.PackageId}' has unsupported FhirVersion '{pkg.FhirVersion}'.");
        }

        string directive = $"{pkg.PackageId}#{pkg.VersionTag}";
        _logger.LogInformation("Resolving FHIR package {Directive}", directive);

        PackageRecord? record = await _packages.InstallAsync(directive, new InstallOptions
        {
            IncludeDependencies = false,
            AllowPreRelease = false,
        }, ct).ConfigureAwait(false);

        if (record is null)
        {
            throw new InvalidOperationException(
                $"fhir-pkg-lib returned no PackageRecord for '{directive}'.");
        }

        // PackageReference is a value-type record struct; it cannot be
        // null but Name/Version may be empty if the SDK fell back to the
        // manifest. Manifest itself is reliably populated post-install.
        string? manifestVersion = record.Manifest?.Version;
        string resolvedVersion = !string.IsNullOrWhiteSpace(record.Reference.Version)
            ? record.Reference.Version
            : !string.IsNullOrWhiteSpace(manifestVersion) ? manifestVersion : pkg.VersionTag;

        _logger.LogInformation(
            "Resolved {Directive} → {PackageId}@{ResolvedVersion} at {Path}",
            directive, pkg.PackageId, resolvedVersion, record.ContentPath);

        return new PackageIngestSnapshot
        {
            PackageId = pkg.PackageId,
            RequestedTag = pkg.VersionTag,
            ResolvedVersion = resolvedVersion,
            FhirVersion = version,
            Resources = EnumerateAsync(record, version, ct),
        };
    }

    private async IAsyncEnumerable<TerminologyResource> EnumerateAsync(
        PackageRecord record,
        FhirMajorVersion version,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string pkgName = record.Reference.Name;
        IReadOnlyList<ResourceIndexEntry> files = record.Index?.Files
            ?? (IReadOnlyList<ResourceIndexEntry>)Array.Empty<ResourceIndexEntry>();

        foreach (ResourceIndexEntry entry in files)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.ResourceType is not ("CodeSystem" or "ValueSet")) continue;
            if (string.IsNullOrWhiteSpace(entry.Filename)) continue;

            string path = Path.Combine(record.ContentPath, entry.Filename);
            if (!File.Exists(path))
            {
                _logger.LogWarning(
                    "Package {PackageId}: indexed file {Filename} missing on disk",
                    pkgName, entry.Filename);
                continue;
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Package {PackageId}: failed to read {Filename}",
                    pkgName, entry.Filename);
                continue;
            }

            Hl7.Fhir.Model.Resource? parsed;
            try
            {
                parsed = _parser.TryParse(json, version);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Package {PackageId}: failed to parse {Filename}",
                    pkgName, entry.Filename);
                continue;
            }

            if (parsed is null)
            {
                _logger.LogDebug(
                    "Package {PackageId}: {Filename} did not parse to a CodeSystem/ValueSet, skipping",
                    pkgName, entry.Filename);
                continue;
            }

            yield return new TerminologyResource(entry.Filename, parsed, json);
        }
    }

    /// <summary>
    /// Applies the configured override / extension registries on the
    /// SDK options. Called from <c>Program.cs</c> when wiring the
    /// FhirPkg DI service.
    /// </summary>
    public static void ApplyOptions(FhirPackageManagerOptions options, TerminologyServiceOptions terminologyOpts)
    {
        options.CachePath = Path.GetFullPath(terminologyOpts.CachePath);

        foreach (RegistryEndpointOptions reg in terminologyOpts.Registries)
        {
            if (string.IsNullOrWhiteSpace(reg.Url)) continue;
            options.Registries.Add(new RegistryEndpoint
            {
                Url = reg.Url,
                Type = RegistryType.FhirNpm,
            });
        }
    }
}
