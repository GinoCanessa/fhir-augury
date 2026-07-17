using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Materializes the configured HL7 work-group CodeSystem XML into
/// <c>&lt;cacheRoot&gt;/&lt;sourceSubdir&gt;/&lt;supportSubdir&gt;/&lt;Filename&gt;</c>.
/// Idempotent and safe to call concurrently from a startup hosted service
/// and the scheduled ingestion worker. Never throws on acquisition failures —
/// logs warnings and returns <c>null</c> when no file can be made present.
/// </summary>
/// <remarks>
/// Promoted from <c>FhirAugury.Source.Jira.Ingestion</c> so the GitHub source
/// can use the same acquirer. The Jira and GitHub source services each
/// register a thin wrapper that constructs an instance with their own cache
/// layout constants.
/// </remarks>
public sealed class WorkGroupSupportFileAcquirer
{
    /// <summary>Named HttpClient used by this acquirer.</summary>
    public const string HttpClientName = "workgroup-support";

    private readonly string _cacheRoot;
    private readonly string _sourceSubdir;
    private readonly string _supportSubdir;
    private readonly WorkGroupSourceXmlOptions _cfg;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _unconfiguredLogged;

    public WorkGroupSupportFileAcquirer(
        string cacheRoot,
        string sourceSubdir,
        string supportSubdir,
        WorkGroupSourceXmlOptions cfg,
        IHttpClientFactory httpClientFactory,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);
        ArgumentException.ThrowIfNullOrEmpty(sourceSubdir);
        ArgumentException.ThrowIfNullOrEmpty(supportSubdir);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _cacheRoot = cacheRoot;
        _sourceSubdir = sourceSubdir;
        _supportSubdir = supportSubdir;
        _cfg = cfg;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the configured XML file exists under
    /// <c>&lt;cacheRoot&gt;/&lt;sourceSubdir&gt;/&lt;supportSubdir&gt;/&lt;Filename&gt;</c>.
    /// Returns the absolute destination path when present after the call, or
    /// <c>null</c> when no file is available.
    /// </summary>
    public async Task<string?> EnsureAsync(CancellationToken ct = default)
    {
        string cacheRoot = Path.GetFullPath(_cacheRoot);
        string dest = Path.Combine(
            cacheRoot,
            _sourceSubdir,
            _supportSubdir,
            _cfg.Filename);
        string destDir = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(destDir);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cfg.LocalFile))
            {
                string source = Path.GetFullPath(_cfg.LocalFile);
                if (File.Exists(source))
                {
                    File.Copy(source, dest, overwrite: true);
                    _logger.LogInformation(
                        "workgroup support file copied from local source {Source} → {Dest}",
                        source, dest);
                }
                else
                {
                    _logger.LogWarning(
                        "Hl7WorkGroupSourceXml.LocalFile {Path} not found; not falling back to Url",
                        source);
                }

                return File.Exists(dest) ? dest : null;
            }

            if (!string.IsNullOrWhiteSpace(_cfg.Url))
            {
                if (File.Exists(dest))
                {
                    _logger.LogDebug("workgroup support file already present at {Dest}", dest);
                    return dest;
                }

                string tmp = dest + ".tmp";
                try
                {
                    HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
                    using HttpResponseMessage resp = await client.GetAsync(
                        _cfg.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "workgroup support file download from {Url} failed: HTTP {Status}",
                            _cfg.Url, (int)resp.StatusCode);
                        TryDelete(tmp);
                        return File.Exists(dest) ? dest : null;
                    }

                    await using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                    }
                    File.Move(tmp, dest, overwrite: true);
                    _logger.LogInformation(
                        "workgroup support file downloaded from {Url} → {Dest}", _cfg.Url, dest);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "workgroup support file download from {Url} failed", _cfg.Url);
                    TryDelete(tmp);
                    return File.Exists(dest) ? dest : null;
                }

                return File.Exists(dest) ? dest : null;
            }

            if (File.Exists(dest))
            {
                _logger.LogDebug("workgroup support file already present at {Dest}", dest);
                return dest;
            }

            if (Interlocked.CompareExchange(ref _unconfiguredLogged, 1, 0) == 0)
            {
                _logger.LogInformation(
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
