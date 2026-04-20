using FhirAugury.Common;

namespace FhirAugury.Source.Jira.Cache;

/// <summary>Constants for Jira cache file layout and naming conventions.</summary>
public static class JiraCacheLayout
{
    /// <summary>The source name used as the cache subdirectory (legacy, kept for metadata compatibility).</summary>
    public const string SourceName = SourceSystems.Jira;

    /// <summary>Key prefix for XML RSS exports (cookie auth) within the jira source.</summary>
    public const string XmlPrefix = "xml";

    /// <summary>Key prefix for JSON REST API responses (apitoken auth) within the jira source.</summary>
    public const string JsonPrefix = "json";

    /// <summary>Builds a cache key for an XML file by prepending the XML prefix.</summary>
    public static string XmlKey(string filename) => $"{XmlPrefix}/{filename}";

    /// <summary>Builds a cache key for a JSON file by prepending the JSON prefix.</summary>
    public static string JsonKey(string filename) => $"{JsonPrefix}/{filename}";

    /// <summary>Extension for JSON API responses.</summary>
    public const string JsonExtension = "json";

    /// <summary>Extension for XML RSS exports.</summary>
    public const string XmlExtension = "xml";

    /// <summary>Metadata file name for Jira cache state.</summary>
    public const string MetadataFileName = "_meta_jira.json";

    /// <summary>Maximum results per XML export request.</summary>
    public const int XmlMaxResults = 1000;

    /// <summary>Default start date for full XML downloads.</summary>
    public static readonly DateOnly DefaultFullSyncStartDate = new(2015, 1, 1);

    /// <summary>
    /// Builds a cache key for a project-scoped XML file.
    /// Example: ProjectXmlKey("FHIR", "DayOf_2026-02-24-000.xml") → "FHIR/xml/DayOf_2026-02-24-000.xml"
    /// </summary>
    public static string ProjectXmlKey(string project, string filename)
        => $"{project}/{XmlPrefix}/{filename}";

    /// <summary>
    /// Builds a cache key for a project-scoped JSON file.
    /// Example: ProjectJsonKey("FHIR-I", "DayOf_2026-02-24-000.json") → "FHIR-I/json/DayOf_2026-02-24-000.json"
    /// </summary>
    public static string ProjectJsonKey(string project, string filename)
        => $"{project}/{JsonPrefix}/{filename}";

    /// <summary>
    /// Returns the cache sub-path prefix for a project.
    /// Used with <c>cache.EnumerateKeys(SourceName, ProjectSubPath(project))</c>.
    /// </summary>
    public static string ProjectSubPath(string project) => project;

    /// <summary>Returns the XML sub-path prefix for a project.</summary>
    public static string ProjectXmlSubPath(string project)
        => $"{project}/{XmlPrefix}";

    /// <summary>Returns the JSON sub-path prefix for a project.</summary>
    public static string ProjectJsonSubPath(string project)
        => $"{project}/{JsonPrefix}";
}
