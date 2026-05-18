using System.Net;
using System.Text.Json;
using FhirAugury.Common.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite.Tests;

public class SpecificationBackfillTests
{
    [Fact]
    public async Task Backfill_PopulatesEmptySpecifications_FromHttpSource()
    {
        string dbPath = NewDbPath();
        await SeedAsync(dbPath,
        [
            new PreparerTestDb.SourceTicketSeed("FHIR-1"),
            new PreparerTestDb.SourceTicketSeed("FHIR-2"),
            new PreparerTestDb.SourceTicketSeed("FHIR-3", Specification: "preexisting"),
        ]);

        Dictionary<string, string> serverSpecs = new()
        {
            ["FHIR-1"] = "fhir-core",
            ["FHIR-2"] = "fhir-extensions",
        };
        using FakeJiraSourceServer server = new(serverSpecs);

        CliOptions options = CliOptionsWith(dbPath, jiraSourceUrl: server.BaseUrl);
        using StringWriter stderr = new();

        int exit = await SpecificationBackfill.RunAsync(dbPath, options, stderr, CancellationToken.None);

        Assert.Equal(0, exit);
        Dictionary<string, string> finalSpecs = await ReadSpecsAsync(dbPath);
        Assert.Equal("fhir-core", finalSpecs["FHIR-1"]);
        Assert.Equal("fhir-extensions", finalSpecs["FHIR-2"]);
        Assert.Equal("preexisting", finalSpecs["FHIR-3"]);
    }

    [Fact]
    public async Task Backfill_LeavesEmptyWhenJiraReturnsEmpty()
    {
        string dbPath = NewDbPath();
        await SeedAsync(dbPath,
        [
            new PreparerTestDb.SourceTicketSeed("FHIR-1"),
        ]);

        Dictionary<string, string> serverSpecs = new()
        {
            ["FHIR-1"] = "",
        };
        using FakeJiraSourceServer server = new(serverSpecs);

        CliOptions options = CliOptionsWith(dbPath, jiraSourceUrl: server.BaseUrl);
        using StringWriter stderr = new();

        int exit = await SpecificationBackfill.RunAsync(dbPath, options, stderr, CancellationToken.None);

        Assert.Equal(0, exit);
        Dictionary<string, string> finalSpecs = await ReadSpecsAsync(dbPath);
        Assert.Equal("", finalSpecs["FHIR-1"]);
    }

    [Fact]
    public async Task Backfill_FallsBackToJiraSourceDb_WhenHttpUnreachable()
    {
        string dbPath = NewDbPath();
        await SeedAsync(dbPath,
        [
            new PreparerTestDb.SourceTicketSeed("FHIR-1"),
        ]);

        string sourceDbPath = NewDbPath("jira-source");
        await SeedJiraSourceDbAsync(sourceDbPath, new Dictionary<string, string>
        {
            ["FHIR-1"] = "fhir-core",
        });

        int unusedPort = FindFreeTcpPort();
        CliOptions options = CliOptionsWith(
            dbPath,
            jiraSourceUrl: $"http://127.0.0.1:{unusedPort}",
            jiraSourceDbPath: sourceDbPath);
        using StringWriter stderr = new();

        int exit = await SpecificationBackfill.RunAsync(dbPath, options, stderr, CancellationToken.None);

        Assert.Equal(0, exit);
        Dictionary<string, string> finalSpecs = await ReadSpecsAsync(dbPath);
        Assert.Equal("fhir-core", finalSpecs["FHIR-1"]);
    }

    [Fact]
    public async Task Backfill_ReportsActionableError_WhenNeitherSourceReachable()
    {
        string dbPath = NewDbPath();
        await SeedAsync(dbPath,
        [
            new PreparerTestDb.SourceTicketSeed("FHIR-1"),
        ]);

        int unusedPort = FindFreeTcpPort();
        CliOptions options = CliOptionsWith(
            dbPath,
            jiraSourceUrl: $"http://127.0.0.1:{unusedPort}");
        using StringWriter stderr = new();

        int exit = await SpecificationBackfill.RunAsync(dbPath, options, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        string stderrText = stderr.ToString();
        Assert.Contains("--jira-source", stderrText, StringComparison.Ordinal);
        Assert.Contains("--jira-source-db", stderrText, StringComparison.Ordinal);
    }

    private static async Task SeedAsync(string dbPath, IReadOnlyList<PreparerTestDb.SourceTicketSeed> seeds)
    {
        await PreparerTestDb.SeedAsync(dbPath, seeds);
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

    private static string NewDbPath(string prefix = "spec-backfill")
        => Path.Combine(AppContext.BaseDirectory, $"{prefix}-{Guid.NewGuid():N}.db");

    private static CliOptions CliOptionsWith(string dbPath, string? jiraSourceUrl = null, string? jiraSourceDbPath = null)
        => new(
            DbPath: dbPath,
            OutPath: null,
            Title: "Preparer Report",
            FilterSpec: null,
            FilterProject: null,
            FilterWorkGroup: null,
            JiraSourceUrl: jiraSourceUrl,
            JiraSourceDbPath: jiraSourceDbPath,
            OrchestratorAddress: null,
            NoHydrate: true,
            Force: false,
            BackfillSpec: true,
            Help: false);

    private static int FindFreeTcpPort()
    {
        System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
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
        System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
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
