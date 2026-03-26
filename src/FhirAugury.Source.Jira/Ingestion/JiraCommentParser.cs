using System.Text.Json;
using FhirAugury.Source.Jira.Database.Records;
using static FhirAugury.Common.DateTimeHelper;

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
        List<JiraCommentRecord> comments = new List<JiraCommentRecord>();

        if (item.Comments?.Items is null)
            return comments;

        foreach (JiraXmlParser.JiraXmlComment xmlComment in item.Comments.Items)
        {
            string body = xmlComment.Text ?? string.Empty;
            comments.Add(new JiraCommentRecord
            {
                Id = JiraCommentRecord.GetIndex(),
                IssueId = issueId,
                IssueKey = issueKey,
                Author = xmlComment.Author ?? "Unknown",
                CreatedAt = ParseDate(xmlComment.Created),
                Body = body,
                BodyPlain = FhirAugury.Common.Text.TextSanitizer.StripHtml(body),
            });
        }

        return comments;
    }
}
