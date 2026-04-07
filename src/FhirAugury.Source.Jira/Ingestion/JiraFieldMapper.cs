using System.Text.Json;
using System.Text.RegularExpressions;
using FhirAugury.Common;
using FhirAugury.Source.Jira.Database.Records;
using static FhirAugury.Common.DateTimeHelper;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Maps Jira REST API JSON responses to database records.</summary>
public static class JiraFieldMapper
{
    private static readonly Dictionary<string, string> CustomFieldMap = new()
    {
        ["customfield_11302"] = nameof(JiraIssueRecord.Specification),
        ["customfield_11400"] = nameof(JiraIssueRecord.WorkGroup),
        ["customfield_11808"] = nameof(JiraIssueRecord.RaisedInVersion),
        ["customfield_10618"] = nameof(JiraIssueRecord.ResolutionDescription),
        ["customfield_11300"] = nameof(JiraIssueRecord.RelatedArtifacts),
        ["customfield_10902"] = nameof(JiraIssueRecord.SelectedBallot),
        ["customfield_14905"] = nameof(JiraIssueRecord.RelatedIssues),
        ["customfield_14909"] = nameof(JiraIssueRecord.DuplicateOf),
        ["customfield_14807"] = nameof(JiraIssueRecord.AppliedVersions),
        ["customfield_14910"] = nameof(JiraIssueRecord.ChangeType),
        ["customfield_10001"] = nameof(JiraIssueRecord.Impact),
        ["customfield_10510"] = nameof(JiraIssueRecord.Vote),
        ["customfield_11402"] = nameof(JiraIssueRecord.Labels),
        ["customfield_10512"] = nameof(JiraIssueRecord.ChangeCategory),
        ["customfield_10511"] = nameof(JiraIssueRecord.ChangeImpact),
    };

    private static readonly Regex VotePattern = new(
        @"^\s*(.+?)\s*/\s*(.+?)\s*:\s*(\d+)\s*-\s*(\d+)\s*-\s*(\d+)\s*$",
        RegexOptions.Compiled);

    internal static string? CleanFieldValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string cleaned = System.Net.WebUtility.HtmlDecode(value).Trim();
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    internal static string CleanTitle(string title, string? key)
    {
        string cleaned = title;

        // Remove the specific [KEY] prefix if key is known
        if (!string.IsNullOrEmpty(key))
        {
            cleaned = cleaned.Replace($"[{key}]", "", StringComparison.OrdinalIgnoreCase);
        }

        // Also remove any generic [PROJECT-NNNNN] pattern at the start
        cleaned = Regex.Replace(cleaned, @"^\s*\[[A-Z]+-\d+\]\s*", "");

        return CleanFieldValue(cleaned) ?? cleaned.Trim();
    }

    internal static string? ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        string plain = FhirAugury.Common.Text.TextSanitizer.StripHtml(html);
        return string.IsNullOrEmpty(plain) ? null : plain;
    }

    internal static (string? Mover, string? Seconder, int? For, int? Against, int? Abstain) ParseVote(string? vote)
    {
        if (string.IsNullOrWhiteSpace(vote))
            return (null, null, null, null, null);

        Match match = VotePattern.Match(vote);
        if (!match.Success)
            return (null, null, null, null, null);

        return (
            match.Groups[1].Value.Trim(),
            match.Groups[2].Value.Trim(),
            int.Parse(match.Groups[3].Value),
            int.Parse(match.Groups[4].Value),
            int.Parse(match.Groups[5].Value)
        );
    }

    public static JiraIssueRecord MapIssue(JsonElement issueJson)
    {
        JsonElement fields = issueJson.GetProperty("fields");
        string key = issueJson.GetProperty("key").GetString()!;

        JiraIssueRecord record = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = JsonElementHelper.GetNestedString(fields, "project", "key") ?? key.Split('-')[0],
            Title = JsonElementHelper.GetString(fields, "summary") ?? string.Empty,
            Description = JsonElementHelper.GetString(fields, "description"),
            DescriptionPlain = null,
            Summary = JsonElementHelper.GetString(fields, "summary"),
            Type = JsonElementHelper.GetNestedString(fields, "issuetype", "name") ?? "Unknown",
            Priority = JsonElementHelper.GetNestedString(fields, "priority", "name") ?? "Unknown",
            Status = JsonElementHelper.GetNestedString(fields, "status", "name") ?? "Unknown",
            Resolution = JsonElementHelper.GetNestedString(fields, "resolution", "name"),
            Assignee = JsonElementHelper.GetNestedString(fields, "assignee", "displayName"),
            Reporter = JsonElementHelper.GetNestedString(fields, "reporter", "displayName"),
            CreatedAt = ParseDate(JsonElementHelper.GetString(fields, "created")),
            UpdatedAt = ParseDate(JsonElementHelper.GetString(fields, "updated")),
            ResolvedAt = ParseNullableDate(JsonElementHelper.GetString(fields, "resolutiondate")),
            Labels = GetLabels(fields),
            CommentCount = GetCommentCount(fields),
            Specification = null,
            WorkGroup = null,
            RaisedInVersion = null,
            ResolutionDescription = null,
            ResolutionDescriptionPlain = null,
            RelatedArtifacts = null,
            SelectedBallot = null,
            RelatedIssues = null,
            DuplicateOf = null,
            AppliedVersions = null,
            ChangeType = null,
            ChangeCategory = null,
            ChangeImpact = null,
            Impact = null,
            Vote = null,
            VoteMover = null,
            VoteSeconder = null,
            VoteForCount = null,
            VoteAgainstCount = null,
            VoteAbstainCount = null,
        };

        foreach ((string? fieldId, string? propertyName) in CustomFieldMap)
        {
            string? value = CleanFieldValue(ExtractCustomFieldValue(fields, fieldId));

            if (value is null)
            {
                continue;
            }

            switch (propertyName)
            {
                case nameof(JiraIssueRecord.Specification): record.Specification = value; break;
                case nameof(JiraIssueRecord.WorkGroup): record.WorkGroup = value; break;
                case nameof(JiraIssueRecord.RaisedInVersion): record.RaisedInVersion = value; break;
                case nameof(JiraIssueRecord.ResolutionDescription): record.ResolutionDescription = value; break;
                case nameof(JiraIssueRecord.RelatedArtifacts): record.RelatedArtifacts = value; break;
                case nameof(JiraIssueRecord.SelectedBallot): record.SelectedBallot = value; break;
                case nameof(JiraIssueRecord.RelatedIssues): record.RelatedIssues = value; break;
                case nameof(JiraIssueRecord.DuplicateOf): record.DuplicateOf = value; break;
                case nameof(JiraIssueRecord.AppliedVersions): record.AppliedVersions = value; break;
                case nameof(JiraIssueRecord.ChangeType): record.ChangeType = value; break;
                case nameof(JiraIssueRecord.Impact): record.Impact = value; break;
                case nameof(JiraIssueRecord.Vote): record.Vote = value; break;
                case nameof(JiraIssueRecord.Labels): record.Labels = value; break;
                case nameof(JiraIssueRecord.ChangeCategory): record.ChangeCategory = value; break;
                case nameof(JiraIssueRecord.ChangeImpact): record.ChangeImpact = value; break;
            }
        }

        // Clean standard field values
        record.Type = CleanFieldValue(record.Type) ?? "Unknown";
        record.Priority = CleanFieldValue(record.Priority) ?? "Unknown";
        record.Status = CleanFieldValue(record.Status) ?? "Unknown";
        record.Resolution = CleanFieldValue(record.Resolution);
        record.Assignee = CleanFieldValue(record.Assignee);
        record.Reporter = CleanFieldValue(record.Reporter);

        // Compute plain text from HTML fields
        record.DescriptionPlain = ToPlainText(record.Description);
        record.ResolutionDescriptionPlain = ToPlainText(record.ResolutionDescription);

        // Clean title: remove ticket identifier prefix
        record.Title = CleanTitle(record.Title, record.Key);

        // Parse vote components
        (record.VoteMover, record.VoteSeconder, record.VoteForCount,
         record.VoteAgainstCount, record.VoteAbstainCount) = ParseVote(record.Vote);

        return record;
    }

    public static List<JiraCommentRecord> MapComments(JsonElement issueJson, int issueId, string issueKey)
    {
        List<JiraCommentRecord> comments = new List<JiraCommentRecord>();
        JsonElement fields = issueJson.GetProperty("fields");

        if (!fields.TryGetProperty("comment", out JsonElement commentField))
            return comments;

        if (!commentField.TryGetProperty("comments", out JsonElement commentArray))
            return comments;

        foreach (JsonElement comment in commentArray.EnumerateArray())
        {
            string body = JsonElementHelper.GetString(comment, "body") ?? string.Empty;
            comments.Add(new JiraCommentRecord
            {
                Id = JiraCommentRecord.GetIndex(),
                IssueId = issueId,
                IssueKey = issueKey,
                Author = JsonElementHelper.GetNestedString(comment, "author", "displayName")
                         ?? JsonElementHelper.GetNestedString(comment, "author", "name")
                         ?? "Unknown",
                CreatedAt = ParseDate(JsonElementHelper.GetString(comment, "created")),
                Body = body,
                BodyPlain = FhirAugury.Common.Text.TextSanitizer.StripHtml(body),
            });
        }

        return comments;
    }

    /// <summary>Extracts issue links from a JSON issue element.</summary>
    public static List<JiraIssueLinkRecord> MapIssueLinks(JsonElement issueJson, string issueKey)
    {
        List<JiraIssueLinkRecord> links = new List<JiraIssueLinkRecord>();
        JsonElement fields = issueJson.GetProperty("fields");

        if (!fields.TryGetProperty("issuelinks", out JsonElement linkArray) || linkArray.ValueKind != JsonValueKind.Array)
            return links;

        foreach (JsonElement link in linkArray.EnumerateArray())
        {
            string linkType = JsonElementHelper.GetNestedString(link, "type", "name") ?? "relates to";

            if (link.TryGetProperty("outwardIssue", out JsonElement outward))
            {
                string? targetKey = JsonElementHelper.GetString(outward, "key");
                if (!string.IsNullOrEmpty(targetKey))
                {
                    links.Add(new JiraIssueLinkRecord
                    {
                        Id = JiraIssueLinkRecord.GetIndex(),
                        SourceKey = issueKey,
                        TargetKey = targetKey,
                        LinkType = linkType,
                    });
                }
            }

            if (link.TryGetProperty("inwardIssue", out JsonElement inward))
            {
                string? sourceKey = JsonElementHelper.GetString(inward, "key");
                if (!string.IsNullOrEmpty(sourceKey))
                {
                    links.Add(new JiraIssueLinkRecord
                    {
                        Id = JiraIssueLinkRecord.GetIndex(),
                        SourceKey = sourceKey,
                        TargetKey = issueKey,
                        LinkType = linkType,
                    });
                }
            }
        }

        return links;
    }

    private static string? ExtractCustomFieldValue(JsonElement fields, string fieldId)
    {
        if (!fields.TryGetProperty(fieldId, out JsonElement prop) || prop.ValueKind == JsonValueKind.Null)
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.Object => GetNestedStringFromObject(prop),
            JsonValueKind.Array => ExtractArrayValues(prop),
            _ => null,
        };
    }

    private static string? GetNestedStringFromObject(JsonElement obj)
    {
        if (obj.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (obj.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
            return name.GetString();

        if (obj.TryGetProperty("displayName", out JsonElement displayName) && displayName.ValueKind == JsonValueKind.String)
            return displayName.GetString();

        if (obj.TryGetProperty("label", out JsonElement label) && label.ValueKind == JsonValueKind.String)
            return label.GetString();

        return obj.ToString();
    }

    private static string? ExtractArrayValues(JsonElement array)
    {
        List<string> values = new List<string>();

        foreach (JsonElement element in array.EnumerateArray())
        {
            string? val = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object => GetNestedStringFromObject(element),
                _ => element.ToString(),
            };

            if (!string.IsNullOrEmpty(val))
                values.Add(val);
        }

        return values.Count > 0 ? string.Join(", ", values) : null;
    }

    private static string? GetLabels(JsonElement fields)
    {
        if (!fields.TryGetProperty("labels", out JsonElement labelsArray) || labelsArray.ValueKind != JsonValueKind.Array)
            return null;

        List<string> labels = new List<string>();
        foreach (JsonElement label in labelsArray.EnumerateArray())
        {
            string? val = CleanFieldValue(label.GetString());
            if (!string.IsNullOrEmpty(val))
                labels.Add(val);
        }

        return labels.Count > 0 ? string.Join(",", labels) : null;
    }

    private static int GetCommentCount(JsonElement fields)
    {
        if (!fields.TryGetProperty("comment", out JsonElement commentField))
            return 0;

        if (commentField.TryGetProperty("total", out JsonElement total) && total.ValueKind == JsonValueKind.Number)
            return total.GetInt32();

        if (commentField.TryGetProperty("comments", out JsonElement comments) && comments.ValueKind == JsonValueKind.Array)
            return comments.GetArrayLength();

        return 0;
    }
}
