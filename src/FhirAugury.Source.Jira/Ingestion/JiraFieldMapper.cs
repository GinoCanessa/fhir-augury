using System.Text.Json;
using FhirAugury.Source.Jira.Database.Records;

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
    };

    public static JiraIssueRecord MapIssue(JsonElement issueJson)
    {
        var fields = issueJson.GetProperty("fields");
        var key = issueJson.GetProperty("key").GetString()!;

        var record = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = GetNestedString(fields, "project", "key") ?? key.Split('-')[0],
            Title = GetString(fields, "summary") ?? string.Empty,
            Description = GetString(fields, "description"),
            Summary = GetString(fields, "summary"),
            Type = GetNestedString(fields, "issuetype", "name") ?? "Unknown",
            Priority = GetNestedString(fields, "priority", "name") ?? "Unknown",
            Status = GetNestedString(fields, "status", "name") ?? "Unknown",
            Resolution = GetNestedString(fields, "resolution", "name"),
            Assignee = GetNestedString(fields, "assignee", "displayName"),
            Reporter = GetNestedString(fields, "reporter", "displayName"),
            CreatedAt = ParseDate(GetString(fields, "created")),
            UpdatedAt = ParseDate(GetString(fields, "updated")),
            ResolvedAt = ParseNullableDate(GetString(fields, "resolutiondate")),
            Labels = GetLabels(fields),
            CommentCount = GetCommentCount(fields),
            Specification = null,
            WorkGroup = null,
            RaisedInVersion = null,
            ResolutionDescription = null,
            RelatedArtifacts = null,
            SelectedBallot = null,
            RelatedIssues = null,
            DuplicateOf = null,
            AppliedVersions = null,
            ChangeType = null,
            Impact = null,
            Vote = null,
        };

        foreach (var (fieldId, propertyName) in CustomFieldMap)
        {
            var value = ExtractCustomFieldValue(fields, fieldId);
            if (value is null)
                continue;

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
            }
        }

        return record;
    }

    public static List<JiraCommentRecord> MapComments(JsonElement issueJson, int issueId, string issueKey)
    {
        var comments = new List<JiraCommentRecord>();
        var fields = issueJson.GetProperty("fields");

        if (!fields.TryGetProperty("comment", out var commentField))
            return comments;

        if (!commentField.TryGetProperty("comments", out var commentArray))
            return comments;

        foreach (var comment in commentArray.EnumerateArray())
        {
            comments.Add(new JiraCommentRecord
            {
                Id = JiraCommentRecord.GetIndex(),
                IssueId = issueId,
                IssueKey = issueKey,
                Author = GetNestedString(comment, "author", "displayName")
                         ?? GetNestedString(comment, "author", "name")
                         ?? "Unknown",
                CreatedAt = ParseDate(GetString(comment, "created")),
                Body = GetString(comment, "body") ?? string.Empty,
            });
        }

        return comments;
    }

    /// <summary>Extracts issue links from a JSON issue element.</summary>
    public static List<JiraIssueLinkRecord> MapIssueLinks(JsonElement issueJson, string issueKey)
    {
        var links = new List<JiraIssueLinkRecord>();
        var fields = issueJson.GetProperty("fields");

        if (!fields.TryGetProperty("issuelinks", out var linkArray) || linkArray.ValueKind != JsonValueKind.Array)
            return links;

        foreach (var link in linkArray.EnumerateArray())
        {
            var linkType = GetNestedString(link, "type", "name") ?? "relates to";

            if (link.TryGetProperty("outwardIssue", out var outward))
            {
                var targetKey = GetString(outward, "key");
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

            if (link.TryGetProperty("inwardIssue", out var inward))
            {
                var sourceKey = GetString(inward, "key");
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

    internal static string? GetString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.Null ? null : prop.ToString();
    }

    internal static string? GetNestedString(JsonElement parent, string propertyName, string childPropertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;

        if (!prop.TryGetProperty(childPropertyName, out var child) || child.ValueKind == JsonValueKind.Null)
            return null;

        return child.ToString();
    }

    private static string? ExtractCustomFieldValue(JsonElement fields, string fieldId)
    {
        if (!fields.TryGetProperty(fieldId, out var prop) || prop.ValueKind == JsonValueKind.Null)
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
        if (obj.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (obj.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            return name.GetString();

        if (obj.TryGetProperty("displayName", out var displayName) && displayName.ValueKind == JsonValueKind.String)
            return displayName.GetString();

        return obj.ToString();
    }

    private static string? ExtractArrayValues(JsonElement array)
    {
        var values = new List<string>();

        foreach (var element in array.EnumerateArray())
        {
            var val = element.ValueKind switch
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
        if (!fields.TryGetProperty("labels", out var labelsArray) || labelsArray.ValueKind != JsonValueKind.Array)
            return null;

        var labels = new List<string>();
        foreach (var label in labelsArray.EnumerateArray())
        {
            var val = label.GetString();
            if (!string.IsNullOrEmpty(val))
                labels.Add(val);
        }

        return labels.Count > 0 ? string.Join(",", labels) : null;
    }

    private static int GetCommentCount(JsonElement fields)
    {
        if (!fields.TryGetProperty("comment", out var commentField))
            return 0;

        if (commentField.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number)
            return total.GetInt32();

        if (commentField.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Array)
            return comments.GetArrayLength();

        return 0;
    }

    internal static DateTimeOffset ParseDate(string? value) =>
        string.IsNullOrEmpty(value) ? DateTimeOffset.MinValue : DateTimeOffset.TryParse(value, out var dt) ? dt : DateTimeOffset.MinValue;

    internal static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateTimeOffset.TryParse(value, out var dt) ? dt : null;
}
