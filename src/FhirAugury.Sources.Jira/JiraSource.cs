using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Sources.Jira;

/// <summary>Jira data source implementing IDataSource for HL7 FHIR Jira issues.</summary>
public class JiraSource(JiraSourceOptions options, HttpClient httpClient, ILogger<JiraSource>? logger = null) : IDataSource
{
    public string SourceName => "jira";

    public async Task<IngestionResult> DownloadAllAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var jql = ingestionOptions.Filter ?? options.DefaultJql;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"{options.BaseUrl}/rest/api/2/search?jql={Uri.EscapeDataString(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger?.LogInformation("Fetching issues: startAt={StartAt}", startAt);

            JsonDocument doc;
            try
            {
                var response = await httpClient.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                doc = JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to fetch issues at startAt={StartAt}", startAt);
                errors.Add(new IngestionError($"page:{startAt}", $"HTTP request failed: {ex.Message}", ex));
                break;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var total = root.GetProperty("total").GetInt32();
                var issues = root.GetProperty("issues");

                using var connection = db.OpenConnection();

                foreach (var issueJson in issues.EnumerateArray())
                {
                    var result = ProcessIssue(issueJson, connection, ingestionOptions.Verbose);
                    itemsProcessed++;

                    switch (result.Outcome)
                    {
                        case ProcessOutcome.New:
                            itemsNew++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Updated:
                            itemsUpdated++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Failed:
                            itemsFailed++;
                            errors.Add(result.Error!);
                            break;
                    }
                }

                startAt += issues.GetArrayLength();
                hasMore = startAt < total;
            }
        }

        return new IngestionResult
        {
            ItemsProcessed = itemsProcessed,
            ItemsNew = itemsNew,
            ItemsUpdated = itemsUpdated,
            ItemsFailed = itemsFailed,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            NewAndUpdatedItems = newAndUpdated,
        };
    }

    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var baseJql = ingestionOptions.Filter ?? options.DefaultJql;
        var sinceStr = since.ToString("yyyy-MM-dd HH:mm");
        var jql = $"{baseJql} AND updated >= '{sinceStr}' ORDER BY updated ASC";
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"{options.BaseUrl}/rest/api/2/search?jql={Uri.EscapeDataString(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger?.LogInformation("Fetching incremental issues: startAt={StartAt}, since={Since}", startAt, sinceStr);

            JsonDocument doc;
            try
            {
                var response = await httpClient.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                doc = JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to fetch incremental issues at startAt={StartAt}", startAt);
                errors.Add(new IngestionError($"page:{startAt}", $"HTTP request failed: {ex.Message}", ex));
                break;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var total = root.GetProperty("total").GetInt32();
                var issues = root.GetProperty("issues");

                using var connection = db.OpenConnection();

                foreach (var issueJson in issues.EnumerateArray())
                {
                    var result = ProcessIssue(issueJson, connection, ingestionOptions.Verbose);
                    itemsProcessed++;

                    switch (result.Outcome)
                    {
                        case ProcessOutcome.New:
                            itemsNew++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Updated:
                            itemsUpdated++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Failed:
                            itemsFailed++;
                            errors.Add(result.Error!);
                            break;
                    }
                }

                startAt += issues.GetArrayLength();
                hasMore = startAt < total;
            }
        }

        return new IngestionResult
        {
            ItemsProcessed = itemsProcessed,
            ItemsNew = itemsNew,
            ItemsUpdated = itemsUpdated,
            ItemsFailed = itemsFailed,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            NewAndUpdatedItems = newAndUpdated,
        };
    }

    public async Task<IngestionResult> IngestItemAsync(string identifier, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var url = $"{options.BaseUrl}/rest/api/2/issue/{Uri.EscapeDataString(identifier)}?fields=*all&expand=renderedFields";

        logger?.LogInformation("Fetching single issue: {Identifier}", identifier);

        try
        {
            var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            using var connection = db.OpenConnection();

            var result = ProcessIssue(doc.RootElement, connection, ingestionOptions.Verbose);

            switch (result.Outcome)
            {
                case ProcessOutcome.New:
                    itemsNew++;
                    newAndUpdated.Add(result.Item!);
                    break;
                case ProcessOutcome.Updated:
                    itemsUpdated++;
                    newAndUpdated.Add(result.Item!);
                    break;
                case ProcessOutcome.Failed:
                    itemsFailed++;
                    errors.Add(result.Error!);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to fetch issue {Identifier}", identifier);
            itemsFailed++;
            errors.Add(new IngestionError(identifier, $"Failed to fetch issue: {ex.Message}", ex));
        }

        return new IngestionResult
        {
            ItemsProcessed = itemsNew + itemsUpdated + itemsFailed,
            ItemsNew = itemsNew,
            ItemsUpdated = itemsUpdated,
            ItemsFailed = itemsFailed,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            NewAndUpdatedItems = newAndUpdated,
        };
    }

    private ProcessResult ProcessIssue(JsonElement issueJson, Microsoft.Data.Sqlite.SqliteConnection connection, bool verbose)
    {
        string key = string.Empty;
        try
        {
            var issue = JiraFieldMapper.MapIssue(issueJson);
            key = issue.Key;

            // Check if the issue already exists
            var existing = JiraIssueRecord.SelectSingle(connection, Key: key);
            bool isNew;

            if (existing is not null)
            {
                // Preserve the existing ID and update
                issue.Id = existing.Id;
                JiraIssueRecord.Update(connection, issue);
                isNew = false;
            }
            else
            {
                JiraIssueRecord.Insert(connection, issue, ignoreDuplicates: true);
                isNew = true;
            }

            // Process comments
            var comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
            foreach (var comment in comments)
            {
                JiraCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
            }

            if (verbose)
            {
                logger?.LogDebug("{Action} issue {Key}: {Title}", isNew ? "Inserted" : "Updated", key, issue.Title);
            }

            var searchableFields = BuildSearchableFields(issue, comments);

            var item = new IngestedItem
            {
                SourceType = SourceName,
                SourceId = issue.Key,
                Title = issue.Title,
                SearchableTextFields = searchableFields,
            };

            return new ProcessResult(isNew ? ProcessOutcome.New : ProcessOutcome.Updated, item, null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to process issue {Key}", key);
            return new ProcessResult(ProcessOutcome.Failed, null, new IngestionError(key, ex.Message, ex));
        }
    }

    private static List<string> BuildSearchableFields(JiraIssueRecord issue, List<JiraCommentRecord> comments)
    {
        var fields = new List<string>();

        AddIfNotEmpty(fields, issue.Key);
        AddIfNotEmpty(fields, issue.Title);
        AddIfNotEmpty(fields, issue.Description);
        AddIfNotEmpty(fields, issue.Summary);
        AddIfNotEmpty(fields, issue.Status);
        AddIfNotEmpty(fields, issue.Resolution);
        AddIfNotEmpty(fields, issue.ResolutionDescription);
        AddIfNotEmpty(fields, issue.WorkGroup);
        AddIfNotEmpty(fields, issue.Specification);
        AddIfNotEmpty(fields, issue.Labels);

        foreach (var comment in comments)
        {
            AddIfNotEmpty(fields, comment.Body);
        }

        return fields;
    }

    private static void AddIfNotEmpty(List<string> fields, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add(value);
    }

    private enum ProcessOutcome { New, Updated, Failed }

    private record ProcessResult(ProcessOutcome Outcome, IngestedItem? Item, IngestionError? Error);
}
