namespace FhirAugury.Source.Jira.Configuration;

/// <summary>Configuration for a single Jira project to ingest.</summary>
public class JiraProjectConfig
{
    /// <summary>The Jira project key (e.g., "FHIR", "FHIR-I", "CDA").</summary>
    public required string Key { get; set; }

    /// <summary>
    /// Optional JQL override for this project. If null, uses
    /// <c>project = "KEY"</c>.
    /// </summary>
    public string? Jql { get; set; }

    /// <summary>
    /// Whether this project is enabled for ingestion. Allows disabling a project
    /// without removing it from config.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
