using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private readonly HttpClient _client;

    public AuthoringHttpClient(
        string orchestratorAddress,
        HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orchestratorAddress);
        _client = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _client.BaseAddress = new Uri(
            orchestratorAddress.EndsWith("/", StringComparison.Ordinal)
                ? orchestratorAddress
                : $"{orchestratorAddress}/",
            UriKind.Absolute);
    }

    public async Task<object> StartAsync<TRequest>(
        string serviceName,
        TRequest request,
        CancellationToken ct)
    {
        EnsureOuterMode();
        AuthoringHttpResponse raw = await SendAsync(
            HttpMethod.Post,
            ControlPath(serviceName),
            request,
            token: null,
            retryTransient: false,
            ct);
        if (raw.StatusCode == HttpStatusCode.NoContent)
        {
            return new AuthoringNoCandidatesResponse();
        }

        AuthoringRunEnvelope response =
            Deserialize<AuthoringRunEnvelope>(
                raw.Content,
                ControlPath(serviceName));
        ValidateRunEnvelope(response);
        return response;
    }

    public async Task<AuthoringRunEnvelope> GetStatusAsync(
        string serviceName,
        string runId,
        CancellationToken ct)
    {
        EnsureOuterMode();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        AuthoringRunEnvelope response = await SendJsonAsync<AuthoringRunEnvelope>(
            HttpMethod.Get,
            $"{ControlPath(serviceName)}/{Uri.EscapeDataString(runId)}",
            body: null,
            token: null,
            retryTransient: true,
            ct);
        ValidateRunEnvelope(response, runId);
        return response;
    }

    public async Task<AuthoringRetryResponse> RetryAsync(
        string serviceName,
        string runId,
        string itemId,
        CancellationToken ct)
    {
        EnsureOuterMode();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        AuthoringRetryResponse response =
            await SendJsonAsync<AuthoringRetryResponse>(
                HttpMethod.Post,
                $"{ControlPath(serviceName)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/retry",
                body: null,
                token: null,
                retryTransient: false,
                ct);
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
        EnsureOuterMode();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        AuthoringItemSupersedeResult response =
            await SendJsonAsync<AuthoringItemSupersedeResult>(
                HttpMethod.Post,
                $"{ControlPath(serviceName)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/supersede",
                new AuthoringItemSupersedeRequest(reason),
                token: null,
                retryTransient: false,
                ct);
        if (!string.Equals(response.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(response.ItemId, itemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Supersede response coordinates '{response.RunId}/{response.ItemId}' do not match requested item '{runId}/{itemId}'.");
        }
        return response;
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
        if (!string.Equals(receipt.RunId, context.RunId, StringComparison.Ordinal) ||
            !string.Equals(receipt.ItemId, context.ItemId, StringComparison.Ordinal) ||
            !string.Equals(receipt.OperationId, context.OperationId, StringComparison.Ordinal) ||
            !string.Equals(receipt.ContentHash, contentHash, StringComparison.Ordinal) ||
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

        string basePath =
            $"{ControlPath(serviceName)}/{Uri.EscapeDataString(runId)}/snapshot";
        byte[] descriptorBytes = await SendBytesAsync(
            HttpMethod.Get,
            basePath,
            body: null,
            token: null,
            retryTransient: true,
            ct);
        AuthoringSnapshotDescriptor descriptor =
            JsonSerializer.Deserialize<AuthoringSnapshotDescriptor>(
                descriptorBytes,
                JsonOptions)
            ?? throw new InvalidOperationException(
                "Snapshot descriptor response was empty.");
        if (!string.Equals(descriptor.RunId, runId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot descriptor belongs to run '{descriptor.RunId}', not '{runId}'.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.FileName) ||
            Path.IsPathRooted(descriptor.FileName) ||
            !string.Equals(
                Path.GetFileName(descriptor.FileName),
                descriptor.FileName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Snapshot descriptor contains an unsafe file name.");
        }

        bool directoryRequest =
            Directory.Exists(requestedSnapshotPath) ||
            Path.EndsInDirectorySeparator(snapshotPath);
        string outputDirectory = directoryRequest
            ? requestedSnapshotPath
            : Path.GetDirectoryName(requestedSnapshotPath)
                ?? Environment.CurrentDirectory;
        string fullSnapshotPath = Path.GetFullPath(
            Path.Combine(outputDirectory, descriptor.FileName));
        string fullDescriptorPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(descriptorPath)
                ? $"{fullSnapshotPath}.descriptor.json"
                : descriptorPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(
            fullSnapshotPath,
            fullDescriptorPath,
            pathComparison))
        {
            throw new ArgumentException(
                "Snapshot and descriptor paths must be different.");
        }

        byte[] snapshotBytes = await SendBytesAsync(
            HttpMethod.Get,
            $"{basePath}/bytes",
            body: null,
            token: null,
            retryTransient: true,
            ct);
        string checksum = Convert.ToHexString(
            SHA256.HashData(snapshotBytes)).ToLowerInvariant();
        if (snapshotBytes.LongLength != descriptor.SizeBytes ||
            !string.Equals(
                checksum,
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Downloaded snapshot bytes do not match the trusted descriptor.");
        }

        await WritePairAtomicallyAsync(
            fullSnapshotPath,
            snapshotBytes,
            fullDescriptorPath,
            descriptorBytes,
            ct);
        return new
        {
            descriptor,
            snapshotPath = fullSnapshotPath,
            descriptorPath = fullDescriptorPath,
        };
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
            Environment.GetEnvironmentVariable("FHIR_AUGURY_AUTHORING_RUN_ID"),
            Environment.GetEnvironmentVariable("FHIR_AUGURY_AUTHORING_ITEM_ID"),
            Environment.GetEnvironmentVariable("FHIR_AUGURY_AUTHORING_CALLBACK_URL"),
            Environment.GetEnvironmentVariable("FHIR_AUGURY_AUTHORING_OPERATION_ID"),
            Environment.GetEnvironmentVariable("FHIR_AUGURY_AUTHORING_OPERATION_TOKEN"),
            Environment.GetEnvironmentVariable("FHIR_AUGURY_AUTHORING_SOURCE_REVISION"),
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
                JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)))
            .ToLowerInvariant();

    public static string HashBallotNoteProse(
        FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts.BallotNoteProsePutRequest prose)
    {
        string normalizedNeedsNote = prose.NeedsNote?.Trim().ToLowerInvariant() switch
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
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    public void Dispose() => _client.Dispose();

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        string? token,
        bool retryTransient,
        CancellationToken ct)
    {
        byte[] bytes = await SendBytesAsync(
            method,
            path,
            body,
            token,
            retryTransient,
            ct);
        return Deserialize<T>(bytes, path);
    }

    private async Task<byte[]> SendBytesAsync(
        HttpMethod method,
        string path,
        object? body,
        string? token,
        bool retryTransient,
        CancellationToken ct)
        => (await SendAsync(
            method,
            path,
            body,
            token,
            retryTransient,
            ct)).Content;

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
                response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
            }
            catch (HttpRequestException ex)
            {
                if (retryTransient &&
                    attempt < MaxAttempts &&
                    (ex.StatusCode is null || IsTransient(ex.StatusCode.Value)))
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
                    await Task.Delay(GetRetryDelay(response, attempt), ct);
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

    private static T Deserialize<T>(byte[] bytes, string path)
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
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return delta;
        }
        if (retryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return TimeSpan.FromMilliseconds(100 * attempt);
    }

    private static string Redact(string value, string? token)
        => string.IsNullOrEmpty(token)
            ? value
            : value.Replace(token, "[REDACTED]", StringComparison.Ordinal);

    private static string ControlPath(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return $"api/v1/processing-services/{Uri.EscapeDataString(serviceName)}/authoring/runs";
    }

    private static void ValidateRunEnvelope(
        AuthoringRunEnvelope response,
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

    private static async Task WritePairAtomicallyAsync(
        string snapshotPath,
        byte[] snapshotContent,
        string descriptorPath,
        byte[] descriptorContent,
        CancellationToken ct)
    {
        EnsureParentDirectory(snapshotPath);
        EnsureParentDirectory(descriptorPath);
        IReadOnlyList<FileStream> publicationLocks =
            await AcquirePublicationLocksAsync(
                [snapshotPath, descriptorPath],
                ct);
        try
        {
            await WritePairUnderLockAsync(
                snapshotPath,
                snapshotContent,
                descriptorPath,
                descriptorContent,
                ct);
        }
        finally
        {
            for (int index = publicationLocks.Count - 1; index >= 0; index--)
            {
                publicationLocks[index].Dispose();
            }
        }
    }

    private static async Task WritePairUnderLockAsync(
        string snapshotPath,
        byte[] snapshotContent,
        string descriptorPath,
        byte[] descriptorContent,
        CancellationToken ct)
    {
        string snapshotTemp = $"{snapshotPath}.{Guid.NewGuid():N}.tmp";
        string descriptorTemp = $"{descriptorPath}.{Guid.NewGuid():N}.tmp";
        string snapshotBackup = $"{snapshotPath}.{Guid.NewGuid():N}.bak";
        string descriptorBackup = $"{descriptorPath}.{Guid.NewGuid():N}.bak";
        bool snapshotBackedUp = false;
        bool descriptorBackedUp = false;
        bool snapshotPublished = false;
        bool descriptorPublished = false;
        try
        {
            await File.WriteAllBytesAsync(snapshotTemp, snapshotContent, ct);
            await File.WriteAllBytesAsync(descriptorTemp, descriptorContent, ct);
            ct.ThrowIfCancellationRequested();

            if (File.Exists(snapshotPath))
            {
                File.Move(snapshotPath, snapshotBackup, overwrite: false);
                snapshotBackedUp = true;
            }
            if (File.Exists(descriptorPath))
            {
                File.Move(descriptorPath, descriptorBackup, overwrite: false);
                descriptorBackedUp = true;
            }

            File.Move(snapshotTemp, snapshotPath, overwrite: false);
            snapshotPublished = true;
            File.Move(descriptorTemp, descriptorPath, overwrite: false);
            descriptorPublished = true;
        }
        catch (Exception original)
        {
            List<Exception> rollbackErrors = [];
            TryRollback(
                () =>
                {
                    if (descriptorPublished && File.Exists(descriptorPath))
                    {
                        File.Delete(descriptorPath);
                    }
                    if (descriptorBackedUp)
                    {
                        File.Move(
                            descriptorBackup,
                            descriptorPath,
                            overwrite: false);
                    }
                },
                rollbackErrors);
            TryRollback(
                () =>
                {
                    if (snapshotPublished && File.Exists(snapshotPath))
                    {
                        File.Delete(snapshotPath);
                    }
                    if (snapshotBackedUp)
                    {
                        File.Move(
                            snapshotBackup,
                            snapshotPath,
                            overwrite: false);
                    }
                },
                rollbackErrors);
            if (rollbackErrors.Count > 0)
            {
                throw new IOException(
                    "Snapshot publication failed and the previous output pair could not be fully restored.",
                    new AggregateException([original, .. rollbackErrors]));
            }
            throw;
        }
        finally
        {
            DeleteIfExists(snapshotTemp);
            DeleteIfExists(descriptorTemp);
        }

        DeleteIfExists(snapshotBackup);
        DeleteIfExists(descriptorBackup);
    }

    private static async Task<IReadOnlyList<FileStream>>
        AcquirePublicationLocksAsync(
            IReadOnlyCollection<string> outputPaths,
            CancellationToken ct)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string[] lockPaths = outputPaths
            .Select(path => $"{path}.fhir-augury.publish.lock")
            .Distinct(comparer)
            .OrderBy(path => path, comparer)
            .ToArray();
        List<FileStream> streams = [];
        try
        {
            foreach (string lockPath in lockPaths)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        streams.Add(new FileStream(
                            lockPath,
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.None,
                            bufferSize: 1,
                            FileOptions.Asynchronous));
                        break;
                    }
                    catch (IOException) when (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                    }
                }
            }
            return streams;
        }
        catch
        {
            for (int index = streams.Count - 1; index >= 0; index--)
            {
                streams[index].Dispose();
            }
            throw;
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void TryRollback(
        Action action,
        ICollection<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record AuthoringHttpResponse(
        HttpStatusCode StatusCode,
        byte[] Content);
}
