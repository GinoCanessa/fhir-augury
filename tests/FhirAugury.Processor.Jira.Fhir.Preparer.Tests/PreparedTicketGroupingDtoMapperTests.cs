using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public class PreparedTicketGroupingDtoMapperTests
{
    [Fact]
    public void ToPayload_NormalisesWorkGroupClean_FromDisplayName()
    {
        PreparedTicketGroupingPutRequest request = new(
            WorkGroupDisplay: "Orders & Observations",
            Topics: []);

        PreparedTicketGroupingPayload payload = PreparedTicketGroupingDtoMapper.ToPayload(
            workGroupClean: "Orders & Observations",
            specification: "FHIR Core",
            type: "Change Request",
            request);

        Assert.Equal("OrdersAndObservations", payload.WorkGroupClean);
        Assert.Equal("Orders & Observations", payload.WorkGroupDisplay);
    }

    [Fact]
    public void ToPayload_AlreadyCleanedSlug_RoundTripsUnchanged()
    {
        PreparedTicketGroupingPutRequest request = new(
            WorkGroupDisplay: "Orders & Observations",
            Topics: []);

        PreparedTicketGroupingPayload payload = PreparedTicketGroupingDtoMapper.ToPayload(
            workGroupClean: "OrdersAndObservations",
            specification: "FHIR Core",
            type: "Change Request",
            request);

        Assert.Equal("OrdersAndObservations", payload.WorkGroupClean);
    }

    [Fact]
    public void ToPayload_LegacyPreparerSlug_NormalisedToCanonical()
    {
        // 'OrdersandObservations' (the legacy REPLACE-form slug) is no
        // longer canonical. The cleaner re-derives the canonical form.
        PreparedTicketGroupingPutRequest request = new(
            WorkGroupDisplay: "Orders and Observations",
            Topics: []);

        PreparedTicketGroupingPayload payload = PreparedTicketGroupingDtoMapper.ToPayload(
            workGroupClean: "Orders and Observations",
            specification: "FHIR Core",
            type: "Change Request",
            request);

        Assert.Equal("OrdersAndObservations", payload.WorkGroupClean);
    }
}
