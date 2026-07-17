using FhirAugury.Common;

namespace FhirAugury.Source.GitHub.Cache;

/// <summary>Constants for GitHub cache file layout and naming conventions.</summary>
public static class GitHubCacheLayout
{
    /// <summary>The source name used as the cache subdirectory.</summary>
    public const string SourceName = SourceSystems.GitHub;

    /// <summary>Extension for JSON API responses.</summary>
    public const string JsonExtension = "json";

    /// <summary>Metadata file name for GitHub cache state.</summary>
    public const string MetadataFileName = "_meta_github.json";

    /// <summary>Subdirectory for cloned repositories.</summary>
    public const string ReposSubDir = "repos";

    /// <summary>Subdirectory name for the local git clone within a repo cache dir.</summary>
    public const string CloneSubDir = "clone";

    /// <summary>
    /// Sub-path for miscellaneous support files (e.g. the HL7 work-group
    /// CodeSystem XML) materialized into <c>cache/github/_support/</c>.
    /// The leading underscore avoids collision with repository owner names.
    /// </summary>
    public const string SupportPrefix = "_support";
}
