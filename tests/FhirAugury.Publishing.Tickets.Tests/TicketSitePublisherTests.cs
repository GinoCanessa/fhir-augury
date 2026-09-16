using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets.Tests;

public sealed class TicketSitePublisherTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ticket-site-publisher-{Guid.NewGuid():N}");

    public TicketSitePublisherTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_root);

    [Fact]
    public async Task PublishesDiscussionSiteAndChooserFromVerifiedPair()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version,
                includeNullProvenance: true);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "discussion-site");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    output,
                    "Tickets for Discussion"));

        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.Equal("preparer", result.Manifest.SiteKind);
        Assert.Equal(pair.SnapshotId, result.Manifest.SnapshotId);
        Assert.Equal(pair.Descriptor.SchemaVersion, result.Manifest.SnapshotSchemaVersion);
        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.Version,
            result.Manifest.SnapshotSchemaVersion);
        Assert.Equal(pair.Descriptor.Sha256, result.Manifest.SnapshotSha256);
        Assert.NotEqual(
            result.Manifest.SnapshotSha256,
            result.Manifest.EmbeddedDbSha256);
        Assert.Equal(pair.Descriptor.ReceiptCount, result.Manifest.IncludedReceiptCount);
        Assert.Equal("Tickets for Discussion", result.Manifest.Title);
        Assert.Equal("Tickets for Discussion - Sept 5, 2026", result.Manifest.DisplayTitle);
        Assert.Null(result.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(
            DiscussionRendererSchema.Version,
            result.Manifest.RendererSchemaVersion);
        Assert.NotNull(result.Manifest.DiscussionReadiness);
        Assert.NotNull(result.Manifest.DiscussionCorpus);
        Assert.Equal(DiscussionDateCoverage.Complete, result.Manifest.DiscussionCorpus.DateCoverage);
        Assert.Equal(1, result.Manifest.DiscussionCorpus.ValidJiraUpdatedAtCount);
        Assert.False(result.Manifest.DiscussionReadiness.IsReady);
        Assert.Contains(
            result.Manifest.DiscussionReadiness.Reasons,
            reason => reason.Code ==
                DiscussionPublicationReadinessReasonCodes
                    .MissingOrdinaryProvenance);
        Assert.Equal(1, result.Manifest.IncludedItemCount);
        Assert.Equal(1, result.Manifest.TableCounts["tickets"]);
        Assert.DoesNotContain(
            result.Manifest.TableCounts.Keys,
            table => table.StartsWith("prepared_", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.False(Directory.Exists(Path.Combine(output, "applying")));
        string chooser = await File.ReadAllTextAsync(
            Path.Combine(output, "index.html"));
        Assert.Contains(
            "Publication readiness: degraded",
            chooser,
            StringComparison.Ordinal);
        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "index.html"));
        Assert.Contains(
            $"assets/app.js?v={result.Manifest.RendererAssetsVersion}",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            $"assets/components.js?v={result.Manifest.RendererAssetsVersion}",
            html,
            StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("assets/components.js", StringComparison.Ordinal) <
            html.IndexOf("assets/app.js", StringComparison.Ordinal));
        Assert.DoesNotContain("type=\"module\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "<script id=\"site-presentation\" type=\"application/json\">",
            html,
            StringComparison.Ordinal);
        byte[] embeddedWasm = await ReadEmbeddedResourceBytesAsync(
            "web-assets/shared/sql-wasm.wasm");
        Assert.Equal(embeddedWasm, ExtractEmbeddedWasm(html));
        string emittedWasmPath = Path.Combine(
            output,
            "discussion",
            "assets",
            "sql-wasm.wasm");
        Assert.True(File.Exists(emittedWasmPath));
        Assert.Equal(
            embeddedWasm,
            await File.ReadAllBytesAsync(emittedWasmPath));
        string script = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "assets",
            "app.js"));
        Assert.Equal(
            await ReadEmbeddedResourceAsync("web-assets/discussion/app.js"),
            script);
        Assert.DoesNotContain("prepared_", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "jira_processing_source_tickets",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "wasmBinary: wasmBinary",
            script,
            StringComparison.Ordinal);
        Assert.Contains("locateFile:", script, StringComparison.Ordinal);
        Assert.Contains("FROM ticket_facets", script, StringComparison.Ordinal);
        AssertFacetHashContract(script);
        AssertCopyForAiContract(script);

        byte[] databaseBytes = await ExtractEmbeddedDatabaseAsync(html);
        Assert.Equal(
            result.Manifest.EmbeddedDbSizeBytes,
            databaseBytes.LongLength);
        Assert.Equal(
            result.Manifest.EmbeddedDbSha256,
            Convert.ToHexString(SHA256.HashData(databaseBytes))
                .ToLowerInvariant());
        string embeddedPath = Path.Combine(
            _root,
            $"embedded-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(embeddedPath, databaseBytes);
            Assert.Equal(
                DiscussionRendererSchema.Tables
                    .Select(table => table.Name)
                    .Order(StringComparer.Ordinal),
                await ReadTableNamesAsync(embeddedPath));
            Assert.Equal(
                "Ada Lovelace",
                await ScalarAsync<string>(
                    embeddedPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'reporter'
                    """));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(embeddedPath);
        }
    }

    [Theory]
    [InlineData(PreparedTicketSnapshotSchemaV1.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV2.Version)]
    public async Task PublishesPreparedV1AndV2WithoutTrustingPeople(
        int schemaVersion)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: schemaVersion);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(
            _root,
            $"prepared-v{schemaVersion}-compatibility");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    output,
                    "Tickets for Discussion"));

        Assert.Equal(schemaVersion, result.Manifest.SnapshotSchemaVersion);
        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "index.html"));
        string embeddedPath = Path.Combine(
            _root,
            $"prepared-v{schemaVersion}-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(
                embeddedPath,
                await ExtractEmbeddedDatabaseAsync(html));
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    embeddedPath,
                    """
                    SELECT COUNT(*)
                    FROM ticket_people
                    WHERE DisplayName IS NOT NULL
                    """));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(embeddedPath);
        }
    }

    [Fact]
    public async Task PostBuildPathMutationCannotChangePublishedRendererBytes()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "post-build-path-mutation");
        const string mutatedTitle = "Mutated after renderer byte capture";
        string? mutatedDigest = null;
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                AfterDiscussionRendererBytesCapturedAsync:
                    async (rendererPath, token) =>
                    {
                        await using (SqliteConnection connection = new(
                            new SqliteConnectionStringBuilder
                            {
                                DataSource = rendererPath,
                                Mode = SqliteOpenMode.ReadWrite,
                                Pooling = false,
                            }.ToString()))
                        {
                            await connection.OpenAsync(token);
                            await using SqliteCommand command =
                                connection.CreateCommand();
                            command.CommandText =
                                """
                                UPDATE tickets
                                SET Title = @title
                                WHERE Key = 'FHIR-1001'
                                """;
                            command.Parameters.AddWithValue(
                                "@title",
                                mutatedTitle);
                            Assert.Equal(
                                1,
                                await command.ExecuteNonQueryAsync(token));
                        }
                        mutatedDigest = await ComputeHashAsync(rendererPath);
                    }));

        TicketSitePublishResult result = await publisher.PublishAsync(
            new TicketSitePublishRequest(
                pair,
                TicketSiteKind.Discussion,
                output,
                "Tickets for Discussion"));

        Assert.NotNull(mutatedDigest);
        Assert.NotEqual(mutatedDigest, result.Manifest.EmbeddedDbSha256);
        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "index.html"));
        string embeddedPath = Path.Combine(
            _root,
            $"post-build-path-mutation-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(
                embeddedPath,
                await ExtractEmbeddedDatabaseAsync(html));
            Assert.Equal(
                "Snapshot title",
                await ScalarAsync<string>(
                    embeddedPath,
                    """
                    SELECT Title
                    FROM tickets
                    WHERE Key = 'FHIR-1001'
                    """));
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    embeddedPath,
                    $"""
                    SELECT COUNT(*)
                    FROM tickets
                    WHERE Title = '{mutatedTitle}'
                    """));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(embeddedPath);
        }
    }

    [Fact]
    public async Task PublishesQualifiedDiscussionTitleWithoutScriptRewriting()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: 2,
                useMultipleRuns: true,
                includeRendererEvidence: true);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "qualified-discussion");
        byte[] originalDatabase = await File.ReadAllBytesAsync(pair.DatabasePath);
        byte[] originalDescriptor = await File.ReadAllBytesAsync(pair.DescriptorPath);
        string pairManifestPath = Path.Combine(
            Assert.IsType<string>(Path.GetDirectoryName(pair.DatabasePath)),
            AuthoringSnapshotPairManifest.ReadyFileName);
        byte[] originalPairManifest = await File.ReadAllBytesAsync(pairManifestPath);

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    output,
                    "Tickets for Discussion"));

        DateTimeOffset expectedRefresh =
            new(2026, 9, 10, 23, 30, 0, TimeSpan.Zero);
        const string expectedTitle =
            "Tickets for Discussion - Sept 6, 2026";
        Assert.Equal("Tickets for Discussion", result.Manifest.Title);
        Assert.Equal(expectedTitle, result.Manifest.DisplayTitle);
        Assert.Equal(
            expectedRefresh,
            result.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(2, result.Manifest.SnapshotSchemaVersion);
        Assert.Equal(DiscussionRendererSchema.Version, result.Manifest.RendererSchemaVersion);
        Assert.Equal(2, result.Manifest.IncludedItemCount);
        Assert.Equal(2, result.Manifest.IncludedReceiptCount);
        DiscussionCorpusSummary corpus = Assert.IsType<DiscussionCorpusSummary>(
            result.Manifest.DiscussionCorpus);
        Assert.Equal(2, corpus.TicketCount);
        Assert.Equal(2, corpus.ExportedProjectCount);
        Assert.Equal(2, corpus.ValidJiraUpdatedAtCount);
        Assert.Equal(DiscussionDateCoverage.Complete, corpus.DateCoverage);
        Assert.Equal(0, corpus.TicketsWithPublicReporter);
        Assert.NotEqual(result.Manifest.GeneratedAt.Date, corpus.MaxJiraUpdatedAt?.Date);
        Assert.Equal(originalDatabase, await File.ReadAllBytesAsync(pair.DatabasePath));
        Assert.Equal(originalDescriptor, await File.ReadAllBytesAsync(pair.DescriptorPath));
        Assert.Equal(originalPairManifest, await File.ReadAllBytesAsync(pairManifestPath));

        string discussionHtml = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "index.html"));
        Assert.Contains(
            $"<h1>{expectedTitle}</h1>",
            discussionHtml,
            StringComparison.Ordinal);
        Assert.Contains($"<title>{expectedTitle}</title>", discussionHtml, StringComparison.Ordinal);
        TicketSitePresentation injected = ReadPresentation(discussionHtml);
        Assert.Equal(expectedTitle, injected.SiteName);
        Assert.Equal(
            TicketSitePresentationJson.Serialize(corpus),
            TicketSitePresentationJson.Serialize(injected.CorpusSummary));
        string rendererPath = Path.Combine(_root, "qualified-renderer.db");
        try
        {
            await File.WriteAllBytesAsync(rendererPath, await ExtractEmbeddedDatabaseAsync(discussionHtml));
            Assert.Equal(3L, await ScalarAsync<long>(rendererPath,
                "SELECT RendererSchemaVersion FROM site_metadata"));
            Assert.Equal(expectedTitle, await ScalarAsync<string>(rendererPath,
                "SELECT SiteName FROM site_metadata"));
            Assert.Equal(TicketSitePresentationJson.Serialize(corpus),
                await ScalarAsync<string>(rendererPath, "SELECT CorpusSummaryJson FROM site_metadata"));
            Assert.Equal(TicketSnapshotFixture.SecondJiraUpdatedAt,
                await ScalarAsync<string>(rendererPath, "SELECT MAX(JiraUpdatedAt) FROM tickets"));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(rendererPath);
        }
        Assert.Contains(
            JsonEncodedText.Encode(expectedTitle).ToString(),
            discussionHtml,
            StringComparison.Ordinal);
        string chooserHtml = await File.ReadAllTextAsync(Path.Combine(
            output,
            "index.html"));
        Assert.Contains(
            $"<div class=\"card-title\">{expectedTitle}</div>",
            chooserHtml,
            StringComparison.Ordinal);

        string emittedScript = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "assets",
            "app.js"));
        Assert.Equal(
            await ReadEmbeddedResourceAsync("web-assets/discussion/app.js"),
            emittedScript);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task UnprovenancedDiscussionStillUsesCompleteTicketDates(
        int schemaVersion,
        bool includeNullProvenance)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: schemaVersion,
                includeNullProvenance: includeNullProvenance);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(
                        _root,
                        $"unsuffixed-{schemaVersion}-{includeNullProvenance}"),
                    "Tickets for Discussion"));

        Assert.Equal(
            "Tickets for Discussion - Sept 5, 2026",
            result.Manifest.DisplayTitle);
        Assert.Null(result.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(DiscussionDateCoverage.Complete, result.Manifest.DiscussionCorpus?.DateCoverage);
        Assert.False(result.Manifest.DiscussionReadiness?.IsReady);
    }

    [Fact]
    public async Task PublisherValidatesRefreshProofAgainstPublishedCorpus()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version);
        DateTimeOffset frozenRefresh =
            new(2026, 9, 14, 17, 15, 0, TimeSpan.Zero);
        await fixture.AttachValidPublicationRefreshProofAsync(
            frozenRefresh,
            sourceContentRevision: 9001);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        TicketSitePublishResult ready =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "refresh-proof-ready"),
                    "Tickets"));
        Assert.True(ready.Manifest.DiscussionReadiness?.IsReady);
        Assert.Equal(
            9001,
            ready.Manifest.DiscussionReadiness
                ?.JiraSourceContentRevision);
        Assert.Equal(
            frozenRefresh,
            ready.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.DoesNotContain(
            ready.Warnings,
            warning => warning.Contains(
                "readiness is degraded",
                StringComparison.Ordinal));

        VerifiedAuthoringSnapshotPair drifted =
            await MutateAndReverifyAsync(
                pair,
                """
                UPDATE authoring_run_items
                SET ItemKind = 'changed-ticket-kind'
                WHERE ItemKind = 'ticket';
                """);
        TicketSitePublishResult degraded =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    drifted,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "refresh-proof-drifted"),
                    "Tickets"));
        Assert.False(degraded.Manifest.DiscussionReadiness?.IsReady);
        Assert.Null(degraded.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal("Tickets - Sept 5, 2026", ready.Manifest.DisplayTitle);
        Assert.Equal(ready.Manifest.DisplayTitle, degraded.Manifest.DisplayTitle);
        Assert.Equal(TicketSitePresentationJson.Serialize(ready.Manifest.DiscussionCorpus!),
            TicketSitePresentationJson.Serialize(degraded.Manifest.DiscussionCorpus!));
        Assert.Contains(
            degraded.Manifest.DiscussionReadiness!.Reasons,
            reason => reason.Code ==
                DiscussionPublicationReadinessReasonCodes
                    .InvalidRefreshProof);
        Assert.Contains(
            degraded.Warnings,
            warning => warning.Contains(
                DiscussionPublicationReadinessReasonCodes
                    .InvalidRefreshProof,
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null, "none", 0)]
    [InlineData(TicketSnapshotFixture.FirstJiraUpdatedAt, null, "partial", 1)]
    [InlineData(null, TicketSnapshotFixture.SecondJiraUpdatedAt, "partial", 1)]
    public async Task IncompleteTicketDatesPublishCoverageWithoutDateSuffix(
        string? firstDate, string? secondDate, string coverage, int validDates)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, schemaVersion: 3,
                firstJiraUpdatedAt: firstDate, secondJiraUpdatedAt: secondDate);
        VerifiedAuthoringSnapshotPair pair = await fixture.CreateVerifiedPairAsync("Preparer");
        TicketSitePublishResult result = await new TicketSitePublisher().PublishAsync(
            new(pair, TicketSiteKind.Discussion, Path.Combine(_root, "incomplete-dates"), "Tickets"));

        Assert.Equal("Tickets", result.Manifest.DisplayTitle);
        Assert.Equal(coverage, result.Manifest.DiscussionCorpus?.DateCoverage);
        Assert.Equal(validDates, result.Manifest.DiscussionCorpus?.ValidJiraUpdatedAtCount);
        Assert.Equal(2, result.Manifest.DiscussionCorpus?.TicketCount);
        Assert.NotNull(result.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Contains(result.Warnings, warning => warning.Contains(
            $"update-date coverage is {coverage}", StringComparison.Ordinal));
        string html = await File.ReadAllTextAsync(Path.Combine(result.SiteOutputPath, "index.html"));
        Assert.Equal(TicketSitePresentationJson.Serialize(result.Manifest.DiscussionCorpus!),
            TicketSitePresentationJson.Serialize(ReadPresentation(html).CorpusSummary));
        Assert.Contains("<title>Tickets</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Tickets</h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscussionBuildIdentityBindsCorpusSummaryIndependentlyOfDatabaseDigest()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root, schemaVersion: 3);
        VerifiedAuthoringSnapshotPair pair = await fixture.CreateVerifiedPairAsync("Preparer");
        TicketSitePublishResult result = await new TicketSitePublisher().PublishAsync(
            new(pair, TicketSiteKind.Discussion, Path.Combine(_root, "corpus-identity"), "Tickets"));
        TicketSiteManifest manifest = result.Manifest;
        DiscussionCorpusSummary corpus = Assert.IsType<DiscussionCorpusSummary>(manifest.DiscussionCorpus);

        string Identity(DiscussionCorpusSummary? summary) =>
            TicketSiteManifest.ComputeBuildIdentity(
                manifest.SiteKind, ResolvedFilters.None, manifest.Title,
                manifest.RendererAssetsVersion, manifest.SnapshotSha256,
                manifest.EmbeddedDbSha256, manifest.DisplayTitle,
                manifest.JiraSourceLastSuccessfulRefreshAt,
                manifest.RendererSchemaVersion, manifest.DiscussionReadiness, summary);
        Assert.Equal(manifest.BuildIdentity, Identity(corpus));
        Assert.NotEqual(manifest.BuildIdentity, Identity(null));
        Assert.NotEqual(manifest.BuildIdentity, Identity(corpus with { TicketsWithPublicReporter = 0 }));
        Assert.NotEqual(manifest.BuildIdentity, Identity(corpus with
        {
            MaxJiraUpdatedAt = corpus.MaxJiraUpdatedAt?.AddTicks(1),
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-09-15T01:00:00")]
    [InlineData("invalid-date")]
    public async Task MalformedSelfDateAbortsAndPreservesPublishedSiteAndInputPair(string date)
    {
        string output = Path.Combine(_root, "date-refusal");
        TicketSnapshotFixture original =
            await TicketSnapshotFixture.CreatePreparerAsync(_root, schemaVersion: 3);
        VerifiedAuthoringSnapshotPair originalPair =
            await original.CreateVerifiedPairAsync("Preparer");
        TicketSitePublishResult published = await new TicketSitePublisher().PublishAsync(
            new(originalPair, TicketSiteKind.Discussion, output, "Tickets"));
        string indexPath = Path.Combine(published.SiteOutputPath, "index.html");
        string manifestPath = Path.Combine(published.SiteOutputPath, TicketSiteManifest.FileName);
        byte[] originalHtml = await File.ReadAllBytesAsync(indexPath);
        byte[] originalManifest = await File.ReadAllBytesAsync(manifestPath);

        TicketSnapshotFixture invalid =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, sequence: 2, schemaVersion: 3, firstJiraUpdatedAt: date);
        VerifiedAuthoringSnapshotPair invalidPair = await invalid.CreateVerifiedPairAsync("Preparer");
        byte[] inputDatabase = await File.ReadAllBytesAsync(invalidPair.DatabasePath);
        byte[] inputDescriptor = await File.ReadAllBytesAsync(invalidPair.DescriptorPath);
        TicketSitePublishException exception =
            await Assert.ThrowsAsync<TicketSitePublishException>(() =>
                new TicketSitePublisher().PublishAsync(
                    new(invalidPair, TicketSiteKind.Discussion, output, "Tickets")));
        Assert.Contains("UpdatedAt", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalHtml, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(originalManifest, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(inputDatabase, await File.ReadAllBytesAsync(invalidPair.DatabasePath));
        Assert.Equal(inputDescriptor, await File.ReadAllBytesAsync(invalidPair.DescriptorPath));
    }

    [Theory]
    [InlineData("heading")]
    [InlineData("html-title")]
    [InlineData("injected-summary")]
    [InlineData("manifest-summary")]
    [InlineData("missing-summary")]
    public async Task StageValidationRejectsDiscussionSurfaceDriftAndPreservesPreviousPublication(string surface)
    {
        string output = Path.Combine(_root, "surface-drift");
        TicketSnapshotFixture original =
            await TicketSnapshotFixture.CreatePreparerAsync(_root, schemaVersion: 3);
        VerifiedAuthoringSnapshotPair originalPair = await original.CreateVerifiedPairAsync("Preparer");
        TicketSitePublishResult published = await new TicketSitePublisher().PublishAsync(
            new(originalPair, TicketSiteKind.Discussion, output, "Tickets"));
        string indexPath = Path.Combine(published.SiteOutputPath, "index.html");
        string manifestPath = Path.Combine(published.SiteOutputPath, TicketSiteManifest.FileName);
        string chooserPath = Path.Combine(output, "index.html");
        byte[] originalHtml = await File.ReadAllBytesAsync(indexPath);
        byte[] originalManifest = await File.ReadAllBytesAsync(manifestPath);
        byte[] originalChooser = await File.ReadAllBytesAsync(chooserPath);
        TicketSnapshotFixture next =
            await TicketSnapshotFixture.CreatePreparerAsync(_root, sequence: 2, schemaVersion: 3);
        VerifiedAuthoringSnapshotPair nextPair = await next.CreateVerifiedPairAsync("Preparer");
        TicketSitePublisher publisher = new(new TicketSitePublisherTestHooks(
            BeforeStageValidationAsync: async (staging, token) =>
            {
                if (surface is "manifest-summary" or "missing-summary")
                {
                    TicketSiteManifest manifest = await TicketSiteManifest.ReadAsync(
                        Path.Combine(staging, TicketSiteManifest.FileName), token);
                    DiscussionCorpusSummary corpus = Assert.IsType<DiscussionCorpusSummary>(manifest.DiscussionCorpus);
                    await TicketSiteManifest.WriteAsync(staging, manifest with
                    {
                        DiscussionCorpus = surface == "missing-summary"
                            ? null
                            : corpus with { TicketsWithPublicReporter = 0 },
                    }, token);
                    return;
                }
                string path = Path.Combine(staging, "index.html");
                string html = await File.ReadAllTextAsync(path, token);
                (string before, string after) = surface switch
                {
                    "heading" => ("<h1>Tickets - Sept 5, 2026</h1>", "<h1>Wrong heading</h1>"),
                    "html-title" => ("<title>Tickets - Sept 5, 2026</title>", "<title>Wrong title</title>"),
                    "injected-summary" => ("\"ticketsWithPublicReporter\":1", "\"ticketsWithPublicReporter\":0"),
                    _ => throw new ArgumentOutOfRangeException(nameof(surface)),
                };
                Assert.Contains(before, html, StringComparison.Ordinal);
                await File.WriteAllTextAsync(path, html.Replace(before, after, StringComparison.Ordinal), token);
            }));
        TicketSitePublishException exception = await Assert.ThrowsAsync<TicketSitePublishException>(() =>
            publisher.PublishAsync(new(nextPair, TicketSiteKind.Discussion, output, "Tickets")));
        Assert.Equal(TicketSitePublishFailure.Publication, exception.Failure);
        Assert.Equal(originalHtml, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(originalManifest, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(originalChooser, await File.ReadAllBytesAsync(chooserPath));
    }

    [Fact]
    public async Task PublishesApplyingSiteFromVerifiedPair()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "applying-site");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Applying,
                    output,
                    "Tickets for Applying"));

        Assert.Equal("planner", result.Manifest.SiteKind);
        Assert.Null(result.Manifest.DisplayTitle);
        Assert.Null(result.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Null(result.Manifest.RendererSchemaVersion);
        Assert.Null(result.Manifest.DiscussionReadiness);
        Assert.Null(result.Manifest.DiscussionCorpus);
        Assert.Equal(1, result.Manifest.SnapshotSchemaVersion);
        Assert.Equal("Tickets for Applying", result.Manifest.Title);
        Assert.DoesNotContain("discussionCorpus", TicketSiteManifest.ToSummaryJson(result.Manifest),
            StringComparison.Ordinal);
        string applyingIdentityInput = string.Join("\n",
            "site-kind=planner", "spec=", "project=", "wg=",
            "title=Tickets for Applying",
            "renderer-assets=" + result.Manifest.RendererAssetsVersion,
            "source=" + result.Manifest.SnapshotSha256,
            "embedded-db=" + result.Manifest.EmbeddedDbSha256);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applyingIdentityInput)))
                .ToLowerInvariant(),
            result.Manifest.BuildIdentity);
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "index.html")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "assets",
            "marked.min.js")));
        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "applying",
            "index.html"));
        Assert.Contains(
            $"assets/purify.min.js?v={result.Manifest.RendererAssetsVersion}",
            html,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
    }

    [Fact]
    public async Task ApplyingPublishPreservesCommittedDiscussionChooserLabel()
    {
        TicketSnapshotFixture preparerFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: 2);
        TicketSnapshotFixture plannerFixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair preparerPair =
            await preparerFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair plannerPair =
            await plannerFixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "preserved-label");

        TicketSitePublishResult discussion =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    preparerPair,
                    TicketSiteKind.Discussion,
                    output,
                    "Tickets for Discussion"));
        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                plannerPair,
                TicketSiteKind.Applying,
                output,
                "Tickets for Applying"));

        string chooser = await File.ReadAllTextAsync(
            Path.Combine(output, "index.html"));
        Assert.Contains(
            $"<div class=\"card-title\">{discussion.Manifest.DisplayTitle}</div>",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "<div class=\"card-title\">Tickets for Applying</div>",
            chooser,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "assets",
            "app.js")));
        Assert.False(File.Exists(Path.Combine(
            output,
            "applying",
            "assets",
            "components.js")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnknownWorkGroupFacetMatchesNullEmptyAndWhitespace(
        string? workGroup)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true);
        await fixture.SetTicketWorkGroupAsync("CDS-2001", workGroup);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(
            _root,
            $"unknown-workgroup-{Guid.NewGuid():N}");

        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                pair,
                TicketSiteKind.Discussion,
                output,
                "Tickets for Discussion"));

        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "index.html"));
        string databasePath = Path.Combine(
            _root,
            $"unknown-workgroup-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(
                databasePath,
                await ExtractEmbeddedDatabaseAsync(html));
            Assert.Equal(
                1,
                await ScalarAsync<long>(
                    databasePath,
                    """
                    SELECT COUNT(DISTINCT TicketKey)
                    FROM ticket_facets
                    WHERE Dimension = 'wg'
                      AND ValueKey = '__unknown__'
                    """));
            Assert.Equal(
                "CDS-2001",
                await ScalarAsync<string>(
                    databasePath,
                    """
                    SELECT TicketKey
                    FROM ticket_facets
                    WHERE Dimension = 'wg'
                      AND ValueKey = '__unknown__'
                    """));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(databasePath);
        }
    }

    [Theory]
    [InlineData("(unknown)", "value:(unknown)")]
    [InlineData("__unknown__", "value:__unknown__")]
    [InlineData("value:machine-key", "value:value:machine-key")]
    public async Task ReservedLookingFacetDisplaysKeepDistinctRendererKeys(
        string displayValue,
        string expectedValueKey)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        await fixture.SetTicketWorkGroupAsync("FHIR-1001", displayValue);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(
            _root,
            $"reserved-looking-facet-{Guid.NewGuid():N}");

        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                pair,
                TicketSiteKind.Discussion,
                output,
                "Tickets for Discussion"));

        string html = await File.ReadAllTextAsync(Path.Combine(
            output,
            "discussion",
            "index.html"));
        string databasePath = Path.Combine(
            _root,
            $"reserved-looking-facet-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(
                databasePath,
                await ExtractEmbeddedDatabaseAsync(html));
            Assert.Equal(
                expectedValueKey,
                await ScalarAsync<string>(
                    databasePath,
                    """
                    SELECT ValueKey
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'wg'
                    """));
            Assert.Equal(
                displayValue,
                await ScalarAsync<string>(
                    databasePath,
                    """
                    SELECT DisplayValue
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'wg'
                    """));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task StageValidationRejectsPresentationDrift()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: 2);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "presentation-drift");
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                BeforeStageValidationAsync: async (staging, token) =>
                {
                    string path = Path.Combine(staging, "index.html");
                    string html = await File.ReadAllTextAsync(path, token);
                    const string expected =
                        "\"siteName\":\"Tickets for Discussion - Sept 5, 2026\"";
                    Assert.Contains(expected, html, StringComparison.Ordinal);
                    await File.WriteAllTextAsync(
                        path,
                        html.Replace(
                            expected,
                            "\"siteName\":\"drifted\"",
                            StringComparison.Ordinal),
                        token);
                }));

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => publisher.PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    output,
                    "Tickets for Discussion")));

        Assert.Equal(TicketSitePublishFailure.Publication, exception.Failure);
        Assert.Contains(
            "presentation",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
    }

    [Fact]
    public async Task ChooserUsesSafeLabelAndOldOrMalformedManifestFallback()
    {
        TicketSnapshotFixture preparerFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: 2);
        TicketSnapshotFixture plannerFixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair preparerPair =
            await preparerFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair plannerPair =
            await plannerFixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "chooser-fallback");

        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                preparerPair,
                TicketSiteKind.Discussion,
                output,
                "<Tickets & Discussion>"));
        string chooserPath = Path.Combine(output, "index.html");
        string chooser = await File.ReadAllTextAsync(chooserPath);
        Assert.Contains(
            "&lt;Tickets &amp; Discussion&gt; - Sept 5, 2026",
            chooser,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<div class=\"card-title\"><Tickets",
            chooser,
            StringComparison.Ordinal);

        string discussionManifestPath = Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName);
        JsonObject oldManifest =
            JsonNode.Parse(await File.ReadAllTextAsync(discussionManifestPath))!
                .AsObject();
        Assert.True(oldManifest.Remove("displayTitle"));
        Assert.True(oldManifest.Remove("jiraSourceLastSuccessfulRefreshAt"));
        Assert.True(oldManifest.Remove("rendererSchemaVersion"));
        Assert.True(oldManifest.Remove("discussionReadiness"));
        Assert.True(oldManifest.Remove("discussionCorpus"));
        await File.WriteAllTextAsync(
            discussionManifestPath,
            oldManifest.ToJsonString(JsonOptions));
        TicketSiteManifest deserializedOldManifest =
            TicketSiteManifest.Read(discussionManifestPath);
        Assert.Null(deserializedOldManifest.DisplayTitle);
        Assert.Null(deserializedOldManifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Null(deserializedOldManifest.RendererSchemaVersion);
        Assert.Null(deserializedOldManifest.DiscussionReadiness);
        Assert.Null(deserializedOldManifest.DiscussionCorpus);
        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                plannerPair,
                TicketSiteKind.Applying,
                output,
                "Tickets for Applying"));

        chooser = await File.ReadAllTextAsync(chooserPath);
        Assert.Contains(
            "<div class=\"card-title\">Tickets for Discussion</div>",
            chooser,
            StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            discussionManifestPath,
            "{ malformed");
        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                plannerPair,
                TicketSiteKind.Applying,
                output,
                "Tickets for Applying"));
        chooser = await File.ReadAllTextAsync(chooserPath);
        Assert.Contains(
            "<div class=\"card-title\">Tickets for Discussion</div>",
            chooser,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscussionAssetsExposeRendererOnlyAccessibleContracts()
    {
        string template = await ReadEmbeddedResourceAsync(
            "web-assets/discussion/index.template.html");
        string componentsScript = await ReadEmbeddedResourceAsync(
            "web-assets/discussion/components.js");
        string appScript = await ReadEmbeddedResourceAsync(
            "web-assets/discussion/app.js");
        string stylesheet = await ReadEmbeddedResourceAsync(
            "web-assets/discussion/app.css");

        Assert.True(
            template.IndexOf(
                "assets/components.js",
                StringComparison.Ordinal) <
            template.IndexOf("assets/app.js", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "type=\"module\"",
            template,
            StringComparison.Ordinal);
        Assert.Contains(
            "button.type = 'button'",
            componentsScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "th.setAttribute('aria-sort'",
            componentsScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "originalIndex",
            componentsScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "unknownLast",
            componentsScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "/[A-Za-z][A-Za-z0-9]*-[0-9]+/g",
            componentsScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonicalKey = match[0].toUpperCase()",
            componentsScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "target = '_blank'",
            componentsScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "rel = 'noopener noreferrer'",
            componentsScript,
            StringComparison.Ordinal);

        foreach (string rendererTable in new[]
        {
            "site_metadata",
            "facet_dimensions",
            "tickets",
            "ticket_people",
            "ticket_facets",
            "summary_sources",
            "related_items",
            "topics",
            "topic_groups",
            "topic_members",
        })
        {
            Assert.Contains(rendererTable, appScript, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("prepared_", appScript, StringComparison.Ordinal);
        Assert.Contains("rendererSchemaVersion !== 3", appScript, StringComparison.Ordinal);
        Assert.Contains("CorpusSummaryJson", appScript, StringComparison.Ordinal);
        Assert.Contains("JSON.stringify(presentation.corpusSummary)", appScript, StringComparison.Ordinal);
        Assert.Contains(
            "resolveFacetValueKey",
            appScript,
            StringComparison.Ordinal);
        AssertFacetHashContract(appScript);
        AssertCopyForAiContract(appScript);
        Assert.Contains(
            "'Related GitHub Summary'",
            appScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sourcesByKind['related-github']",
            appScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "appendPlainSection(body, 'Existing Proposed'",
            appScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: repeat(4, minmax(0, 1fr))",
            stylesheet,
            StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: repeat(2, minmax(0, 1fr))",
            stylesheet,
            StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: minmax(0, 1fr)",
            stylesheet,
            StringComparison.Ordinal);
        Assert.Contains(
            ".grouped-ticket-table",
            stylesheet,
            StringComparison.Ordinal);
        Assert.Contains(
            "overflow-x: auto",
            stylesheet,
            StringComparison.Ordinal);
        Assert.Contains(".count-column", stylesheet, StringComparison.Ordinal);
        Assert.Contains(
            "white-space: nowrap",
            stylesheet,
            StringComparison.Ordinal);
        Assert.Contains(
            "min-inline-size:",
            stylesheet,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmittedCopyForAiSerializesZulipThreadContext()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: 2,
                includeRendererEvidence: true);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "copy-for-ai-zulip");

        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                pair,
                TicketSiteKind.Discussion,
                output,
                "Tickets for Discussion"));

        string appScriptPath = Path.Combine(
            output,
            "discussion",
            "assets",
            "app.js");
        string markdown = await RunCopyForAiProbeAsync(appScriptPath);
        const string expected =
            "### Related Zulip threads (2)\n\n" +
            "| Thread | Detail | Justification |\n" +
            "| --- | --- | --- |\n" +
            "| thread-1 | FHIR \u203a Ticket discussion \u00b7 " +
            "4 messages \u00b7 last 2026-09-10 | zulip why |\n" +
            "| thread-fallback | 2 messages | fallback why |\n\n";

        Assert.Equal(expected, markdown.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task ServiceMismatchIsRejectedBeforeDatabaseValidation()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        await File.WriteAllTextAsync(pair.DatabasePath, "not sqlite");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Applying,
                    Path.Combine(_root, "mismatch"),
                    "Wrong")));

        Assert.Equal(
            TicketSitePublishFailure.ServiceMismatch,
            exception.Failure);
        Assert.False(Directory.Exists(Path.Combine(_root, "mismatch")));
    }

    [Fact]
    public async Task ChangedVerifiedDatabaseIsRejectedByChecksum()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        await File.AppendAllTextAsync(pair.DatabasePath, "changed");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, Path.Combine(_root, "checksum"))));

        Assert.Equal(
            TicketSitePublishFailure.SnapshotValidation,
            exception.Failure);
        Assert.Contains(
            "length",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SchemaDriftIsRejectedUsingPublicCatalog()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        pair = await MutateAndReverifyAsync(
            pair,
            """
            ALTER TABLE prepared_ticket_repos
            ADD COLUMN UnexpectedV2Column TEXT
            """);

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, Path.Combine(_root, "schema-drift"))));

        Assert.Equal(
            TicketSitePublishFailure.SnapshotValidation,
            exception.Failure);
        Assert.Contains("schema v1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnexpectedV2Column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvenanceMismatchIsRejected()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        pair = await MutateAndReverifyAsync(
            pair,
            "UPDATE authoring_snapshot_provenance SET RunId = 'other-run'");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, Path.Combine(_root, "provenance"))));

        Assert.Contains(
            "provenance",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FiltersResolveCanonicallyAndTrimEmbeddedSite()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        TicketSitePublishResult result =
            await new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "filtered"),
                    "Tickets",
                    new TicketSiteFilters(
                        Specification: "fhir",
                        Project: "fhir",
                        WorkGroup: "fhir-i")));

        Assert.Equal("FHIR", result.ResolvedFilters.Specification);
        Assert.Equal("FHIR", result.ResolvedFilters.Project);
        Assert.Equal(
            "FHIR Infrastructure",
            result.ResolvedFilters.WorkGroup);
        Assert.Equal(1, result.Manifest.IncludedItemCount);
        Assert.Equal(2, result.Manifest.IncludedReceiptCount);
        Assert.Equal(1, result.Manifest.TableCounts["tickets"]);
    }

    [Fact]
    public async Task UnknownFilterReturnsTypedFailure()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "unknown-filter"),
                    "Tickets",
                    new TicketSiteFilters(Specification: "unknown"))));

        Assert.Equal(
            TicketSitePublishFailure.FilterValidation,
            exception.Failure);
        Assert.Contains("Available values", exception.Message);
    }

    [Fact]
    public async Task RepeatedBuildIsIdempotent()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 7);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "idempotent");
        TicketSitePublisher publisher = new();

        TicketSitePublishResult first =
            await publisher.PublishAsync(Request(pair, output));
        string manifestPath = Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName);
        DateTime firstWrite = File.GetLastWriteTimeUtc(manifestPath);
        await Task.Delay(50);
        TicketSitePublishResult second =
            await publisher.PublishAsync(Request(pair, output));

        Assert.Equal(TicketSitePublishOutcome.Published, first.Outcome);
        Assert.Equal(TicketSitePublishOutcome.Idempotent, second.Outcome);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(manifestPath));
        Assert.Equal(first.Manifest.GeneratedAt, second.Manifest.GeneratedAt);
    }

    [Fact]
    public async Task OlderSequenceCannotReplacePublishedSite()
    {
        TicketSnapshotFixture newer =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 11,
                snapshotId: "snapshot-11");
        TicketSnapshotFixture older =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 10,
                snapshotId: "snapshot-10");
        VerifiedAuthoringSnapshotPair newerPair =
            await newer.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair olderPair =
            await older.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "stale");
        TicketSitePublisher publisher = new();
        await publisher.PublishAsync(Request(newerPair, output));

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => publisher.PublishAsync(Request(olderPair, output)));

        Assert.Contains("older than", exception.Message, StringComparison.OrdinalIgnoreCase);
        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName));
        Assert.Equal("snapshot-11", manifest.SnapshotId);
    }

    [Fact]
    public async Task ValidationFailureRollsBackToPreviousSite()
    {
        TicketSnapshotFixture firstFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 20,
                snapshotId: "snapshot-20");
        TicketSnapshotFixture secondFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                sequence: 21,
                snapshotId: "snapshot-21");
        VerifiedAuthoringSnapshotPair firstPair =
            await firstFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair secondPair =
            await secondFixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "rollback");
        await new TicketSitePublisher().PublishAsync(Request(firstPair, output));
        TicketSitePublisher failing = new(new TicketSitePublisherTestHooks(
            BeforeStageValidationAsync: (_, _) =>
                throw new IOException("simulated staged validation failure")));

        await Assert.ThrowsAsync<TicketSitePublishException>(
            () => failing.PublishAsync(Request(secondPair, output)));

        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName));
        Assert.Equal("snapshot-20", manifest.SnapshotId);
    }

    [Fact]
    public async Task CleanupFailureBecomesSuccessfulResultWarning()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string? deferredPath = null;
        TicketSitePublisher publisher = new(new TicketSitePublisherTestHooks(
            DeleteFilteredDatabaseAsync: path =>
            {
                deferredPath = path;
                throw new IOException("simulated cleanup failure");
            }));

        try
        {
            TicketSitePublishResult result = await publisher.PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    TicketSiteKind.Discussion,
                    Path.Combine(_root, "cleanup-warning"),
                    "Tickets",
                    new TicketSiteFilters(Specification: "FHIR")));

            Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains(
                    "deferred cleanup",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (deferredPath is not null)
            {
                TestFileCleanup.SafeDeleteFile(deferredPath);
            }
        }
    }

    [Fact]
    public async Task CleanupFailureIsObservableWhenPublicationFails()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string? deferredPath = null;
        List<string> observedWarnings = [];
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                DeleteFilteredDatabaseAsync: path =>
                {
                    deferredPath = path;
                    throw new IOException("simulated cleanup failure");
                },
                BeforeStageValidationAsync: (_, _) =>
                    throw new IOException("simulated publication failure")),
            observedWarnings.Add);

        try
        {
            await Assert.ThrowsAsync<TicketSitePublishException>(
                () => publisher.PublishAsync(
                    new TicketSitePublishRequest(
                        pair,
                        TicketSiteKind.Discussion,
                        Path.Combine(_root, "failed-cleanup-warning"),
                        "Tickets",
                        new TicketSiteFilters(Specification: "FHIR"))));

            Assert.Contains(
                observedWarnings,
                warning => warning.Contains(
                    "deferred cleanup",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (deferredPath is not null)
            {
                TestFileCleanup.SafeDeleteFile(deferredPath);
            }
        }
    }

    [Fact]
    public async Task CleanupFailureIsObservableWhenPublicationIsCanceled()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string? deferredPath = null;
        List<string> observedWarnings = [];
        using CancellationTokenSource cancellation = new();
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                DeleteFilteredDatabaseAsync: path =>
                {
                    deferredPath = path;
                    throw new IOException("simulated cleanup failure");
                },
                BeforeStageValidationAsync: (_, token) =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
            observedWarnings.Add);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => publisher.PublishAsync(
                    new TicketSitePublishRequest(
                        pair,
                        TicketSiteKind.Discussion,
                        Path.Combine(_root, "canceled-cleanup-warning"),
                        "Tickets",
                        new TicketSiteFilters(Specification: "FHIR")),
                    cancellation.Token));

            Assert.Contains(
                observedWarnings,
                warning => warning.Contains(
                    "deferred cleanup",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (deferredPath is not null)
            {
                TestFileCleanup.SafeDeleteFile(deferredPath);
            }
        }
    }

    [Fact]
    public async Task ConcurrentSiteKindsSerializeChooserAndPreserveBothStates()
    {
        TicketSnapshotFixture preparerFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        TicketSnapshotFixture plannerFixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair preparerPair =
            await preparerFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair plannerPair =
            await plannerFixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "concurrent-chooser");
        object counterGate = new();
        int activeChooserWriters = 0;
        int maximumChooserWriters = 0;
        TicketSitePublisherTestHooks hooks = new(
            BeforeChooserCommitAsync: async (_, _, token) =>
            {
                lock (counterGate)
                {
                    activeChooserWriters++;
                    maximumChooserWriters = Math.Max(
                        maximumChooserWriters,
                        activeChooserWriters);
                }
                try
                {
                    await Task.Delay(75, token);
                }
                finally
                {
                    lock (counterGate)
                    {
                        activeChooserWriters--;
                    }
                }
            });

        Task<TicketSitePublishResult> discussion =
            new TicketSitePublisher(hooks).PublishAsync(
                Request(preparerPair, output));
        Task<TicketSitePublishResult> applying =
            new TicketSitePublisher(hooks).PublishAsync(
                new TicketSitePublishRequest(
                    plannerPair,
                    TicketSiteKind.Applying,
                    output,
                    "Tickets for Applying"));
        await Task.WhenAll(discussion, applying);

        Assert.Equal(1, maximumChooserWriters);
        string chooser = await File.ReadAllTextAsync(
            Path.Combine(output, "index.html"));
        Assert.Contains(
            "card card-discussion live",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "card card-applying live",
            chooser,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicationLockStaysInsideCallerOwnedOutputRoot()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string parent = Path.Combine(_root, "read-only-parent");
        string output = Path.Combine(parent, "site");
        Directory.CreateDirectory(output);
        string expectedLockPath = Path.Combine(
            output,
            TicketSiteOutputRootLock.LockFileName);
        int openAttempts = 0;
        TicketSitePublisher publisher = new(new TicketSitePublisherTestHooks(
            OpenOutputRootLockFile: path =>
            {
                openAttempts++;
                if (!string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(expectedLockPath),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException(
                        "The simulated read-only parent rejects lock files.");
                }

                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }));

        TicketSitePublishResult result = await publisher.PublishAsync(
            Request(pair, output));

        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.Equal(1, openAttempts);
        Assert.True(File.Exists(expectedLockPath));
        Assert.Equal(
            [output],
            Directory.EnumerateFileSystemEntries(parent).ToArray());
    }

    [Fact]
    public async Task PermanentOutputRootLockIoFailureIsNotRetried()
    {
        string output = Path.Combine(_root, "permanent-lock-failure");
        int openAttempts = 0;

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => TicketSiteOutputRootLock.AcquireAsync(
                    output,
                    CancellationToken.None,
                    path =>
                    {
                        openAttempts++;
                        Assert.True(Directory.Exists(output));
                        throw new IOException(
                            "simulated disk-full failure",
                            unchecked((int)0x80070070));
                    })
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(1, openAttempts);
        Assert.Contains(output, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            TicketSiteOutputRootLock.LockFileName,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "simulated disk-full failure",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChooserFailureAfterCommitReturnsSuccessAndReplacesNothing()
    {
        TicketSnapshotFixture preparerFixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        TicketSnapshotFixture plannerFixture =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        VerifiedAuthoringSnapshotPair preparerPair =
            await preparerFixture.CreateVerifiedPairAsync("Preparer");
        VerifiedAuthoringSnapshotPair plannerPair =
            await plannerFixture.CreateVerifiedPairAsync("Planner");
        string output = Path.Combine(_root, "chooser-failure");
        await new TicketSitePublisher().PublishAsync(
            Request(preparerPair, output));
        string chooserPath = Path.Combine(output, "index.html");
        string chooserCssPath = Path.Combine(output, "assets", "chooser.css");
        string originalChooser = await File.ReadAllTextAsync(chooserPath);
        string originalCss = await File.ReadAllTextAsync(chooserCssPath);
        List<string> observedWarnings = [];
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                BeforeChooserCommitAsync: (_, _, _) =>
                    throw new IOException("simulated chooser failure")),
            observedWarnings.Add);

        TicketSitePublishResult result = await publisher.PublishAsync(
            new TicketSitePublishRequest(
                plannerPair,
                TicketSiteKind.Applying,
                output,
                "Tickets for Applying"));

        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "index.html")));
        Assert.Equal(originalChooser, await File.ReadAllTextAsync(chooserPath));
        Assert.Equal(originalCss, await File.ReadAllTextAsync(chooserCssPath));
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains(
                "chooser",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            observedWarnings,
            warning => warning.Contains(
                "chooser",
                StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(
            output,
            "*.tmp-*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationAfterSiteCommitDoesNotHideSuccess()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "post-commit-cancellation");
        using CancellationTokenSource cancellation = new();
        TicketSitePublisher publisher = new(
            new TicketSitePublisherTestHooks(
                BeforeChooserCommitAsync: (_, _, _) =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }));

        TicketSitePublishResult result = await publisher.PublishAsync(
            Request(pair, output),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(TicketSitePublishOutcome.Published, result.Outcome);
        Assert.True(File.Exists(Path.Combine(output, "discussion", "index.html")));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task UnownedSiteRequiresForceBeforeTakeover()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "unowned");
        string discussion = Path.Combine(output, "discussion");
        Directory.CreateDirectory(discussion);
        await File.WriteAllTextAsync(
            Path.Combine(discussion, "index.html"),
            "unrelated");
        TicketSitePublisher publisher = new();

        TicketSitePublishException exception = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => publisher.PublishAsync(Request(pair, output)));
        Assert.Contains(
            "not owned",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        TicketSitePublishResult forced = await publisher.PublishAsync(
            Request(pair, output) with { Force = true });
        Assert.Equal(TicketSitePublishOutcome.Published, forced.Outcome);
        Assert.True(File.Exists(Path.Combine(
            discussion,
            TicketSiteManifest.FileName)));
    }

    [Fact]
    public async Task PublicationCanRetryFromSameAlreadyVerifiedPair()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "retry");
        TicketSitePublisher failing = new(new TicketSitePublisherTestHooks(
            BeforeStageValidationAsync: (_, _) =>
                throw new IOException("first attempt failed")));

        await Assert.ThrowsAsync<TicketSitePublishException>(
            () => failing.PublishAsync(Request(pair, output)));
        TicketSitePublishResult retried =
            await new TicketSitePublisher().PublishAsync(Request(pair, output));

        Assert.Equal(TicketSitePublishOutcome.Published, retried.Outcome);
        Assert.Equal(pair.SnapshotId, retried.Manifest.SnapshotId);
    }

    [Fact]
    public async Task OutputCannotContainOrBeContainedByInputPair()
    {
        string output = Path.Combine(_root, "contained");
        Directory.CreateDirectory(output);
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(output);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, output)));

        Assert.Contains(
            "publisher control",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        TicketSnapshotFixture outside =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair outsidePair =
            await outside.CreateVerifiedPairAsync("Preparer");
        string nestedOutput = Path.Combine(
            outsidePair.DirectoryPath,
            "site");
        TicketSitePublishException nested = await Assert.ThrowsAsync<
            TicketSitePublishException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(outsidePair, nestedOutput)));
        Assert.Equal(TicketSitePublishFailure.InvalidRequest, nested.Failure);
    }

    [Fact]
    public async Task CancellationStopsBeforePublication()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "canceled");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TicketSitePublisher().PublishAsync(
                Request(pair, output),
                cancellation.Token));

        Assert.False(Directory.Exists(output));
    }

    private static TicketSitePresentation ReadPresentation(string html)
    {
        const string marker = "<script id=\"site-presentation\" type=\"application/json\">";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return TicketSitePresentationJson.Deserialize(html[start..end]);
    }

    private static async Task<string> ReadEmbeddedResourceAsync(string name)
    {
        Assembly assembly = typeof(TicketSitePublisher).Assembly;
        await using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Missing embedded resource '{name}'.");
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<byte[]> ReadEmbeddedResourceBytesAsync(
        string name)
    {
        Assembly assembly = typeof(TicketSitePublisher).Assembly;
        await using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Missing embedded resource '{name}'.");
        using MemoryStream output = new();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static byte[] ExtractEmbeddedWasm(string html)
    {
        const string marker = "window.__SQL_WASM__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Embedded SQL.js WASM marker was not found.");
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        Assert.True(end > start, "Embedded SQL.js WASM payload was not found.");
        return Convert.FromBase64String(html[start..end]);
    }

    private static async Task<byte[]> ExtractEmbeddedDatabaseAsync(string html)
    {
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Embedded database marker was not found.");
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        Assert.True(end > start, "Embedded database payload was not found.");
        byte[] compressed = Convert.FromBase64String(html[start..end]);
        await using MemoryStream input = new(compressed);
        await using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        await gzip.CopyToAsync(output);
        return output.ToArray();
    }

    private async Task<string> RunCopyForAiProbeAsync(string appScriptPath)
    {
        string runnerPath = Path.Combine(
            _root,
            $"copy-for-ai-probe-{Guid.NewGuid():N}.cjs");
        await File.WriteAllTextAsync(
            runnerPath,
            """
            const fs = require('node:fs');
            const vm = require('node:vm');

            const appPath = process.argv[2];
            const marker = "  if (document.readyState === 'loading') {";
            let source = fs.readFileSync(appPath, 'utf8');
            if (!source.includes(marker)) {
              throw new Error('Copy for AI probe marker was not found.');
            }

            const probe = `
              globalThis.__copyForAiMarkdown = (function () {
                var zulipRows = [
                  {
                    ItemKey: 'thread-1',
                    LinkType: null,
                    Label: 'FHIR \u203a Ticket discussion',
                    Detail: '4 messages \u00b7 last 2026-09-10',
                    Justification: 'zulip why',
                    HydrationStatus: 'resolved',
                    HydrationReason: null
                  },
                  {
                    ItemKey: 'thread-fallback',
                    LinkType: null,
                    Label: 'thread-fallback',
                    Detail: '2 messages',
                    Justification: 'fallback why',
                    HydrationStatus: 'resolved',
                    HydrationReason: null
                  }
                ];
                db = {
                  prepare: function (sql) {
                    var projection = sql.slice(0, sql.indexOf('FROM'));
                    var rows = sql.indexOf("Kind = 'zulip'") >= 0
                      ? zulipRows
                      : [];
                    var index = -1;
                    return {
                      bind: function () {},
                      step: function () {
                        index++;
                        return index < rows.length;
                      },
                      getAsObject: function () {
                        var projected = {};
                        Object.keys(rows[index]).forEach(function (key) {
                          if (projection.indexOf(key) >= 0) {
                            projected[key] = rows[index][key];
                          }
                        });
                        return projected;
                      },
                      getColumnNames: function () { return []; },
                      free: function () {}
                    };
                  }
                };
                return serializeZulipItemsMarkdown(
                  readCopyRelatedItems('FHIR-1001').relatedZulip);
              })();
            `;
            source = source.replace(marker, probe + '\n' + marker);

            const sandbox = {
              document: {
                readyState: 'loading',
                addEventListener: function () {}
              },
              window: {}
            };
            vm.runInNewContext(source, sandbox, { filename: appPath });
            process.stdout.write(sandbox.__copyForAiMarkdown);
            """);

        ProcessStartInfo startInfo = new()
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.ArgumentList.Add(appScriptPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Node.js.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string standardOutput = await output;
        string standardError = await error;
        Assert.True(
            process.ExitCode == 0,
            $"Copy for AI JavaScript probe failed:{Environment.NewLine}" +
            standardError);
        return standardOutput;
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(
        string databasePath)
    {
        List<string> names = [];
        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<T> ScalarAsync<T>(
        string databasePath,
        string sql)
    {
        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        if (value is T typed)
        {
            return typed;
        }
        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertFacetHashContract(string script)
    {
        Assert.Contains(
            "FROM facet_dimensions ORDER BY SortOrder",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "facetKeyParameters[dimensionKey] = dimensionKey + 'Key';",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "parameters.getAll(facetKeyParameters[currentDimension])",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "parameters.getAll(currentDimension)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "resolveLegacyFacetDisplayValue",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "value.toLowerCase() === '(unknown)'",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "value.toLowerCase() === '__unknown__'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "facetKeyParameters[dimension],",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "removeChipValue(capturedDimension, capturedValueKey);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "function withFacetSelection(dimension, valueKey, state)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "return Object.freeze(frozen);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "visibleListFacetDimensions",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var Crosscuts = {",
            script,
            StringComparison.Ordinal);
    }

    private static void AssertCopyForAiContract(string script)
    {
        int start = script.IndexOf(
            "function readCopyRelatedItems",
            StringComparison.Ordinal);
        int end = script.IndexOf(
            "if (document.readyState === 'loading')",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string contract = script[start..end];

        AssertAppearsInOrder(
            contract,
            "Kind = 'repo'",
            "Kind = 'jira'",
            "Kind = 'jira-xref'",
            "Kind = 'zulip'",
            "Kind = 'github'");
        AssertAppearsInOrder(
            contract,
            "['Key', ticket.Key]",
            "['Title', ticket.Title]",
            "['Workgroup', ticket.WorkGroup]",
            "['Status', ticket.Status]",
            "['Type', ticket.Type]",
            "['Priority', ticket.Priority]",
            "['Resolution', ticket.Resolution]",
            "['Specification', ticket.Specification]",
            "['Raised in', ticket.RaisedInVersion]",
            "['Selected ballot', ticket.SelectedBallot]",
            "['Change category', ticket.ChangeCategory]",
            "['Impact', ticket.Impact]",
            "['Comments', ticket.CommentCount]",
            "['Recommendation', ticket.Recommendation]",
            "['Saved', ticket.SavedAt]");
        AssertAppearsInOrder(
            contract,
            "'Original request'",
            "'Proposed / accepted resolution'",
            "['Request Summary', ticket.RequestSummary]",
            "['Comment Summary', ticket.CommentSummary]",
            "['Linked Ticket Summary', ticket.LinkedTicketSummary]",
            "['Related Ticket Summary', ticket.RelatedTicketSummary]",
            "['Related Zulip Summary', ticket.RelatedZulipSummary]",
            "['Related GitHub Summary', ticket.RelatedGitHubSummary]",
            "['Existing Proposed', ticket.ExistingProposed]",
            "'Proposal A'",
            "'Proposal B'",
            "'Proposal C'",
            "'Recommendation'");
        AssertAppearsInOrder(
            contract,
            "serializeRepoItemsMarkdown(context.repos || [])",
            "serializeJiraItemsMarkdown(context.relatedJira || [])",
            "serializeJiraXrefItemsMarkdown(context.jiraXrefs || [])",
            "serializeZulipItemsMarkdown(context.relatedZulip || [])",
            "serializeGitHubItemsMarkdown(context.relatedGitHub || [])");
        Assert.Contains(
            "['Repo', 'Category', 'Detail', 'Justification']",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "['Key', 'Link type', 'Detail', 'Justification']",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "['Source', 'Key', 'Detail']",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "['Thread', 'Detail', 'Justification']",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "['Item', 'Detail', 'Justification']",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (items.length === 0) return '';",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "String(item.ItemKey || '')",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "SELECT ItemKey, LinkType, Label, Detail, Justification",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "copyZulipDetail(item)",
            contract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ticket_people",
            contract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "summary_sources",
            contract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DisplayName",
            contract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "['Thread', 'Link type'",
            contract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "['Item', 'Link type'",
            contract,
            StringComparison.Ordinal);
    }

    private static void AssertAppearsInOrder(
        string value,
        params string[] expectedValues)
    {
        int offset = 0;
        foreach (string expected in expectedValues)
        {
            int index = value.IndexOf(
                expected,
                offset,
                StringComparison.Ordinal);
            Assert.True(
                index >= 0,
                $"Expected '{expected}' after offset {offset}.");
            offset = index + expected.Length;
        }
    }

    private static TicketSitePublishRequest Request(
        VerifiedAuthoringSnapshotPair pair,
        string output)
        => new(
            pair,
            TicketSiteKind.Discussion,
            output,
            "Tickets");

    private static async Task<VerifiedAuthoringSnapshotPair>
        MutateAndReverifyAsync(
            VerifiedAuthoringSnapshotPair pair,
            string sql)
    {
        await using (SqliteConnection connection = new(
            $"Data Source={pair.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        string databaseSha256 = await ComputeHashAsync(pair.DatabasePath);
        AuthoringSnapshotDescriptor descriptor = pair.Descriptor with
        {
            Sha256 = databaseSha256,
            SizeBytes = new FileInfo(pair.DatabasePath).Length,
        };
        await File.WriteAllTextAsync(
            pair.DescriptorPath,
            JsonSerializer.Serialize(descriptor, JsonOptions));
        byte[] descriptorBytes =
            await File.ReadAllBytesAsync(pair.DescriptorPath);
        AuthoringSnapshotPairManifest manifest = pair.Manifest with
        {
            SizeBytes = descriptor.SizeBytes,
            DescriptorSha256 =
                Convert.ToHexString(SHA256.HashData(descriptorBytes))
                    .ToLowerInvariant(),
            DatabaseSha256 = databaseSha256,
        };
        await File.WriteAllTextAsync(
            pair.ReadyManifestPath!,
            JsonSerializer.Serialize(manifest, JsonOptions));
        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                pair.ServiceName,
                pair.RunId,
                pair.DirectoryPath);
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }
}
