using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Fetches one Confluence URL and returns its response body. The single
/// injectable seam that makes discovery and the sweep testable offline.
/// </summary>
public delegate Task<string> ConfluenceFetch(string url, CancellationToken ct);

/// <summary>
/// Streams a binary payload to <paramref name="copyTo"/>, refusing anything
/// larger than <paramref name="maxBytes"/> (<c>0</c> = unlimited). Returns false
/// when the transfer was rejected or aborted by the cap.
/// </summary>
public delegate Task<bool> ConfluenceBlobFetch(
    string url, long maxBytes, Func<Stream, Task> copyTo);

/// <summary>Shared HTTP plumbing for discovery and the sweep.</summary>
internal static class ConfluenceHttp
{
    /// <summary>The production fetch: the rate-limited "confluence" client.</summary>
    public static ConfluenceFetch CreateFetch(
        IHttpClientFactory httpClientFactory, ConfluenceServiceOptions options) =>
        async (url, ct) =>
        {
            HttpClient client = httpClientFactory.CreateClient("confluence");
            using HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                client, url, ct, options.RateLimiting.MaxRetries, ConfluenceCacheLayout.SourceName);

            // The edge appliance answers before Confluence does; a challenge is
            // not a per-item failure, so it stops the run instead.
            if (ConfluenceChallengeDetector.Detect(response, url) is { } challenge)
            {
                throw challenge;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        };

    /// <summary>
    /// Walks a paginated Confluence collection to exhaustion via
    /// <c>_links.next</c>, which this instance returns as a site-relative path.
    /// </summary>
    public static async IAsyncEnumerable<JsonElement> EnumerateResultsAsync(
        ConfluenceFetch fetch,
        string baseUrl,
        string startUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = startUrl;

        while (!string.IsNullOrEmpty(next))
        {
            ct.ThrowIfCancellationRequested();

            string json = await fetch(Absolute(baseUrl, next), ct);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("results", out JsonElement results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in results.EnumerateArray())
                {
                    yield return element.Clone();
                }
            }

            next = root.TryGetProperty("_links", out JsonElement links)
                && links.TryGetProperty("next", out JsonElement nextLink)
                && nextLink.ValueKind == JsonValueKind.String
                ? nextLink.GetString()
                : null;
        }
    }

    /// <summary>
    /// Streams an attachment's bytes with an explicit transfer deadline and a
    /// two-layer size guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <see cref="HttpCompletionOption.ResponseHeadersRead"/> rather than
    /// <c>GetWithRetryAsync</c>, which defaults to <c>ResponseContentRead</c>
    /// and would buffer a whole PDF or deck into memory before a byte reached
    /// disk. At instance scale that is an out-of-memory risk, not a
    /// micro-optimization.
    /// </para>
    /// <para>
    /// With <c>ResponseHeadersRead</c> the client timeout and the resilience
    /// handler's timeouts cover only the <em>headers</em>, so the stream copy
    /// gets its own linked deadline. A timed-out transfer is a failed unit that
    /// the next run re-attempts; convergence makes that safe.
    /// </para>
    /// </remarks>
    public static ConfluenceBlobFetch CreateBlobFetch(
        IHttpClientFactory httpClientFactory, ConfluenceServiceOptions options) =>
        async (url, maxBytes, copyTo) =>
        {
            HttpClient client = httpClientFactory.CreateClient("confluence");

            using HttpResponseMessage response = await HttpRetryHelper.ExecuteWithRetryAsync(
                token =>
                {
                    HttpRequestMessage request = new(HttpMethod.Get, url);
                    return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                },
                CancellationToken.None,
                options.RateLimiting.MaxRetries,
                ConfluenceCacheLayout.SourceName);

            // Same fingerprint, same verdict: the blob path reaches the same
            // edge appliance as the JSON path.
            if (ConfluenceChallengeDetector.Detect(response, url) is { } challenge)
            {
                throw challenge;
            }

            response.EnsureSuccessStatusCode();

            // Layer one: reject before transferring a byte when the server told
            // us how big it is.
            if (maxBytes > 0 && response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                return false;
            }

            using CancellationTokenSource transferTimeout = new(BlobTransferTimeout);
            await using Stream body = await response.Content.ReadAsStreamAsync(transferTimeout.Token);

            // Layer two: the only guard that holds when neither the manifest nor
            // the header is trustworthy.
            await using CountingStream counted = new(body, maxBytes);

            try
            {
                await copyTo(counted);
            }
            catch (ConfluenceBlobTooLargeException)
            {
                return false;
            }

            return true;
        };

    /// <summary>Deadline for the body transfer itself, separate from the headers.</summary>
    private static readonly TimeSpan BlobTransferTimeout = TimeSpan.FromMinutes(10);

    private static string Absolute(string baseUrl, string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{baseUrl.TrimEnd('/')}/{url.TrimStart('/')}";
}

/// <summary>Raised when a blob exceeds the configured cap mid-transfer.</summary>
internal sealed class ConfluenceBlobTooLargeException(long limit)
    : Exception($"Attachment exceeded the configured AttachmentMaxBytes of {limit}.");

/// <summary>
/// Read-only pass-through stream that aborts once more than
/// <paramref name="maxBytes"/> have been read. The partial write is discarded by
/// the caller, and the next run re-attempts the unit.
/// </summary>
internal sealed class CountingStream(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Track(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Track(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Track(int read)
    {
        _read += read;
        if (maxBytes > 0 && _read > maxBytes)
        {
            throw new ConfluenceBlobTooLargeException(maxBytes);
        }

        return read;
    }
}
