namespace FhirAugury.Source.Jira.Cache;

/// <summary>Constants for Jira cache file layout and naming conventions.</summary>
public static class JiraCacheLayout
{
    /// <summary>The source name used as the cache subdirectory.</summary>
    public const string SourceName = "jira";

    /// <summary>Extension for JSON API responses.</summary>
    public const string JsonExtension = "json";

    /// <summary>Extension for XML RSS exports.</summary>
    public const string XmlExtension = "xml";

    /// <summary>Metadata file name for Jira cache state.</summary>
    public const string MetadataFileName = "_meta_jira.json";

    /// <summary>Subdirectory for JIRA-Spec-Artifacts clone.</summary>
    public const string SpecArtifactsSubDir = "jira-spec-artifacts";
}
