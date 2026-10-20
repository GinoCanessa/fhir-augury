using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using FhirAugury.Publishing.Tickets;
using FhirAugury.Publishing.Tickets.Tests;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class SiteBuildManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ticket-site-cli-{Guid.NewGuid():N}");

    public SiteBuildManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_root);

    [Theory]
    [InlineData(PreparedTicketSnapshotSchemaV1.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV2.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV3.Version)]
    public async Task PreparerInvocationWritesCompatibleSummaryAndChooser(int schemaVersion)
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: schemaVersion,
                includeSecondTicket: true,
                useMultipleRuns: true,
                includeRendererEvidence: true,
                firstJiraUpdatedAt: "2026-09-14T20:15:00-05:00",
                secondJiraUpdatedAt: "2026-09-25T12:00:00Z");
        byte[] originalDatabase = await File.ReadAllBytesAsync(snapshot.DatabasePath);
        byte[] originalDescriptor = await File.ReadAllBytesAsync(snapshot.DescriptorPath);
        string output = Path.Combine(_root, "site");

        (int exit, string stdout, string stderr) =
            await ChooserAndCliTests.RunAsync(
                "--preparer-snapshot", snapshot.DatabasePath,
                "--snapshot-descriptor", snapshot.DescriptorPath,
                "--out", output,
                "--title", "Discussion tickets",
                "--spec", "fhir",
                "--project", "fhir",
                "--wg", "fhir-i",
                "--force");

        Assert.True(exit == 0, stderr);
        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName));
        Assert.Equal(snapshot.Descriptor.SnapshotId, manifest.SnapshotId);
        Assert.Equal(
            schemaVersion,
            manifest.SnapshotSchemaVersion);
        Assert.Equal("Discussion tickets", manifest.Title);
        Assert.Equal(
            "Discussion tickets - Sept 15, 2026 " +
            "(filtered: spec=FHIR, project=FHIR, wg=FHIR Infrastructure)",
            manifest.DisplayTitle);
        Assert.Equal(
            schemaVersion == 1 ? (DateTimeOffset?)null : new DateTimeOffset(
                2026,
                9,
                8,
                5,
                0,
                0,
                TimeSpan.Zero),
            manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(3, manifest.RendererSchemaVersion);
        Assert.NotNull(manifest.DiscussionReadiness);
        Assert.Equal(schemaVersion == 3, manifest.DiscussionReadiness.IsReady);
        if (schemaVersion < 3)
        {
            Assert.Contains(
                manifest.DiscussionReadiness.Reasons,
                reason => reason.Code ==
                    DiscussionPublicationReadinessReasonCodes.LegacySnapshotSchema);
        }
        DiscussionCorpusSummary corpus = Assert.IsType<DiscussionCorpusSummary>(manifest.DiscussionCorpus);
        Assert.Equal(1, corpus.TicketCount);
        Assert.Equal(1, corpus.ExportedProjectCount);
        Assert.Equal(1, corpus.ValidJiraUpdatedAtCount);
        Assert.Equal(DiscussionDateCoverage.Complete, corpus.DateCoverage);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 1, 15, 0, TimeSpan.Zero), corpus.MaxJiraUpdatedAt);
        Assert.Equal(schemaVersion == 3 ? 1 : 0, corpus.TicketsWithPublicReporter);
        Assert.Equal(schemaVersion == 3 ? 1 : 0, corpus.TicketsWithPublicAssignee);
        Assert.Equal(schemaVersion == 3 ? 1 : 0, corpus.TicketsWithPublicRequester);
        Assert.Equal(1, manifest.IncludedItemCount);
        Assert.Equal(2, manifest.IncludedReceiptCount);
        Assert.Contains("site_metadata", manifest.TableCounts.Keys);
        Assert.Contains("ticket_people", manifest.TableCounts.Keys);
        Assert.Contains(
            snapshot.Descriptor.SnapshotId,
            stdout,
            StringComparison.Ordinal);
        Assert.Contains(TicketSiteManifest.ToSummaryJson(manifest), stdout, StringComparison.Ordinal);
        Assert.Contains(
            "Resolved --spec 'fhir' → 'FHIR'.",
            stdout,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        string chooser = await File.ReadAllTextAsync(
            Path.Combine(output, "index.html"));
        Assert.Contains(
            "Discussion tickets - Sept 15, 2026 " +
            "(filtered: spec=FHIR, project=FHIR, wg=FHIR Infrastructure)",
            chooser,
            StringComparison.Ordinal);
        await AssertEmittedDiscussionAsync(
            manifest, ["2026-09-15T01:15:00.0000000+00:00"]);
        Assert.Equal(originalDatabase, await File.ReadAllBytesAsync(snapshot.DatabasePath));
        Assert.Equal(originalDescriptor, await File.ReadAllBytesAsync(snapshot.DescriptorPath));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("partial")]
    [InlineData("empty")]
    public async Task PreparerInvocationReportsIncompleteAndEmptyDateCoverage(string coverage)
    {
        bool empty = coverage == "empty";
        string? firstDate = coverage == "none" ? null : TicketSnapshotFixture.FirstJiraUpdatedAt;
        TicketSnapshotFixture snapshot = await TicketSnapshotFixture.CreatePreparerAsync(
            _root, includeSecondTicket: true, schemaVersion: 3,
            firstJiraUpdatedAt: firstDate, secondJiraUpdatedAt: null);
        string output = Path.Combine(_root, "coverage-site");
        List<string> args =
        [
            "--preparer-snapshot", snapshot.DatabasePath,
            "--snapshot-descriptor", snapshot.DescriptorPath,
            "--out", output,
        ];
        if (empty)
            args.AddRange(["--spec", "FHIR", "--project", "CDS"]);
        (int exit, string stdout, string stderr) =
            await ChooserAndCliTests.RunAsync(args.ToArray());
        Assert.True(exit == 0, stderr);
        TicketSiteManifest manifest = TicketSiteManifest.Read(
            Path.Combine(output, "discussion", TicketSiteManifest.FileName));
        DiscussionCorpusSummary corpus = Assert.IsType<DiscussionCorpusSummary>(manifest.DiscussionCorpus);
        Assert.Equal(coverage, corpus.DateCoverage);
        Assert.Equal(empty ? 0 : 2, corpus.TicketCount);
        Assert.Equal(coverage == "partial" ? 1 : 0, corpus.ValidJiraUpdatedAtCount);
        Assert.Equal(
            empty ? "Tickets for Discussion (filtered: spec=FHIR, project=CDS)" : "Tickets for Discussion",
            manifest.DisplayTitle);
        Assert.Contains($"update-date coverage is {coverage}", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains(TicketSiteManifest.ToSummaryJson(manifest), stdout, StringComparison.Ordinal);
        await AssertEmittedDiscussionAsync(manifest, empty ? [] : [null, firstDate]);
    }

    [Fact]
    public async Task PlannerInvocationWritesApplyingSite()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        string output = Path.Combine(_root, "planner-site");

        (int exit, string stdout, string stderr) =
            await ChooserAndCliTests.RunAsync(
                "--planner-snapshot", snapshot.DatabasePath,
                "--snapshot-descriptor", snapshot.DescriptorPath,
                "--out", output);

        Assert.True(exit == 0, stderr);
        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "applying",
            TicketSiteManifest.FileName));
        Assert.Equal("Ticket Site", manifest.Title);
        Assert.Equal(1, manifest.SnapshotSchemaVersion);
        Assert.Null(manifest.DisplayTitle);
        Assert.Null(manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Null(manifest.RendererSchemaVersion);
        Assert.Null(manifest.DiscussionReadiness);
        Assert.Null(manifest.DiscussionCorpus);
        Assert.DoesNotContain("discussionCorpus", stdout, StringComparison.Ordinal);
        Assert.Contains(
            snapshot.Descriptor.SnapshotId,
            stdout,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "index.html")));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
    }

    private async Task AssertEmittedDiscussionAsync(
        TicketSiteManifest manifest,
        IReadOnlyList<string?> expectedDates)
    {
        string html = await File.ReadAllTextAsync(Path.Combine(manifest.OutputPath, "index.html"));
        string encodedTitle = WebUtility.HtmlEncode(Assert.IsType<string>(manifest.DisplayTitle));
        Assert.Contains($"<title>{encodedTitle}</title>", html, StringComparison.Ordinal);
        Assert.Contains($"<h1>{encodedTitle}</h1>", html, StringComparison.Ordinal);
        string corpusJson = JsonSerializer.Serialize(
            manifest.DiscussionCorpus, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using JsonDocument presentation = JsonDocument.Parse(Extract(
            html, "<script id=\"site-presentation\" type=\"application/json\">", "</script>"));
        Assert.Equal(3, presentation.RootElement.GetProperty("rendererSchemaVersion").GetInt32());
        Assert.Equal(manifest.Title, presentation.RootElement.GetProperty("baseTitle").GetString());
        Assert.Equal(manifest.DisplayTitle, presentation.RootElement.GetProperty("siteName").GetString());
        Assert.Equal(corpusJson, presentation.RootElement.GetProperty("corpusSummary").GetRawText());
        using JsonDocument manifestJson = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(manifest.OutputPath, TicketSiteManifest.FileName)));
        JsonElement manifestCorpus = manifestJson.RootElement.GetProperty("discussionCorpus");
        Assert.True(manifestCorpus.TryGetProperty("maxJiraUpdatedAt", out JsonElement maximum));
        Assert.Equal(
            manifest.DiscussionCorpus?.MaxJiraUpdatedAt is null ? JsonValueKind.Null : JsonValueKind.String,
            maximum.ValueKind);

        byte[] compressed = Convert.FromBase64String(Extract(html, "window.__DB__='", "'"));
        using MemoryStream input = new(compressed);
        await using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream database = new();
        await gzip.CopyToAsync(database);
        string path = Path.Combine(_root, $"emitted-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(path, database.ToArray());
            await using SqliteConnection connection = new(
                $"Data Source={path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT RendererSchemaVersion, SiteName, CorpusSummaryJson FROM site_metadata";
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(3, reader.GetInt32(0));
                Assert.Equal(manifest.DisplayTitle, reader.GetString(1));
                Assert.Equal(corpusJson, reader.GetString(2));
                Assert.False(await reader.ReadAsync());
            }
            command.CommandText = "SELECT JiraUpdatedAt FROM tickets ORDER BY Key";
            List<string?> dates = [];
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string? date = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (date is not null)
                        Assert.Equal(date, DateTimeOffset.Parse(date, CultureInfo.InvariantCulture)
                            .ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    dates.Add(date);
                }
            }
            Assert.Equal(expectedDates, dates);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(path);
        }
    }

    private static string Extract(string html, string opener, string closer)
    {
        int start = html.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += opener.Length;
        int end = html.IndexOf(closer, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return html[start..end];
    }
}
