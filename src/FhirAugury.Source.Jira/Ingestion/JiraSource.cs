using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using System.Text.Json;
using System.Web;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Fetches issues from the Jira REST API or XML export endpoint, caches responses, and upserts into the database.
/// Supports full and incremental downloads. Auth mode determines the download strategy:
/// <list type="bullet">
///   <item><c>apitoken</c> / <c>basic</c>: REST API → JSON cache (<c>cache/jira/json/</c>)</item>
///   <item><c>cookie</c>: XML export endpoint → XML cache (<c>cache/jira/xml/</c>)</item>
/// </list>
/// </summary>
public class JiraSource(
    IOptions<JiraServiceOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    JiraDatabase database,
    IResponseCache cache,
    JiraUserMapper userMapper,
    ILogger<JiraSource> logger)
{
    private readonly JiraServiceOptions options = optionsAccessor.Value;

    public const string SourceName = SourceSystems.Jira;

    /// <summary>Performs a full download of all issues matching the configured JQL.</summary>
    public Task<IngestionResult> DownloadAllAsync(string project, string? jqlOverride, CancellationToken ct)
        => DownloadAllAsync(new JiraProjectConfig { Key = project, Jql = jqlOverride }, jqlOverride, ct);

    /// <summary>Performs a full download of all issues for the given project configuration.</summary>
    public async Task<IngestionResult> DownloadAllAsync(JiraProjectConfig projectConfig, string? jqlOverride, CancellationToken ct)
    {
        string jql = jqlOverride ?? $"project = \"{projectConfig.Key}\"";

        if (IsApiTokenAuth())
        {
            return await DownloadJsonAsync(projectConfig.Key, jql, ct);
        }

        DateOnly defaultStart = JiraCacheLayout.DefaultFullSyncStartDate;
        return await DownloadXmlAsync(projectConfig, jql, defaultStart, ct);
    }

    /// <summary>Performs an incremental download of issues updated since the given timestamp.</summary>
    public Task<IngestionResult> DownloadIncrementalAsync(string project, string? jqlOverride, DateTimeOffset since, CancellationToken ct)
        => DownloadIncrementalAsync(new JiraProjectConfig { Key = project, Jql = jqlOverride }, jqlOverride, since, ct);

    /// <summary>Performs an incremental download for the given project configuration.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(JiraProjectConfig projectConfig, string? jqlOverride, DateTimeOffset since, CancellationToken ct)
    {
        string baseJql = jqlOverride ?? $"project = \"{projectConfig.Key}\"";

        if (IsApiTokenAuth())
        {
            string sinceStr = since.ToString("yyyy-MM-dd HH:mm");
            string jql = $"{baseJql} AND updated >= '{sinceStr}' ORDER BY updated ASC";
            return await DownloadJsonAsync(projectConfig.Key, jql, ct);
        }

        DateOnly startDate = DateOnly.FromDateTime(since.UtcDateTime);
        return await DownloadXmlAsync(projectConfig, baseJql, startDate, ct);
    }

    /// <summary>Downloads via the REST API (JSON), used for apitoken/basic auth modes.</summary>
    private async Task<IngestionResult> DownloadJsonAsync(string project, string jql, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        string jsonSubPath = JiraCacheLayout.ProjectJsonSubPath(project);
        List<string> existingKeys = cache.EnumerateKeys(JiraCacheLayout.SourceName, jsonSubPath).ToList();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        IReadOnlyDictionary<string, JiraProjectShape> shapeMap = options.ShapeByProjectKey;

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            string url = $"{options.BaseUrl}/rest/api/2/search?jql={HttpUtility.UrlEncode(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger.LogInformation("Fetching issues: startAt={StartAt}", startAt);

            string json;
            bool trustedPeopleOrigin = false;
            string requestSourceFingerprint = PublicPeopleSourceFingerprint;
            bool authenticatedPeopleRequest = HasPeopleAuthentication();
            try
            {
                HttpClient httpClient = httpClientFactory.CreateClient("jira");
                HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                    httpClient, url, ct, options.RateLimiting.MaxRetries, "jira");
                response.EnsureSuccessStatusCode();
                trustedPeopleOrigin = authenticatedPeopleRequest
                    && requestSourceFingerprint == PublicPeopleSourceFingerprint && IsExpectedPeopleOrigin(response, url);
                json = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch issues at startAt={StartAt}", startAt);
                errors.Add($"page:{startAt} - {ex.Message}");
                break;
            }

            // Write to cache
            string cacheKey = CacheFileNaming.GenerateFileName(today, today, JiraCacheLayout.JsonExtension, existingKeys);
            existingKeys.Add(cacheKey);
            using (MemoryStream cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            {
                await cache.PutAsync(JiraCacheLayout.SourceName, JiraCacheLayout.ProjectJsonKey(project, cacheKey), cacheStream, ct);
            }
            if (trustedPeopleOrigin)
                await RecordPeopleCacheOriginAsync(JiraCacheLayout.ProjectJsonKey(project, cacheKey), json,
                    JiraPeopleFormat.Json, requestSourceFingerprint, ct);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int total = root.GetProperty("total").GetInt32();
            JsonElement issues = root.GetProperty("issues");

            using SqliteConnection connection = database.OpenConnection();

            try
            {
                List<JiraParsedItem> parsed = [];
                foreach (JsonElement issueJson in issues.EnumerateArray())
                {
                    string issueKey = issueJson.GetProperty("key").GetString() ?? string.Empty;
                    string projectKey = JsonElementHelper.GetNestedString(issueJson.GetProperty("fields"), "project", "key")
                                        ?? (issueKey.Contains('-') ? issueKey.Split('-')[0] : string.Empty);
                    JiraProjectShape shape = shapeMap.TryGetValue(projectKey, out JiraProjectShape s)
                        ? s : JiraProjectShape.FhirChangeRequest;

                    JiraParsedItem item = ParseJsonIssue(issueJson, shape, issueKey);
                    parsed.Add(item);
                }

                (int pageNew, int pageUpdated) = UpsertParsedItems(connection, parsed);
                itemsNew += pageNew;
                itemsUpdated += pageUpdated;
                itemsProcessed += parsed.Count;

                if (itemsProcessed >= 1000 && itemsProcessed % 1000 < parsed.Count)
                    logger.LogInformation("Download progress: {Count} issues processed", itemsProcessed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process JSON page at startAt={StartAt}", startAt);
                itemsFailed++;
                errors.Add($"page:{startAt} - {ex.Message}");
            }

            startAt += issues.GetArrayLength();
            hasMore = startAt < total;
        }

        logger.LogInformation(
            "JSON download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    /// <summary>Downloads via the XML export endpoint, used for cookie auth mode.</summary>
    private async Task<IngestionResult> DownloadXmlAsync(JiraProjectConfig projectConfig, string baseJql, DateOnly startDate, CancellationToken ct)
    {
        string project = projectConfig.Key;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        HashSet<DateOnly> cachedDates = GetCachedDates(project);
        string xmlSubPath = JiraCacheLayout.ProjectXmlSubPath(project);
        List<string> existingXmlKeys = cache.EnumerateKeys(JiraCacheLayout.SourceName, xmlSubPath).ToList();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        int window = Math.Clamp(projectConfig.DownloadWindowDays, 1, JiraProjectConfig.DownloadWindowDaysMax);
        DateOnly effectiveStart = projectConfig.StartDate is DateOnly sd && sd > startDate ? sd : startDate;

        using SqliteConnection connection = database.OpenConnection();

        for (DateOnly winStart = effectiveStart; winStart <= today && !ct.IsCancellationRequested; winStart = winStart.AddDays(window))
        {
            DateOnly winEnd = winStart.AddDays(window - 1);
            if (winEnd > today) winEnd = today;

            bool includesToday = winStart <= today && today <= winEnd;
            if (!includesToday && AllDatesCached(winStart, winEnd, cachedDates))
                continue;

            string startStr = winStart.ToString("yyyy-MM-dd");
            string endStr = winEnd.ToString("yyyy-MM-dd");
            int winDays = (winEnd.DayNumber - winStart.DayNumber) + 1;
            string dayJql = $"{baseJql} AND updated >= '{startStr} 00:00' AND updated <= '{endStr} 23:59' ORDER BY updated ASC";
            string url = $"{options.BaseUrl}/sr/jira.issueviews:searchrequest-xml/temp/SearchRequest.xml" +
                         $"?jqlQuery={HttpUtility.UrlEncode(dayJql)}&tempMax={JiraCacheLayout.XmlMaxResults}";

            logger.LogInformation(
                "Fetching XML for {Project} window {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} ({Days} days)",
                project, winStart, winEnd, winDays);

            string xml;
            bool trustedPeopleOrigin = false;
            string requestSourceFingerprint = PublicPeopleSourceFingerprint;
            bool authenticatedPeopleRequest = HasPeopleAuthentication();
            try
            {
                HttpClient httpClient = httpClientFactory.CreateClient("jira-xml");
                HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                    httpClient, url, ct, options.RateLimiting.MaxRetries, "jira-xml");
                response.EnsureSuccessStatusCode();
                trustedPeopleOrigin = authenticatedPeopleRequest
                    && requestSourceFingerprint == PublicPeopleSourceFingerprint && IsExpectedPeopleOrigin(response, url);
                xml = await response.Content.ReadAsStringAsync(ct);

                // Basic sanity check: the response should be XML
                if (!xml.TrimStart().StartsWith('<'))
                {
                    logger.LogWarning("Response for window {Start}..{End} does not appear to be XML, skipping", startStr, endStr);
                    errors.Add($"window:{startStr}..{endStr} - response is not XML");
                    continue;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch XML for window {Start}..{End}", startStr, endStr);
                errors.Add($"window:{startStr}..{endStr} - {ex.Message}");
                continue;
            }

            // Write to cache
            string cacheKey = CacheFileNaming.GenerateFileName(winStart, winEnd, JiraCacheLayout.XmlExtension, existingXmlKeys);
            existingXmlKeys.Add(cacheKey);
            using (MemoryStream cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)))
            {
                await cache.PutAsync(JiraCacheLayout.SourceName, JiraCacheLayout.ProjectXmlKey(project, cacheKey), cacheStream, ct);
            }
            if (trustedPeopleOrigin)
                await RecordPeopleCacheOriginAsync(JiraCacheLayout.ProjectXmlKey(project, cacheKey), xml,
                    JiraPeopleFormat.Xml, requestSourceFingerprint, ct);

            // Parse and upsert
            try
            {
                using MemoryStream parseStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

                List<JiraParsedItem> parsed = JiraXmlParser.ParseExport(parseStream, options.ShapeByProjectKey).ToList();
                int windowIssueCount = parsed.Count;

                (int winNew, int winUpdated) = UpsertParsedItems(connection, parsed);
                itemsNew += winNew;
                itemsUpdated += winUpdated;
                itemsProcessed += windowIssueCount;

                if (windowIssueCount >= JiraCacheLayout.XmlMaxResults)
                {
                    logger.LogWarning(
                        "XML export for {Project} window {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} hit XmlMaxResults ({Cap}); consider shrinking DownloadWindowDays",
                        project, winStart, winEnd, JiraCacheLayout.XmlMaxResults);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse XML for window {Start}..{End}", startStr, endStr);
                itemsFailed++;
                errors.Add($"parse:{startStr}..{endStr} - {ex.Message}");
            }

            if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                logger.LogInformation("XML download progress: {Count} issues processed", itemsProcessed);
        }

        logger.LogInformation(
            "XML download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    /// <summary>Loads all issues from cached API responses (no network). Merges XML and JSON caches in date order.</summary>
    public Task<IngestionResult> LoadFromCacheAsync(string? project = null, CancellationToken ct = default)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        using SqliteConnection connection = database.OpenConnection();

        foreach ((string source, string key) in MergeAndSortCacheEntries(project))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!cache.TryGet(JiraCacheLayout.SourceName, key, out Stream? stream))
            {
                continue;
            }

            using (stream)
            {
                try
                {
                    List<JiraParsedItem> records = ParseCachedFile(stream, key, options.ShapeByProjectKey).ToList();
                    (int fileNew, int fileUpdated) = UpsertParsedItems(connection, records);
                    itemsNew += fileNew;
                    itemsUpdated += fileUpdated;
                    itemsProcessed += records.Count;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process cached file {Source}/{Key}", source, key);
                    itemsFailed++;
                    errors.Add($"{source}/{key}: {ex.Message}");
                }
            }
        }

        logger.LogInformation(
            "Cache ingestion complete: {Processed} processed, {New} new, {Updated} updated",
            itemsProcessed, itemsNew, itemsUpdated);

        return Task.FromResult(new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt));
    }

    /// <summary>
    /// Clears user ID mappings before a database reset is replayed or after
    /// people maintenance commits. The caller holds the pipeline gate.
    /// </summary>
    internal virtual void ClearUserCache() => userMapper.ClearCache();

    internal string PublicPeopleSourceFingerprint => JiraPublicPeopleObservationReader.Digest(
        $"jira:{JiraPublicPeopleObservationReader.CurrentVersion}:{options.BaseUrl.TrimEnd('/')}:{options.AuthMode.ToLowerInvariant()}");

    /// <summary>
    /// Read-only acquisition for maintenance. Never calls the cache loader,
    /// ordinary download/upsert path, or upstream-refresh watermark writers.
    /// </summary>
    internal async Task<JiraPeopleEvidence> ReadPublicPeopleEvidenceAsync(
        IReadOnlyList<string> keys, string mode, CancellationToken ct)
    {
        string fingerprint = PublicPeopleSourceFingerprint;
        Dictionary<string, IReadOnlyList<JiraPeopleObservation>> observations = new(StringComparer.Ordinal);
        Dictionary<string, string> failures = new(StringComparer.Ordinal);
        if (mode == JiraPublicPeopleEvidenceModes.CacheOnly)
        {
            try
            {
                return JiraPublicPeopleObservationReader.Read(
                    ReadPeopleCacheResponses(fingerprint, ct), keys, fingerprint, upstream: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Jira public people cache enumeration refused: {Code}; failure: {Failure}",
                    JiraPublicPeopleCodes.UnknownCacheOrigin,
                    ex is UnauthorizedAccessException ? "cache-access-denied" : "cache-io-failure");
                return new(fingerprint, false, observations,
                    keys.ToDictionary(key => key, _ => JiraPublicPeopleCodes.UnknownCacheOrigin, StringComparer.Ordinal));
            }
        }
        if (mode != JiraPublicPeopleEvidenceModes.Upstream)
            throw new ArgumentException("Invalid evidence mode.", nameof(mode));

        if (!HasPeopleAuthentication()
            || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? origin)
            || origin.Scheme is not ("http" or "https")
            || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            RefuseRemaining("unqualified-source");
            return new(fingerprint, true, observations, failures);
        }

        bool json = IsApiTokenAuth();
        string clientName = json ? "jira" : "jira-xml";
        using HttpClient client = httpClientFactory.CreateClient(clientName);
        foreach (string key in keys)
        {
            ct.ThrowIfCancellationRequested();
            string url = json
                ? $"{options.BaseUrl.TrimEnd('/')}/rest/api/2/issue/{Uri.EscapeDataString(key)}?fields=updated,reporter,assignee,customfield_11000"
                : $"{options.BaseUrl.TrimEnd('/')}/sr/jira.issueviews:searchrequest-xml/temp/SearchRequest.xml?jqlQuery={HttpUtility.UrlEncode($"key = \"{key}\"")}&tempMax=1";
            try
            {
                using HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                    client, url, ct, options.RateLimiting.MaxRetries, clientName);
                response.EnsureSuccessStatusCode();
                if (!IsExpectedPeopleOrigin(response, url))
                {
                    RefuseRemaining("unexpected-upstream-origin");
                    break;
                }
                string body = await response.Content.ReadAsStringAsync(ct);
                JiraPeopleEvidence evidence = JiraPublicPeopleObservationReader.Read(
                    [new(body, json ? JiraPeopleFormat.Json : JiraPeopleFormat.Xml, fingerprint)],
                    [key], fingerprint, upstream: true);
                foreach (KeyValuePair<string, IReadOnlyList<JiraPeopleObservation>> pair in evidence.Observations)
                    observations.Add(pair.Key, pair.Value);
                foreach (KeyValuePair<string, string> pair in evidence.Failures)
                {
                    failures[pair.Key] = pair.Value == JiraPublicPeopleCodes.MalformedValue
                        ? JiraPublicPeopleCodes.UpstreamUnavailable : pair.Value;
                    if (pair.Value == JiraPublicPeopleCodes.MalformedValue)
                        logger.LogWarning("Jira public people acquisition refused: {Code}; failure: {Failure}",
                            JiraPublicPeopleCodes.UpstreamUnavailable, "malformed-upstream-response");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
                or TimeoutRejectedException or BrokenCircuitException or RateLimiterRejectedException or IOException)
            {
                // Classify exhausted operational failures from HttpRetryHelper,
                // not arbitrary exceptions or every type in the Polly namespace.
                RefuseRemaining(ex switch
                {
                    HttpRequestException { StatusCode: { } status } when HttpRetryHelper.IsAuthFailure(status)
                        => "upstream-authentication-failed",
                    HttpRequestException => "upstream-http-failure",
                    TaskCanceledException or TimeoutException or TimeoutRejectedException => "upstream-timeout",
                    BrokenCircuitException => "upstream-circuit-open",
                    RateLimiterRejectedException => "upstream-rate-limited",
                    _ => "upstream-io-failure",
                });
                break;
            }
        }
        return new(fingerprint, true, observations, failures);

        void RefuseRemaining(string failure)
        {
            // Never log exception objects: even their inner exceptions can
            // contain identities, URLs, cache paths or upstream bodies.
            logger.LogWarning("Jira public people acquisition refused: {Code}; failure: {Failure}",
                JiraPublicPeopleCodes.UpstreamUnavailable, failure);
            foreach (string remaining in keys.Where(value => !observations.ContainsKey(value)))
                failures[remaining] = JiraPublicPeopleCodes.UpstreamUnavailable;
        }
    }

    private bool HasPeopleAuthentication() => options.AuthMode.ToLowerInvariant() switch
    {
        "cookie" => !string.IsNullOrWhiteSpace(options.Cookie),
        "apitoken" or "basic" => !string.IsNullOrWhiteSpace(options.Email) && !string.IsNullOrWhiteSpace(options.ApiToken),
        _ => false,
    };

    private static bool IsExpectedPeopleOrigin(HttpResponseMessage response, string url) =>
        response.RequestMessage?.RequestUri is Uri actual
        && Uri.TryCreate(url, UriKind.Absolute, out Uri? expected)
        && actual.GetLeftPart(UriPartial.Path) == expected.GetLeftPart(UriPartial.Path);

    private sealed record PeopleCacheOrigin(int Version, string SourceFingerprint, string KeyDigest, string BodyDigest, JiraPeopleFormat Format);

    private static string PeopleOriginKey(string key) =>
        $"{JiraCacheLayout.SupportPrefix}/public-people-origins/_meta_{JiraPublicPeopleObservationReader.Digest(key)}.json";

    private async Task RecordPeopleCacheOriginAsync(
        string key, string body, JiraPeopleFormat format, string sourceFingerprint, CancellationToken ct)
    {
        // This is an acquisition receipt, not a retroactive migration of cache
        // filenames or user rows. Failure leaves that cache entry untrusted and
        // must not change normal ingestion results.
        try
        {
            PeopleCacheOrigin receipt = new(JiraPublicPeopleObservationReader.CurrentVersion,
                sourceFingerprint, JiraPublicPeopleObservationReader.Digest(key),
                JiraPublicPeopleObservationReader.Digest(body), format);
            using MemoryStream stream = new(JsonSerializer.SerializeToUtf8Bytes(receipt));
            await cache.PutAsync(JiraCacheLayout.SourceName, PeopleOriginKey(key), stream, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Jira cache acquisition receipt unavailable: {Code}; failure: {Failure}",
                JiraPublicPeopleCodes.UnknownCacheOrigin, "cache-write-cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Jira cache acquisition receipt unavailable: {Code}; failure: {Failure}",
                JiraPublicPeopleCodes.UnknownCacheOrigin,
                ex is UnauthorizedAccessException ? "cache-access-denied" : "cache-io-failure");
        }
    }

    private IEnumerable<JiraRawPeopleObservation> ReadPeopleCacheResponses(string fingerprint, CancellationToken ct)
    {
        foreach ((string format, string key) in MergeAndSortCacheEntries())
        {
            ct.ThrowIfCancellationRequested();
            JiraRawPeopleObservation observation = new("", JiraPeopleFormat.Json, null);
            try
            {
                if (cache.TryGet(JiraCacheLayout.SourceName, PeopleOriginKey(key), out Stream? receiptStream))
                {
                    PeopleCacheOrigin? receipt;
                    using (receiptStream)
                        receipt = JsonSerializer.Deserialize<PeopleCacheOrigin>(receiptStream);
                    JiraPeopleFormat expected = format == JiraCacheLayout.XmlPrefix ? JiraPeopleFormat.Xml : JiraPeopleFormat.Json;
                    if (receipt is not null && receipt.Version == JiraPublicPeopleObservationReader.CurrentVersion
                        && receipt.SourceFingerprint == fingerprint && receipt.Format == expected
                        && receipt.KeyDigest == JiraPublicPeopleObservationReader.Digest(key)
                        && cache.TryGet(JiraCacheLayout.SourceName, key, out Stream? raw))
                    {
                        using (raw)
                        using (StreamReader reader = new(raw))
                        {
                            string body = reader.ReadToEnd();
                            if (receipt.BodyDigest == JiraPublicPeopleObservationReader.Digest(body))
                                observation = new(body, expected, fingerprint);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning("Jira public people cache read refused: {Code}; failure: {Failure}",
                    JiraPublicPeopleCodes.UnknownCacheOrigin, ex switch
                    {
                        JsonException => "malformed-cache-receipt",
                        UnauthorizedAccessException => "cache-access-denied",
                        _ => "cache-io-failure",
                    });
            }
            yield return observation;
        }
    }

    private static IEnumerable<JiraParsedItem> ParseCachedFile(
        Stream stream, string key, IReadOnlyDictionary<string, JiraProjectShape> shapeMap)
    {
        if (key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return JiraXmlParser.ParseExport(stream, shapeMap);
        }

        return ParseJsonCacheFile(stream, shapeMap);
    }

    private static IEnumerable<JiraParsedItem> ParseJsonCacheFile(
        Stream stream, IReadOnlyDictionary<string, JiraProjectShape> shapeMap)
    {
        using JsonDocument doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("issues", out JsonElement issues))
            yield break;

        foreach (JsonElement issueJson in issues.EnumerateArray())
        {
            string issueKey = issueJson.GetProperty("key").GetString() ?? string.Empty;
            string projectKey = JsonElementHelper.GetNestedString(issueJson.GetProperty("fields"), "project", "key")
                                ?? (issueKey.Contains('-') ? issueKey.Split('-')[0] : string.Empty);
            JiraProjectShape shape = shapeMap.TryGetValue(projectKey, out JiraProjectShape s)
                ? s : JiraProjectShape.FhirChangeRequest;

            yield return ParseJsonIssue(issueJson, shape, issueKey);
        }
    }

    /// <summary>
    /// Maps a single JSON issue element into the correct
    /// <see cref="JiraParsedItem"/> subtype based on <paramref name="shape"/>.
    /// </summary>
    private static JiraParsedItem ParseJsonIssue(JsonElement issueJson, JiraProjectShape shape, string issueKey)
    {
        JsonElement fields = issueJson.GetProperty("fields");
        (string? assigneeUsername, string? assigneeDisplayName) = JiraFieldMapper.ExtractUserRef(fields, "assignee");
        (string? reporterUsername, string? reporterDisplayName) = JiraFieldMapper.ExtractUserRef(fields, "reporter");
        JiraXmlUserInfo userInfo = new()
        {
            AssigneeUsername = assigneeUsername,
            AssigneeDisplayName = assigneeDisplayName,
            ReporterUsername = reporterUsername,
            ReporterDisplayName = reporterDisplayName,
        };

        List<JiraInPersonRef> inPersons = JiraFieldMapper.ExtractInPersonRequesters(fields);
        List<JiraCommentRecord> comments = JiraFieldMapper.MapComments(issueJson, issueKey);
        List<JiraIssueLinkRecord> links = JiraFieldMapper.MapIssueLinks(issueJson, issueKey);

        return shape switch
        {
            JiraProjectShape.ProjectScopeStatement => new JiraParsedProjectScopeStatement
            {
                Record = JiraFieldMapper.MapProjectScopeStatement(issueJson),
                Comments = comments,
                UserInfo = userInfo,
                InPersons = inPersons,
                Links = links,
            },
            JiraProjectShape.BallotDefinition => new JiraParsedBaldef
            {
                Record = JiraFieldMapper.MapBaldef(issueJson),
                Comments = comments,
                UserInfo = userInfo,
                InPersons = inPersons,
                Links = links,
            },
            JiraProjectShape.BallotVote => new JiraParsedBallot
            {
                Record = JiraFieldMapper.MapBallot(issueJson),
                Comments = comments,
                UserInfo = userInfo,
                InPersons = inPersons,
                Links = links,
            },
            _ => new JiraParsedFhirIssue
            {
                Record = JiraFieldMapper.MapIssue(issueJson),
                Comments = comments,
                UserInfo = userInfo,
                InPersons = inPersons,
                Links = links,
            },
        };
    }

    /// <summary>Returns true when the auth mode uses the REST API (apitoken or basic).</summary>
    private bool IsApiTokenAuth() =>
        options.AuthMode.Equals("apitoken", StringComparison.OrdinalIgnoreCase) ||
        options.AuthMode.Equals("basic", StringComparison.OrdinalIgnoreCase);

    /// <summary>Collects all dates that have cached files for a specific project.</summary>
    internal HashSet<DateOnly> GetCachedDates(string project)
    {
        HashSet<DateOnly> dates = [];
        string subPath = JiraCacheLayout.ProjectSubPath(project);

        foreach (string key in cache.EnumerateKeys(JiraCacheLayout.SourceName, subPath))
        {
            if (CacheFileNaming.TryParse(Path.GetFileName(key), out CacheFileNaming.ParsedCacheFile? parsed))
            {
                for (DateOnly d = parsed.StartDate; d <= parsed.EndDate; d = d.AddDays(1))
                    dates.Add(d);
            }
        }

        return dates;
    }

    private static bool AllDatesCached(DateOnly start, DateOnly end, HashSet<DateOnly> cached)
    {
        for (DateOnly d = start; d <= end; d = d.AddDays(1))
            if (!cached.Contains(d)) return false;
        return true;
    }

    /// <summary>
    /// Enumerates <paramref name="window"/>-sized <c>(start, end)</c> pairs that cover
    /// <paramref name="start"/>..<paramref name="today"/>, clamping the final window to
    /// <paramref name="today"/>. Exposed internally for unit tests.
    /// </summary>
    internal static IEnumerable<(DateOnly Start, DateOnly End)> ComputeWindows(DateOnly start, DateOnly today, int window)
    {
        if (window < 1) window = 1;
        for (DateOnly winStart = start; winStart <= today; winStart = winStart.AddDays(window))
        {
            DateOnly winEnd = winStart.AddDays(window - 1);
            if (winEnd > today) winEnd = today;
            yield return (winStart, winEnd);
        }
    }

    /// <summary>
    /// Merges cache entries from both XML and JSON sources, sorted by date for correct ingestion order.
    /// Files without a parseable date are appended at the end.
    /// </summary>
    private List<(string Source, string Key)> MergeAndSortCacheEntries(string? project = null)
    {
        List<(string Source, string Key, CacheFileNaming.ParsedCacheFile? Parsed)> entries = [];

        IEnumerable<string> allKeys = project is not null
            ? cache.EnumerateKeys(JiraCacheLayout.SourceName, JiraCacheLayout.ProjectSubPath(project))
            : cache.EnumerateKeys(JiraCacheLayout.SourceName);

        foreach (string key in allKeys)
        {
            // Classify by segments to handle both legacy and project-scoped keys:
            //   Legacy:  "xml/<file>"       (2 segments)
            //   New:     "FHIR/xml/<file>"  (3 segments)
            string[] segments = key.Split('/');
            string sourceType;

            if (segments.Length == 3)
            {
                sourceType = segments[1];
            }
            else if (segments.Length == 2)
            {
                sourceType = segments[0];
            }
            else
            {
                continue; // skip metadata files, unexpected formats
            }

            if (sourceType is not (JiraCacheLayout.XmlPrefix or JiraCacheLayout.JsonPrefix))
            {
                continue; // skip non-data directories
            }

            CacheFileNaming.TryParse(Path.GetFileName(key), out CacheFileNaming.ParsedCacheFile? parsed);
            entries.Add((sourceType, key, parsed));
        }

        entries.Sort((a, b) =>
        {
            // Files with dates come before files without dates
            bool aHasDate = a.Parsed is not null;
            bool bHasDate = b.Parsed is not null;
            if (aHasDate != bHasDate)
                return aHasDate ? -1 : 1;

            if (a.Parsed is not null && b.Parsed is not null)
            {
                int dateCmp = a.Parsed.StartDate.CompareTo(b.Parsed.StartDate);
                if (dateCmp != 0)
                    return dateCmp;

                // Wider range (later end date) ingests first
                int endCmp = b.Parsed.EndDate.CompareTo(a.Parsed.EndDate);
                if (endCmp != 0)
                    return endCmp;

                int seqCmp = (a.Parsed.SequenceNumber ?? 0).CompareTo(b.Parsed.SequenceNumber ?? 0);
                if (seqCmp != 0)
                    return seqCmp;
            }

            // Tie-break by source type (xml before json) then key name
            int srcCmp = string.Compare(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);
            if (srcCmp != 0)
                return srcCmp;

            return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        });

        return entries.Select(e => (e.Source, e.Key)).ToList();
    }

    /// <summary>
    /// Shared upsert path used by all three ingestion entry points
    /// (REST/JSON, XML, cache replay). Routes each parsed item to the
    /// correct typed table based on its concrete <see cref="JiraParsedItem"/>
    /// subtype, performs key-based existence lookups, removes superseded
    /// side-table rows, and bulk-writes records + comments + in-persons +
    /// links + (FHIR-only) related-issue rows.
    /// </summary>
    private (int New, int Updated) UpsertParsedItems(
        SqliteConnection conn,
        IReadOnlyList<JiraParsedItem> items)
    {
        // Resolve users for the entire batch up front so jira_users rows
        // exist before any record is written. Dedup-on-key per shape happens
        // below; user resolution is intentionally not deduplicated.
        foreach (JiraParsedItem item in items)
        {
            item.BaseRecord.AssigneeUserId = userMapper.ResolveUser(
                conn,
                item.UserInfo.AssigneeUsername,
                item.UserInfo.AssigneeDisplayName);
            item.BaseRecord.ReporterUserId = userMapper.ResolveUser(
                conn,
                item.UserInfo.ReporterUsername,
                item.UserInfo.ReporterDisplayName);
            userMapper.ResolveByDisplayName(conn, item.VoteMover);
            userMapper.ResolveByDisplayName(conn, item.VoteSeconder);

            foreach (JiraCommentRecord comment in item.Comments)
            {
                userMapper.ResolveUser(conn, comment.Author, null);
            }
        }

        // Group by concrete shape, last-write-wins per Key per shape (mirrors
        // the previous Dictionary<string, T>-keyed accumulation).
        Dictionary<string, JiraParsedFhirIssue> fhirByKey = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JiraParsedProjectScopeStatement> pssByKey = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JiraParsedBaldef> baldefByKey = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JiraParsedBallot> ballotByKey = new(StringComparer.OrdinalIgnoreCase);

        foreach (JiraParsedItem item in items)
        {
            switch (item)
            {
                case JiraParsedFhirIssue f: fhirByKey[item.Key] = f; break;
                case JiraParsedProjectScopeStatement p: pssByKey[item.Key] = p; break;
                case JiraParsedBaldef b: baldefByKey[item.Key] = b; break;
                case JiraParsedBallot v: ballotByKey[item.Key] = v; break;
            }
        }

        int totalNew = 0;
        int totalUpdated = 0;

        // Side-table accumulators (all four shapes contribute uniformly).
        List<JiraCommentRecord> commentsToInsert = [];
        List<JiraIssueLinkRecord> linksToInsert = [];
        List<JiraIssueInPersonRecord> inPersonsToInsert = [];
        List<JiraIssueRelatedRecord> relatedToInsert = [];

        if (fhirByKey.Count > 0)
        {
            (int n, int u) = UpsertShape(
                conn, fhirByKey, x => x.Record,
                keys => JiraIssueRecord.SelectList(conn, KeyValues: keys).ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase),
                JiraIssueRecord.GetIndex,
                (toInsert, toUpdate) =>
                {
                    toUpdate.Update(conn);
                    toInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
                });
            totalNew += n;
            totalUpdated += u;

            foreach (JiraParsedFhirIssue item in fhirByKey.Values)
            {
                CollectSideTables(item, commentsToInsert, linksToInsert, inPersonsToInsert, conn);
                foreach (string relatedKey in item.RelatedIssueKeys)
                {
                    relatedToInsert.Add(new JiraIssueRelatedRecord
                    {
                        Id = JiraIssueRelatedRecord.GetIndex(),
                        IssueKey = item.Key,
                        RelatedIssueKey = relatedKey,
                    });
                }
            }
        }

        if (pssByKey.Count > 0)
        {
            (int n, int u) = UpsertShape(
                conn, pssByKey, x => x.Record,
                keys => JiraProjectScopeStatementRecord.SelectList(conn, KeyValues: keys).ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase),
                JiraProjectScopeStatementRecord.GetIndex,
                (toInsert, toUpdate) =>
                {
                    toUpdate.Update(conn);
                    toInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
                });
            totalNew += n;
            totalUpdated += u;

            foreach (JiraParsedProjectScopeStatement item in pssByKey.Values)
            {
                CollectSideTables(item, commentsToInsert, linksToInsert, inPersonsToInsert, conn);
            }
        }

        if (baldefByKey.Count > 0)
        {
            (int n, int u) = UpsertShape(
                conn, baldefByKey, x => x.Record,
                keys => JiraBaldefRecord.SelectList(conn, KeyValues: keys).ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase),
                JiraBaldefRecord.GetIndex,
                (toInsert, toUpdate) =>
                {
                    toUpdate.Update(conn);
                    toInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
                });
            totalNew += n;
            totalUpdated += u;

            foreach (JiraParsedBaldef item in baldefByKey.Values)
            {
                CollectSideTables(item, commentsToInsert, linksToInsert, inPersonsToInsert, conn);
            }
        }

        if (ballotByKey.Count > 0)
        {
            (int n, int u) = UpsertShape(
                conn, ballotByKey, x => x.Record,
                keys => JiraBallotRecord.SelectList(conn, KeyValues: keys).ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase),
                JiraBallotRecord.GetIndex,
                (toInsert, toUpdate) =>
                {
                    toUpdate.Update(conn);
                    toInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
                });
            totalNew += n;
            totalUpdated += u;

            foreach (JiraParsedBallot item in ballotByKey.Values)
            {
                CollectSideTables(item, commentsToInsert, linksToInsert, inPersonsToInsert, conn);
            }
        }

        if (commentsToInsert.Count > 0)
        {
            commentsToInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (relatedToInsert.Count > 0)
        {
            relatedToInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (inPersonsToInsert.Count > 0)
        {
            inPersonsToInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (linksToInsert.Count > 0)
        {
            linksToInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        return (totalNew, totalUpdated);
    }

    /// <summary>
    /// Generic per-shape upsert: chunks the keys for the existence lookup,
    /// classifies each parsed item into insert/update buckets (assigning
    /// existing IDs on update, fresh IDs on insert), removes superseded
    /// side-table rows for updated keys, and finally calls
    /// <paramref name="flush"/> to persist the two buckets.
    /// </summary>
    private (int New, int Updated) UpsertShape<TParsed, TRecord>(
        SqliteConnection conn,
        Dictionary<string, TParsed> byKey,
        Func<TParsed, TRecord> getRecord,
        Func<string[], Dictionary<string, TRecord>> selectExisting,
        Func<int> getIndex,
        Action<List<TRecord>, List<TRecord>> flush)
        where TParsed : JiraParsedItem
        where TRecord : JiraIssueBaseRecord
    {
        Dictionary<string, TRecord> existingByKey = new(StringComparer.OrdinalIgnoreCase);
        string[] keys = byKey.Keys.ToArray();
        foreach (string[] chunk in keys.Chunk(500))
        {
            foreach (KeyValuePair<string, TRecord> kvp in selectExisting(chunk))
            {
                existingByKey[kvp.Key] = kvp.Value;
            }
        }

        List<TRecord> toInsert = [];
        List<TRecord> toUpdate = [];

        foreach (KeyValuePair<string, TParsed> kvp in byKey)
        {
            TRecord record = getRecord(kvp.Value);

            if (existingByKey.TryGetValue(kvp.Key, out TRecord? dbExisting))
            {
                record.Id = dbExisting.Id;
                toUpdate.Add(record);
                RemoveExistingComments(conn, kvp.Key);
                RemoveRelatedIssues(conn, kvp.Key);
                RemoveExistingInPersons(conn, kvp.Key);
            }
            else
            {
                if (record.Id <= 0)
                {
                    record.Id = getIndex();
                }
                toInsert.Add(record);
            }
        }

        flush(toInsert, toUpdate);
        return (toInsert.Count, toUpdate.Count);
    }

    /// <summary>
    /// Appends a parsed item's side-table rows (comments, links, in-persons)
    /// to the shared accumulators. In-person rows resolve to user IDs via
    /// <see cref="userMapper"/>; users that cannot be resolved are dropped.
    /// </summary>
    private void CollectSideTables(
        JiraParsedItem item,
        List<JiraCommentRecord> comments,
        List<JiraIssueLinkRecord> links,
        List<JiraIssueInPersonRecord> inPersons,
        SqliteConnection conn)
    {
        foreach (JiraCommentRecord comment in item.Comments)
        {
            if (comment.Id <= 0) comment.Id = JiraCommentRecord.GetIndex();
            comments.Add(comment);
        }

        foreach (JiraIssueLinkRecord link in item.Links)
        {
            if (link.Id <= 0) link.Id = JiraIssueLinkRecord.GetIndex();
            links.Add(link);
        }

        foreach (JiraInPersonRef ipRef in item.InPersons)
        {
            int? userId = userMapper.ResolveUser(conn, ipRef.Username, ipRef.DisplayName);
            if (userId is not null)
            {
                inPersons.Add(new JiraIssueInPersonRecord
                {
                    Id = JiraIssueInPersonRecord.GetIndex(),
                    IssueKey = item.Key,
                    UserId = userId.Value,
                });
            }
        }
    }

    private static void RemoveExistingComments(SqliteConnection conn, string issueKey)
    {
        using SqliteCommand deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM jira_comments WHERE IssueKey = @key";
        deleteCmd.Parameters.AddWithValue("@key", issueKey);
        deleteCmd.ExecuteNonQuery();
    }

    private static void RemoveRelatedIssues(SqliteConnection conn, string issueKey)
    {
        using SqliteCommand deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM jira_issue_related WHERE IssueKey = @key";
        deleteCmd.Parameters.AddWithValue("@key", issueKey);
        deleteCmd.ExecuteNonQuery();
    }

    private static void RemoveExistingInPersons(SqliteConnection conn, string issueKey)
    {
        using SqliteCommand deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM jira_issue_inpersons WHERE IssueKey = @key";
        deleteCmd.Parameters.AddWithValue("@key", issueKey);
        deleteCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Seeds the <c>jira_projects</c> table from configuration. For new keys
    /// we insert a row using the configured <c>BaselineValue</c> and
    /// <c>Enabled</c>; for keys that already exist we only refresh the
    /// ingestion-owned <c>Enabled</c> flag so user edits to
    /// <c>BaselineValue</c> (made via the management API) survive subsequent
    /// syncs.
    /// </summary>
    public void UpsertProjectsFromConfig()
    {
        List<JiraProjectConfig> configured = options.GetEffectiveProjects();

        if (configured.Count == 0) return;

        using SqliteConnection connection = database.OpenConnection();
        foreach (JiraProjectConfig cfg in configured)
        {
            JiraProjectRecord? existing = JiraProjectRecord.SelectSingle(connection, Key: cfg.Key);
            if (existing is null)
            {
                JiraProjectRecord record = new JiraProjectRecord
                {
                    Id = JiraProjectRecord.GetIndex(),
                    Key = cfg.Key,
                    Enabled = cfg.Enabled,
                    BaselineValue = Math.Clamp(cfg.BaselineValue, 0, 10),
                    IssueCount = 0,
                    LastSyncAt = null,
                };
                JiraProjectRecord.Insert(connection, record, ignoreDuplicates: true);
            }
            else if (existing.Enabled != cfg.Enabled)
            {
                existing.Enabled = cfg.Enabled;
                JiraProjectRecord.Update(connection, existing);
            }
        }
    }

    /// <summary>
    /// Updates the <c>IssueCount</c> and aligns <c>LastSyncAt</c> with the
    /// canonical successful upstream watermark. Local rebuild or partial-run
    /// completion times are never substituted. No-op if the project row does
    /// not exist.
    /// </summary>
    public void UpdateProjectCounters(
        string projectKey,
        DateTimeOffset? lastSuccessfulSyncAt)
    {
        using SqliteConnection connection = database.OpenConnection();
        JiraProjectRecord? existing = JiraProjectRecord.SelectSingle(connection, Key: projectKey);
        if (existing is null) return;

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jira_issues WHERE ProjectKey = @key";
        cmd.Parameters.AddWithValue("@key", projectKey);
        int count = Convert.ToInt32(cmd.ExecuteScalar());

        existing.IssueCount = count;
        existing.LastSyncAt = lastSuccessfulSyncAt;
        JiraProjectRecord.Update(connection, existing);
    }
}

/// <summary>Result of an ingestion run.</summary>
public record IngestionResult(
    int ItemsProcessed,
    int ItemsNew,
    int ItemsUpdated,
    int ItemsFailed,
    List<string> Errors,
    DateTimeOffset StartedAt)
{
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}
