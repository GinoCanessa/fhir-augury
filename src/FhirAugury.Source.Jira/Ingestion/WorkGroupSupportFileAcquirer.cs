using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Materializes the configured HL7 work-group CodeSystem XML into
/// <c>cache/jira/_support/&lt;Filename&gt;</c>. Idempotent and safe to call
/// concurrently from the startup hosted service and the scheduled ingestion
/// worker. Never throws on acquisition failures — logs warnings and returns
/// <c>null</c> when no file can be made present.
/// </summary>
public sealed class WorkGroupSupportFileAcquirer(
    IOptions<JiraServiceOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    ILogger<WorkGroupSupportFileAcquirer> logger)
{
    /// <summary>Named HttpClient used by this acquirer.</summary>
    public const string HttpClientName = "workgroup-support";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _unconfiguredLogged;

    /// <summary>
    /// Ensures the configured XML file exists under
    /// <c>cache/jira/_support/&lt;Filename&gt;</c>. Returns the absolute
    /// destination path when present after the call, or <c>null</c> when no
    /// file is available.
    /// </summary>
    public async Task<string?> EnsureAsync(CancellationToken ct = default)
    {
        JiraServiceOptions opts = optionsAccessor.Value;
        WorkGroupSourceXmlOptions cfg = opts.Hl7WorkGroupSourceXml;

        string cacheRoot = Path.GetFullPath(opts.CachePath);
        string dest = Path.Combine(
            cacheRoot,
            JiraCacheLayout.SourceName,
            JiraCacheLayout.SupportPrefix,
            cfg.Filename);
        string destDir = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(destDir);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(cfg.LocalFile))
            {
                string source = Path.GetFullPath(cfg.LocalFile);
                if (File.Exists(source))
                {
                    File.Copy(source, dest, overwrite: true);
                    logger.LogInformation(
                        "workgroup support file copied from local source {Source} → {Dest}",
                        source, dest);
                }
                else
                {
                    logger.LogWarning(
                        "Hl7WorkGroupSourceXml.LocalFile {Path} not found; not falling back to Url",
                        source);
                }

                return File.Exists(dest) ? dest : null;
            }

            if (!string.IsNullOrWhiteSpace(cfg.Url))
            {
                if (File.Exists(dest))
                {
                    logger.LogDebug("workgroup support file already present at {Dest}", dest);
                    return dest;
                }

                string tmp = dest + ".tmp";
                try
                {
                    HttpClient client = httpClientFactory.CreateClient(HttpClientName);
                    using HttpResponseMessage resp = await client.GetAsync(
                        cfg.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        logger.LogWarning(
                            "workgroup support file download from {Url} failed: HTTP {Status}",
                            cfg.Url, (int)resp.StatusCode);
                        TryDelete(tmp);
                        return File.Exists(dest) ? dest : null;
                    }

                    await using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                    }
                    File.Move(tmp, dest, overwrite: true);
                    logger.LogInformation(
                        "workgroup support file downloaded from {Url} → {Dest}", cfg.Url, dest);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "workgroup support file download from {Url} failed", cfg.Url);
                    TryDelete(tmp);
                    return File.Exists(dest) ? dest : null;
                }

                return File.Exists(dest) ? dest : null;
            }

            if (File.Exists(dest))
            {
                logger.LogDebug("workgroup support file already present at {Dest}", dest);
                return dest;
            }

            if (Interlocked.CompareExchange(ref _unconfiguredLogged, 1, 0) == 0)
            {
                logger.LogInformation(
                    "Hl7WorkGroupSourceXml not configured; skipping work-group ingestion");
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
