using FhirAugury.Common.IO;
using FhirAugury.Processing.Client;
using FhirAugury.Processor.Jira.Fhir.Planner.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets.Tests;

public sealed class HydrationAssertionTests
{
    [Theory]
    [InlineData(PreparedTicketSnapshotSchemaV1.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV2.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV3.Version)]
    public async Task DiscussionAcceptsPreparedV1V2AndV3(int schemaVersion)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-discussion-{schemaVersion}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture fixture =
                await TicketSnapshotFixture.CreatePreparerAsync(
                    root,
                    schemaVersion: schemaVersion);
            await using ImmutableFileSnapshot snapshot =
                await CreateImmutableSnapshotAsync(fixture.DatabasePath);

            HydrationAssertion.SnapshotValidationResult result =
                await HydrationAssertion.ValidateSnapshotAsync(
                    snapshot,
                    fixture.Descriptor,
                    TicketSiteKind.Discussion,
                    CancellationToken.None);

            Assert.Equal(schemaVersion, result.Descriptor.SchemaVersion);
            Assert.Equal(
                schemaVersion != PreparedTicketSnapshotSchemaV1.Version,
                result.TableCounts.ContainsKey(
                    "prepared_ticket_in_person_requesters"));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ApplyingAcceptsOnlyPlannerV1()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-applying-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture fixture =
                await TicketSnapshotFixture.CreatePlannerAsync(root);
            await using ImmutableFileSnapshot snapshot =
                await CreateImmutableSnapshotAsync(fixture.DatabasePath);

            HydrationAssertion.SnapshotValidationResult result =
                await HydrationAssertion.ValidateSnapshotAsync(
                    snapshot,
                    fixture.Descriptor,
                    TicketSiteKind.Applying,
                    CancellationToken.None);

            Assert.Equal(
                PlannedTicketSnapshotSchemaV1.Version,
                result.Descriptor.SchemaVersion);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(PreparedTicketSnapshotSchemaV2.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV3.Version)]
    public async Task ApplyingRejectsPreparedV2AndV3BeforeSchemaInspection(
        int schemaVersion)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-applying-v2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture fixture =
                await TicketSnapshotFixture.CreatePreparerAsync(
                    root,
                    schemaVersion: schemaVersion);
            await using ImmutableFileSnapshot snapshot =
                await CreateImmutableSnapshotAsync(fixture.DatabasePath);

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => HydrationAssertion.ValidateSnapshotAsync(
                        snapshot,
                        fixture.Descriptor,
                        TicketSiteKind.Applying,
                        CancellationToken.None));

            Assert.Contains(
                "Applying",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                $"version {schemaVersion}",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(TicketSiteKind.Discussion)]
    [InlineData(TicketSiteKind.Applying)]
    public async Task UnknownSchemaVersionIsRejectedForEverySiteKind(
        TicketSiteKind siteKind)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-unknown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture fixture =
                await TicketSnapshotFixture.CreatePreparerAsync(root);
            await using ImmutableFileSnapshot snapshot =
                await CreateImmutableSnapshotAsync(fixture.DatabasePath);

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => HydrationAssertion.ValidateSnapshotAsync(
                        snapshot,
                        fixture.Descriptor with { SchemaVersion = 99 },
                        siteKind,
                        CancellationToken.None));

            Assert.Contains(
                "version 99",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(PreparedTicketSnapshotSchemaV2.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV3.Version)]
    public async Task DiscussionV2AndV3RejectExactCatalogDrift(
        int schemaVersion)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-v2-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture fixture =
                await TicketSnapshotFixture.CreatePreparerAsync(
                    root,
                    schemaVersion: schemaVersion);
            await using (SqliteConnection connection = new(
                $"Data Source={fixture.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    ALTER TABLE prepared_ticket_in_person_requesters
                    ADD COLUMN UserName TEXT
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await fixture.RefreshDescriptorHashAsync();
            await using ImmutableFileSnapshot snapshot =
                await CreateImmutableSnapshotAsync(fixture.DatabasePath);

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => HydrationAssertion.ValidateSnapshotAsync(
                        snapshot,
                        fixture.Descriptor,
                        TicketSiteKind.Discussion,
                        CancellationToken.None));

            Assert.Contains(
                $"schema v{schemaVersion}",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UserName", exception.Message);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SnapshotValidationRejectsDescriptorProcessorMismatch()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-descriptor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePreparerAsync(root);
            await TicketSnapshotFixture.WriteDescriptorAsync(
                snapshot.DescriptorPath,
                snapshot.Descriptor with { ProcessorKind = "wrong-processor" });

            InvalidOperationException exception = await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => snapshot.CreateVerifiedPairAsync("Preparer"));

            Assert.Contains(
                "processor",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SnapshotValidationAcceptsStateBackedHistoricalLedger()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-historical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePreparerAsync(
                    root,
                    includeSecondTicket: true);
            await snapshot.MoveSecondTicketToHistoricalLedgerAsync();

            VerifiedAuthoringSnapshotPair pair =
                await snapshot.CreateVerifiedPairAsync("Preparer");
            TicketSitePublishResult result =
                await new TicketSitePublisher().PublishAsync(
                    new TicketSitePublishRequest(
                        pair,
                        TicketSiteKind.Discussion,
                        Path.Combine(root, "site"),
                        "Tickets"));

            Assert.Equal(2, result.Manifest.IncludedReceiptCount);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    private static async Task<ImmutableFileSnapshot>
        CreateImmutableSnapshotAsync(string databasePath)
    {
        const int attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await ImmutableFileSnapshot.CreateAsync(databasePath);
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
