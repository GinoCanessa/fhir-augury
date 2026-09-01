using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;

/// <summary>
/// Strongly typed Processing options for the Jira FHIR preparer.
/// </summary>
public sealed class PreparerServiceOptions : ProcessingServiceOptions
{
    public new const string SectionName = ProcessingServiceOptions.SectionName;

    public PreparerServiceOptions()
    {
        DatabasePath = "./data/processor.jira.fhir.preparer.db";
        SyncSchedule = "00:05:00";
        MaxConcurrentProcessingThreads = 4;
        StartProcessingOnStartup = true;
        Ports.Http = 5171;
        OrchestratorAddress = "http://localhost:5150";
    }

    public HydrationOptions Hydration { get; set; } = new();

    public new IEnumerable<string> Validate()
    {
        foreach (string error in base.Validate())
        {
            yield return error;
        }

        foreach (string error in Hydration.Validate())
        {
            yield return error;
        }
    }
}
