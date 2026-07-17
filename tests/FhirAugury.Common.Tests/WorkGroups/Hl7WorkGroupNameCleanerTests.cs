using FhirAugury.Common.WorkGroups;

namespace FhirAugury.Common.Tests.WorkGroups;

public class Hl7WorkGroupNameCleanerTests
{
    [Theory]
    [InlineData("FHIR Infrastructure", "FHIRInfrastructure")]
    [InlineData("Patient Care", "PatientCare")]
    [InlineData("Orders & Observations", "OrdersAndObservations")]
    [InlineData("Clinical Quality Information", "ClinicalQualityInformation")]
    [InlineData("CDS (Clinical Decision Support)", "CDSClinicalDecisionSupport")]
    [InlineData("International Patient Summary (IPS)", "InternationalPatientSummaryIPS")]
    [InlineData("   trailing & leading whitespace  ", "TrailingAndLeadingWhitespace")]
    public void Clean_MatchesSpecExamples(string input, string expected)
    {
        Assert.Equal(expected, Hl7WorkGroupNameCleaner.Clean(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clean_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, Hl7WorkGroupNameCleaner.Clean(input));
    }
}
