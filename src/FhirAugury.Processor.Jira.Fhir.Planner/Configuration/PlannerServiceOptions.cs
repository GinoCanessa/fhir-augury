using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Configuration;

/// <summary>
/// Strongly typed Processing options for the Jira FHIR planner. Carries
/// the shared <see cref="HydrationOptions"/> block so the planner's
/// startup hosted service can read <c>BackfillOnStartup</c> through the
/// same idiom the preparer uses.
/// </summary>
public sealed class PlannerServiceOptions : ProcessingServiceOptions
{
    public new const string SectionName = ProcessingServiceOptions.SectionName;

    public PlannerServiceOptions()
    {
        DatabasePath = "./data/processor.jira.fhir.planner.db";
        SyncSchedule = "00:05:00";
        MaxConcurrentProcessingThreads = 4;
        StartProcessingOnStartup = true;
        Ports.Http = 5172;
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
