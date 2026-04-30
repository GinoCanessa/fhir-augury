using FhirAugury.Processing.Common.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Configuration;

/// <summary>
/// Strongly-typed Processing options for the Jira FHIR applier. The applier defaults to
/// single-threaded operation because each per-(ticket, repo) apply mutates a worktree on
/// disk and a baseline build can be expensive — operators raise this only when they
/// understand the disk and build cost.
/// </summary>
public sealed class ApplierServiceOptions : ProcessingServiceOptions
{
    public new const string SectionName = ProcessingServiceOptions.SectionName;

    public ApplierServiceOptions()
    {
        DatabasePath = "./data/processor.jira.fhir.applier.db";
        SyncSchedule = "00:05:00";
        MaxConcurrentProcessingThreads = 1;
        StartProcessingOnStartup = true;
        OrchestratorAddress = "http://localhost:5150";
        Ports.Http = 5173;
    }
}
