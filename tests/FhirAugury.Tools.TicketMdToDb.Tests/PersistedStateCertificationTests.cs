using FhirAugury.Tools.TicketMdToDb.Compilation;
using FhirAugury.Tools.TicketMdToDb.Import;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketMdToDb.Tests;

public sealed class PersistedStateCertificationTests
{
    [Fact]
    public async Task PersistedReader_DetectsScalarAndChildCorruption()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        CompilationResult compilation = ReportCompiler.Compile(
            new CompilationRequest(directory.CorpusRoot, 1));
        DateTimeOffset importedAt =
            DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        ManifestTicket manifestTicket = Assert.Single(compilation.Manifest.Tickets);
        manifestTicket.Payload.SavedAt = importedAt;
        using StagingDatabase staging = StagingDatabase.Create(
            Path.Combine(directory.OutputRoot, "corrupt.staging"));
        await staging.Database.SavePreparedTicketAsync(manifestTicket.Payload);

        await using (SqliteConnection connection = Open(staging.Path))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE prepared_tickets
                SET CommentSummary = 'corrupted scalar'
                WHERE Key = 'FHIR-100';
                UPDATE prepared_ticket_related_jira
                SET Justification = 'corrupted child'
                WHERE RowId = (
                    SELECT MIN(RowId) FROM prepared_ticket_related_jira
                    WHERE TicketKey = 'FHIR-100'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        PersistedDatabaseState state =
            await PersistedPreparedTicketReader.ReadAsync(staging.Path);
        IReadOnlyList<ReadinessFailure> failures =
            ReadinessVerifier.VerifyPreparedProjection(
                compilation.Manifest,
                importedAt,
                state);

        Assert.Contains(failures, failure => failure.Code == "prepared-scalar-mismatch");
        Assert.Contains(failures, failure => failure.Code == "prepared-child-mismatch");
    }

    [Fact]
    public async Task ReadinessVerifier_VisitsEveryWorkgroupAndRejectsUnpartitionableRows()
    {
        using (ImportTestDirectory directory = new())
        {
            string first = File.ReadAllText(
                directory.CopyFixture(
                    "CanonicalWithEmbeddedHeading.md",
                    "FHIR-100.md"));
            directory.WriteReport(
                "FHIR-102.md",
                first.Replace("FHIR-100", "FHIR-102", StringComparison.Ordinal));
            FakeOrchestratorHandler handler = new();
            handler.SetJiraProjection("FHIR-100", "FHIR Infrastructure");
            handler.SetJiraProjection("FHIR-102", "Orders & Observations");

            TicketImportRunResult result = await directory.RunAsync(handler);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                ["FHIRInfrastructure", "OrdersAndObservations"],
                result.Audit.Readiness!.WorkGroups);
            Assert.Equal(2, result.Audit.Readiness.FullyReadyTickets);
            Assert.Contains(
                "FHIR Core\u001fChange Request",
                result.Audit.Readiness.Partitions);
        }

        using (ImportTestDirectory invalid = new())
        {
            invalid.CopyFixture(
                "CanonicalWithEmbeddedHeading.md",
                "FHIR-100.md");
            FakeOrchestratorHandler handler = new();
            handler.SetJiraMode("FHIR-100", FakeJiraHydrationMode.InvalidType);

            TicketImportRunResult result = await invalid.RunAsync(handler);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                result.Audit.Readiness!.Failures,
                failure => failure.Code == "self-type-invalid" && !failure.Waived);
        }
    }

    [Fact]
    public async Task PersistedReader_ReadsHydrationAndLocalSourceProjection()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");

        TicketImportRunResult result = await directory.RunAsync(
            new FakeOrchestratorHandler());

        Assert.Equal(0, result.ExitCode);
        PersistedDatabaseState state =
            await PersistedPreparedTicketReader.ReadAsync(directory.DatabasePath);
        Assert.Single(state.HydrationParents);
        Assert.Equal(3, state.HydrationJiraRows.Count);
        Assert.Single(state.HydrationZulipRows);
        Assert.Single(state.HydrationGitHubRows);
        Assert.Single(state.HydrationRepoRows);
        Assert.Single(state.SourceTickets);
    }

    private static SqliteConnection Open(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
}
