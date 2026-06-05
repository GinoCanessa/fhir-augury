using System.Net;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class SpecificationBackfillServiceTests
{
    [Fact]
    public async Task RunAsync_PopulatesEmptySpecifications_FromHttpSource()
    {
        string dbPath = NewDbPath();
        await SeedSourceTicketsAsync(dbPath,
        [
            ("FHIR-1", ""),
            ("FHIR-2", ""),
            ("FHIR-3", "preexisting"),
        ]);

        Dictionary<string, string> serverSpecs = new()
        {
            ["FHIR-1"] = "fhir-core",
            ["FHIR-2"] = "fhir-extensions",
        };
        using FakeJiraSourceServer server = new(serverSpecs);

        SpecificationBackfillService service = CreateService(httpBaseUrl: server.BaseUrl);

        SpecificationBackfillResult result = await service.RunAsync(dbPath, CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal(2, result.Updated);
        Dictionary<string, string> finalSpecs = await ReadSpecsAsync(dbPath);
        Assert.Equal("fhir-core", finalSpecs["FHIR-1"]);
        Assert.Equal("fhir-extensions", finalSpecs["FHIR-2"]);
        Assert.Equal("preexisting", finalSpecs["FHIR-3"]);
    }

    [Fact]
    public async Task RunAsync_FallsBackToSqlite_WhenHttpUnreachable()
    {
        string dbPath = NewDbPath();
        await SeedSourceTicketsAsync(dbPath,
        [
            ("FHIR-1", ""),
        ]);

        string sourceDbPath = NewDbPath("jira-source");
        await SeedJiraSourceDbAsync(sourceDbPath, new Dictionary<string, string>
        {
            ["FHIR-1"] = "fhir-core",
        });

        int unusedPort = FindFreeTcpPort();
        SpecificationBackfillService service = CreateService(
            httpBaseUrl: $"http://127.0.0.1:{unusedPort}",
            jiraSourceDbPath: sourceDbPath);

        SpecificationBackfillResult result = await service.RunAsync(dbPath, CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal(1, result.Updated);
        Dictionary<string, string> finalSpecs = await ReadSpecsAsync(dbPath);
        Assert.Equal("fhir-core", finalSpecs["FHIR-1"]);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureResult_WhenBothUpstreamsUnreachable()
    {
        string dbPath = NewDbPath();
        await SeedSourceTicketsAsync(dbPath,
        [
            ("FHIR-1", ""),
        ]);

        int unusedPort = FindFreeTcpPort();
        SpecificationBackfillService service = CreateService(
            httpBaseUrl: $"http://127.0.0.1:{unusedPort}");

        SpecificationBackfillResult result = await service.RunAsync(dbPath, CancellationToken.None);

        Assert.NotNull(result.Failure);
        Assert.Contains("Jira source HTTP unreachable", result.Failure!.Reason, StringComparison.Ordinal);
        Assert.Contains("JiraSourceDbPath", result.Failure.Reason, StringComparison.Ordinal);
        Assert.Equal(0, result.Updated);
    }

    [Fact]
    public async Task RunAsync_NoEmptyKeys_IsShortCircuitNoOp()
    {
        string dbPath = NewDbPath();
        await SeedSourceTicketsAsync(dbPath,
        [
            ("FHIR-1", "fhir-core"),
        ]);

        // HTTP base intentionally unreachable; short-circuit must not touch it.
        int unusedPort = FindFreeTcpPort();
        SpecificationBackfillService service = CreateService(
            httpBaseUrl: $"http://127.0.0.1:{unusedPort}");

        SpecificationBackfillResult result = await service.RunAsync(dbPath, CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.StillEmpty);
        Assert.Equal(0, result.NotFound);
    }

    private static SpecificationBackfillService CreateService(string httpBaseUrl, string? jiraSourceDbPath = null)
    {
        HttpClient client = new()
        {
            BaseAddress = new Uri(httpBaseUrl.EndsWith('/') ? httpBaseUrl : httpBaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        HydrationOptions options = new() { JiraSourceDbPath = jiraSourceDbPath };
        return new SpecificationBackfillService(
            client,
            Options.Create(options),
            NullLogger<SpecificationBackfillService>.Instance);
    }

    private static async Task SeedSourceTicketsAsync(string dbPath, IReadOnlyList<(string Key, string Specification)> tickets)
    {
        // Construct the store so EnsureSchema runs against a fresh DB.
        _ = new JiraProcessingSourceTicketStore(dbPath);
        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync();
        foreach ((string key, string spec) in tickets)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO jira_processing_source_tickets " +
                "(Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification, SourceTicketShape, LastSyncedAt, LastUpdated, ProcessingAttemptCount, ProcessingStatus) " +
                "VALUES (@id, @key, @title, NULL, 'FHIR', 'Open', 'FHIR Infrastructure', 'Change Request', @spec, 'default', @synced, @updated, 0, 'Done')";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@title", $"Title {key}");
            cmd.Parameters.AddWithValue("@spec", spec);
            cmd.Parameters.AddWithValue("@synced", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Dictionary<string, string>> ReadSpecsAsync(string dbPath)
    {
        Dictionary<string, string> specs = new(StringComparer.Ordinal);
        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Key, Specification FROM jira_processing_source_tickets";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            specs[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        }

        return specs;
    }

    private static async Task SeedJiraSourceDbAsync(string dbPath, IReadOnlyDictionary<string, string> specsByKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".");
        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE jira_issues (Key TEXT PRIMARY KEY, Specification TEXT)";
        await create.ExecuteNonQueryAsync();
        foreach ((string key, string spec) in specsByKey)
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO jira_issues (Key, Specification) VALUES (@k, @s)";
            insert.Parameters.AddWithValue("@k", key);
            insert.Parameters.AddWithValue("@s", spec);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static string NewDbPath(string prefix = "spec-backfill-svc")
        => Path.Combine(AppContext.BaseDirectory, $"{prefix}-{Guid.NewGuid():N}.db");

    private static int FindFreeTcpPort()
    {
        System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}

internal sealed class FakeJiraSourceServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Dictionary<string, string> _specsByKey;

    public FakeJiraSourceServer(IReadOnlyDictionary<string, string> specsByKey)
    {
        _specsByKey = new Dictionary<string, string>(specsByKey, StringComparer.Ordinal);
        int port = FindFreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public string BaseUrl { get; }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            try
            {
                if (ctx.Request.HttpMethod == "POST"
                    && ctx.Request.Url is { } url
                    && url.AbsolutePath.Equals("/api/v1/local-processing/tickets", StringComparison.Ordinal))
                {
                    List<JiraIssueSummaryEntry> results = [];
                    foreach ((string key, string spec) in _specsByKey)
                    {
                        results.Add(new JiraIssueSummaryEntry
                        {
                            Key = key,
                            Title = key,
                            Specification = spec,
                        });
                    }

                    JiraLocalProcessingListResponse payload = new(results, results.Count, 0, results.Count);
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                    ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                else
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    ctx.Response.ContentLength64 = 0;
                }
            }
            catch
            {
                /* best-effort */
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }
    }

    private static int FindFreeTcpPort()
    {
        System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
