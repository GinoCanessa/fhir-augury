using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Resolves the deterministic per-repo default HL7 work-group attribution
/// used as the final fallback by <see cref="WorkGroupResolutionPass"/>.
/// </summary>
/// <remarks>
/// Order:
/// <list type="number">
///   <item>Explicit override from <c>GitHubServiceOptions.RepoOverrides[repo].WorkGroup</c>
///         resolved through <see cref="WorkGroupResolver"/>; <c>Source = "config"</c>.</item>
///   <item>Majority canonical <c>WorkGroupCode</c> across <see cref="JiraWorkgroupRecord"/>
///         rows for the repo joined to <see cref="JiraSpecRecord.DefaultWorkgroup"/>
///         (key-based, not free-text). Tie-break by code ascending.
///         <c>Source = "majority-jira-spec"</c>.</item>
/// </list>
/// When no signal at all is available, returns a result with both code and
/// raw <c>null</c> and <c>Source = "majority-jira-spec"</c> (so callers can
/// still upsert a row recording "we tried and there was no signal").
/// </remarks>
public sealed class RepoDefaultWorkGroupResolver
{
    public const string SourceConfig = "config";
    public const string SourceMajorityJiraSpec = "majority-jira-spec";

    private readonly GitHubServiceOptions _options;
    private readonly WorkGroupResolver _resolver;
    private readonly ILogger<RepoDefaultWorkGroupResolver> _logger;

    public RepoDefaultWorkGroupResolver(
        IOptions<GitHubServiceOptions> options,
        WorkGroupResolver resolver,
        ILogger<RepoDefaultWorkGroupResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _resolver = resolver;
        _logger = logger;
    }

    public RepoDefaultResult Resolve(SqliteConnection connection, string repoFullName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoFullName);

        if (_options.RepoOverrides.TryGetValue(repoFullName, out RepoOverrideOptions? overrideOpt) &&
            !string.IsNullOrWhiteSpace(overrideOpt?.WorkGroup))
        {
            string raw = overrideOpt.WorkGroup.Trim();
            string? code = _resolver.Resolve(raw);
            if (code is null)
            {
                _logger.LogWarning(
                    "Repo override WG for {Repo} did not resolve: {Raw}",
                    repoFullName, raw);
                return new RepoDefaultResult(null, raw, SourceConfig);
            }

            string? rawForRow = string.Equals(code, raw, StringComparison.OrdinalIgnoreCase) ? null : raw;
            return new RepoDefaultResult(code, rawForRow, SourceConfig);
        }

        return ResolveMajorityFromJiraSpec(connection, repoFullName);
    }

    private RepoDefaultResult ResolveMajorityFromJiraSpec(SqliteConnection connection, string repoFullName)
    {
        // Join jira_specs.DefaultWorkgroup → jira_workgroups.WorkgroupKey (key-based)
        // and tally canonical codes. Ties broken by code ascending.
        Dictionary<string, int> codeCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> rawCounts = new(StringComparer.OrdinalIgnoreCase);

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT jw.WorkGroupCode, jw.Name
            FROM jira_specs s
            INNER JOIN jira_workgroups jw
                ON jw.RepoFullName = s.RepoFullName AND jw.WorkgroupKey = s.DefaultWorkgroup
            WHERE s.RepoFullName = @repo AND s.DefaultWorkgroup IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@repo", repoFullName);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? code = r.IsDBNull(0) ? null : r.GetString(0);
            string? name = r.IsDBNull(1) ? null : r.GetString(1);
            if (!string.IsNullOrEmpty(code))
            {
                codeCounts[code] = codeCounts.GetValueOrDefault(code) + 1;
            }
            else if (!string.IsNullOrEmpty(name))
            {
                rawCounts[name] = rawCounts.GetValueOrDefault(name) + 1;
            }
        }

        if (codeCounts.Count > 0)
        {
            string winner = codeCounts
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .First()
                .Key;
            return new RepoDefaultResult(winner, null, SourceMajorityJiraSpec);
        }

        if (rawCounts.Count > 0)
        {
            string winnerRaw = rawCounts
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .First()
                .Key;
            return new RepoDefaultResult(null, winnerRaw, SourceMajorityJiraSpec);
        }

        return new RepoDefaultResult(null, null, SourceMajorityJiraSpec);
    }
}

public readonly record struct RepoDefaultResult(string? Code, string? Raw, string Source);
