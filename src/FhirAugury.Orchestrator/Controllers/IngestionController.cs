using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1")]
public class IngestionController(
    SourceHttpClient httpClient,
    OrchestratorDatabase database,
    ILoggerFactory loggerFactory) : ControllerBase
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("OrchestratorHttpApi");

    [HttpPost("ingest/trigger")]
    public async Task<IActionResult> TriggerIngestion(
        [FromQuery] string? type,
        [FromQuery] string? sources,
        CancellationToken ct)
    {
        string ingestionType = type ?? "incremental";
        List<string> targetSources = string.IsNullOrEmpty(sources)
            ? httpClient.GetEnabledSourceNames().ToList()
            : CsvParser.ParseSourceList(sources) ?? [];

        List<object> statuses = [];
        foreach (string sourceName in targetSources)
        {
            if (!httpClient.IsSourceEnabled(sourceName))
            {
                statuses.Add(new { source = sourceName, status = "error", message = "Source not configured" });
                continue;
            }

            try
            {
                IngestionStatusResponse? result = await httpClient.TriggerIngestionAsync(sourceName, ingestionType, ct);
                statuses.Add(new { source = sourceName, status = result?.Status ?? "unknown", itemsTotal = result?.ItemsTotal ?? 0 });
            }
            catch (Exception ex)
            {
                statuses.Add(new { source = sourceName, status = "error", message = ex.Message });
            }
        }

        return Ok(new { type = ingestionType, statuses });
    }

    [HttpPost("rebuild-index")]
    public async Task<IActionResult> RebuildIndex(
        [FromQuery] string? type,
        [FromQuery] string? sources,
        CancellationToken ct)
    {
        string indexType = type ?? "all";
        List<string> targets = string.IsNullOrEmpty(sources)
            ? httpClient.GetEnabledSourceNames().ToList()
            : CsvParser.ParseSourceList(sources) ?? [];

        List<object> results = [];

        await Task.WhenAll(targets.Select(async source =>
        {
            if (!httpClient.IsSourceEnabled(source))
            {
                lock (results)
                    results.Add(new { source, success = false, actionTaken = (string?)null, error = "Source not configured" });
                return;
            }

            try
            {
                RebuildIndexResponse? resp = await httpClient.RebuildIndexAsync(source, indexType, ct);
                lock (results)
                    results.Add(new { source, success = resp?.Success ?? false, actionTaken = resp?.ActionTaken, error = resp?.Error });
            }
            catch (Exception ex)
            {
                lock (results)
                    results.Add(new { source, success = false, actionTaken = (string?)null, error = ex.Message });
            }
        }));

        return Ok(new { indexType, results });
    }

    [HttpPost("notify-ingestion")]
    public async Task<IActionResult> NotifyIngestion([FromBody] PeerIngestionNotification notification, CancellationToken ct)
    {
        _logger.LogInformation("Ingestion complete from {Source}", notification.Source);

        using SqliteConnection connection = database.OpenConnection();
        XrefScanStateRecord? existing = XrefScanStateRecord.SelectSingle(
            connection, SourceName: notification.Source);

        DateTimeOffset completedAt = notification.CompletedAt is not null
            ? DateTimeOffset.Parse(notification.CompletedAt)
            : DateTimeOffset.UtcNow;

        XrefScanStateRecord record = new XrefScanStateRecord
        {
            Id = existing?.Id ?? XrefScanStateRecord.GetIndex(),
            SourceName = notification.Source,
            LastCursor = null,
            LastScanAt = completedAt,
        };

        if (existing is not null)
            XrefScanStateRecord.Update(connection, record);
        else
            XrefScanStateRecord.Insert(connection, record);

        // Fan out to all OTHER sources
        PeerIngestionNotification peerNotification = new(
            Source: notification.Source,
            CompletedAt: notification.CompletedAt);

        List<string> fanOutTargets = httpClient.GetEnabledSourceNames()
            .Where(s => !s.Equals(notification.Source, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Task[] fanOutTasks = fanOutTargets.Select(async targetSource =>
        {
            try
            {
                await httpClient.NotifyPeerAsync(targetSource, peerNotification, ct);
            }
            catch (Exception ex)
            {
                if (ex.IsTransientHttpError(out string statusDescription))
                    _logger.LogWarning("Failed to notify {Target} of {Source} ingestion ({HttpStatus})",
                        targetSource, notification.Source, statusDescription);
                else
                    _logger.LogWarning(ex, "Failed to notify {Target} of {Source} ingestion",
                        targetSource, notification.Source);
            }
        }).ToArray();

        await Task.WhenAll(fanOutTasks);

        return Ok(new { acknowledged = true });
    }
}
