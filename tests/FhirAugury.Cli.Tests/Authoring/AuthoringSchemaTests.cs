using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Models;
using FhirAugury.Cli.Schemas;
using FhirAugury.Processing.Client;

namespace FhirAugury.Cli.Tests.Authoring;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthoringEnvironmentCollection
{
    public const string Name = "authoring-environment";
}

public sealed class AuthoringSchemaTests
{
    [Fact]
    public void DispatcherAndSchemasExposeAllTypedAuthoringFamilies()
    {
        string[] expected =
        [
            "prepared-ticket-authoring",
            "planned-ticket-authoring",
            "ballot-note-authoring",
        ];

        Assert.All(
            expected,
            command =>
            {
                Assert.Contains(command, CommandDispatcher.KnownCommands);
                Assert.Contains(command, SchemaGenerator.AvailableCommands);
            });
        Assert.DoesNotContain("prepared-ticket-write", CommandDispatcher.KnownCommands);
        Assert.DoesNotContain("prepared-ticket-write", SchemaGenerator.AvailableCommands);
        string authoringSchema = System.Text.Json.JsonSerializer.Serialize(
            SchemaGenerator.GenerateForCommand("prepared-ticket-authoring"));
        Assert.Contains("observedSourceRevision", authoringSchema);
        Assert.Contains("\"refresh-publication\"", authoringSchema);
        Assert.Contains("\"reconcile-publication\"", authoringSchema);
        Assert.Contains("\"reconciliation-status\"", authoringSchema);
        Assert.Contains("\"retry-reconciliation\"", authoringSchema);
        Assert.Contains("\"abandon-reconciliation\"", authoringSchema);
        Assert.Contains("\"sourceRunId\"", authoringSchema);
        Assert.Contains("\"comparison\"", authoringSchema);
        Assert.Contains("\"counts\"", authoringSchema);
        Assert.Contains("\"groupingImpacts\"", authoringSchema);
        Assert.Contains("\"promotion\"", authoringSchema);
        Assert.Contains("\"invalidatedTicketKeys\"", authoringSchema);
        Assert.Contains("\"publicationProof\"", authoringSchema);
        Assert.Contains("\"failureCode\"", authoringSchema);
        Assert.Contains("canonical-unpublished", authoringSchema);
        Assert.Contains("metadata-only", authoringSchema);
        Assert.Contains("source run identifier", authoringSchema);
        Assert.Contains("outcome-unknown", authoringSchema);
        Assert.Contains("\"candidates\"", authoringSchema);
        Assert.DoesNotContain(
            "\"refresh-publication\"",
            System.Text.Json.JsonSerializer.Serialize(
                SchemaGenerator.GenerateForCommand(
                    "planned-ticket-authoring")));
        Assert.DoesNotContain(
            "\"refresh-publication\"",
            System.Text.Json.JsonSerializer.Serialize(
                SchemaGenerator.GenerateForCommand(
                    "ballot-note-authoring")));
        Assert.DoesNotContain(
            "\"reconcile-publication\"",
            System.Text.Json.JsonSerializer.Serialize(
                SchemaGenerator.GenerateForCommand(
                    "planned-ticket-authoring")));
        Assert.DoesNotContain(
            "commands/prepared-ticket-write",
            SchemaGenerator.GenerateForCommand("prepared-ticket-write").Keys);

        foreach (string command in expected)
        {
            string schema = System.Text.Json.JsonSerializer.Serialize(
                SchemaGenerator.GenerateForCommand(command));
            Assert.Contains("\"supersede\"", schema);
            Assert.Contains("\"reason\"", schema);
            Assert.Contains("Required non-blank reason", schema);
        }
    }

    [Fact]
    public void PublicationRefreshSchemaAllowsSerializedNullError()
    {
        JsonElement schema = JsonSerializer.SerializeToElement(
            SchemaGenerator.GenerateForCommand(
                "prepared-ticket-authoring"),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase,
            });
        JsonElement errorTypes = schema
            .GetProperty(
                "commands/prepared-ticket-authoring")
            .GetProperty("outputSchema")
            .GetProperty("properties")
            .GetProperty("error")
            .GetProperty("type");

        Assert.Equal(
            ["string", "null"],
            errorTypes
                .EnumerateArray()
                .Select(type => type.GetString()!)
                .ToArray());
    }

    [Fact]
    public void DispatcherPreservesTypedAuthoringFailureCodeAtTopLevel()
    {
        OutputEnvelope envelope =
            CommandDispatcher.CreateAuthoringControlFailure(
                "prepared-ticket-authoring",
                new AuthoringControlException(
                    HttpStatusCode.Conflict,
                    "recovery-in-progress",
                    "Snapshot publication is still pending.",
                    relatedRunIds: ["reconciliation-run"]));

        Assert.False(envelope.Success);
        Assert.Equal(
            "recovery-in-progress",
            Assert.IsType<ErrorInfo>(envelope.Error).Code);
        Assert.Contains(
            "Snapshot publication is still pending.",
            envelope.Error.Message);
    }

    [Fact]
    public void CliReferencesContractsButNoProcessorImplementationAssemblies()
    {
        string[] references = typeof(CommandDispatcher).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("FhirAugury.Processing.Contracts", references);
        Assert.Contains(
            "FhirAugury.Processor.Jira.Fhir.Preparer.Contracts",
            references);
        Assert.Contains(
            "FhirAugury.Processor.Jira.Fhir.Planner.Contracts",
            references);
        Assert.Contains(
            "FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts",
            references);
        Assert.DoesNotContain(
            references,
            name => name.Contains(".Persistence", StringComparison.Ordinal) ||
                name.EndsWith(".Preparer", StringComparison.Ordinal) ||
                name.EndsWith(".Planner", StringComparison.Ordinal) ||
                name.EndsWith(".BallotNotes", StringComparison.Ordinal));
    }

    [Fact]
    public void PhaseEightSkillDescriptionsRemainWithinCatalogLimit()
    {
        string root = FindRepositoryRoot();
        string[] skills =
        [
            "ticket-prep",
            "ticket-plan",
            "notes-artifact",
            "notes-page",
            "notes-datatype",
            "orchestrate-prep",
            "orchestrate-plan",
            "orchestrate-notes",
            "topic-groupings",
            "orchestrate-topic-groupings",
            "planner-topic-groupings",
            "orchestrate-planner-topic-groupings",
        ];

        foreach (string skill in skills)
        {
            string text = File.ReadAllText(
                Path.Combine(root, ".github", "skills", skill, "SKILL.md"));
            Match match = Regex.Match(
                text,
                "^description:\\s*\"(?<description>.*)\"\\r?$",
                RegexOptions.Multiline);
            Assert.True(match.Success, $"Missing description for {skill}.");
            Assert.True(
                match.Groups["description"].Value.Length < 1024,
                $"{skill} description exceeds 1023 characters.");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null &&
            !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}

internal sealed class AuthoringEnvironmentScope : IDisposable
{
    private static readonly string[] Names =
    [
        "FHIR_AUGURY_AUTHORING_WORKER",
        "FHIR_AUGURY_AUTHORING_RUN_ID",
        "FHIR_AUGURY_AUTHORING_ITEM_ID",
        "FHIR_AUGURY_AUTHORING_CALLBACK_URL",
        "FHIR_AUGURY_AUTHORING_OPERATION_ID",
        "FHIR_AUGURY_AUTHORING_OPERATION_TOKEN",
        "FHIR_AUGURY_AUTHORING_SOURCE_REVISION",
    ];

    private readonly Dictionary<string, string?> _original =
        Names.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

    public AuthoringEnvironmentScope(
        string runId = "run-1",
        string itemId = "item-1",
        string operationId = "operation-1",
        string operationToken = "secret-token",
        string sourceRevision = "revision-1",
        string callbackUrl = "http://processor/callback")
    {
        Environment.SetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_WORKER",
            "1");
        Environment.SetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_RUN_ID",
            runId);
        Environment.SetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_ITEM_ID",
            itemId);
        Environment.SetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_CALLBACK_URL",
            callbackUrl);
        Environment.SetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_OPERATION_ID",
            operationId);
        Environment.SetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_OPERATION_TOKEN",
            operationToken);
        Environment.SetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_SOURCE_REVISION",
            sourceRevision);
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

internal sealed class DelegateHttpHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>
        callback) : HttpMessageHandler
{
    private int _calls;

    public int Calls => _calls;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        callback(
            request,
            Interlocked.Increment(ref _calls),
            cancellationToken);

    public static HttpResponseMessage Json(
        object body,
        HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = System.Net.Http.Json.JsonContent.Create(body),
        };
}
