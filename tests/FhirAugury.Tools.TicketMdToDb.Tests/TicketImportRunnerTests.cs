using System.Net;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Tools.TicketMdToDb.Import;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketMdToDb.Tests;

public sealed class TicketImportRunnerTests
{
    [Fact]
    public void CliOptions_RejectsWriteOnlySwitchesDuringDryRun()
    {
        Assert.False(CliOptions.TryParse(
            [
                "--input", ".",
                "--db", "prepared.db",
                "--orchestrator", "http://localhost:5150",
                "--expected-count", "1",
                "--dry-run",
                "--replace-existing",
            ],
            out _,
            out string? replaceError));
        Assert.Contains(
            "--replace-existing",
            replaceError ?? string.Empty,
            StringComparison.Ordinal);

        Assert.False(CliOptions.TryParse(
            [
                "--input", ".",
                "--db", "prepared.db",
                "--orchestrator", "http://localhost:5150",
                "--expected-count", "1",
                "--dry-run",
                "--accept-unresolved-hydration",
            ],
            out _,
            out string? acceptError));
        Assert.Contains(
            "--accept-unresolved-hydration",
            acceptError ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRun_ImportsEveryManifestRecordThroughPreparerDatabase()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();

        TicketImportRunResult result = await directory.RunAsync(handler);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", result.Audit.Run.Status);
        Assert.True(File.Exists(directory.DatabasePath));
        Assert.True(File.Exists(directory.AuditPath));
        PersistedDatabaseState state =
            await PersistedPreparedTicketReader.ReadAsync(directory.DatabasePath);
        PersistedPreparedTicket ticket = Assert.Single(state.PreparedTickets).Value;
        Assert.Equal("FHIR-100", ticket.Payload.Key);
        Assert.Equal(result.Audit.Persistence!.ImportedAt, ticket.Payload.SavedAt);
        Assert.Equal(
            result.Compilation.Manifest.Tickets[0].Payload.Repos.Count,
            ticket.Payload.Repos.Count);
        Assert.True(result.Audit.Persistence.DeepReadbackMatched);
    }

    [Fact]
    public async Task WriteRun_DeduplicatedChildrenHydrateWithoutUniqueConstraintFailure()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalReferenceVariants.md", "FHIR-101.md");
        FakeOrchestratorHandler handler = new();

        TicketImportRunResult result = await directory.RunAsync(handler);

        Assert.Equal(0, result.ExitCode);
        PersistedDatabaseState state =
            await PersistedPreparedTicketReader.ReadAsync(directory.DatabasePath);
        PersistedPreparedTicket ticket = Assert.Single(state.PreparedTickets).Value;
        Assert.Equal(2, ticket.Payload.RelatedGitHubItems.Count);
        Assert.Equal(2, state.HydrationGitHubRows.Count);
        Assert.Single(
            state.HydrationGitHubRows,
            row => row.Row.GitHubItemId == "HL7/fhir#4124");
    }

    [Fact]
    public async Task WriteRun_HydratesAndProjectsOnlySupportedSourceColumns()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");

        TicketImportRunResult result = await directory.RunAsync(
            new FakeOrchestratorHandler());

        Assert.Equal(0, result.ExitCode);
        PersistedDatabaseState state =
            await PersistedPreparedTicketReader.ReadAsync(directory.DatabasePath);
        var source = Assert.Single(state.SourceTickets);
        Assert.Equal("FHIR", source.Project);
        Assert.Equal("Title for FHIR-100", source.Title);
        Assert.Equal("complete", source.ProcessingStatus);
        Assert.NotNull(source.CompletedProcessingAt);
        Assert.False(string.IsNullOrWhiteSpace(source.CompletionId));

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = directory.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(jira_processing_source_tickets)";
        List<string> columns = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }
        Assert.DoesNotContain("Priority", columns);
        Assert.DoesNotContain("Url", columns);
    }

    [Fact]
    public async Task WriteRun_RetriesTransientOrchestratorResponses()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        handler.EnqueueStatus(
            "/api/v1/jira/items/FHIR-100?includeContent=true",
            HttpStatusCode.ServiceUnavailable,
            TimeSpan.FromMilliseconds(1));

        TicketImportRunResult result = await directory.RunAsync(handler);

        Assert.Equal(0, result.ExitCode);
        Assert.True(
            handler.CountRequestsContaining(
                "/api/v1/jira/items/FHIR-100?includeContent=true") >= 2);
    }

    [Theory]
    [InlineData(FakeJiraHydrationMode.NotFound)]
    [InlineData(FakeJiraHydrationMode.MissingSelfMetadata)]
    public async Task WriteRun_UnresolvedOrIncompleteSelfRefusesPromotionByDefault(
        FakeJiraHydrationMode mode)
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        handler.SetJiraMode("FHIR-100", mode);

        TicketImportRunResult result = await directory.RunAsync(handler);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("failed", result.Audit.Run.Status);
        Assert.False(File.Exists(directory.DatabasePath));
        Assert.NotNull(result.EvidencePath);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(directory.OutputRoot),
            path => path.EndsWith(".staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteRun_AcceptUnresolvedPromotesAsDegradedAccepted()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        handler.SetJiraMode("FHIR-100", FakeJiraHydrationMode.NotFound);

        TicketImportRunResult result = await directory.RunAsync(
            handler,
            acceptUnresolved: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("degraded-accepted", result.Audit.Run.Status);
        Assert.True(File.Exists(directory.DatabasePath));
        Assert.Contains(result.Messages, message => message.StartsWith("WARNING:", StringComparison.Ordinal));
        Assert.All(
            result.Audit.Readiness!.Failures,
            failure => Assert.True(failure.Waived));
    }
}

internal sealed class ImportTestDirectory : IDisposable
{
    private static readonly DateTimeOffset FixedTime =
        DateTimeOffset.Parse("2026-09-03T12:00:00Z");
    private readonly string _root = Path.Combine(
        Environment.CurrentDirectory,
        "temp",
        "ticket-md-to-db-write-tests",
        Guid.NewGuid().ToString("N"));
    private int _runSequence;

    public ImportTestDirectory()
    {
        Directory.CreateDirectory(CorpusRoot);
        Directory.CreateDirectory(OutputRoot);
    }

    public string CorpusRoot => Path.Combine(_root, "corpus");
    public string OutputRoot => Path.Combine(_root, "output");
    public string DatabasePath => Path.Combine(OutputRoot, "prepared.db");
    public string AuditPath => $"{DatabasePath}.import-audit.json";
    public string TemplatePath => $"{DatabasePath}.override-template.json";

    public string CopyFixture(string fixtureName, string? destinationName = null)
    {
        string destination = Path.Combine(
            CorpusRoot,
            destinationName ?? fixtureName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName),
            destination);
        return destination;
    }

    public void WriteReport(string fileName, string markdown) =>
        File.WriteAllText(Path.Combine(CorpusRoot, fileName), markdown);

    public CliOptions Options(
        bool replaceExisting = false,
        bool acceptUnresolved = false,
        string? databasePath = null,
        string? auditPath = null,
        string? templatePath = null) =>
        new(
            CorpusRoot,
            databasePath ?? DatabasePath,
            new Uri("http://orchestrator.test"),
            Directory.EnumerateFiles(
                CorpusRoot,
                "FHIR-*.md",
                SearchOption.AllDirectories).Count(),
            null,
            auditPath ?? AuditPath,
            templatePath ?? TemplatePath,
            DryRun: false,
            ReplaceExisting: replaceExisting,
            AcceptUnresolvedHydration: acceptUnresolved);

    public Task<TicketImportRunResult> RunAsync(
        FakeOrchestratorHandler handler,
        bool replaceExisting = false,
        bool acceptUnresolved = false,
        Action<PromotionBoundary>? fault = null,
        string? runId = null,
        string? databasePath = null,
        string? auditPath = null,
        string? templatePath = null)
    {
        string selectedRunId = runId ?? $"test-run-{Interlocked.Increment(ref _runSequence)}";
        TicketImportRunner runner = new(
            () => selectedRunId,
            () => FixedTime,
            handler,
            fault);
        return runner.RunAsync(Options(
            replaceExisting,
            acceptUnresolved,
            databasePath,
            auditPath,
            templatePath));
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_root);
}
