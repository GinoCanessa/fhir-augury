using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Dispatch.Handlers;
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
    [InlineData("fhir-interfaces")]
    [InlineData("fhir-elements")]
    [InlineData("fhir-element")]
    [InlineData("fhir-codesystems")]
    [InlineData("fhir-codesystem")]
    [InlineData("fhir-codesystem-lookup")]
    [InlineData("fhir-codesystem-concepts")]
    [InlineData("fhir-valuesets")]
    [InlineData("fhir-valueset-expand")]
    [InlineData("fhir-valueset-lookup")]
    [InlineData("fhir-valueset-bindings")]
    [InlineData("fhir-operations")]
    [InlineData("fhir-operation")]
    [InlineData("fhir-searchparameters")]
    [InlineData("fhir-searchparameter")]
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

    // ── New verb Require / BuildPath coverage (Phase 4) ──────────────────

    [Fact]
    public async Task FhirCodesystem_MissingSystem_Errors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-codesystem","release":"R5"}""");

        Assert.False(env.Success);
        Assert.Contains("system", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FhirElement_MissingName_Errors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-element","release":"R5","path":"Patient.name"}""");

        Assert.False(env.Success);
        Assert.Contains("name", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FhirElement_MissingPath_Errors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-element","release":"R5","name":"Patient"}""");

        Assert.False(env.Success);
        Assert.Contains("path", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FhirSearchParameter_MissingIdOrCode_Errors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"fhir-searchparameter","release":"R5"}""");

        Assert.False(env.Success);
        Assert.Contains("idOrCode", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPath_Interfaces()
    {
        string path = FhirHandler.BuildPath(new FhirRequest { Command = "fhir-interfaces", Release = "R5" });
        Assert.Equal("/api/v1/fhir/R5/interfaces", path);
    }

    [Fact]
    public void BuildPath_Interfaces_WithFilters()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-interfaces", Release = "R5", WorkGroup = "fhir-i", Maturity = 5,
        });
        Assert.StartsWith("/api/v1/fhir/R5/interfaces?", path);
        Assert.Contains("workGroup=fhir-i", path);
        Assert.Contains("maturity=5", path);
    }

    [Fact]
    public void BuildPath_Elements_NestedAppendsQuery()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-elements", Release = "R5", Name = "Observation", Nested = true,
        });
        Assert.Equal("/api/v1/fhir/R5/structures/Observation/elements?nested=true", path);
    }

    [Fact]
    public void BuildPath_Element_PassesPathRaw()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-element", Release = "R5", Name = "Patient", Path = "Patient.contact.name",
        });
        // The element path is forwarded raw (no percent-encoding) to match the
        // source catch-all {*path} route and the MCP GetFhirElement tool.
        Assert.Equal("/api/v1/fhir/R5/structures/Patient/elements/Patient.contact.name", path);
    }

    [Fact]
    public void BuildPath_CodeSystemLookup_EncodesSystem()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-codesystem", Release = "R5", System = "http://hl7.org/fhir/observation-status",
        });
        Assert.StartsWith("/api/v1/fhir/R5/codesystems/lookup?system=", path);
        Assert.Contains("%2F", path);
    }

    [Fact]
    public void BuildPath_CodeSystemConcepts_HierarchicalAppends()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-codesystem-concepts", Release = "R5", System = "x", Hierarchical = true,
        });
        Assert.Equal("/api/v1/fhir/R5/codesystems/concepts?system=x&hierarchical=true", path);
    }

    [Fact]
    public void BuildPath_Operation()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-operation", Release = "R5", IdOrCode = "expand",
        });
        Assert.Equal("/api/v1/fhir/R5/operations/expand", path);
    }

    [Fact]
    public void BuildPath_SearchParameter()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-searchparameter", Release = "R5", IdOrCode = "Observation-code",
        });
        Assert.Equal("/api/v1/fhir/R5/searchparameters/Observation-code", path);
    }

    [Fact]
    public void BuildPath_ValueSetLookup_EncodesUrl()
    {
        string path = FhirHandler.BuildPath(new FhirRequest
        {
            Command = "fhir-valueset-lookup", Release = "R5", Url = "http://hl7.org/fhir/ValueSet/observation-status",
        });
        Assert.StartsWith("/api/v1/fhir/R5/valuesets/lookup?url=", path);
        Assert.Contains("ValueSet", path);
    }
}
