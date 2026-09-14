using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Planner.Processing;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannedTicketMaintenancePurposeTests
{
    [Fact]
    public void GroupingMaintenanceClassificationUsesPurposeNotDatabaseOnly()
    {
        AuthoringRunRecord grouping = CreateRun(
            AuthoringRunPurposeValues.GroupingMaintenance,
            databaseOnly: false);
        AuthoringRunRecord ordinaryDatabaseOnly = CreateRun(
            AuthoringRunPurposeValues.Authoring,
            databaseOnly: true);

        Assert.True(
            PlannedTicketGroupingMaintenanceService
                .IsGroupingMaintenanceRun(grouping));
        Assert.False(
            PlannedTicketGroupingMaintenanceService
                .IsGroupingMaintenanceRun(ordinaryDatabaseOnly));
        Assert.False(
            PlannedTicketRunPostProcessor.RequiresWorkGroupCatalog(
                grouping.Purpose));
        Assert.True(
            PlannedTicketRunPostProcessor.RequiresWorkGroupCatalog(
                ordinaryDatabaseOnly.Purpose));
    }

    private static AuthoringRunRecord CreateRun(
        string purpose,
        bool databaseOnly)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ProcessorKind = "jira-fhir",
            Status = AuthoringStatusValues.Runs.Running,
            Purpose = purpose,
            DatabaseOnly = databaseOnly,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
