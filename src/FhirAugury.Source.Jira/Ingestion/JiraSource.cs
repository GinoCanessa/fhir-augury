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
            string cacheKey = CacheFileNaming.GenerateFileName(today, today, JiraCacheLayout.JsonExtension, existingKeys);
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

            Dictionary<string, JiraIssueRecord> toUpdate = [];
            Dictionary<string, JiraIssueRecord> toInsert = [];
            Dictionary<string, List<JiraCommentRecord>> commentsToInsert = [];
            Dictionary<string, List<JiraIssueRelatedRecord>> relatedIssuesToInsert = [];
            List<JiraIssueInPersonRecord> inPersonsToInsert = [];
            List<JiraIssueLinkRecord> linksToInsert = [];
            List<(JsonElement Json, JiraIssueRecord Issue)> parsedPage = [];
            List<string> pageKeys = [];

            try
            {
                foreach (JsonElement issueJson in issues.EnumerateArray())
                {
                    JiraIssueRecord issue = JiraFieldMapper.MapIssue(issueJson);

                    JsonElement fields = issueJson.GetProperty("fields");
                    (string? assigneeUsername, string? assigneeDisplayName) = JiraFieldMapper.ExtractUserRef(fields, "assignee");
                    (string? reporterUsername, string? reporterDisplayName) = JiraFieldMapper.ExtractUserRef(fields, "reporter");

                    // Ensure jira_users rows exist for every referenced user;
                    // the resolved Ids are no longer persisted on jira_issues.
                    userMapper.ResolveUser(connection, assigneeUsername, assigneeDisplayName);
                    userMapper.ResolveUser(connection, reporterUsername, reporterDisplayName);
                    userMapper.ResolveByDisplayName(connection, issue.VoteMover);
                    userMapper.ResolveByDisplayName(connection, issue.VoteSeconder);

                    pageKeys.Add(issue.Key);
                    parsedPage.Add((issueJson, issue));
                }

                Dictionary<string, JiraIssueRecord> existingByKey = [];
                if (pageKeys.Count > 0)
                {
                    // Chunk to stay well under SQLite's 999-parameter limit.
                    foreach (string[] chunk in pageKeys.Chunk(500).Select(c => c.ToArray()))
                    {
                        foreach (JiraIssueRecord row in JiraIssueRecord.SelectList(connection, KeyValues: chunk))
                        {
                            existingByKey[row.Key] = row;
                        }
                    }
                }

                foreach ((JsonElement issueJson, JiraIssueRecord issue) in parsedPage)
                {
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
                    else if (existingByKey.TryGetValue(issue.Key, out JiraIssueRecord? dbExisting))
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

                    List<JiraCommentRecord> comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
                    foreach (JiraCommentRecord comment in comments)
                    {
                        if (comment.Id <= 0)
                        {
                            comment.Id = JiraCommentRecord.GetIndex();
                        }
                        comment.IssueId = issue.Id;
                        (string? commentAuthorUsername, string? commentAuthorDisplayName) =
                            JiraFieldMapper.ExtractCommentAuthorRef(issueJson, comment);
                        userMapper.ResolveUser(connection, commentAuthorUsername, commentAuthorDisplayName ?? comment.Author);
                    }
                    commentsToInsert[issue.Key] = comments;

                    JsonElement issueFields = issueJson.GetProperty("fields");
                    foreach (JiraInPersonRef ipRef in JiraFieldMapper.ExtractInPersonRequesters(issueFields))
                    {
                        int? userId = userMapper.ResolveUser(connection, ipRef.Username, ipRef.DisplayName);
                        if (userId is not null)
                        {
                            inPersonsToInsert.Add(new JiraIssueInPersonRecord
                            {
                                Id = JiraIssueInPersonRecord.GetIndex(),
                                IssueId = issue.Id,
                                UserId = userId.Value,
                            });
                        }
                    }

                    foreach (JiraIssueLinkRecord link in JiraFieldMapper.MapIssueLinks(issueJson, issue.Key))
                    {
                        linksToInsert.Add(link);
                    }

                    if (issue.RelatedIssues is not null)
                    {
                        List<JiraIssueRelatedRecord> related = [];
                        string[] keys = issue.RelatedIssues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        foreach (string relatedKey in keys)
                        {
                            related.Add(new JiraIssueRelatedRecord
                            {
                                Id = JiraIssueRelatedRecord.GetIndex(),
                                IssueId = issue.Id,
                                IssueKey = issue.Key,
                                RelatedIssueKey = relatedKey,
                            });
                        }
                        if (related.Count > 0)
                        {
                            relatedIssuesToInsert[issue.Key] = related;
                        }
                    }

                    itemsProcessed++;

                    if (itemsProcessed % 1000 == 0)
                        logger.LogInformation("Download progress: {Count} issues processed", itemsProcessed);
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

                if (linksToInsert.Count > 0)
                {
                    linksToInsert.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                itemsNew += toInsert.Count;
                itemsUpdated += toUpdate.Count;
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

            // Parse and upsert
            try
            {
                using MemoryStream parseStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

                Dictionary<string, JiraIssueRecord> toUpdate = [];
                Dictionary<string, JiraIssueRecord> toInsert = [];

                Dictionary<string, List<JiraCommentRecord>> commentsToInsert = [];
                Dictionary<string, List<JiraIssueRelatedRecord>> relatedIssuesToInsert = [];
                List<JiraIssueInPersonRecord> inPersonsToInsert = [];

                int windowIssueCount = 0;
                foreach (JiraParsedIssue parsed in JiraXmlParser.ParseExport(parseStream))
                {
                    windowIssueCount++;
                    JiraIssueRecord issue = parsed.Issue;

                    // Resolve users to ensure jira_users rows exist.
                    userMapper.ResolveUser(connection, parsed.UserInfo.AssigneeUsername, parsed.UserInfo.AssigneeDisplayName);
                    userMapper.ResolveUser(connection, parsed.UserInfo.ReporterUsername, parsed.UserInfo.ReporterDisplayName);
                    userMapper.ResolveByDisplayName(connection, issue.VoteMover);
                    userMapper.ResolveByDisplayName(connection, issue.VoteSeconder);

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

                        // Resolve users to ensure jira_users rows exist.
                        userMapper.ResolveUser(connection, parsed.UserInfo.AssigneeUsername, parsed.UserInfo.AssigneeDisplayName);
                        userMapper.ResolveUser(connection, parsed.UserInfo.ReporterUsername, parsed.UserInfo.ReporterDisplayName);
                        userMapper.ResolveByDisplayName(connection, issue.VoteMover);
                        userMapper.ResolveByDisplayName(connection, issue.VoteSeconder);

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
                continue; // skip non-data directories (e.g., jira-spec-artifacts)
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
