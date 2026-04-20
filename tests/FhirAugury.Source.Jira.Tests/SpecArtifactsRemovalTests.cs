using System.Reflection;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Regression tests locking in the removal of the dead Jira-side spec-artifacts
/// surface. The live spec-artifacts data is owned by the GitHub source via
/// `api/v1/jira-specs/...`.
/// </summary>
public class SpecArtifactsRemovalTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public SpecArtifactsRemovalTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_specrm_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void SpecificationsController_DoesNotExposeSpecArtifactsRoute()
    {
        IEnumerable<MethodInfo> actions = typeof(SpecificationsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (MethodInfo action in actions)
        {
            foreach (HttpMethodAttribute http in action.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
            {
                Assert.NotEqual("spec-artifacts", http.Template);
            }
        }

        Assert.Null(typeof(SpecificationsController).GetMethod(
            "GetSpecArtifacts",
            BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void LifecycleController_GetStats_DoesNotEmitSpecArtifactsCount()
    {
        LifecycleController controller = new LifecycleController(
            pipeline: null!,
            db: _db,
            cache: NullResponseCache.Instance,
            indexTracker: null!);

        IActionResult result = controller.GetStats();
        StatsResponse stats = Assert.IsType<StatsResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.NotNull(stats.AdditionalCounts);
        Assert.False(stats.AdditionalCounts!.ContainsKey("spec_artifacts"),
            "Jira stats should no longer publish a 'spec_artifacts' counter — the GitHub source owns that data.");
    }
}
