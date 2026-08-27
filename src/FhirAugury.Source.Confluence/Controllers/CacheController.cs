using FhirAugury.Common;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Controllers;

/// <summary>
/// Answers "is my cache complete, and what is missing?" from local disk.
/// </summary>
/// <remarks>
/// No network. The verdict is a pure function of (manifest, cache tree), so it
/// is answerable at any moment — including part-way through a multi-hour pull,
/// because manifests land per space at the front of the run.
/// </remarks>
[ApiController]
[Route("api/v1/cache")]
public class CacheController(
    ConfluenceSource source,
    IOptions<ConfluenceServiceOptions> optionsAccessor) : ControllerBase
{
    private const int DefaultMissingSample = 20;

    /// <summary>Per-space reconciliation report computed from the cache alone.</summary>
    /// <param name="missingSample">How many missing ids to name per space.</param>
    [HttpGet("reconcile-report")]
    public IActionResult GetReconcileReport([FromQuery] int? missingSample)
    {
        int sample = Math.Clamp(missingSample ?? DefaultMissingSample, 0, 500);
        ConfluenceServiceOptions options = optionsAccessor.Value;

        IReadOnlyList<ConfluenceReconcilePlan> plans = source.ReconcileReport(source.BuildPolicy());

        List<object> spaces = [];
        foreach (ConfluenceReconcilePlan plan in plans)
        {
            spaces.Add(new
            {
                spaceKey = plan.SpaceKey,
                verdict = Describe(plan.Verdict),
                manifestItems = plan.ManifestItemCount,
                cached = plan.CachedCount,
                stale = plan.StaleCount,
                missing = plan.MissingCount,
                vanished = plan.VanishedCount,
                attachments = plan.AttachmentCount,
                skippedByPolicy = plan.SkippedByPolicyCount,
                skippedBytes = plan.SkippedByPolicyBytes,
                readFailures = plan.ReadFailures,
                sweptAt = plan.SweptAt,
                manifestAge = plan.SweptAt is null ? null : DateTimeOffset.UtcNow - plan.SweptAt,
                lastSweepOutcome = plan.LastSweepOutcome?.ToString().ToLowerInvariant(),
                unknownReason = plan.UnknownReason,
                profiles = new
                {
                    page = ConfluenceCacheLayout.PageProfile,
                    comment = ConfluenceCacheLayout.CommentProfile,
                    attachment = ConfluenceCacheLayout.AttachmentProfile,
                },
                missingIds = sample == 0
                    ? []
                    : plan.Items
                        .Where(i => i.NeedsFetch || i.NeedsBlobFetch)
                        .Take(sample)
                        .Select(i => new { id = i.Entry.Id, type = i.Entry.Type })
                        .ToArray<object>(),
            });
        }

        return Ok(new
        {
            source = SourceSystems.Confluence,
            generatedAt = DateTimeOffset.UtcNow,
            attachmentMaxBytes = options.AttachmentMaxBytes,
            overallVerdict = Describe(Overall(plans)),
            spaces,
        });
    }

    /// <summary>
    /// The whole instance is only as complete as its least complete space.
    /// </summary>
    private static ConfluenceSpaceVerdict Overall(IReadOnlyList<ConfluenceReconcilePlan> plans)
    {
        if (plans.Count == 0 || plans.Any(p => p.Verdict == ConfluenceSpaceVerdict.Unknown))
        {
            return ConfluenceSpaceVerdict.Unknown;
        }

        if (plans.Any(p => p.Verdict == ConfluenceSpaceVerdict.Partial))
        {
            return ConfluenceSpaceVerdict.Partial;
        }

        return plans.Any(p => p.Verdict == ConfluenceSpaceVerdict.CompleteWithSkips)
            ? ConfluenceSpaceVerdict.CompleteWithSkips
            : ConfluenceSpaceVerdict.Complete;
    }

    /// <summary>
    /// <c>complete_with_skips</c> says out loud that every item is accounted for
    /// but some attachment bytes were excluded by policy. A verdict that quietly
    /// meant "complete except for things I am not telling you about here" is the
    /// false confidence this whole shape exists to remove.
    /// </summary>
    private static string Describe(ConfluenceSpaceVerdict verdict) => verdict switch
    {
        ConfluenceSpaceVerdict.Complete => "complete",
        ConfluenceSpaceVerdict.CompleteWithSkips => "complete_with_skips",
        ConfluenceSpaceVerdict.Partial => "partial",
        _ => "unknown",
    };
}
