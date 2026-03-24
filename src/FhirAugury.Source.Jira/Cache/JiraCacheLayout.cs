namespace FhirAugury.Source.Jira.Cache;

/// <summary>Constants for Jira cache file layout and naming conventions.</summary>
public static class JiraCacheLayout
{
    /// <summary>The source name used as the cache subdirectory (legacy, kept for metadata compatibility).</summary>
    public const string SourceName = "jira";

    /// <summary>Cache source subdirectory for XML RSS exports (cookie auth).</summary>
    public const string XmlSource = "xml";

    /// <summary>Cache source subdirectory for JSON REST API responses (apitoken auth).</summary>
    public const string JsonSource = "json";

    /// <summary>Extension for JSON API responses.</summary>
    public const string JsonExtension = "json";

    /// <summary>Extension for XML RSS exports.</summary>
    public const string XmlExtension = "xml";

    /// <summary>Metadata file name for Jira cache state.</summary>
    public const string MetadataFileName = "_meta_jira.json";

    /// <summary>Subdirectory for JIRA-Spec-Artifacts clone.</summary>
    public const string SpecArtifactsSubDir = "jira-spec-artifacts";

    /// <summary>Maximum results per XML export request.</summary>
    public const int XmlMaxResults = 1000;

    /// <summary>Default start date for full XML downloads.</summary>
    public static readonly DateOnly DefaultFullSyncStartDate = new(2015, 1, 1);
}
