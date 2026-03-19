using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;
using FhirAugury.Indexing.Bm25;
using FhirAugury.Models;
using FhirAugury.Models.Caching;
using FhirAugury.Sources.Confluence;
using FhirAugury.Sources.GitHub;
using FhirAugury.Sources.Jira;
using FhirAugury.Sources.Zulip;
using Microsoft.Extensions.Options;

namespace FhirAugury.Service.Workers;

/// <summary>Background worker that processes ingestion requests from the queue.</summary>
public class IngestionWorker(
    IngestionQueue queue,
    DatabaseService dbService,
    IOptions<AuguryConfiguration> config,
    IHttpClientFactory httpClientFactory,
    IResponseCache responseCache,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    /// <summary>The request currently being processed, if any.</summary>
    public IngestionRequest? ActiveRequest { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ingestion worker started");

        await foreach (var request in queue.DequeueAllAsync(stoppingToken))
        {
            ActiveRequest = request;
            try
            {
                logger.LogInformation("Processing ingestion request {RequestId}: {Source} ({Type})",
                    request.RequestId, request.SourceName, request.Type);

                await ProcessRequestAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Ingestion worker stopping — finishing request {RequestId}", request.RequestId);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingestion request {RequestId} failed", request.RequestId);
                LogIngestionFailure(request, ex);
            }
            finally
            {
                ActiveRequest = null;
            }

            // Check for cancellation between requests (graceful drain)
            if (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Ingestion worker draining — no more requests will be processed");
                break;
            }
        }

        logger.LogInformation("Ingestion worker stopped (queue depth: {Depth})", queue.Count);
    }

    private async Task ProcessRequestAsync(IngestionRequest request, CancellationToken ct)
    {
        var cfg = config.Value;
        var options = new IngestionOptions
        {
            DatabasePath = cfg.DatabasePath,
            Filter = request.Filter,
        };

        var source = CreateDataSource(request.SourceName, cfg);
        if (source is null)
        {
            logger.LogWarning("Unknown source: {Source}", request.SourceName);
            return;
        }

        IngestionResult result;

        switch (request.Type)
        {
            case IngestionType.Full:
                result = await source.DownloadAllAsync(options, ct);
                break;

            case IngestionType.Incremental:
                var since = GetLastSyncTime(request.SourceName);
                result = await source.DownloadIncrementalAsync(since, options, ct);
                break;

            case IngestionType.OnDemand:
                if (string.IsNullOrEmpty(request.Identifier))
                {
                    logger.LogWarning("OnDemand request missing identifier");
                    return;
                }
                result = await source.IngestItemAsync(request.Identifier, options, ct);
                break;

            default:
                logger.LogWarning("Unknown ingestion type: {Type}", request.Type);
                return;
        }

        // Post-ingestion: update indexes
        if (result.NewAndUpdatedItems.Count > 0)
        {
            try
            {
                using var conn = dbService.OpenConnection();
                CrossRefLinker.LinkNewItems(conn, result);

                // Convert IngestedItems to the format Bm25Calculator expects
                var bm25Items = result.NewAndUpdatedItems
                    .Select(item => (item.SourceType, item.SourceId, string.Join(" ", item.SearchableTextFields)))
                    .ToList();
                Bm25Calculator.UpdateIndex(conn, bm25Items, cfg.Bm25.K1, cfg.Bm25.B, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Post-ingestion indexing failed for {RequestId}", request.RequestId);
            }
        }

        // Log the ingestion run
        LogIngestionResult(request, result);
        UpdateSyncState(request.SourceName, result);

        logger.LogInformation("Ingestion {RequestId} complete: {Processed} processed, {New} new, {Updated} updated",
            request.RequestId, result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    private IDataSource? CreateDataSource(string sourceName, AuguryConfiguration cfg)
    {
        if (!cfg.Sources.TryGetValue(sourceName, out var sourceCfg))
            return null;

        // Resolve per-source cache mode: source override → global default
        var effectiveCacheMode = sourceCfg.Cache.Mode ?? cfg.Cache.DefaultMode;

        switch (sourceName)
        {
            case "jira":
            {
                var jiraOptions = new JiraSourceOptions
                {
                    BaseUrl = sourceCfg.BaseUrl.Length > 0 ? sourceCfg.BaseUrl : "https://jira.hl7.org",
                    AuthMode = sourceCfg.AuthMode?.Equals("ApiToken", StringComparison.OrdinalIgnoreCase) == true
                        ? JiraAuthMode.ApiToken : JiraAuthMode.Cookie,
                    Cookie = sourceCfg.Cookie,
                    ApiToken = sourceCfg.ApiToken,
                    Email = sourceCfg.Email,
                    DefaultJql = sourceCfg.DefaultJql ?? "project = \"FHIR Specification Feedback\"",
                    CacheMode = effectiveCacheMode,
                    Cache = responseCache,
                };
                var httpClient = httpClientFactory.CreateClient($"source-{sourceName}");
                JiraAuthHandler.ConfigureHttpClient(httpClient, jiraOptions);
                return new JiraSource(jiraOptions, httpClient);
            }

            case "zulip":
            {
                var zulipOptions = new ZulipSourceOptions
                {
                    BaseUrl = sourceCfg.BaseUrl.Length > 0 ? sourceCfg.BaseUrl : "https://chat.fhir.org",
                    Email = sourceCfg.Email,
                    ApiKey = sourceCfg.ApiKey,
                    CredentialFile = sourceCfg.CredentialFile,
                    OnlyWebPublic = sourceCfg.OnlyWebPublic,
                    CacheMode = effectiveCacheMode,
                    Cache = responseCache,
                };
                var httpClient = httpClientFactory.CreateClient($"source-{sourceName}");
                ZulipAuthHandler.ConfigureHttpClient(httpClient, zulipOptions);
                return new ZulipSource(zulipOptions, httpClient);
            }

            case "confluence":
            {
                var confluenceOptions = new ConfluenceSourceOptions
                {
                    BaseUrl = sourceCfg.BaseUrl.Length > 0 ? sourceCfg.BaseUrl : "https://confluence.hl7.org",
                    AuthMode = sourceCfg.AuthMode?.Equals("Basic", StringComparison.OrdinalIgnoreCase) == true
                        ? ConfluenceAuthMode.Basic : ConfluenceAuthMode.Cookie,
                    Username = sourceCfg.Username,
                    ApiToken = sourceCfg.ApiToken,
                    Cookie = sourceCfg.Cookie,
                    Spaces = sourceCfg.Spaces,
                    PageSize = sourceCfg.PageSize > 0 ? sourceCfg.PageSize : 25,
                    CacheMode = effectiveCacheMode,
                    Cache = responseCache,
                };
                var httpClient = httpClientFactory.CreateClient($"source-{sourceName}");
                ConfluenceAuthHandler.ConfigureHttpClient(httpClient, confluenceOptions);
                return new ConfluenceSource(confluenceOptions, httpClient);
            }

            case "github":
            {
                var githubOptions = new GitHubSourceOptions
                {
                    PersonalAccessToken = sourceCfg.PersonalAccessToken,
                    Repositories = sourceCfg.Repositories,
                    PageSize = sourceCfg.PageSize > 0 ? sourceCfg.PageSize : 100,
                    RateLimitBuffer = sourceCfg.RateLimitBuffer > 0 ? sourceCfg.RateLimitBuffer : 100,
                };
                var httpClient = httpClientFactory.CreateClient($"source-{sourceName}");
                GitHubRateLimiter.ConfigureHttpClient(httpClient, githubOptions);
                return new GitHubSource(githubOptions, httpClient);
            }

            default:
                return null;
        }
    }

    private DateTimeOffset GetLastSyncTime(string sourceName)
    {
        using var conn = dbService.OpenConnection();
        var syncState = SyncStateRecord.SelectSingle(conn, SourceName: sourceName);
        return syncState?.LastSyncAt ?? DateTimeOffset.MinValue;
    }

    private void UpdateSyncState(string sourceName, IngestionResult result)
    {
        try
        {
            using var conn = dbService.OpenConnection();
            var existing = SyncStateRecord.SelectSingle(conn, SourceName: sourceName);
            if (existing is not null)
            {
                existing.LastSyncAt = result.CompletedAt;
                existing.ItemsIngested += result.ItemsProcessed;
                existing.Status = result.ItemsFailed > 0 ? "completed_with_errors" : "completed";
                existing.LastError = result.Errors.Count > 0 ? result.Errors[0].Message : null;
                SyncStateRecord.Update(conn, existing);
            }
            else
            {
                SyncStateRecord.Insert(conn, new SyncStateRecord
                {
                    Id = SyncStateRecord.GetIndex(),
                    SourceName = sourceName,
                    SubSource = null,
                    LastSyncAt = result.CompletedAt,
                    LastCursor = null,
                    ItemsIngested = result.ItemsProcessed,
                    SyncSchedule = null,
                    NextScheduledAt = null,
                    Status = "completed",
                    LastError = result.Errors.Count > 0 ? result.Errors[0].Message : null,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update sync state for {Source}", sourceName);
        }
    }

    private void LogIngestionResult(IngestionRequest request, IngestionResult result)
    {
        try
        {
            using var conn = dbService.OpenConnection();
            IngestionLogRecord.Insert(conn, new IngestionLogRecord
            {
                Id = IngestionLogRecord.GetIndex(),
                SourceName = request.SourceName,
                RunType = request.Type.ToString(),
                StartedAt = result.StartedAt,
                CompletedAt = result.CompletedAt,
                ItemsProcessed = result.ItemsProcessed,
                ItemsNew = result.ItemsNew,
                ItemsUpdated = result.ItemsUpdated,
                ErrorMessage = result.Errors.Count > 0
                    ? string.Join("; ", result.Errors.Take(5).Select(e => e.Message))
                    : null,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log ingestion result for {RequestId}", request.RequestId);
        }
    }

    private void LogIngestionFailure(IngestionRequest request, Exception ex)
    {
        try
        {
            using var conn = dbService.OpenConnection();
            IngestionLogRecord.Insert(conn, new IngestionLogRecord
            {
                Id = IngestionLogRecord.GetIndex(),
                SourceName = request.SourceName,
                RunType = request.Type.ToString(),
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                ItemsProcessed = 0,
                ItemsNew = 0,
                ItemsUpdated = 0,
                ErrorMessage = ex.Message,
            });

            // Update sync_state with failure
            var syncState = SyncStateRecord.SelectSingle(conn, SourceName: request.SourceName);
            if (syncState is not null)
            {
                syncState.Status = "failed";
                syncState.LastError = ex.Message;
                SyncStateRecord.Update(conn, syncState);
            }
        }
        catch (Exception logEx)
        {
            logger.LogWarning(logEx, "Failed to log ingestion failure for {RequestId}", request.RequestId);
        }
    }
}
