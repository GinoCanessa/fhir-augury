using FhirAugury.Common.Api;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

/// <summary>API endpoints for querying JIRA-Spec-Artifacts data.</summary>
[ApiController]
[Route("api/v1/jira-specs")]
public class JiraSpecsController(GitHubDatabase db) : ControllerBase
{
    /// <summary>List specifications, optionally filtered by family or workgroup.</summary>
    [HttpGet]
    public IActionResult ListSpecs([FromQuery] string? family, [FromQuery] string? workgroup)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection);

        if (!string.IsNullOrEmpty(family))
            specs = specs.Where(s => string.Equals(s.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(workgroup))
            specs = specs.Where(s => string.Equals(s.DefaultWorkgroup, workgroup, StringComparison.OrdinalIgnoreCase)).ToList();

        List<JiraSpecSummary> summaries = specs.Select(s => BuildSummary(s, connection)).ToList();
        return Ok(new JiraSpecListResponse(summaries));
    }

    /// <summary>Get full detail for a specific specification by its key.</summary>
    [HttpGet("{specKey}")]
    public IActionResult GetSpec(string specKey)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: specKey);
        JiraSpecRecord? spec = specs.FirstOrDefault();
        if (spec is null)
            return NotFound();

        JiraSpecSummary summary = BuildSummary(spec, connection);

        List<JiraSpecVersionInfo> versions = JiraSpecVersionRecord.SelectList(connection, JiraSpecId: spec.Id)
            .Select(v => new JiraSpecVersionInfo(v.Code, v.Url, v.Deprecated))
            .ToList();

        List<JiraSpecArtifactInfo> artifacts = JiraSpecArtifactRecord.SelectList(connection, JiraSpecId: spec.Id)
            .Select(a => new JiraSpecArtifactInfo(a.ArtifactKey, a.Name, a.ArtifactId, a.ResourceType, a.Workgroup, a.Deprecated))
            .ToList();

        List<JiraSpecPageInfo> pages = JiraSpecPageRecord.SelectList(connection, JiraSpecId: spec.Id)
            .Select(p => new JiraSpecPageInfo(p.PageKey, p.Name, p.Url, p.Workgroup, p.Deprecated))
            .ToList();

        return Ok(new JiraSpecDetail(summary, versions, artifacts, pages));
    }

    /// <summary>List artifacts for a specific specification.</summary>
    [HttpGet("{specKey}/artifacts")]
    public IActionResult GetSpecArtifacts(string specKey)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecArtifactRecord> artifacts = JiraSpecArtifactRecord.SelectList(connection, SpecKey: specKey);
        List<JiraSpecArtifactInfo> result = artifacts
            .Select(a => new JiraSpecArtifactInfo(a.ArtifactKey, a.Name, a.ArtifactId, a.ResourceType, a.Workgroup, a.Deprecated))
            .ToList();
        return Ok(result);
    }

    /// <summary>List pages for a specific specification.</summary>
    [HttpGet("{specKey}/pages")]
    public IActionResult GetSpecPages(string specKey)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecPageRecord> pages = JiraSpecPageRecord.SelectList(connection, SpecKey: specKey);
        List<JiraSpecPageInfo> result = pages
            .Select(p => new JiraSpecPageInfo(p.PageKey, p.Name, p.Url, p.Workgroup, p.Deprecated))
            .ToList();
        return Ok(result);
    }

    /// <summary>List versions for a specific specification.</summary>
    [HttpGet("{specKey}/versions")]
    public IActionResult GetSpecVersions(string specKey)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecVersionRecord> versions = JiraSpecVersionRecord.SelectList(connection, SpecKey: specKey);
        List<JiraSpecVersionInfo> result = versions
            .Select(v => new JiraSpecVersionInfo(v.Code, v.Url, v.Deprecated))
            .ToList();
        return Ok(result);
    }

    /// <summary>Resolve a Jira artifact key to its specification context. Returns a list (keys may span specs).</summary>
    [HttpGet("resolve-artifact/{artifactKey}")]
    public IActionResult ResolveArtifact(string artifactKey, [FromQuery] string? specKey)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecArtifactRecord> artifacts = JiraSpecArtifactRecord.SelectList(connection, ArtifactKey: artifactKey);

        if (!string.IsNullOrEmpty(specKey))
            artifacts = artifacts.Where(a => string.Equals(a.SpecKey, specKey, StringComparison.OrdinalIgnoreCase)).ToList();

        List<JiraArtifactResolution> results = [];
        foreach (JiraSpecArtifactRecord artifact in artifacts)
        {
            List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: artifact.SpecKey);
            JiraSpecRecord? spec = specs.FirstOrDefault();

            results.Add(new JiraArtifactResolution(
                artifact.ArtifactKey,
                artifact.SpecKey,
                spec?.Family ?? "",
                spec?.SpecName,
                artifact.ArtifactId,
                artifact.ResourceType,
                artifact.Name,
                spec?.CanonicalUrl,
                artifact.Deprecated));
        }

        return Ok(results);
    }

    /// <summary>Resolve a Jira page key to its specification context. Returns a list (keys may span specs).</summary>
    [HttpGet("resolve-page/{pageKey}")]
    public IActionResult ResolvePage(string pageKey, [FromQuery] string? specKey)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecPageRecord> pages = JiraSpecPageRecord.SelectList(connection, PageKey: pageKey);

        if (!string.IsNullOrEmpty(specKey))
            pages = pages.Where(p => string.Equals(p.SpecKey, specKey, StringComparison.OrdinalIgnoreCase)).ToList();

        List<JiraPageResolution> results = [];
        foreach (JiraSpecPageRecord page in pages)
        {
            List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: page.SpecKey);
            JiraSpecRecord? spec = specs.FirstOrDefault();

            results.Add(new JiraPageResolution(
                page.PageKey,
                page.SpecKey,
                spec?.Family ?? "",
                spec?.SpecName,
                page.Name,
                page.Url,
                spec?.CanonicalUrl,
                page.Deprecated));
        }

        return Ok(results);
    }

    /// <summary>List all workgroups.</summary>
    [HttpGet("workgroups")]
    public IActionResult ListWorkgroups()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraWorkgroupRecord> workgroups = JiraWorkgroupRecord.SelectList(connection);
        List<JiraSpecRecord> allSpecs = JiraSpecRecord.SelectList(connection);

        List<JiraWorkgroupInfo> result = workgroups
            .Select(wg => new JiraWorkgroupInfo(
                wg.WorkgroupKey,
                wg.Name,
                wg.Webcode,
                wg.Listserv,
                wg.Deprecated,
                allSpecs.Count(s => string.Equals(s.DefaultWorkgroup, wg.WorkgroupKey, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        return Ok(new JiraWorkgroupListResponse(result));
    }

    /// <summary>List product families with spec counts.</summary>
    [HttpGet("families")]
    public IActionResult ListFamilies()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecFamilyRecord> families = JiraSpecFamilyRecord.SelectList(connection);

        List<JiraFamilyInfo> result = families
            .GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .Select(g => new JiraFamilyInfo(
                g.Key,
                g.Count(),
                g.Count(f => !f.Deprecated)))
            .ToList();

        return Ok(new JiraFamilyListResponse(result));
    }

    /// <summary>Find specifications by GitHub repository URL.</summary>
    [HttpGet("by-git-url")]
    public IActionResult FindByGitUrl([FromQuery] string? url)
    {
        if (string.IsNullOrEmpty(url))
            return BadRequest("url parameter is required");

        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, GitUrl: url);

        List<JiraSpecSummary> summaries = specs.Select(s => BuildSummary(s, connection)).ToList();
        return Ok(new JiraSpecListResponse(summaries));
    }

    /// <summary>Find a specification by canonical URL.</summary>
    [HttpGet("by-canonical")]
    public IActionResult FindByCanonical([FromQuery] string? url)
    {
        if (string.IsNullOrEmpty(url))
            return BadRequest("url parameter is required");

        using SqliteConnection connection = db.OpenConnection();
        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, CanonicalUrl: url);

        List<JiraSpecSummary> summaries = specs.Select(s => BuildSummary(s, connection)).ToList();
        return Ok(new JiraSpecListResponse(summaries));
    }

    // ── Helpers ──────────────────────────────────────────────

    private static JiraSpecSummary BuildSummary(JiraSpecRecord spec, SqliteConnection connection)
    {
        int artifactCount = JiraSpecArtifactRecord.SelectList(connection, JiraSpecId: spec.Id).Count;
        int pageCount = JiraSpecPageRecord.SelectList(connection, JiraSpecId: spec.Id).Count;
        int versionCount = JiraSpecVersionRecord.SelectList(connection, JiraSpecId: spec.Id).Count;

        return new JiraSpecSummary(
            spec.SpecKey,
            spec.Family,
            spec.SpecName,
            spec.CanonicalUrl,
            spec.GitUrl,
            spec.DefaultWorkgroup,
            spec.DefaultVersion,
            artifactCount,
            pageCount,
            versionCount);
    }
}
