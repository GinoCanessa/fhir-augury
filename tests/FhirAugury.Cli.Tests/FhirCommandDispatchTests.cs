using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Tests;

public class FhirCommandDispatchTests
{
    [Theory]
    [InlineData("fhir-releases")]
    [InlineData("fhir-resources")]
    [InlineData("fhir-structure")]
    [InlineData("fhir-datatypes")]
    [InlineData("fhir-profiles")]
    [InlineData("fhir-codesystems")]
    [InlineData("fhir-codesystem-lookup")]
    [InlineData("fhir-valuesets")]
    [InlineData("fhir-valueset-expand")]
    [InlineData("fhir-operations")]
    [InlineData("fhir-searchparameters")]
    [InlineData("fhir-resolve")]
    [InlineData("fhir-search")]
    public void KnownCommands_IncludesFhirCommand(string command)
    {
        Assert.Contains(command, CommandDispatcher.KnownCommands);
    }

    // The following exercise the parse → dispatch → FhirHandler path: the error
    // messages are produced by FhirHandler.Require before any HTTP call, proving
    // the command deserialized to FhirRequest and routed to the FHIR handler.

    [Fact]
    public async Task FhirStructure_MissingName_RoutesToFhirHandlerAndErrors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-structure","release":"R5"}""");

        Assert.False(env.Success);
        Assert.Contains("name", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FhirCodesystemLookup_MissingSystem_Errors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-codesystem-lookup","release":"R5","code":"final"}""");

        Assert.False(env.Success);
        Assert.Contains("system", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FhirResolve_MissingUrl_Errors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-resolve","release":"R5"}""");

        Assert.False(env.Success);
        Assert.Contains("url", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FhirSearch_MissingQuery_Errors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-search","release":"R5"}""");

        Assert.False(env.Success);
        Assert.Contains("query", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FhirCommands_DeserializeToFhirRequest()
    {
        FhirRequest? request = System.Text.Json.JsonSerializer.Deserialize<FhirRequest>(
            """{"command":"fhir-structure","release":"R5","name":"Observation"}""");

        Assert.NotNull(request);
        Assert.Equal("R5", request!.Release);
        Assert.Equal("Observation", request.Name);
    }
}
