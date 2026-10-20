using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Client.Tests;

internal sealed class DelegateHttpHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>
        callback)
    : HttpMessageHandler
{
    private int _calls;

    public int Calls => _calls;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => callback(
            request,
            Interlocked.Increment(ref _calls),
            cancellationToken);

    public static HttpResponseMessage Json(
        object body,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = JsonContent.Create(body),
        };
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"fhir-augury-processing-client-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] segments)
        => System.IO.Path.Combine([Path, .. segments]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class AuthoringClientTestData
{
    public static readonly DateTimeOffset Timestamp =
        DateTimeOffset.Parse("2026-09-04T00:00:00Z");

    public static AuthoringRunResponse RunResponse(
        string runId = "run-1")
        => new(
            new AuthoringRunStatus(
                runId,
                "jira-fhir",
                1,
                "queued",
                false,
                1,
                0,
                0,
                Timestamp,
                null,
                null,
                null),
            [
                new AuthoringRunItemStatus(
                    "item-1",
                    runId,
                    "FHIR-1",
                    "jira-ticket",
                    "revision-1",
                    "pending",
                    null,
                    null,
                    0,
                    Timestamp,
                    null,
                    null,
                    null),
            ]);

    public static AuthoringSnapshotDescriptor Descriptor(
        byte[] bytes,
        string serviceName = "Preparer",
        string runId = "run-1",
        string snapshotId = "snapshot-1",
        long sequence = 1,
        string fileName = "snapshot.db",
        string? sha256 = null,
        long? sizeBytes = null)
        => new(
            serviceName == "BallotNotes"
                ? "github-fhir-ballot-notes"
                : "jira-fhir",
            runId,
            snapshotId,
            1,
            sequence,
            1,
            sha256 ?? Hash(bytes),
            sizeBytes ?? bytes.LongLength,
            1,
            1,
            new Dictionary<string, long>(),
            fileName,
            Timestamp);

    public static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    public static AuthoringControlClient CreateClient(
        HttpMessageHandler handler,
        int maxReadRetries = 0,
        int maxStreamRetries = 0,
        AuthoringSnapshotPairPromotionHooks? hooks = null)
    {
        HttpClient httpClient = new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://orchestrator/"),
        };
        return new AuthoringControlClient(
            httpClient,
            maxReadRetries,
            maxStreamRetries,
            TimeSpan.Zero,
            hooks);
    }

    public static DelegateHttpHandler SnapshotHandler(
        AuthoringSnapshotDescriptor descriptor,
        Func<int, Stream> streamFactory)
        => new((request, call, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/bytes", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content = new StreamContent(streamFactory(call)),
                });
            }
            return Task.FromResult(
                DelegateHttpHandler.Json(descriptor));
        });
}
