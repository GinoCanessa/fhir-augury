using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Client;

public sealed record AuthoringStartResult(
    AuthoringRunResponse? Run)
{
    public bool NoCandidates => Run is null;
}

public sealed record AuthoringRetryResponse(
    string ItemId,
    bool RequiresAuthoring);

public interface IAuthoringControlClient
{
    Task<AuthoringStartResult> StartAsync<TRequest>(
        string serviceName,
        TRequest request,
        CancellationToken ct);

    Task<AuthoringRunListResponse> ListAsync(
        string serviceName,
        int? limit,
        CancellationToken ct);

    Task<AuthoringRunResponse> GetAsync(
        string serviceName,
        string runId,
        CancellationToken ct);

    Task<AuthoringRetryResponse> RetryAsync(
        string serviceName,
        string runId,
        string itemId,
        CancellationToken ct);

    Task<AuthoringItemSupersedeResult> SupersedeAsync(
        string serviceName,
        string runId,
        string itemId,
        string reason,
        CancellationToken ct);

    Task<VerifiedAuthoringSnapshotPair> DownloadSnapshotPairAsync(
        string serviceName,
        string runId,
        string pairDirectory,
        CancellationToken ct);
}

public sealed class AuthoringControlClient : IAuthoringControlClient
{
    private const int DefaultStreamRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

    private readonly HttpClient _httpClient;
    private readonly int _maxReadRetries;
    private readonly int _maxStreamRetries;
    private readonly TimeSpan _streamRetryDelay;
    private readonly AuthoringSnapshotPairPromotionHooks? _promotionHooks;

    public AuthoringControlClient(HttpClient httpClient)
        : this(
            httpClient,
            HttpRetryHelper.DefaultMaxRetries,
            DefaultStreamRetries,
            TimeSpan.FromMilliseconds(100),
            promotionHooks: null)
    {
    }

    internal AuthoringControlClient(
        HttpClient httpClient,
        int maxReadRetries,
        int maxStreamRetries,
        TimeSpan streamRetryDelay,
        AuthoringSnapshotPairPromotionHooks? promotionHooks)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.BaseAddress is null ||
            !httpClient.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The authoring control HttpClient must have an absolute Orchestrator base address.",
                nameof(httpClient));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(maxReadRetries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxStreamRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            streamRetryDelay,
            TimeSpan.Zero);

        _httpClient = httpClient;
        _maxReadRetries = maxReadRetries;
        _maxStreamRetries = maxStreamRetries;
        _streamRetryDelay = streamRetryDelay;
        _promotionHooks = promotionHooks;
    }

    public async Task<AuthoringStartResult> StartAsync<TRequest>(
        string serviceName,
        TRequest request,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentNullException.ThrowIfNull(request);
        string path = ControlPath(service);
        MutationResponse response = await SendMutationAsync(
            "start",
            service,
            runId: null,
            itemId: null,
            path,
            request,
            ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return new AuthoringStartResult(null);
        }

        AuthoringRunResponse run = Deserialize<AuthoringRunResponse>(
            response.Content,
            path);
        ValidateRunResponse(run);
        return new AuthoringStartResult(run);
    }

    public async Task<AuthoringRunListResponse> ListAsync(
        string serviceName,
        int? limit,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "Authoring run list limit must be between 1 and 100.");
        }

        string path = ControlPath(service);
        if (limit.HasValue)
        {
            path +=
                $"?limit={limit.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        AuthoringRunListResponse response =
            await SendReadJsonAsync<AuthoringRunListResponse>(path, ct);
        if (response.Runs.Any(run => string.IsNullOrWhiteSpace(run.RunId)))
        {
            throw new InvalidOperationException(
                "Authoring run list contains an empty run identifier.");
        }
        return response;
    }

    public async Task<AuthoringRunResponse> GetAsync(
        string serviceName,
        string runId,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}";
        AuthoringRunResponse response =
            await SendReadJsonAsync<AuthoringRunResponse>(path, ct);
        ValidateRunResponse(response, runId);
        return response;
    }

    public async Task<AuthoringRetryResponse> RetryAsync(
        string serviceName,
        string runId,
        string itemId,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/retry";
        MutationResponse raw = await SendMutationAsync(
            "retry",
            service,
            runId,
            itemId,
            path,
            body: null,
            ct);
        AuthoringRetryResponse response =
            Deserialize<AuthoringRetryResponse>(raw.Content, path);
        if (!string.Equals(response.ItemId, itemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Retry response item '{response.ItemId}' does not match requested item '{itemId}'.");
        }
        return response;
    }

    public async Task<AuthoringItemSupersedeResult> SupersedeAsync(
        string serviceName,
        string runId,
        string itemId,
        string reason,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/supersede";
        MutationResponse raw = await SendMutationAsync(
            "supersede",
            service,
            runId,
            itemId,
            path,
            new AuthoringItemSupersedeRequest(reason),
            ct);
        AuthoringItemSupersedeResult response =
            Deserialize<AuthoringItemSupersedeResult>(raw.Content, path);
        if (!string.Equals(response.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(response.ItemId, itemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Supersede response coordinates '{response.RunId}/{response.ItemId}' do not match requested item '{runId}/{itemId}'.");
        }
        return response;
    }

    public async Task<VerifiedAuthoringSnapshotPair>
        DownloadSnapshotPairAsync(
            string serviceName,
            string runId,
            string pairDirectory,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairDirectory);

        string targetDirectory = Path.GetFullPath(pairDirectory);
        AuthoringSnapshotPairPaths paths =
            AuthoringSnapshotPairPaths.Create(targetDirectory);
        VerifiedAuthoringSnapshotPair? existing =
            await AuthoringSnapshotPairPromoter.RecoverAndVerifyAsync(
                paths,
                service,
                runId,
                ct);
        if (existing is not null)
        {
            return existing;
        }

        string basePath =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/snapshot";
        byte[] descriptorBytes = await SendReadBytesAsync(basePath, ct);
        AuthoringSnapshotDescriptor descriptor =
            AuthoringSnapshotPairVerifier.ParseAndValidateDescriptor(
                descriptorBytes,
                service,
                runId);
        string descriptorFileName =
            $"{descriptor.FileName}.descriptor.json";
        AuthoringSnapshotPairVerifier.ValidateDurablePairFileNames(
            descriptor.FileName,
            descriptorFileName);

        for (int attempt = 0; ; attempt++)
        {
            string stagingDirectory = paths.CreateStagingDirectory();
            try
            {
                VerifiedAuthoringSnapshotPair staged =
                    await DownloadStagedPairAsync(
                        service,
                        runId,
                        basePath,
                        descriptor,
                        descriptorBytes,
                        descriptorFileName,
                        stagingDirectory,
                        ct);
                return await AuthoringSnapshotPairPromoter.PromoteAsync(
                    paths,
                    staged,
                    _promotionHooks,
                    ct);
            }
            catch (AuthoringSnapshotPairSimulatedCrashException)
            {
                throw;
            }
            catch (SnapshotStreamException)
                when (attempt < _maxStreamRetries)
            {
                AuthoringSnapshotPairFileSystem.DeleteDirectoryIfExists(
                    stagingDirectory);
                if (_streamRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_streamRetryDelay, ct);
                }
            }
            catch
            {
                if (!File.Exists(paths.StatePath))
                {
                    AuthoringSnapshotPairFileSystem.TryDeleteDirectory(
                        stagingDirectory);
                }
                throw;
            }
        }
    }

    private async Task<VerifiedAuthoringSnapshotPair>
        DownloadStagedPairAsync(
            string serviceName,
            string runId,
            string basePath,
            AuthoringSnapshotDescriptor descriptor,
            byte[] descriptorBytes,
            string descriptorFileName,
            string stagingDirectory,
            CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDirectory);
        string ownershipMarker = Path.Combine(
            stagingDirectory,
            AuthoringSnapshotPairPaths.StagingMarkerFileName);
        await AuthoringSnapshotPairFileSystem.WriteBytesDurablyAsync(
            ownershipMarker,
            Encoding.UTF8.GetBytes("fhir-augury-authoring-pair"),
            ct);

        string descriptorPath =
            Path.Combine(stagingDirectory, descriptorFileName);
        await AuthoringSnapshotPairFileSystem.WriteBytesDurablyAsync(
            descriptorPath,
            descriptorBytes,
            ct);

        string databasePath =
            Path.Combine(stagingDirectory, descriptor.FileName);
        (long sizeBytes, string sha256) = await DownloadSnapshotBytesAsync(
            $"{basePath}/bytes",
            databasePath,
            ct);
        if (sizeBytes != descriptor.SizeBytes ||
            !string.Equals(
                sha256,
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Downloaded snapshot bytes do not match the trusted descriptor.");
        }

        string descriptorSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(descriptorBytes))
            .ToLowerInvariant();
        AuthoringSnapshotPairManifest manifest = new(
            AuthoringSnapshotPairManifest.CurrentFormatVersion,
            serviceName,
            runId,
            descriptor.SnapshotId,
            descriptorFileName,
            descriptor.FileName,
            sizeBytes,
            descriptorSha256,
            sha256);
        File.Delete(ownershipMarker);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            JsonOptions);
        await AuthoringSnapshotPairFileSystem.WriteBytesDurablyAsync(
            Path.Combine(
                stagingDirectory,
                AuthoringSnapshotPairManifest.ReadyFileName),
            manifestBytes,
            ct);

        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                serviceName,
                runId,
                stagingDirectory,
                ct);
    }

    private async Task<(long SizeBytes, string Sha256)>
        DownloadSnapshotBytesAsync(
            string path,
            string destinationPath,
            CancellationToken ct)
    {
        using HttpResponseMessage response =
            await SendReadResponseAsync(
                path,
                bufferSuccessfulContent: false,
                ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateControlExceptionAsync(response, path, ct);
        }

        Stream input;
        try
        {
            input = await response.Content.ReadAsStreamAsync(ct);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new SnapshotStreamException(
                "Snapshot response stream could not be opened.",
                ex);
        }

        await using (input)
        await using (FileStream output = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (System.Security.Cryptography.IncrementalHash hash =
               System.Security.Cryptography.IncrementalHash.CreateHash(
                   System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[128 * 1024];
            long sizeBytes = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, ct);
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or IOException ||
                    ex is OperationCanceledException &&
                    !ct.IsCancellationRequested)
                {
                    throw new SnapshotStreamException(
                        "Snapshot response stream ended unexpectedly.",
                        ex);
                }
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                sizeBytes = checked(sizeBytes + read);
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
            string sha256 = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            return (sizeBytes, sha256);
        }
    }

    private async Task<T> SendReadJsonAsync<T>(
        string path,
        CancellationToken ct)
    {
        byte[] bytes = await SendReadBytesAsync(path, ct);
        return Deserialize<T>(bytes, path);
    }

    private async Task<byte[]> SendReadBytesAsync(
        string path,
        CancellationToken ct)
    {
        using HttpResponseMessage response =
            await SendReadResponseAsync(
                path,
                bufferSuccessfulContent: true,
                ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateControlExceptionAsync(response, path, ct);
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<HttpResponseMessage> SendReadResponseAsync(
        string path,
        bool bufferSuccessfulContent,
        CancellationToken ct)
    {
        try
        {
            return await HttpRetryHelper.ExecuteWithRetryAsync(
                async token =>
                {
                    HttpRequestMessage request =
                        new(HttpMethod.Get, path);
                    HttpResponseMessage response =
                        await SendAndDisposeRequestAsync(
                            request,
                            token);
                    if (bufferSuccessfulContent &&
                        response.IsSuccessStatusCode)
                    {
                        try
                        {
                            await response.Content.LoadIntoBufferAsync(
                                token);
                        }
                        catch (IOException ex)
                        {
                            response.Dispose();
                            throw new HttpRequestException(
                                "Authoring response body was interrupted.",
                                ex);
                        }
                        catch
                        {
                            response.Dispose();
                            throw;
                        }
                    }
                    return response;
                },
                ct,
                _maxReadRetries,
                "Orchestrator authoring control");
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode statusCode)
        {
            throw new AuthoringControlException(
                statusCode,
                $"http-{(int)statusCode}",
                ex.Message,
                retryAfterHeader: null,
                retryAfter: null,
                relatedRunIds: [],
                path,
                ex);
        }
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using (request)
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }
    }

    private async Task<MutationResponse> SendMutationAsync(
        string operation,
        string serviceName,
        string? runId,
        string? itemId,
        string path,
        object? body,
        CancellationToken ct)
    {
        byte[]? bodyBytes = body is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(
                body,
                body.GetType(),
                JsonOptions);
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        if (bodyBytes is not null)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateControlExceptionAsync(
                    response,
                    path,
                    ct);
            }

            byte[] content =
                await response.Content.ReadAsByteArrayAsync(ct);
            return new MutationResponse(response.StatusCode, content);
        }
        catch (AuthoringControlException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or
                OperationCanceledException)
        {
            throw new AuthoringMutationOutcomeUnknownException(
                operation,
                serviceName,
                runId,
                itemId,
                ex);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<AuthoringControlException>
        CreateControlExceptionAsync(
            HttpResponseMessage response,
            string path,
            CancellationToken ct)
    {
        byte[] content;
        try
        {
            content = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or
                OperationCanceledException)
        {
            return CreateFallbackControlException(
                response,
                path,
                detail: ex.Message,
                ex);
        }

        AuthoringConflictResponse? error = null;
        try
        {
            error = JsonSerializer.Deserialize<AuthoringConflictResponse>(
                content,
                JsonOptions);
        }
        catch (JsonException)
        {
        }

        string responseText = Encoding.UTF8.GetString(content);
        string errorCode = string.IsNullOrWhiteSpace(error?.Error)
            ? $"http-{(int)response.StatusCode}"
            : error.Error;
        string? detail = error is not null
            ? error.Detail
            : string.IsNullOrWhiteSpace(responseText)
                ? null
                : responseText;
        IEnumerable<string> relatedRunIds =
            (error?.ConflictingRunIds ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(error?.RunId))
        {
            relatedRunIds = relatedRunIds.Append(error.RunId);
        }
        string[] distinctRelatedRunIds = relatedRunIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        (string? retryAfterHeader, TimeSpan? retryAfter) =
            ReadRetryAfter(response);
        return new AuthoringControlException(
            response.StatusCode,
            errorCode,
            detail,
            retryAfterHeader,
            retryAfter,
            distinctRelatedRunIds,
            path);
    }

    private static AuthoringControlException
        CreateFallbackControlException(
            HttpResponseMessage response,
            string path,
            string? detail,
            Exception? innerException)
    {
        (string? retryAfterHeader, TimeSpan? retryAfter) =
            ReadRetryAfter(response);
        return new AuthoringControlException(
            response.StatusCode,
            $"http-{(int)response.StatusCode}",
            detail,
            retryAfterHeader,
            retryAfter,
            relatedRunIds: [],
            path,
            innerException);
    }

    private static (string? Header, TimeSpan? Delay) ReadRetryAfter(
        HttpResponseMessage response)
    {
        RetryConditionHeaderValue? value = response.Headers.RetryAfter;
        string? header = value?.ToString();
        if (value?.Delta is TimeSpan delta)
        {
            return (header, delta);
        }
        if (value?.Date is DateTimeOffset date)
        {
            TimeSpan delay = date - DateTimeOffset.UtcNow;
            return (
                header,
                delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        }
        return (header, null);
    }

    private static T Deserialize<T>(byte[] bytes, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Authoring endpoint '{path}' returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Authoring endpoint '{path}' returned invalid JSON.",
                ex);
        }
    }

    private static void ValidateRunResponse(
        AuthoringRunResponse response,
        string? expectedRunId = null)
    {
        if (string.IsNullOrWhiteSpace(response.Run.RunId) ||
            expectedRunId is not null &&
            !string.Equals(
                response.Run.RunId,
                expectedRunId,
                StringComparison.Ordinal) ||
            response.Items.Any(item =>
                !string.Equals(
                    item.RunId,
                    response.Run.RunId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Authoring run response contains inconsistent coordinates.");
        }
    }

    private static string ControlPath(string serviceName)
        => $"api/v1/processing-services/{Uri.EscapeDataString(serviceName)}/authoring/runs";

    private sealed record MutationResponse(
        HttpStatusCode StatusCode,
        byte[] Content);

    private sealed class SnapshotStreamException(
        string message,
        Exception innerException)
        : IOException(message, innerException);
}
