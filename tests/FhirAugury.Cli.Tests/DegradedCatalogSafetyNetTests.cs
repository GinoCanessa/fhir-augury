using FhirAugury.Cli.Safety;

namespace FhirAugury.Cli.Tests;

public class DegradedCatalogSafetyNetTests
{
    [Fact]
    public void HealthyCatalog_AlwaysProceeds()
    {
        Assert.Equal(
            DegradedCatalogSafetyOutcome.Proceed,
            DegradedCatalogSafetyNet.Evaluate(
                catalogJoinDegraded: false,
                selectorIsAll: true,
                replaceModeIsWipeFirst: true,
                allowDegradedWipeAll: false));
    }

    [Fact]
    public void NarrowSelector_Proceeds()
    {
        Assert.Equal(
            DegradedCatalogSafetyOutcome.Proceed,
            DegradedCatalogSafetyNet.Evaluate(
                catalogJoinDegraded: true,
                selectorIsAll: false,
                replaceModeIsWipeFirst: true,
                allowDegradedWipeAll: false));
    }

    [Fact]
    public void NonWipeFirst_Proceeds()
    {
        Assert.Equal(
            DegradedCatalogSafetyOutcome.Proceed,
            DegradedCatalogSafetyNet.Evaluate(
                catalogJoinDegraded: true,
                selectorIsAll: true,
                replaceModeIsWipeFirst: false,
                allowDegradedWipeAll: false));
    }

    [Fact]
    public void AllWipeFirstDegraded_WithoutOverride_RequiresOverride()
    {
        Assert.Equal(
            DegradedCatalogSafetyOutcome.RequiresOverride,
            DegradedCatalogSafetyNet.Evaluate(
                catalogJoinDegraded: true,
                selectorIsAll: true,
                replaceModeIsWipeFirst: true,
                allowDegradedWipeAll: false));
    }

    [Fact]
    public void AllWipeFirstDegraded_WithOverride_Proceeds()
    {
        Assert.Equal(
            DegradedCatalogSafetyOutcome.Proceed,
            DegradedCatalogSafetyNet.Evaluate(
                catalogJoinDegraded: true,
                selectorIsAll: true,
                replaceModeIsWipeFirst: true,
                allowDegradedWipeAll: true));
    }

    [Fact]
    public void BuildRequiresOverrideError_MentionsOverrideField()
    {
        object payload = DegradedCatalogSafetyNet.BuildRequiresOverrideError();
        string serialized = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Contains("allowDegradedWipeAll", serialized);
        Assert.Contains("requires-override", serialized);
    }
}
