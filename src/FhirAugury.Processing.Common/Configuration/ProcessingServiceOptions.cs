using FhirAugury.Common.Configuration;

namespace FhirAugury.Processing.Common.Configuration;

/// <summary>
/// Strongly typed configuration shared by Processing services.
/// </summary>
public class ProcessingServiceOptions
{
    public const string SectionName = "Processing";

    public string DatabasePath { get; set; } = "./data/processing.db";
    public string SyncSchedule { get; set; } = "00:05:00";
    public int MaxConcurrentProcessingThreads { get; set; } = 1;
    public bool StartProcessingOnStartup { get; set; } = true;
    public string? OrchestratorAddress { get; set; }
    public PortConfiguration Ports { get; set; } = new() { Http = 5170 };
    public string OrphanedInProgressThreshold { get; set; } = "00:10:00";

    /// <summary>
    /// Validates configuration. Returns human-readable errors; an empty sequence means valid.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        if (MaxConcurrentProcessingThreads < 1)
        {
            yield return "MaxConcurrentProcessingThreads must be greater than or equal to 1.";
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            yield return "DatabasePath must be non-empty.";
        }

        if (!TimeSpan.TryParse(SyncSchedule, out TimeSpan syncSchedule) || syncSchedule <= TimeSpan.Zero)
        {
            yield return "SyncSchedule must be a positive TimeSpan string.";
        }

        if (!TimeSpan.TryParse(OrphanedInProgressThreshold, out TimeSpan orphanedThreshold) || orphanedThreshold <= TimeSpan.Zero)
        {
            yield return "OrphanedInProgressThreshold must be a positive TimeSpan string.";
        }

        if (Ports.Http <= 0 || Ports.Http > 65535)
        {
            yield return "Ports.Http must be between 1 and 65535.";
        }
    }
}
