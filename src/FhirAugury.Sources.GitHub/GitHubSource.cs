using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Sources.GitHub;

/// <summary>GitHub data source implementing IDataSource for issues and pull requests.</summary>
public class GitHubSource(GitHubSourceOptions options, HttpClient httpClient, ILogger<GitHubSource>? logger = null) : IDataSource
{
    private const string GitHubApiBase = "https://api.github.com";

    public string SourceName => "github";

    public async Task<IngestionResult> DownloadAllAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var repos = ingestionOptions.Filter is not null
            ? [ingestionOptions.Filter]
            : options.Repositories;

        using var connection = db.OpenConnection();

        foreach (var repoFullName in repos)
        {
            if (ct.IsCancellationRequested) break;

            logger?.LogInformation("Fetching repository: {Repo}", repoFullName);

            // Fetch and upsert repo metadata
            try
            {
                var repoUrl = $"{GitHubApiBase}/repos/{repoFullName}";
                var repoResponse = await httpClient.GetAsync(repoUrl, ct);
                repoResponse.EnsureSuccessStatusCode();
                var repoJson = await repoResponse.Content.ReadAsStringAsync(ct);
                using var repoDoc = JsonDocument.Parse(repoJson);
                UpsertRepo(connection, repoDoc.RootElement);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to fetch repo metadata for {Repo}", repoFullName);
            }

            // Paginate all issues (includes PRs)
            int page = 1;
            bool hasMore = true;

            while (hasMore && !ct.IsCancellationRequested)
            {
                var url = $"{GitHubApiBase}/repos/{repoFullName}/issues?state=all&per_page={options.PageSize}&page={page}&sort=updated&direction=asc";

                logger?.LogInformation("Fetching issues: repo={Repo}, page={Page}", repoFullName, page);

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
                    logger?.LogError(ex, "Failed to fetch issues for {Repo} at page={Page}", repoFullName, page);
                    errors.Add(new IngestionError($"repo:{repoFullName}:page:{page}", $"HTTP request failed: {ex.Message}", ex));
                    break;
                }

                using (doc)
                {
                    var issues = doc.RootElement;
                    if (issues.GetArrayLength() == 0)
                    {
                        hasMore = false;
                        break;
                    }

                    foreach (var issueJson in issues.EnumerateArray())
                    {
                        var result = ProcessIssue(issueJson, repoFullName, connection, ingestionOptions.Verbose);
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

                        // Fetch comments for this issue
                        if (result.Outcome != ProcessOutcome.Failed)
                        {
                            await FetchIssueCommentsAsync(
                                connection, repoFullName,
                                issueJson.GetProperty("number").GetInt32(),
                                result.DbId, ct, errors);
                        }
                    }

                    hasMore = issues.GetArrayLength() >= options.PageSize;
                    page++;
                }
            }
        }

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var repos = ingestionOptions.Filter is not null
            ? [ingestionOptions.Filter]
            : options.Repositories;

        using var connection = db.OpenConnection();
        var sinceStr = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        foreach (var repoFullName in repos)
        {
            if (ct.IsCancellationRequested) break;

            int page = 1;
            bool hasMore = true;

            while (hasMore && !ct.IsCancellationRequested)
            {
                var url = $"{GitHubApiBase}/repos/{repoFullName}/issues?state=all&since={sinceStr}&per_page={options.PageSize}&page={page}&sort=updated&direction=asc";

                logger?.LogInformation("Fetching incremental issues: repo={Repo}, page={Page}, since={Since}", repoFullName, page, sinceStr);

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
                    logger?.LogError(ex, "Failed to fetch incremental issues for {Repo}", repoFullName);
                    errors.Add(new IngestionError($"repo:{repoFullName}:page:{page}", ex.Message, ex));
                    break;
                }

                using (doc)
                {
                    var issues = doc.RootElement;
                    if (issues.GetArrayLength() == 0)
                    {
                        hasMore = false;
                        break;
                    }

                    foreach (var issueJson in issues.EnumerateArray())
                    {
                        var result = ProcessIssue(issueJson, repoFullName, connection, ingestionOptions.Verbose);
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

                        if (result.Outcome != ProcessOutcome.Failed)
                        {
                            await FetchIssueCommentsAsync(
                                connection, repoFullName,
                                issueJson.GetProperty("number").GetInt32(),
                                result.DbId, ct, errors);
                        }
                    }

                    hasMore = issues.GetArrayLength() >= options.PageSize;
                    page++;
                }
            }
        }

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    public async Task<IngestionResult> IngestItemAsync(string identifier, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        // Parse "owner/repo#number"
        var hashIdx = identifier.IndexOf('#');
        if (hashIdx < 0)
        {
            errors.Add(new IngestionError(identifier, "Identifier must be in 'owner/repo#number' format"));
            return BuildResult(startedAt, 0, 0, 0, 1, errors, newAndUpdated);
        }

        var repoFullName = identifier[..hashIdx];
        var numberStr = identifier[(hashIdx + 1)..];
        if (!int.TryParse(numberStr, out var number))
        {
            errors.Add(new IngestionError(identifier, $"Invalid issue number: {numberStr}"));
            return BuildResult(startedAt, 0, 0, 0, 1, errors, newAndUpdated);
        }

        try
        {
            var url = $"{GitHubApiBase}/repos/{repoFullName}/issues/{number}";
            var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            using var connection = db.OpenConnection();

            var result = ProcessIssue(doc.RootElement, repoFullName, connection, ingestionOptions.Verbose);

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

            if (result.Outcome != ProcessOutcome.Failed)
            {
                await FetchIssueCommentsAsync(connection, repoFullName, number, result.DbId, ct, errors);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to fetch issue {Identifier}", identifier);
            itemsFailed++;
            errors.Add(new IngestionError(identifier, $"Failed to fetch issue: {ex.Message}", ex));
        }

        return BuildResult(startedAt, itemsNew + itemsUpdated + itemsFailed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    private ProcessResult ProcessIssue(JsonElement issueJson, string repoFullName, Microsoft.Data.Sqlite.SqliteConnection connection, bool verbose)
    {
        string uniqueKey = string.Empty;
        int dbId = 0;
        try
        {
            var record = GitHubIssueMapper.MapIssue(issueJson, repoFullName);
            uniqueKey = record.UniqueKey;

            var existing = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey);
            bool isNew;

            if (existing is not null)
            {
                record.Id = existing.Id;
                GitHubIssueRecord.Update(connection, record);
                isNew = false;
            }
            else
            {
                GitHubIssueRecord.Insert(connection, record, ignoreDuplicates: true);
                isNew = true;
            }

            dbId = record.Id;

            if (verbose)
            {
                logger?.LogDebug("{Action} issue {Key}: {Title}", isNew ? "Inserted" : "Updated", uniqueKey, record.Title);
            }

            var searchableFields = new List<string>();
            AddIfNotEmpty(searchableFields, record.Title);
            AddIfNotEmpty(searchableFields, record.Body);
            AddIfNotEmpty(searchableFields, record.Labels);

            var item = new IngestedItem
            {
                SourceType = SourceName,
                SourceId = uniqueKey,
                Title = record.Title,
                SearchableTextFields = searchableFields,
            };

            return new ProcessResult(isNew ? ProcessOutcome.New : ProcessOutcome.Updated, item, null, dbId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to process issue {Key}", uniqueKey);
            return new ProcessResult(ProcessOutcome.Failed, null, new IngestionError(uniqueKey, ex.Message, ex), 0);
        }
    }

    private static void UpsertRepo(Microsoft.Data.Sqlite.SqliteConnection connection, JsonElement repoJson)
    {
        var record = GitHubIssueMapper.MapRepo(repoJson);
        var existing = GitHubRepoRecord.SelectSingle(connection, FullName: record.FullName);

        if (existing is not null)
        {
            record.Id = existing.Id;
            GitHubRepoRecord.Update(connection, record);
        }
        else
        {
            GitHubRepoRecord.Insert(connection, record, ignoreDuplicates: true);
        }
    }

    private async Task FetchIssueCommentsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string repoFullName, int issueNumber, int issueDbId,
        CancellationToken ct, List<IngestionError> errors)
    {
        try
        {
            var url = $"{GitHubApiBase}/repos/{repoFullName}/issues/{issueNumber}/comments?per_page={options.PageSize}";
            var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            foreach (var commentJson in doc.RootElement.EnumerateArray())
            {
                var comment = GitHubIssueMapper.MapComment(commentJson, issueDbId, repoFullName, issueNumber);
                GitHubCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to fetch comments for {Repo}#{Number}", repoFullName, issueNumber);
            errors.Add(new IngestionError($"comments:{repoFullName}#{issueNumber}", ex.Message, ex));
        }
    }

    private static void AddIfNotEmpty(List<string> fields, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add(value);
    }

    private static IngestionResult BuildResult(
        DateTimeOffset startedAt, int processed, int newCount, int updated, int failed,
        List<IngestionError> errors, List<IngestedItem> newAndUpdated) => new()
    {
        ItemsProcessed = processed,
        ItemsNew = newCount,
        ItemsUpdated = updated,
        ItemsFailed = failed,
        Errors = errors,
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
        NewAndUpdatedItems = newAndUpdated,
    };

    private enum ProcessOutcome { New, Updated, Failed }

    private record ProcessResult(ProcessOutcome Outcome, IngestedItem? Item, IngestionError? Error, int DbId);
}
