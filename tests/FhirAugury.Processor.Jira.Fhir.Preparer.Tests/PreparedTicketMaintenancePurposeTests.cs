using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketMaintenancePurposeTests
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
            PreparedTicketGroupingMaintenanceService
                .IsGroupingMaintenanceRun(grouping));
        Assert.False(
            PreparedTicketGroupingMaintenanceService
                .IsGroupingMaintenanceRun(ordinaryDatabaseOnly));
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
