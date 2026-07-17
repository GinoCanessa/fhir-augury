using System.Net;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkGroupSourceXmlOptions = FhirAugury.Common.WorkGroups.WorkGroupSourceXmlOptions;

namespace FhirAugury.Source.Jira.Tests;

public class WorkGroupSupportFileAcquirerTests : IDisposable
{
    private readonly string _root;

    public WorkGroupSupportFileAcquirerTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "augury-wg-acquirer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private string CachePath() => Path.Combine(_root, "cache");

    private string ExpectedDest(string filename = "CodeSystem-hl7-work-group.xml")
        => Path.Combine(CachePath(), JiraCacheLayout.SourceName,
            JiraCacheLayout.SupportPrefix, filename);

    private static IOptions<JiraServiceOptions> Opts(string cachePath, WorkGroupSourceXmlOptions cfg)
        => Options.Create(new JiraServiceOptions
        {
            CachePath = cachePath,
            Hl7WorkGroupSourceXml = cfg,
        });

    private static StubHttpClientFactory Factory(MockHttpMessageHandler handler)
        => new StubHttpClientFactory(handler);

    // ── local-copy success ───────────────────────────────────────────────

    [Fact]
    public async Task LocalFile_Present_CopiesToDest_AndIsIdempotent()
    {
        string srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        string srcFile = Path.Combine(srcDir, "wg.xml");
        await File.WriteAllTextAsync(srcFile, "<CodeSystem/>");

        WorkGroupSourceXmlOptions cfg = new() { LocalFile = srcFile };
        MockHttpMessageHandler handler = new();
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? dest1 = await acquirer.EnsureAsync();
        string? dest2 = await acquirer.EnsureAsync();

        Assert.Equal(ExpectedDest(), dest1);
        Assert.Equal(dest1, dest2);
        Assert.Equal("<CodeSystem/>", await File.ReadAllTextAsync(dest1!));
        Assert.Empty(handler.SentRequests);
    }

    // ── local-copy unconditional overwrite ───────────────────────────────

    [Fact]
    public async Task LocalFile_Present_OverwritesExistingDest()
    {
        string srcFile = Path.Combine(_root, "wg.xml");
        await File.WriteAllTextAsync(srcFile, "NEW");
        string dest = ExpectedDest();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllTextAsync(dest, "OLD");

        WorkGroupSourceXmlOptions cfg = new() { LocalFile = srcFile };
        MockHttpMessageHandler handler = new();
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? result = await acquirer.EnsureAsync();

        Assert.Equal(dest, result);
        Assert.Equal("NEW", await File.ReadAllTextAsync(dest));
    }

    // ── LocalFile missing → no fallback to Url ──────────────────────────

    [Fact]
    public async Task LocalFile_Missing_DoesNotFallBackToUrl_ReturnsNull()
    {
        string missing = Path.Combine(_root, "nope.xml");
        WorkGroupSourceXmlOptions cfg = new()
        {
            LocalFile = missing,
            Url = "https://example.invalid/wg.xml",
        };
        MockHttpMessageHandler handler = new();
        handler.RespondWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("would be downloaded")
        });
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? result = await acquirer.EnsureAsync();

        Assert.Null(result);
        Assert.Empty(handler.SentRequests);
        Assert.False(File.Exists(ExpectedDest()));
    }

    // ── URL download success ────────────────────────────────────────────

    [Fact]
    public async Task Url_Configured_DestAbsent_DownloadsAndStoresFile()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            Url = "https://example.invalid/wg.xml",
        };
        MockHttpMessageHandler handler = new();
        handler.RespondWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<CodeSystem id=\"x\"/>")
        });
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? result = await acquirer.EnsureAsync();

        Assert.Equal(ExpectedDest(), result);
        Assert.Equal("<CodeSystem id=\"x\"/>", await File.ReadAllTextAsync(result!));
        Assert.False(File.Exists(result + ".tmp"));
        Assert.Single(handler.SentRequests);
    }

    // ── URL download skipped when present ───────────────────────────────

    [Fact]
    public async Task Url_Configured_DestPresent_DoesNotIssueRequest()
    {
        string dest = ExpectedDest();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllTextAsync(dest, "EXISTING");

        WorkGroupSourceXmlOptions cfg = new()
        {
            Url = "https://example.invalid/wg.xml",
        };
        MockHttpMessageHandler handler = new();
        handler.RespondWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("REPLACED")
        });
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? result = await acquirer.EnsureAsync();

        Assert.Equal(dest, result);
        Assert.Equal("EXISTING", await File.ReadAllTextAsync(dest));
        Assert.Empty(handler.SentRequests);
    }

    // ── URL transport failure ───────────────────────────────────────────

    [Fact]
    public async Task Url_TransportFailure_LeavesNoFile_ReturnsNull()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            Url = "https://example.invalid/wg.xml",
        };
        MockHttpMessageHandler handler = new();
        handler.RespondWith(_ => throw new HttpRequestException("boom"));
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? result = await acquirer.EnsureAsync();

        Assert.Null(result);
        Assert.False(File.Exists(ExpectedDest()));
        Assert.False(File.Exists(ExpectedDest() + ".tmp"));
    }

    // ── URL non-2xx ─────────────────────────────────────────────────────

    [Fact]
    public async Task Url_Non2xx_LeavesNoFile_ReturnsNull()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            Url = "https://example.invalid/wg.xml",
        };
        MockHttpMessageHandler handler = new();
        handler.RespondWith(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? result = await acquirer.EnsureAsync();

        Assert.Null(result);
        Assert.False(File.Exists(ExpectedDest()));
    }

    // ── Nothing configured ──────────────────────────────────────────────

    [Fact]
    public async Task NothingConfigured_ReturnsNull()
    {
        WorkGroupSourceXmlOptions cfg = new();
        MockHttpMessageHandler handler = new();
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        string? result = await acquirer.EnsureAsync();

        Assert.Null(result);
        Assert.Empty(handler.SentRequests);
    }

    // ── Concurrent EnsureAsync ──────────────────────────────────────────

    [Fact]
    public async Task ConcurrentEnsureAsync_IssuesAtMostOneHttpRequest()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            Url = "https://example.invalid/wg.xml",
        };
        MockHttpMessageHandler handler = new();
        handler.RespondWithAsync(async _ =>
        {
            await Task.Delay(50);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<x/>")
            };
        });
        WorkGroupSupportFileAcquirer acquirer = new(
            Opts(CachePath(), cfg), Factory(handler), NullLogger<WorkGroupSupportFileAcquirer>.Instance);

        Task<string?> a = acquirer.EnsureAsync();
        Task<string?> b = acquirer.EnsureAsync();
        string?[] results = await Task.WhenAll(a, b);

        Assert.All(results, r => Assert.Equal(ExpectedDest(), r));
        Assert.True(handler.SentRequests.Count <= 1,
            $"expected at most 1 request, got {handler.SentRequests.Count}");
    }

    // ── Test doubles ────────────────────────────────────────────────────

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, Task<HttpResponseMessage>>? _responseFactory;
        public List<HttpRequestMessage> SentRequests { get; } = [];

        public void RespondWith(Func<HttpRequestMessage, HttpResponseMessage> factory) =>
            _responseFactory = req => Task.FromResult(factory(req));

        public void RespondWithAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory) =>
            _responseFactory = factory;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (SentRequests) SentRequests.Add(request);
            if (_responseFactory is not null)
                return await _responseFactory(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(handler, disposeHandler: false);
    }
}
