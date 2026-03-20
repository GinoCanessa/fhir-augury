using Fhiraugury;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Routing;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.CrossRef;

/// <summary>
/// Scans text from source services via gRPC StreamSearchableText,
/// extracts cross-references using CrossRefPatterns, and stores them in the database.
/// </summary>
public class CrossRefLinker(
    OrchestratorDatabase database,
    SourceRouter router,
    OrchestratorOptions options,
    ILogger<CrossRefLinker> logger)
{
    /// <summary>
    /// Performs a cross-reference scan across all enabled sources.
    /// Returns the number of new links discovered.
    /// </summary>
    public async Task<int> ScanAllSourcesAsync(bool fullRescan, CancellationToken ct)
    {
        var totalNewLinks = 0;

        foreach (var (sourceName, config) in options.Services)
        {
            if (!config.Enabled) continue;

            try
            {
                var newLinks = await ScanSourceAsync(sourceName, fullRescan, ct);
                totalNewLinks += newLinks;
                logger.LogInformation("Cross-ref scan for {Source}: {NewLinks} new links", sourceName, newLinks);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cross-ref scan failed for {Source}", sourceName);
            }
        }

        return totalNewLinks;
    }

    /// <summary>
    /// Scans a single source for cross-references.
    /// </summary>
    public async Task<int> ScanSourceAsync(string sourceName, bool fullRescan, CancellationToken ct)
    {
        var client = router.GetSourceClient(sourceName);
        if (client is null)
        {
            logger.LogWarning("No source client found for {Source}", sourceName);
            return 0;
        }

        using var connection = database.OpenConnection();

        // Get scan state for cursor-based incremental scanning
        var scanState = XrefScanStateRecord.SelectSingle(connection, SourceName: sourceName);
        var request = new StreamTextRequest();
        if (!fullRescan && scanState?.LastScanAt is DateTimeOffset lastScan)
        {
            request.Since = Timestamp.FromDateTimeOffset(lastScan);
        }

        var newLinks = 0;
        var latestTimestamp = scanState?.LastScanAt ?? DateTimeOffset.MinValue;

        using var stream = client.StreamSearchableText(request, cancellationToken: ct);
        while (await stream.ResponseStream.MoveNext(ct))
        {
            var item = stream.ResponseStream.Current;

            // Extract links from all text fields
            var combinedText = string.Join("\n", new[] { item.Title }.Concat(item.TextFields));
            var extractedLinks = CrossRefPatternHelper.ExtractLinks(combinedText);

            foreach (var (targetType, targetId, context) in extractedLinks)
            {
                // Skip self-references
                if (targetType == sourceName && targetId == item.Id)
                    continue;

                // Check for existing link
                var existingLinks = CrossRefLinkRecord.SelectList(connection,
                    SourceType: sourceName, SourceId: item.Id);
                var exists = existingLinks.Any(l =>
                    l.TargetType == targetType && l.TargetId == targetId);

                if (!exists)
                {
                    CrossRefLinkRecord.Insert(connection, new CrossRefLinkRecord
                    {
                        Id = CrossRefLinkRecord.GetIndex(),
                        SourceType = sourceName,
                        SourceId = item.Id,
                        TargetType = targetType,
                        TargetId = targetId,
                        LinkType = "mentions",
                        Context = context,
                        DiscoveredAt = DateTimeOffset.UtcNow,
                    }, insertPrimaryKey: true);
                    newLinks++;
                }
            }

            if (item.UpdatedAt is not null)
            {
                var itemTime = item.UpdatedAt.ToDateTimeOffset();
                if (itemTime > latestTimestamp)
                    latestTimestamp = itemTime;
            }
        }

        // Update scan state
        if (scanState is not null)
        {
            XrefScanStateRecord.Update(connection, scanState with
            {
                LastCursor = null,
                LastScanAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            XrefScanStateRecord.Insert(connection, new XrefScanStateRecord
            {
                Id = XrefScanStateRecord.GetIndex(),
                SourceName = sourceName,
                LastCursor = null,
                LastScanAt = DateTimeOffset.UtcNow,
            }, insertPrimaryKey: true);
        }

        return newLinks;
    }
}
