using FhirAugury.Processing.Common.Configuration;

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
}
