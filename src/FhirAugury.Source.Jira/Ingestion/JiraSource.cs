using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    public async Task<IngestionResult> DownloadAllAsync(string project, string? jqlOverride, CancellationToken ct)
    {
        string jql = jqlOverride ?? $"project = \"{project}\"";

        if (IsApiTokenAuth())
        {
            return await DownloadJsonAsync(project, jql, ct);
        }

        return await DownloadXmlAsync(project, jql, JiraCacheLayout.DefaultFullSyncStartDate, ct);
    }

    /// <summary>Performs an incremental download of issues updated since the given timestamp.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(string project, string? jqlOverride, DateTimeOffset since, CancellationToken ct)
    {
        string baseJql = jqlOverride ?? $"project = \"{project}\"";

        if (IsApiTokenAuth())
        {
            string sinceStr = since.ToString("yyyy-MM-dd HH:mm");
            string jql = $"{baseJql} AND updated >= '{sinceStr}' ORDER BY updated ASC";
            return await DownloadJsonAsync(project, jql, ct);
        }

        DateOnly startDate = DateOnly.FromDateTime(since.UtcDateTime);
        return await DownloadXmlAsync(project, baseJql, startDate, ct);
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

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            string url = $"{options.BaseUrl}/rest/api/2/search?jql={HttpUtility.UrlEncode(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger.LogInformation("Fetching issues: startAt={StartAt}", startAt);

            string json;
            try
            {
                HttpClient httpClient = httpClientFactory.CreateClient("jira");
                HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                    httpClient, url, ct, options.RateLimiting.MaxRetries, "jira");
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch issues at startAt={StartAt}", startAt);
                errors.Add($"page:{startAt} - {ex.Message}");
                break;
            }

            // Write to cache
            string cacheKey = CacheFileNaming.GenerateDailyFileName(today, JiraCacheLayout.JsonExtension, existingKeys);
            existingKeys.Add(cacheKey);
            using (MemoryStream cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            {
                await cache.PutAsync(JiraCacheLayout.SourceName, JiraCacheLayout.ProjectJsonKey(project, cacheKey), cacheStream, ct);
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int total = root.GetProperty("total").GetInt32();
            JsonElement issues = root.GetProperty("issues");

            using SqliteConnection connection = database.OpenConnection();

            foreach (JsonElement issueJson in issues.EnumerateArray())
            {
                (ProcessOutcome outcome, string? error) = ProcessIssue(issueJson, connection);
                itemsProcessed++;

                switch (outcome)
                {
                    case ProcessOutcome.New: itemsNew++; break;
                    case ProcessOutcome.Updated: itemsUpdated++; break;
                    case ProcessOutcome.Failed:
                        itemsFailed++;
                        if (error is not null) errors.Add(error);
                        break;
                }

                if (itemsProcessed % 1000 == 0)
                    logger.LogInformation("Download progress: {Count} issues processed", itemsProcessed);
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
    private async Task<IngestionResult> DownloadXmlAsync(string project, string baseJql, DateOnly startDate, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        HashSet<DateOnly> cachedDates = GetCachedDates(project);
        string xmlSubPath = JiraCacheLayout.ProjectXmlSubPath(project);
        List<string> existingXmlKeys = cache.EnumerateKeys(JiraCacheLayout.SourceName, xmlSubPath).ToList();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        using SqliteConnection connection = database.OpenConnection();

        for (DateOnly date = startDate; date <= today && !ct.IsCancellationRequested; date = date.AddDays(1))
        {
            // Always refresh today; skip dates already cached in either folder
            if (date != today && cachedDates.Contains(date))
                continue;

            string dateStr = date.ToString("yyyy-MM-dd");
            string dayJql = $"{baseJql} AND updated >= '{dateStr} 00:00' AND updated <= '{dateStr} 23:59' ORDER BY updated ASC";
            string url = $"{options.BaseUrl}/sr/jira.issueviews:searchrequest-xml/temp/SearchRequest.xml" +
                         $"?jqlQuery={HttpUtility.UrlEncode(dayJql)}&tempMax={JiraCacheLayout.XmlMaxResults}";

            logger.LogInformation("Fetching XML for date {Date}", dateStr);

            string xml;
            try
            {
                HttpClient httpClient = httpClientFactory.CreateClient("jira-xml");
                HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                    httpClient, url, ct, options.RateLimiting.MaxRetries, "jira-xml");
                response.EnsureSuccessStatusCode();
                xml = await response.Content.ReadAsStringAsync(ct);

                // Basic sanity check: the response should be XML
                if (!xml.TrimStart().StartsWith('<'))
                {
                    logger.LogWarning("Response for date {Date} does not appear to be XML, skipping", dateStr);
                    errors.Add($"date:{dateStr} - response is not XML");
                    continue;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch XML for date {Date}", dateStr);
                errors.Add($"date:{dateStr} - {ex.Message}");
                continue;
            }

            // Write to cache
            string cacheKey = CacheFileNaming.GenerateDailyFileName(date, JiraCacheLayout.XmlExtension, existingXmlKeys);
            existingXmlKeys.Add(cacheKey);
            using (MemoryStream cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)))
            {
                await cache.PutAsync(JiraCacheLayout.SourceName, JiraCacheLayout.ProjectXmlKey(project, cacheKey), cacheStream, ct);
            }

            // Parse and upsert
            try
            {
                using MemoryStream parseStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

                Dictionary<string, JiraIssueRecord> toUpdate = [];
                Dictionary<string, JiraIssueRecord> toInsert = [];

                Dictionary<string, List<JiraCommentRecord>> commentsToInsert = [];
                Dictionary<string, List<JiraIssueRelatedRecord>> relatedIssuesToInsert = [];
                List<JiraIssueInPersonRecord> inPersonsToInsert = [];

                foreach (JiraParsedIssue parsed in JiraXmlParser.ParseExport(parseStream))
                {
                    JiraIssueRecord issue = parsed.Issue;

                    // Resolve users and set FK columns
                    issue.AssigneeId = userMapper.ResolveUser(connection, parsed.UserInfo.AssigneeUsername, parsed.UserInfo.AssigneeDisplayName);
                    issue.ReporterId = userMapper.ResolveUser(connection, parsed.UserInfo.ReporterUsername, parsed.UserInfo.ReporterDisplayName);
                    issue.VoteMoverId = userMapper.ResolveByDisplayName(connection, issue.VoteMover);
                    issue.VoteSeconderId = userMapper.ResolveByDisplayName(connection, issue.VoteSeconder);

                    if (toInsert.TryGetValue(issue.Key, out JiraIssueRecord? existing))
                    {
                        issue.Id = existing.Id;
                        toInsert[issue.Key] = issue;
                    }
                    else if (toUpdate.TryGetValue(issue.Key, out existing))
                    {
                        issue.Id = existing.Id;
                        toUpdate[issue.Key] = issue;
                    }
                    else if (JiraIssueRecord.SelectSingle(connection, Key: issue.Key) is JiraIssueRecord dbExisting)
                    {
                        issue.Id = dbExisting.Id;
                        toUpdate[issue.Key] = issue;
                        RemoveExistingComments(connection, issue.Key);
                        RemoveRelatedIssues(connection, issue.Key);
                    }
                    else
                    {
                        if (issue.Id <= 0)
                        {
                            issue.Id = JiraIssueRecord.GetIndex();
                        }

                        toInsert[issue.Key] = issue;
                    }

                    foreach (JiraCommentRecord comment in parsed.Comments)
                    {
                        if (comment.Id <= 0) 
                        {
                            comment.Id = JiraCommentRecord.GetIndex();
                        }
                        comment.IssueId = issue.Id;
                        // Resolve comment author (XML: author attribute is username)
                        userMapper.ResolveUser(connection, comment.Author, null);
                    }

                    commentsToInsert[issue.Key] = parsed.Comments;

                    // Collect in-person requesters
                    foreach (JiraInPersonRef ipRef in parsed.InPersons)
                    {
                        int? userId = userMapper.ResolveUser(connection, ipRef.Username, ipRef.DisplayName);
                        if (userId is not null)
                        {
                            inPersonsToInsert.Add(new()
                            {
                                Id = JiraIssueInPersonRecord.GetIndex(),
                                IssueId = issue.Id,
                                UserId = userId.Value,
                            });
                        }
                    }

                    if (issue.RelatedIssues is not null)
                    {
                        List<JiraIssueRelatedRecord> related = [];

                        string[] keys = issue.RelatedIssues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        foreach (string relatedKey in keys)
                        {
                            related.Add(new()
                            {
                                Id = JiraIssueRelatedRecord.GetIndex(),
                                IssueId = issue.Id,
                                IssueKey = issue.Key,
                                RelatedIssueKey = relatedKey
                            });
                        }

                        if (related.Count > 0)
                        {
                            relatedIssuesToInsert[issue.Key] = related;
                        }
                        else if (relatedIssuesToInsert.ContainsKey(issue.Key))
                        {
                            relatedIssuesToInsert.Remove(issue.Key);
                        }
                    }

                    itemsProcessed++;
                }

                toUpdate.Values.Update(connection);
                toInsert.Values.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);

                foreach (List<JiraCommentRecord> comments in commentsToInsert.Values)
                {
                    comments.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                foreach (List<JiraIssueRelatedRecord> relateds in relatedIssuesToInsert.Values)
                {
                    relateds.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                if (inPersonsToInsert.Count > 0)
                {
                    inPersonsToInsert.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                itemsNew += toInsert.Count;
                itemsUpdated += toUpdate.Count;

            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse XML for date {Date}", dateStr);
                itemsFailed++;
                errors.Add($"parse:{dateStr} - {ex.Message}");
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
                Dictionary<string, JiraIssueRecord> toUpdate = [];
                Dictionary<string, JiraIssueRecord> toInsert = [];

                Dictionary<string, List<JiraCommentRecord>> commentsToInsert = [];
                Dictionary<string, List<JiraIssueRelatedRecord>> relatedIssuesToInsert = [];
                List<JiraIssueInPersonRecord> inPersonsToInsert = [];

                try
                {
                    IEnumerable<JiraParsedIssue> records = ParseCachedFile(stream, key);
                    foreach (JiraParsedIssue parsed in records)
                    {
                        JiraIssueRecord issue = parsed.Issue;

                        // Resolve users and set FK columns
                        issue.AssigneeId = userMapper.ResolveUser(connection, parsed.UserInfo.AssigneeUsername, parsed.UserInfo.AssigneeDisplayName);
                        issue.ReporterId = userMapper.ResolveUser(connection, parsed.UserInfo.ReporterUsername, parsed.UserInfo.ReporterDisplayName);
                        issue.VoteMoverId = userMapper.ResolveByDisplayName(connection, issue.VoteMover);
                        issue.VoteSeconderId = userMapper.ResolveByDisplayName(connection, issue.VoteSeconder);

                        if (toInsert.TryGetValue(issue.Key, out JiraIssueRecord? existing))
                        {
                            issue.Id = existing.Id;
                            toInsert[issue.Key] = issue;
                        }
                        else if (toUpdate.TryGetValue(issue.Key, out existing))
                        {
                            issue.Id = existing.Id;
                            toUpdate[issue.Key] = issue;
                        }
                        else if (JiraIssueRecord.SelectSingle(connection, Key: issue.Key) is JiraIssueRecord dbExisting)
                        {
                            issue.Id = dbExisting.Id;
                            toUpdate[issue.Key] = issue;
                            RemoveExistingComments(connection, issue.Key);
                            RemoveRelatedIssues(connection, issue.Key);
                            RemoveExistingInPersons(connection, issue.Id);
                        }
                        else
                        {
                            if (issue.Id <= 0)
                            {
                                issue.Id = JiraIssueRecord.GetIndex();
                            }

                            toInsert[issue.Key] = issue;
                        }

                        foreach (JiraCommentRecord comment in parsed.Comments)
                        {
                            if (comment.Id <= 0)
                            {
                                comment.Id = JiraCommentRecord.GetIndex();
                            }
                            comment.IssueId = issue.Id;
                            // Resolve comment author
                            userMapper.ResolveUser(connection, comment.Author, null);
                        }

                        commentsToInsert[issue.Key] = parsed.Comments;

                        // Collect in-person requesters
                        foreach (JiraInPersonRef ipRef in parsed.InPersons)
                        {
                            int? userId = userMapper.ResolveUser(connection, ipRef.Username, ipRef.DisplayName);
                            if (userId is not null)
                            {
                                inPersonsToInsert.Add(new()
                                {
                                    Id = JiraIssueInPersonRecord.GetIndex(),
                                    IssueId = issue.Id,
                                    UserId = userId.Value,
                                });
                            }
                        }

                        if (issue.RelatedIssues is not null)
                        {
                            List<JiraIssueRelatedRecord> related = [];

                            string[] keys = issue.RelatedIssues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            foreach (string relatedKey in keys)
                            {
                                related.Add(new()
                                {
                                    Id = JiraIssueRelatedRecord.GetIndex(),
                                    IssueId = issue.Id,
                                    IssueKey = issue.Key,
                                    RelatedIssueKey = relatedKey
                                });
                            }

                            if (related.Count > 0)
                            {
                                relatedIssuesToInsert[issue.Key] = related;
                            }
                            else if (relatedIssuesToInsert.ContainsKey(issue.Key))
                            {
                                relatedIssuesToInsert.Remove(issue.Key);
                            }
                        }

                        itemsProcessed++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process cached file {Source}/{Key}", source, key);
                    itemsFailed++;
                    errors.Add($"{source}/{key}: {ex.Message}");
                }

                toUpdate.Values.Update(connection);
                toInsert.Values.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);

                foreach (List<JiraCommentRecord> comments in commentsToInsert.Values)
                {
                    comments.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                foreach (List<JiraIssueRelatedRecord> relateds in relatedIssuesToInsert.Values)
                {
                    relateds.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                if (inPersonsToInsert.Count > 0)
                {
                    inPersonsToInsert.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                itemsNew += toInsert.Count;
                itemsUpdated += toUpdate.Count;

            }
        }

        logger.LogInformation(
            "Cache ingestion complete: {Processed} processed, {New} new, {Updated} updated",
            itemsProcessed, itemsNew, itemsUpdated);

        return Task.FromResult(new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt));
    }

    private (ProcessOutcome Outcome, string? Error) ProcessIssue(
        JsonElement issueJson, Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        string key = string.Empty;
        try
        {
            JiraIssueRecord issue = JiraFieldMapper.MapIssue(issueJson);
            key = issue.Key;

            // Resolve users
            JsonElement fields = issueJson.GetProperty("fields");
            (string? assigneeUsername, string? assigneeDisplayName) = JiraFieldMapper.ExtractUserRef(fields, "assignee");
            (string? reporterUsername, string? reporterDisplayName) = JiraFieldMapper.ExtractUserRef(fields, "reporter");

            issue.AssigneeId = userMapper.ResolveUser(connection, assigneeUsername, assigneeDisplayName);
            issue.ReporterId = userMapper.ResolveUser(connection, reporterUsername, reporterDisplayName);
            issue.VoteMoverId = userMapper.ResolveByDisplayName(connection, issue.VoteMover);
            issue.VoteSeconderId = userMapper.ResolveByDisplayName(connection, issue.VoteSeconder);

            JiraIssueRecord? existing = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (existing is not null)
            {
                issue.Id = existing.Id;
                JiraIssueRecord.Update(connection, issue);
                RemoveExistingInPersons(connection, issue.Id);
            }
            else
            {
                JiraIssueRecord.Insert(connection, issue, ignoreDuplicates: true);
            }

            // Resolve comment authors
            List<JiraCommentRecord> comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
            foreach (JiraCommentRecord comment in comments)
            {
                // Extract username separately for proper user resolution
                (string? commentAuthorUsername, string? commentAuthorDisplayName) =
                    JiraFieldMapper.ExtractCommentAuthorRef(issueJson, comment);
                userMapper.ResolveUser(connection, commentAuthorUsername, commentAuthorDisplayName ?? comment.Author);

                JiraCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
            }

            // Resolve in-person requesters
            List<JiraInPersonRef> inPersonRefs = JiraFieldMapper.ExtractInPersonRequesters(fields);
            foreach (JiraInPersonRef ipRef in inPersonRefs)
            {
                int? userId = userMapper.ResolveUser(connection, ipRef.Username, ipRef.DisplayName);
                if (userId is not null)
                {
                    JiraIssueInPersonRecord inPersonRecord = new()
                    {
                        Id = JiraIssueInPersonRecord.GetIndex(),
                        IssueId = issue.Id,
                        UserId = userId.Value,
                    };
                    JiraIssueInPersonRecord.Insert(connection, inPersonRecord, ignoreDuplicates: true);
                }
            }

            List<JiraIssueLinkRecord> links = JiraFieldMapper.MapIssueLinks(issueJson, issue.Key);
            foreach (JiraIssueLinkRecord link in links)
            {
                JiraIssueLinkRecord.Insert(connection, link, ignoreDuplicates: true);
            }

            RemoveRelatedIssues(connection, issue.Key);

            if (issue.RelatedIssues is not null)
            {
                List<JiraIssueRelatedRecord> related = [];

                string[] keys = issue.RelatedIssues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (string relatedKey in keys)
                {
                    related.Add(new()
                    {
                        Id = JiraIssueRelatedRecord.GetIndex(),
                        IssueId = issue.Id,
                        IssueKey = issue.Key,
                        RelatedIssueKey = relatedKey
                    });
                }

                if (related.Count > 0)
                {
                    related.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }
            }

            return (existing is not null ? ProcessOutcome.Updated : ProcessOutcome.New, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process issue {Key}", key);
            return (ProcessOutcome.Failed, $"{key}: {ex.Message}");
        }
    }

    private static IEnumerable<JiraParsedIssue> ParseCachedFile(Stream stream, string key)
    {
        if (key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return JiraXmlParser.ParseExport(stream);
        }

        return ParseJsonCacheFile(stream);
    }

    private static IEnumerable<JiraParsedIssue> ParseJsonCacheFile(Stream stream)
    {
        using JsonDocument doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("issues", out JsonElement issues))
            yield break;

        foreach (JsonElement issueJson in issues.EnumerateArray())
        {
            JiraIssueRecord issue = JiraFieldMapper.MapIssue(issueJson);
            List<JiraCommentRecord> comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);

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

            yield return new JiraParsedIssue(issue, comments, userInfo, inPersons);
        }
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
            if (CacheFileNaming.TryParse(Path.GetFileName(key), out CacheFileNaming.ParsedBatchFile? parsed))
                dates.Add(parsed.Date);
        }

        return dates;
    }

    /// <summary>
    /// Merges cache entries from both XML and JSON sources, sorted by date for correct ingestion order.
    /// Files without a parseable date are appended at the end.
    /// </summary>
    private List<(string Source, string Key)> MergeAndSortCacheEntries(string? project = null)
    {
        List<(string Source, string Key, CacheFileNaming.ParsedBatchFile? Parsed)> entries = [];

        IEnumerable<string> allKeys = project is not null
            ? cache.EnumerateKeys(JiraCacheLayout.SourceName, JiraCacheLayout.ProjectSubPath(project))
            : cache.EnumerateKeys(JiraCacheLayout.SourceName);

        foreach (string key in allKeys)
        {
            // Classify by segments to handle both legacy and project-scoped keys:
            //   Legacy:  "xml/DayOf_2026-02-24-000.xml"  (2 segments)
            //   New:     "FHIR/xml/DayOf_2026-02-24-000.xml" (3 segments)
            string[] segments = key.Split('/');
            string sourceType;

            if (segments.Length == 3)
            {
                // Project-scoped: "{project}/{xml|json}/{filename}"
                sourceType = segments[1];
            }
            else if (segments.Length == 2)
            {
                // Legacy flat: "{xml|json}/{filename}"
                sourceType = segments[0];
            }
            else
            {
                continue; // skip metadata files, unexpected formats
            }

            if (sourceType is not (JiraCacheLayout.XmlPrefix or JiraCacheLayout.JsonPrefix))
            {
                continue; // skip non-data directories (e.g., jira-spec-artifacts)
            }

            CacheFileNaming.TryParse(Path.GetFileName(key), out CacheFileNaming.ParsedBatchFile? parsed);
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
                int dateCmp = a.Parsed.Date.CompareTo(b.Parsed.Date);
                if (dateCmp != 0)
                    return dateCmp;

                // Same date: WeekOf before DayOf
                int prefixCmp = a.Parsed.Prefix.CompareTo(b.Parsed.Prefix);
                if (prefixCmp != 0)
                    return prefixCmp;

                // Same prefix and date: sort by sequence number
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

    private enum ProcessOutcome { New, Updated, Failed }

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

    private static void RemoveExistingInPersons(SqliteConnection conn, int issueId)
    {
        using SqliteCommand deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM jira_issue_inpersons WHERE IssueId = @id";
        deleteCmd.Parameters.AddWithValue("@id", issueId);
        deleteCmd.ExecuteNonQuery();
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
