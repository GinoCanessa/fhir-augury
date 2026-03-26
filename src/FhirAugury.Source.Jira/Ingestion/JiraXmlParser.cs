using System.Xml;
using System.Xml.Serialization;
using FhirAugury.Source.Jira.Database.Records;
using static FhirAugury.Common.DateTimeHelper;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Parses Jira XML RSS export format into database records.</summary>
public static class JiraXmlParser
{
    private static readonly XmlSerializer Serializer = new(typeof(JiraRss));

    private static readonly Dictionary<string, string> CustomFieldKeyMap = new()
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

    public static IEnumerable<(JiraIssueRecord Issue, List<JiraCommentRecord> Comments)> ParseExport(Stream stream)
    {
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
        using XmlReader reader = XmlReader.Create(stream, settings);
        JiraRss rss = (JiraRss?)Serializer.Deserialize(reader)
                  ?? throw new InvalidOperationException("Failed to deserialize Jira XML export.");

        if (rss.Channel?.Items is null)
            yield break;

        foreach (JiraItem item in rss.Channel.Items)
        {
            string key = item.Key?.Text ?? string.Empty;
            int issueId = JiraIssueRecord.GetIndex();

            JiraIssueRecord record = new JiraIssueRecord
            {
                Id = issueId,
                Key = key,
                ProjectKey = item.Project?.Key ?? key.Split('-')[0],
                Title = item.Title ?? string.Empty,
                Description = item.Description,
                DescriptionPlain = null,
                Summary = item.Summary,
                Type = item.Type?.Text ?? "Unknown",
                Priority = item.Priority?.Text ?? "Unknown",
                Status = item.Status?.Text ?? "Unknown",
                Resolution = item.Resolution?.Text,
                ResolutionDescription = null,
                ResolutionDescriptionPlain = null,
                Assignee = item.Assignee?.Text ?? item.Assignee?.Username,
                Reporter = item.Reporter?.Text ?? item.Reporter?.Username,
                CreatedAt = ParseDate(item.Created),
                UpdatedAt = ParseDate(item.Updated),
                ResolvedAt = ParseNullableDate(item.Resolved),
                WorkGroup = null,
                Specification = null,
                RaisedInVersion = null,
                SelectedBallot = null,
                RelatedArtifacts = null,
                RelatedIssues = null,
                DuplicateOf = null,
                AppliedVersions = null,
                ChangeType = null,
                Impact = null,
                Vote = null,
                VoteMover = null,
                VoteSeconder = null,
                VoteForCount = null,
                VoteAgainstCount = null,
                VoteAbstainCount = null,
                Labels = null,
                CommentCount = item.Comments?.Items?.Length ?? 0,
            };

            if (item.CustomFields?.Items is not null)
            {
                foreach (JiraXmlCustomField cf in item.CustomFields.Items)
                {
                    if (cf.Id is null || !CustomFieldKeyMap.TryGetValue(cf.Id, out string? propertyName))
                        continue;

                    string? value = JiraFieldMapper.CleanFieldValue(
                        cf.Values?.Items is { Length: > 0 }
                            ? string.Join(", ", cf.Values.Items.Where(v => !string.IsNullOrEmpty(v)))
                            : null);

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
            }

            // Clean standard field values
            record.Type = JiraFieldMapper.CleanFieldValue(record.Type) ?? "Unknown";
            record.Priority = JiraFieldMapper.CleanFieldValue(record.Priority) ?? "Unknown";
            record.Status = JiraFieldMapper.CleanFieldValue(record.Status) ?? "Unknown";
            record.Resolution = JiraFieldMapper.CleanFieldValue(record.Resolution);
            record.Assignee = JiraFieldMapper.CleanFieldValue(record.Assignee);
            record.Reporter = JiraFieldMapper.CleanFieldValue(record.Reporter);

            // Compute plain text from HTML fields
            record.DescriptionPlain = JiraFieldMapper.ToPlainText(record.Description);
            record.ResolutionDescriptionPlain = JiraFieldMapper.ToPlainText(record.ResolutionDescription);

            // Clean title: remove ticket identifier prefix
            record.Title = JiraFieldMapper.CleanTitle(record.Title, record.Key);

            // Parse vote components
            (record.VoteMover, record.VoteSeconder, record.VoteForCount,
             record.VoteAgainstCount, record.VoteAbstainCount) = JiraFieldMapper.ParseVote(record.Vote);

            List<JiraCommentRecord> comments= JiraCommentParser.ParseXmlComments(item, issueId, key);
            yield return (record, comments);
        }
    }

    #region XML Serialization Classes

    [XmlRoot("rss")]
    public class JiraRss
    {
        [XmlElement("channel")]
        public JiraChannel? Channel { get; set; }
    }

    public class JiraChannel
    {
        [XmlElement("item")]
        public JiraItem[]? Items { get; set; }
    }

    public class JiraItem
    {
        [XmlElement("title")]
        public string? Title { get; set; }

        [XmlElement("link")]
        public string? Link { get; set; }

        [XmlElement("description")]
        public string? Description { get; set; }

        [XmlElement("summary")]
        public string? Summary { get; set; }

        [XmlElement("project")]
        public JiraXmlProject? Project { get; set; }

        [XmlElement("key")]
        public JiraXmlKey? Key { get; set; }

        [XmlElement("type")]
        public JiraXmlNamedElement? Type { get; set; }

        [XmlElement("priority")]
        public JiraXmlNamedElement? Priority { get; set; }

        [XmlElement("status")]
        public JiraXmlNamedElement? Status { get; set; }

        [XmlElement("resolution")]
        public JiraXmlNamedElement? Resolution { get; set; }

        [XmlElement("assignee")]
        public JiraXmlPerson? Assignee { get; set; }

        [XmlElement("reporter")]
        public JiraXmlPerson? Reporter { get; set; }

        [XmlElement("created")]
        public string? Created { get; set; }

        [XmlElement("updated")]
        public string? Updated { get; set; }

        [XmlElement("resolved")]
        public string? Resolved { get; set; }

        [XmlElement("comments")]
        public JiraXmlComments? Comments { get; set; }

        [XmlElement("customfields")]
        public JiraXmlCustomFields? CustomFields { get; set; }
    }

    public class JiraXmlProject
    {
        [XmlAttribute("id")]
        public string? Id { get; set; }

        [XmlAttribute("key")]
        public string? Key { get; set; }

        [XmlText]
        public string? Name { get; set; }
    }

    public class JiraXmlKey
    {
        [XmlAttribute("id")]
        public string? Id { get; set; }

        [XmlText]
        public string? Text { get; set; }
    }

    public class JiraXmlNamedElement
    {
        [XmlAttribute("id")]
        public string? Id { get; set; }

        [XmlText]
        public string? Text { get; set; }
    }

    public class JiraXmlPerson
    {
        [XmlAttribute("username")]
        public string? Username { get; set; }

        [XmlText]
        public string? Text { get; set; }
    }

    public class JiraXmlComments
    {
        [XmlElement("comment")]
        public JiraXmlComment[]? Items { get; set; }
    }

    public class JiraXmlComment
    {
        [XmlAttribute("id")]
        public string? Id { get; set; }

        [XmlAttribute("author")]
        public string? Author { get; set; }

        [XmlAttribute("created")]
        public string? Created { get; set; }

        [XmlText]
        public string? Text { get; set; }
    }

    public class JiraXmlCustomFields
    {
        [XmlElement("customfield")]
        public JiraXmlCustomField[]? Items { get; set; }
    }

    public class JiraXmlCustomField
    {
        [XmlAttribute("id")]
        public string? Id { get; set; }

        [XmlAttribute("key")]
        public string? Key { get; set; }

        [XmlElement("customfieldname")]
        public string? Name { get; set; }

        [XmlElement("customfieldvalues")]
        public JiraXmlCustomFieldValues? Values { get; set; }
    }

    public class JiraXmlCustomFieldValues
    {
        [XmlElement("customfieldvalue")]
        public string[]? Items { get; set; }
    }

    #endregion
}
