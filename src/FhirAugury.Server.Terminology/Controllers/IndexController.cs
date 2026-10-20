using FhirAugury.Common.Hosting;
using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Database.Records;
using FhirAugury.Server.Terminology.Hosting;
using FhirAugury.Server.Terminology.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Controllers;

/// <summary>
/// Endpoints describing the state of the THO terminology index and
/// triggering ad-hoc re-ingestion.
/// </summary>
[ApiController]
[Route("api/v1/terminology/index")]
public class IndexController : ControllerBase
{
    private readonly TerminologyDatabase _db;
    private readonly TerminologyServiceOptions _options;
    private readonly TerminologyIndexStatusTracker _tracker;
    private readonly TerminologyIngestionPipeline _pipeline;
    private readonly ILogger<IndexController> _logger;

    public IndexController(
        TerminologyDatabase db,
        IOptions<TerminologyServiceOptions> options,
        TerminologyIndexStatusTracker tracker,
        TerminologyIngestionPipeline pipeline,
        ILogger<IndexController> logger)
    {
        _db = db;
        _options = options.Value;
        _tracker = tracker;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <summary>
    /// Returns per-package indexing state plus the most recent refresh attempt.
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        Dictionary<string, TerminologyPackageRecord> indexed = LoadIndexedPackages();

        List<object> rows = [];
        foreach (PackageOptions cfg in _options.Packages)
        {
            indexed.TryGetValue(cfg.PackageId, out TerminologyPackageRecord? row);

            rows.Add(new
            {
                packageId = cfg.PackageId,
                fhirVersion = cfg.FhirVersion,
                requestedVersionTag = cfg.VersionTag,
                resolvedVersion = row?.ResolvedVersion,
                ingestedAt = row?.IngestedAt,
                artifactCount = row?.ArtifactCount ?? 0,
                conceptCount = row?.ConceptCount ?? 0,
            });
        }

        TerminologyRefreshSnapshot? latest = _tracker.Current;
        bool ready = _options.Packages.All(p => indexed.ContainsKey(p.PackageId))
            && latest is not null
            && latest.State == StartupRebuildState.Completed;

        return Ok(new
        {
            ready,
            packages = rows,
            lastRefresh = latest is null
                ? null
                : new
                {
                    correlationId = latest.CorrelationId,
                    startedAt = latest.StartedAt,
                    completedAt = latest.CompletedAt,
                    state = latest.State.ToString(),
                    currentPhase = latest.CurrentPhase,
                    lastError = latest.LastError,
                },
        });
    }

    /// <summary>
    /// Queues an ad-hoc reingest of every configured package. Returns
    /// 202 Accepted with the correlation id of the queued refresh.
    /// </summary>
    [HttpPost("refresh")]
    public IActionResult Refresh(CancellationToken ct)
    {
        TerminologyRefreshSnapshot? in_flight = _tracker.Current;
        if (in_flight is not null && in_flight.State == StartupRebuildState.Running)
        {
            return Accepted(new
            {
                correlationId = in_flight.CorrelationId,
                state = in_flight.State.ToString(),
                message = "A refresh is already in progress.",
            });
        }

        string correlationId = _tracker.BeginRefresh();

        // Fire-and-forget on a background thread; we want the HTTP
        // response to return immediately. CancellationToken from the
        // request is intentionally NOT propagated (the client may
        // disconnect; the refresh should continue).
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.RunAsync(_tracker.SetPhase, CancellationToken.None).ConfigureAwait(false);
                _tracker.Complete();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ad-hoc terminology refresh failed.");
                _tracker.Fail(ex);
            }
        });

        return Accepted(new { correlationId, state = StartupRebuildState.Running.ToString() });
    }

    private Dictionary<string, TerminologyPackageRecord> LoadIndexedPackages()
    {
        Dictionary<string, TerminologyPackageRecord> map = new(StringComparer.OrdinalIgnoreCase);

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, PackageId, RequestedVersionTag, ResolvedVersion, FhirVersion,
                   IngestedAt, ArtifactCount, ConceptCount
            FROM terminology_packages;
            """;

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            TerminologyPackageRecord row = new()
            {
                Id = r.GetInt32(0),
                PackageId = r.GetString(1),
                RequestedVersionTag = r.GetString(2),
                ResolvedVersion = r.GetString(3),
                FhirVersion = r.GetString(4),
                IngestedAt = DateTimeOffset.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                ArtifactCount = r.GetInt32(6),
                ConceptCount = r.GetInt32(7),
            };
            map[row.PackageId] = row;
        }

        return map;
    }
}

