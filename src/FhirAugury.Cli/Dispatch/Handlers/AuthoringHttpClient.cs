using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Cli.Dispatch.Handlers;

internal sealed record AuthoringRunEnvelope(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);

internal sealed record AuthoringRetryResponse(
    string ItemId,
    bool RequiresAuthoring);

internal sealed record AuthoringSubmitResponse(
    AuthoringResultReceipt Receipt,
    bool IsReplay);

internal sealed record AuthoringNoCandidatesResponse(
    string Status = "no-candidates");

internal sealed record AuthoringPublicationRefreshReconciliationResponse(
    string Outcome,
    string SourceRunId,
    string Reconciliation,
    IReadOnlyList<AuthoringRunStatus> Candidates,
    bool ListTruncated,
    string Message,
    string? Error = null);

internal sealed record AuthoringWorkerContext(
    string RunId,
    string ItemId,
    string CallbackUrl,
    string OperationId,
    string OperationToken,
    string SourceRevision);

internal sealed class AuthoringHttpClient : IDisposable
{
    internal const string OperationTokenHeader =
        "X-Fhir-Augury-Authoring-Token";

    private const int MaxAttempts = 3;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

    private readonly HttpClient _httpClient;
    private readonly AuthoringControlClient _controlClient;

    public AuthoringHttpClient(
        string orchestratorAddress,
        HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orchestratorAddress);
        _httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _httpClient.BaseAddress = new Uri(
            orchestratorAddress.EndsWith("/", StringComparison.Ordinal)
                ? orchestratorAddress
                : $"{orchestratorAddress}/",
            UriKind.Absolute);
        _controlClient = new AuthoringControlClient(_httpClient);
    }

    public async Task<object> StartAsync<TRequest>(
        string serviceName,
        TRequest request,
        CancellationToken ct)
    {
        EnsureOuterMode();
        AuthoringStartResult result =
            await _controlClient.StartAsync(
                serviceName,
                request,
                ct);
        return result.Run is null
            ? new AuthoringNoCandidatesResponse()
            : ToEnvelope(result.Run);
    }

    public async Task<AuthoringRunEnvelope> GetStatusAsync(
        string serviceName,
        string runId,
        CancellationToken ct)
    {
        EnsureOuterMode();
        return ToEnvelope(
            await _controlClient.GetAsync(
                serviceName,
                runId,
                ct));
    }

    public async Task<AuthoringRunEnvelope>
        StartPublicationRefreshAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return ToEnvelope(
            await _controlClient.StartPublicationRefreshAsync(
                serviceName,
                sourceRunId,
                ct));
    }

    public Task<PublicationReconciliationStartResult>
        StartPublicationReconciliationAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.StartPublicationReconciliationAsync(
            serviceName,
            sourceRunId,
            ct);
    }

    public Task<PublicationReconciliationStatusResult>
        GetPublicationReconciliationStatusAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.GetPublicationReconciliationAsync(
            serviceName,
            runId,
            ct);
    }

    public Task<PublicationReconciliationRetryResult>
        RetryPublicationReconciliationAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.RetryPublicationReconciliationAsync(
            serviceName,
            runId,
            ct);
    }

    public Task<PublicationReconciliationAbandonResult>
        AbandonPublicationReconciliationAsync(
            string serviceName,
            string runId,
            string reason,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.AbandonPublicationReconciliationAsync(
            serviceName,
            runId,
            reason,
            ct);
    }

    public Task<PublicationReconciliationCancelResult>
        CancelPublicationReconciliationAsync(
            string serviceName,
            string runId,
            string reason,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.CancelPublicationReconciliationAsync(
            serviceName,
            runId,
            reason,
            ct);
    }

    public Task<CanonicalEpochRecoveryStartResult>
        StartCanonicalEpochRecoveryAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.StartCanonicalEpochRecoveryAsync(
            serviceName,
            sourceRunId,
            ct);
    }

    public Task<CanonicalEpochRecoveryStatusResult>
        GetCanonicalEpochRecoveryStatusAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.GetCanonicalEpochRecoveryAsync(
            serviceName,
            runId,
            ct);
    }

    public Task<CanonicalEpochRecoveryRetryResult>
        RetryCanonicalEpochRecoveryAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.RetryCanonicalEpochRecoveryAsync(
            serviceName,
            runId,
            ct);
    }

    public Task<AuthoringRunListResponse> ListAsync(
        string serviceName,
        int? limit,
        CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.ListAsync(
            serviceName,
            limit,
            ct);
    }

    public async Task<AuthoringRetryResponse> RetryAsync(
        string serviceName,
        string runId,
        string itemId,
        CancellationToken ct)
    {
        EnsureOuterMode();
        FhirAugury.Processing.Client.AuthoringRetryResponse result =
            await _controlClient.RetryAsync(
                serviceName,
                runId,
                itemId,
                ct);
        return new AuthoringRetryResponse(
            result.ItemId,
            result.RequiresAuthoring);
    }

    public Task<AuthoringItemSupersedeResult> SupersedeAsync(
        string serviceName,
        string runId,
        string itemId,
        string reason,
        CancellationToken ct)
    {
        EnsureOuterMode();
        return _controlClient.SupersedeAsync(
            serviceName,
            runId,
            itemId,
            reason,
            ct);
    }

    public async Task<AuthoringSubmitResponse> SubmitAsync<TRequest>(
        TRequest request,
        string contentHash,
        string observedSourceRevision,
        CancellationToken ct)
    {
        AuthoringWorkerContext context = ReadWorkerContext();
        AuthoringSubmitResponse response =
            await SendJsonAsync<AuthoringSubmitResponse>(
                HttpMethod.Post,
                context.CallbackUrl,
                request,
                context.OperationToken,
                retryTransient: true,
                ct);
        AuthoringResultReceipt receipt = response.Receipt;
        if (!string.Equals(
                receipt.RunId,
                context.RunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ItemId,
                context.ItemId,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.OperationId,
                context.OperationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ContentHash,
                contentHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ObservedSourceRevision,
                observedSourceRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ExpectedSourceRevision,
                context.SourceRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Authoring receipt coordinates do not match the submitted worker operation.");
        }
        return response;
    }

    public async Task<object> DownloadSnapshotAsync(
        string serviceName,
        string runId,
        string snapshotPath,
        string? descriptorPath,
        CancellationToken ct)
    {
        EnsureOuterMode();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);

        string requestedSnapshotPath = Path.GetFullPath(snapshotPath);
        bool directoryRequest =
            Directory.Exists(requestedSnapshotPath) ||
            Path.EndsInDirectorySeparator(snapshotPath);
        if (directoryRequest)
        {
            VerifiedAuthoringSnapshotPair pair =
                await _controlClient.DownloadSnapshotPairAsync(
                    serviceName,
                    runId,
                    requestedSnapshotPath,
                    ct);
            string outputDescriptorPath =
                string.IsNullOrWhiteSpace(descriptorPath)
                    ? pair.DescriptorPath
                    : Path.GetFullPath(descriptorPath);
            if (!AuthoringPathsEqual(
                    outputDescriptorPath,
                    pair.DescriptorPath))
            {
                AuthoringSnapshotPairMaterialization materialized =
                    await AuthoringSnapshotPairMaterializer.MaterializeAsync(
                        pair,
                        pair.DatabasePath,
                        outputDescriptorPath,
                        ct);
                return ToSnapshotResult(materialized);
            }

            return new
            {
                descriptor = pair.Descriptor,
                snapshotPath = pair.DatabasePath,
                descriptorPath = pair.DescriptorPath,
            };
        }

        string privateRoot = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-authoring-pair-{Guid.NewGuid():N}");
        try
        {
            VerifiedAuthoringSnapshotPair pair =
                await _controlClient.DownloadSnapshotPairAsync(
                    serviceName,
                    runId,
                    Path.Combine(privateRoot, "pair"),
                    ct);
            string outputDirectory =
                Path.GetDirectoryName(requestedSnapshotPath)
                ?? Environment.CurrentDirectory;
            string outputSnapshotPath = Path.GetFullPath(
                Path.Combine(
                    outputDirectory,
                    pair.Descriptor.FileName));
            string outputDescriptorPath = Path.GetFullPath(
                string.IsNullOrWhiteSpace(descriptorPath)
                    ? $"{outputSnapshotPath}.descriptor.json"
                    : descriptorPath);
            AuthoringSnapshotPairMaterialization materialized =
                await AuthoringSnapshotPairMaterializer.MaterializeAsync(
                    pair,
                    outputSnapshotPath,
                    outputDescriptorPath,
                    ct);
            return ToSnapshotResult(materialized);
        }
        finally
        {
            TryDeletePrivateDirectory(privateRoot);
        }
    }

    public static AuthoringWorkerContext ReadWorkerContext()
    {
        string? sentinel = Environment.GetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_WORKER");
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["run"] = Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_RUN_ID"),
            ["item"] = Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_ITEM_ID"),
            ["callback"] = Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_CALLBACK_URL"),
            ["operation"] = Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_OPERATION_ID"),
            ["token"] = Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_OPERATION_TOKEN"),
            ["source revision"] = Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_SOURCE_REVISION"),
        };
        if (!string.Equals(sentinel, "1", StringComparison.Ordinal) ||
            values.Values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Worker submit requires FHIR_AUGURY_AUTHORING_WORKER=1 and complete run, item, callback, operation, token, and source-revision environment values.");
        }

        return new AuthoringWorkerContext(
            values["run"]!,
            values["item"]!,
            values["callback"]!,
            values["operation"]!,
            values["token"]!,
            values["source revision"]!);
    }

    public static void EnsureOuterMode()
    {
        string? sentinel = Environment.GetEnvironmentVariable(
            "FHIR_AUGURY_AUTHORING_WORKER");
        string?[] workerValues =
        [
            Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_RUN_ID"),
            Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_ITEM_ID"),
            Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_CALLBACK_URL"),
            Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_OPERATION_ID"),
            Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_OPERATION_TOKEN"),
            Environment.GetEnvironmentVariable(
                "FHIR_AUGURY_AUTHORING_SOURCE_REVISION"),
        ];
        if (!string.IsNullOrWhiteSpace(sentinel) ||
            workerValues.Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException(
                "Outer authoring actions reject worker sentinel or callback environment values.");
        }
    }

    public static string HashWebJson<T>(T value)
        => Convert.ToHexString(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    value,
                    JsonOptions)))
            .ToLowerInvariant();

    public static string HashBallotNoteProse(
        FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts.BallotNoteProsePutRequest prose)
    {
        string normalizedNeedsNote =
            prose.NeedsNote?.Trim().ToLowerInvariant() switch
            {
                "yes" or "true" => "yes",
                "no" or "false" => "no",
                _ => "unknown",
            };
        string json = JsonSerializer.Serialize(new
        {
            NeedsNote = normalizedNeedsNote,
            ProposedBallotNoteHtml =
                prose.ProposedBallotNoteHtml ?? string.Empty,
            RollupSummaryMarkdown =
                prose.RollupSummaryMarkdown ?? string.Empty,
            NotesForReviewerMarkdown =
                prose.NotesForReviewerMarkdown ?? string.Empty,
            SourceFilesNote = prose.SourceFilesNote ?? string.Empty,
        });
        string normalized = json
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        string? token,
        bool retryTransient,
        CancellationToken ct)
    {
        byte[] bytes = (await SendAsync(
            method,
            path,
            body,
            token,
            retryTransient,
            ct)).Content;
        return Deserialize<T>(bytes, path);
    }

    private async Task<AuthoringHttpResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? token,
        bool retryTransient,
        CancellationToken ct)
    {
        byte[]? bodyBytes = body is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(
                body,
                body.GetType(),
                JsonOptions);

        for (int attempt = 1; ; attempt++)
        {
            using HttpRequestMessage request = new(method, path);
            if (bodyBytes is not null)
            {
                request.Content = new ByteArrayContent(bodyBytes);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json");
            }
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.TryAddWithoutValidation(
                    OperationTokenHeader,
                    token);
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
            }
            catch (HttpRequestException ex)
            {
                if (retryTransient &&
                    attempt < MaxAttempts &&
                    (ex.StatusCode is null ||
                     IsTransient(ex.StatusCode.Value)))
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(100 * attempt),
                        ct);
                    continue;
                }

                throw new HttpRequestException(
                    $"Authoring endpoint '{path}' failed: {Redact(ex.Message, token)}",
                    ex,
                    ex.StatusCode);
            }

            using (response)
            {
                byte[] responseBytes =
                    await response.Content.ReadAsByteArrayAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    return new AuthoringHttpResponse(
                        response.StatusCode,
                        responseBytes);
                }

                if (retryTransient &&
                    attempt < MaxAttempts &&
                    IsTransient(response.StatusCode))
                {
                    await Task.Delay(
                        GetRetryDelay(response, attempt),
                        ct);
                    continue;
                }

                string detail = Redact(
                    Encoding.UTF8.GetString(responseBytes),
                    token);
                throw new HttpRequestException(
                    $"Authoring endpoint '{path}' returned {(int)response.StatusCode}: {detail}",
                    inner: null,
                    response.StatusCode);
            }
        }
    }

    private static T Deserialize<T>(
        byte[] bytes,
        string path)
        => JsonSerializer.Deserialize<T>(bytes, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Authoring endpoint '{path}' returned an empty response.");

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests ||
            (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(
        HttpResponseMessage response,
        int attempt)
    {
        RetryConditionHeaderValue? retryAfter =
            response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return delta;
        }
        if (retryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero
                ? delay
                : TimeSpan.Zero;
        }
        return TimeSpan.FromMilliseconds(100 * attempt);
    }

    private static string Redact(
        string value,
        string? token)
        => string.IsNullOrEmpty(token)
            ? value
            : value.Replace(
                token,
                "[REDACTED]",
                StringComparison.Ordinal);

    private static AuthoringRunEnvelope ToEnvelope(
        AuthoringRunResponse response)
        => new(response.Run, response.Items);

    private static object ToSnapshotResult(
        AuthoringSnapshotPairMaterialization materialized)
        => new
        {
            descriptor = materialized.Descriptor,
            snapshotPath = materialized.SnapshotPath,
            descriptorPath = materialized.DescriptorPath,
        };

    private static bool AuthoringPathsEqual(
        string left,
        string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void TryDeletePrivateDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record AuthoringHttpResponse(
        HttpStatusCode StatusCode,
        byte[] Content);
}
