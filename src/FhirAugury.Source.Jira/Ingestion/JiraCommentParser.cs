using System.Text.Json;
using FhirAugury.Source.Jira.Database.Records;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Extracts comment records from both JSON and XML Jira data.</summary>
public static class JiraCommentParser
{
    /// <summary>Extracts comments from a Jira REST API JSON issue response.</summary>
    public static List<JiraCommentRecord> ParseJsonComments(JsonElement issueJson, int issueId, string issueKey) =>
        JiraFieldMapper.MapComments(issueJson, issueId, issueKey);

    /// <summary>Extracts comments from a Jira XML export item.</summary>
    public static List<JiraCommentRecord> ParseXmlComments(JiraXmlParser.JiraItem item, int issueId, string issueKey)
    {
        var comments = new List<JiraCommentRecord>();

        if (item.Comments?.Items is null)
            return comments;

        foreach (var xmlComment in item.Comments.Items)
        {
            comments.Add(new JiraCommentRecord
            {
                Id = JiraCommentRecord.GetIndex(),
                IssueId = issueId,
                IssueKey = issueKey,
                Author = xmlComment.Author ?? "Unknown",
                CreatedAt = ParseDate(xmlComment.Created),
                Body = xmlComment.Text ?? string.Empty,
            });
        }

        return comments;
    }

    private static DateTimeOffset ParseDate(string? value) =>
        string.IsNullOrEmpty(value) ? DateTimeOffset.MinValue : DateTimeOffset.TryParse(value, out var dt) ? dt : DateTimeOffset.MinValue;
}
