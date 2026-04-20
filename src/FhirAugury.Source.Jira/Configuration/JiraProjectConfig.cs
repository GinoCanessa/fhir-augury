namespace FhirAugury.Source.Jira.Configuration;

/// <summary>Configuration for a single Jira project to ingest.</summary>
public class JiraProjectConfig
{
    /// <summary>Upper bound for <see cref="DownloadWindowDays"/>.</summary>
    public const int DownloadWindowDaysMax = 400;

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

    /// <summary>
    /// Size, in days, of each cookie-auth XML download window. Wider windows
    /// reduce HTTP request counts for quiet projects but risk hitting
    /// <c>JiraCacheLayout.XmlMaxResults</c> on busy projects. Must be in the
    /// inclusive range <c>[1, <see cref="DownloadWindowDaysMax"/>]</c>. Has no
    /// effect on REST/JSON (apitoken/basic) auth modes.
    /// </summary>
    public int DownloadWindowDays { get; set; } = 1;

    /// <summary>
    /// Optional project-specific start date for full backfills. When set,
    /// overrides <c>JiraCacheLayout.DefaultFullSyncStartDate</c> but only for
    /// the initial full sync; subsequent incremental syncs resume from the
    /// recorded sync cursor. Must not be in the future.
    /// </summary>
    public DateOnly? StartDate { get; set; }

    /// <summary>
    /// Baseline ranking value for this project (0–10, default 5). Scores from
    /// this project are multiplied by <c>BaselineValue / 5.0</c>, so the
    /// default is neutral. Use lower values (e.g. <c>1</c>) to keep tickets
    /// searchable but down-rank them (e.g. ballot vote tracking); use
    /// <c>0</c> to suppress entries from ranked output entirely. Lookups by
    /// key, project-scoped queries, and cross-reference endpoints remain
    /// unaffected. Only seeded into the persisted record on first insert;
    /// edits via <c>PUT /api/v1/projects/{key}</c> survive subsequent syncs.
    /// </summary>
    public int BaselineValue { get; set; } = 5;
}
