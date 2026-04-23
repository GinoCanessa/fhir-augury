using System.Xml;
using System.Xml.Serialization;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database.Records;
using static FhirAugury.Common.DateTimeHelper;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Raw user references extracted from XML/JSON, before resolution to jira_users IDs.</summary>
public record JiraXmlUserInfo
{
    public string? AssigneeUsername { get; init; }
    public string? AssigneeDisplayName { get; init; }
    public string? ReporterUsername { get; init; }
    public string? ReporterDisplayName { get; init; }
}

/// <summary>A user reference for in-person discussion requests.</summary>
public record JiraInPersonRef(string? Username, string? DisplayName);

/// <summary>
/// Parses Jira XML RSS export format into typed <see cref="JiraParsedItem"/>
/// payloads. The shape map (project key → <see cref="JiraProjectShape"/>)
/// chooses which concrete record type each item is mapped into; unknown
/// project keys default to <see cref="JiraProjectShape.FhirChangeRequest"/>.
/// </summary>
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
        ["customfield_11402"] = nameof(JiraIssueRecord.Labels),
        ["customfield_10512"] = nameof(JiraIssueRecord.ChangeCategory),
        ["customfield_10511"] = nameof(JiraIssueRecord.ChangeImpact),
        ["customfield_14500"] = nameof(JiraIssueRecord.SponsoringWorkGroup),
        ["customfield_14501"] = nameof(JiraIssueRecord.CoSponsoringWorkGroups),
        ["customfield_13704"] = nameof(JiraIssueRecord.Realm),
    };

    private static readonly HashSet<string> PssCustomFieldIds = new()
    {
        "customfield_14500", "customfield_12110", "customfield_14501", "customfield_12106",
        "customfield_13704", "customfield_13727", "customfield_12109", "customfield_12108",
        "customfield_12105", "customfield_12801", "customfield_12316", "customfield_13709",
        "customfield_13710", "customfield_13720", "customfield_12802", "customfield_13707",
        "customfield_13708", "customfield_13714", "customfield_13716", "customfield_13717",
        "customfield_13715", "customfield_13718", "customfield_13725", "customfield_13726",
        "customfield_13721", "customfield_13700", "customfield_13701", "customfield_13702",
        "customfield_13703", "customfield_13705", "customfield_13706", "customfield_13711",
        "customfield_13712", "customfield_13713", "customfield_13719", "customfield_13722",
        "customfield_13723", "customfield_13724", "customfield_12702",
    };

    private static readonly HashSet<string> BaldefCustomFieldIds = new()
    {
        "customfield_11704", "customfield_11604", "customfield_11302", "customfield_11706",
        "customfield_10900", "customfield_10901", "customfield_12105", "customfield_11610",
        "customfield_11606", "customfield_11607", "customfield_11608", "customfield_11609",
        "customfield_11806", "customfield_11810", "customfield_11300", "customfield_11301",
    };

    private static readonly HashSet<string> BallotCustomFieldIds = new()
    {
        "customfield_10519", "customfield_10521", "customfield_11707", "customfield_10601",
        "customfield_11805", "customfield_11604", "customfield_11603", "customfield_11302",
        "customfield_11810",
    };

    /// <summary>
    /// Backwards-compatible overload: treats every project as
    /// <see cref="JiraProjectShape.FhirChangeRequest"/>. Use the overload
    /// that accepts a shape map for multi-shape exports.
    /// </summary>
    public static IEnumerable<JiraParsedItem> ParseExport(Stream stream)
        => ParseExport(stream, new Dictionary<string, JiraProjectShape>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Parses a Jira XML RSS export and yields one <see cref="JiraParsedItem"/>
    /// per item, dispatched to the correct concrete subtype based on
    /// <paramref name="shapeByProjectKey"/>.
    /// </summary>
    public static IEnumerable<JiraParsedItem> ParseExport(
        Stream stream,
        IReadOnlyDictionary<string, JiraProjectShape> shapeByProjectKey)
    {
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
        using XmlReader reader = XmlReader.Create(stream, settings);
        JiraRss rss = (JiraRss?)Serializer.Deserialize(reader)
                  ?? throw new InvalidOperationException("Failed to deserialize Jira XML export.");

        if (rss.Channel?.Items is null)
        {
            yield break;
        }

        foreach (JiraItem item in rss.Channel.Items)
        {
            string key = item.Key?.Text ?? string.Empty;
            string projectKey = item.Project?.Key ?? (key.Contains('-') ? key.Split('-')[0] : string.Empty);

            JiraProjectShape shape = shapeByProjectKey.TryGetValue(projectKey, out JiraProjectShape s)
                ? s
                : JiraProjectShape.FhirChangeRequest;

            yield return shape switch
            {
                JiraProjectShape.ProjectScopeStatement => ParseProjectScopeStatement(item, key, projectKey),
                JiraProjectShape.BallotDefinition => ParseBaldef(item, key, projectKey),
                JiraProjectShape.BallotVote => ParseBallot(item, key, projectKey),
                _ => ParseFhirIssue(item, key, projectKey),
            };
        }
    }

    private static void PopulateBase(JiraIssueBaseRecord record, JiraItem item, string key, string projectKey)
    {
        record.Key = key;
        record.ProjectKey = projectKey;
        record.Title = item.Title ?? string.Empty;
        record.Description = item.Description;
        record.DescriptionPlain = JiraFieldMapper.ToPlainText(item.Description);
        record.Summary = item.Summary;
        record.Type = JiraFieldMapper.CleanFieldValue(item.Type?.Text) ?? "Unknown";
        record.Priority = JiraFieldMapper.CleanFieldValue(item.Priority?.Text) ?? "Unknown";
        record.Status = JiraFieldMapper.CleanFieldValue(item.Status?.Text) ?? "Unknown";
        record.Assignee = JiraFieldMapper.CleanFieldValue(item.Assignee?.Text ?? item.Assignee?.Username);
        record.Reporter = JiraFieldMapper.CleanFieldValue(item.Reporter?.Text ?? item.Reporter?.Username);
        record.CreatedAt = ParseDate(item.Created);
        record.UpdatedAt = ParseDate(item.Updated);
        record.ResolvedAt = ParseNullableDate(item.Resolved);
        record.CommentCount = item.Comments?.Items?.Length ?? 0;
        record.Title = JiraFieldMapper.CleanTitle(record.Title, key);
    }

    private static string? GetCustomFieldValue(JiraXmlCustomField cf)
    {
        if (cf.Values?.Items is { Length: > 0 } items)
        {
            return JiraFieldMapper.CleanFieldValue(string.Join(", ", items.Where(v => !string.IsNullOrEmpty(v))));
        }
        if (cf.Values?.Labels is { Length: > 0 } labels)
        {
            return JiraFieldMapper.CleanFieldValue(string.Join(", ", labels.Where(v => !string.IsNullOrEmpty(v))));
        }
        return null;
    }

    private static JiraXmlUserInfo BuildUserInfo(JiraItem item) => new()
    {
        AssigneeUsername = item.Assignee?.Username,
        AssigneeDisplayName = item.Assignee?.Text,
        ReporterUsername = item.Reporter?.Username,
        ReporterDisplayName = item.Reporter?.Text,
    };

    private static List<JiraInPersonRef> ExtractInPersons(JiraItem item)
    {
        List<JiraInPersonRef> result = [];
        if (item.CustomFields?.Items is null) return result;

        JiraXmlCustomField? ipField = item.CustomFields.Items
            .FirstOrDefault(cf => cf.Id == "customfield_11000");
        if (ipField?.Values?.Items is null) return result;

        foreach (string username in ipField.Values.Items)
        {
            if (!string.IsNullOrWhiteSpace(username))
            {
                result.Add(new JiraInPersonRef(username.Trim(), null));
            }
        }
        return result;
    }

    private static List<JiraIssueLinkRecord> ExtractIssueLinks(JiraItem item, string sourceKey)
    {
        List<JiraIssueLinkRecord> result = [];
        if (item.IssueLinks?.LinkTypes is null) return result;

        foreach (JiraXmlIssueLinkType linkType in item.IssueLinks.LinkTypes)
        {
            string typeName = linkType.OutwardLinks?.Description
                              ?? linkType.InwardLinks?.Description
                              ?? linkType.Name
                              ?? "relates to";

            if (linkType.OutwardLinks?.Links is { } outLinks)
            {
                foreach (JiraXmlIssueLink link in outLinks)
                {
                    string? targetKey = link.IssueKey?.Text;
                    if (!string.IsNullOrEmpty(targetKey))
                    {
                        result.Add(new JiraIssueLinkRecord
                        {
                            Id = JiraIssueLinkRecord.GetIndex(),
                            SourceKey = sourceKey,
                            TargetKey = targetKey,
                            LinkType = linkType.OutwardLinks.Description ?? typeName,
                        });
                    }
                }
            }

            if (linkType.InwardLinks?.Links is { } inLinks)
            {
                foreach (JiraXmlIssueLink link in inLinks)
                {
                    string? otherKey = link.IssueKey?.Text;
                    if (!string.IsNullOrEmpty(otherKey))
                    {
                        result.Add(new JiraIssueLinkRecord
                        {
                            Id = JiraIssueLinkRecord.GetIndex(),
                            SourceKey = otherKey,
                            TargetKey = sourceKey,
                            LinkType = linkType.InwardLinks.Description ?? typeName,
                        });
                    }
                }
            }
        }

        return result;
    }

    private static JiraParsedFhirIssue ParseFhirIssue(JiraItem item, string key, string projectKey)
    {
        JiraIssueRecord record = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = string.Empty,
            Description = null,
            Summary = null,
            Type = "Unknown",
            Priority = "Unknown",
            Status = "Unknown",
            Resolution = JiraFieldMapper.CleanFieldValue(item.Resolution?.Text),
            ResolutionDescription = null,
            Assignee = null,
            Reporter = null,
            CreatedAt = default,
            UpdatedAt = default,
            ResolvedAt = null,
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
        };
        PopulateBase(record, item, key, projectKey);

        if (item.CustomFields?.Items is not null)
        {
            foreach (JiraXmlCustomField cf in item.CustomFields.Items)
            {
                if (cf.Id is null || !CustomFieldKeyMap.TryGetValue(cf.Id, out string? propertyName)) continue;
                string? value = GetCustomFieldValue(cf);
                if (value is null) continue;

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
                    case nameof(JiraIssueRecord.SponsoringWorkGroup): record.SponsoringWorkGroup = value; break;
                    case nameof(JiraIssueRecord.CoSponsoringWorkGroups): record.CoSponsoringWorkGroups = value; break;
                    case nameof(JiraIssueRecord.Realm): record.Realm = value; break;
                }
            }
        }

        record.ResolutionDescriptionPlain = JiraFieldMapper.ToPlainText(record.ResolutionDescription);
        (record.VoteMover, record.VoteSeconder, record.VoteForCount,
         record.VoteAgainstCount, record.VoteAbstainCount) = JiraFieldMapper.ParseVote(record.Vote);

        List<JiraCommentRecord> comments = JiraCommentParser.ParseXmlComments(item, key);
        return new JiraParsedFhirIssue
        {
            Record = record,
            Comments = comments,
            UserInfo = BuildUserInfo(item),
            InPersons = ExtractInPersons(item),
            Links = ExtractIssueLinks(item, key),
        };
    }

    private static JiraParsedProjectScopeStatement ParseProjectScopeStatement(JiraItem item, string key, string projectKey)
    {
        JiraProjectScopeStatementRecord record = new JiraProjectScopeStatementRecord
        {
            Id = JiraProjectScopeStatementRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = string.Empty,
            Description = null,
            Summary = null,
            Type = "Unknown",
            Priority = "Unknown",
            Status = "Unknown",
            Assignee = null,
            Reporter = null,
            CreatedAt = default,
            UpdatedAt = default,
            ResolvedAt = null,
        };
        PopulateBase(record, item, key, projectKey);

        if (item.CustomFields?.Items is not null)
        {
            foreach (JiraXmlCustomField cf in item.CustomFields.Items)
            {
                if (cf.Id is null || !PssCustomFieldIds.Contains(cf.Id)) continue;
                string? value = GetCustomFieldValue(cf);
                if (value is null) continue;

                switch (cf.Id)
                {
                    case "customfield_14500": record.SponsoringWorkGroup = value; break;
                    case "customfield_12110": record.SponsoringWorkGroupsLegacy = value; break;
                    case "customfield_14501":
                        record.CoSponsoringWorkGroups = JiraFieldMapper.StripHtmlTableToCsv(value);
                        break;
                    case "customfield_12106": record.CoSponsoringWorkGroupsLegacy = value; break;
                    case "customfield_13704": record.Realm = value; break;
                    case "customfield_13727": record.OtherRealm = value; break;
                    case "customfield_12109": record.SteeringDivision = value; break;
                    case "customfield_12108": record.ManagementGroups = value; break;
                    case "customfield_12105": record.ProductFamily = value; break;
                    case "customfield_12801": record.BallotCycleTarget = value; break;
                    case "customfield_12316": record.ApprovalDate = ParseNullableDate(value); break;
                    case "customfield_13709": record.RejectionDate = ParseNullableDate(value); break;
                    case "customfield_13710": record.OptOutDate = ParseNullableDate(value); break;
                    case "customfield_13720": record.ProjectCommonName = value; break;
                    case "customfield_12802":
                        record.ProjectDescription = value;
                        record.ProjectDescriptionPlain = JiraFieldMapper.ToPlainText(value);
                        break;
                    case "customfield_13707":
                        record.ProjectNeed = value;
                        record.ProjectNeedPlain = JiraFieldMapper.ToPlainText(value);
                        break;
                    case "customfield_13708": record.ProjectDocumentRepositoryUrl = JiraFieldMapper.ExtractAnchorHref(value); break;
                    case "customfield_13714": record.ProjectFacilitator = value; break;
                    case "customfield_13716": record.PublishingFacilitator = value; break;
                    case "customfield_13717": record.VocabularyFacilitator = value; break;
                    case "customfield_13715": record.OtherInterestedParties = value; break;
                    case "customfield_13718": record.Implementers = value; break;
                    case "customfield_13725": record.Stakeholders = value; break;
                    case "customfield_13726": record.OtherStakeholders = value; break;
                    case "customfield_13721":
                        record.ProjectDependencies = value;
                        record.ProjectDependenciesPlain = JiraFieldMapper.ToPlainText(value);
                        break;
                    case "customfield_13700": record.Accelerators = value; break;
                    case "customfield_13701": record.NormativeNotification = value; break;
                    case "customfield_13702": record.ProductInfo = value; break;
                    case "customfield_13703": record.ExternalContentMajority = value; break;
                    case "customfield_13705": record.JointCopyright = value; break;
                    case "customfield_13706": record.ExternalCodeSystems = value; break;
                    case "customfield_13711": record.IsoStandardToAdopt = value; break;
                    case "customfield_13712": record.ExcerptText = value; break;
                    case "customfield_13713": record.UnitOfMeasure = value; break;
                    case "customfield_13719": record.ExternalDrivers = value; break;
                    case "customfield_13722": record.BackwardsCompatibility = value; break;
                    case "customfield_13723": record.ExternalProjectCollaboration = value; break;
                    case "customfield_13724": record.DevelopersOfExternalContent = value; break;
                    case "customfield_12702": record.ContactEmail = JiraFieldMapper.ExtractAnchorHref(value); break;
                }
            }
        }

        return new JiraParsedProjectScopeStatement
        {
            Record = record,
            Comments = JiraCommentParser.ParseXmlComments(item, key),
            UserInfo = BuildUserInfo(item),
            InPersons = ExtractInPersons(item),
            Links = ExtractIssueLinks(item, key),
        };
    }

    private static JiraParsedBaldef ParseBaldef(JiraItem item, string key, string projectKey)
    {
        JiraBaldefRecord record = new JiraBaldefRecord
        {
            Id = JiraBaldefRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = string.Empty,
            Description = null,
            Summary = null,
            Type = "Unknown",
            Priority = "Unknown",
            Status = "Unknown",
            Assignee = null,
            Reporter = null,
            CreatedAt = default,
            UpdatedAt = default,
            ResolvedAt = null,
        };
        PopulateBase(record, item, key, projectKey);

        if (item.CustomFields?.Items is not null)
        {
            foreach (JiraXmlCustomField cf in item.CustomFields.Items)
            {
                if (cf.Id is null || !BaldefCustomFieldIds.Contains(cf.Id)) continue;
                string? value = GetCustomFieldValue(cf);
                if (value is null) continue;

                switch (cf.Id)
                {
                    case "customfield_11704":
                        record.BallotCode = value;
                        (record.BallotCycle, record.BallotPackageName) = JiraFieldMapper.SplitBallotCode(value);
                        break;
                    case "customfield_11604": record.BallotCategory = value; break;
                    case "customfield_11302": record.Specification = value; break;
                    case "customfield_11706": record.SpecificationLocation = value; break;
                    case "customfield_10900": record.BallotOpens = ParseNullableDate(value); break;
                    case "customfield_10901": record.BallotCloses = ParseNullableDate(value); break;
                    case "customfield_12105": record.ProductFamily = value; break;
                    case "customfield_11610": record.ApprovalStatus = value; break;
                    case "customfield_11606": record.VotersTotalEligible = JiraFieldMapper.TryParseInt(value); break;
                    case "customfield_11607": record.VotersAffirmative = JiraFieldMapper.TryParseInt(value); break;
                    case "customfield_11608": record.VotersNegative = JiraFieldMapper.TryParseInt(value); break;
                    case "customfield_11609": record.VotersAbstain = JiraFieldMapper.TryParseInt(value); break;
                    case "customfield_11806":
                        record.OrganizationalParticipation = value;
                        record.OrganizationalParticipationPlain = JiraFieldMapper.ToPlainText(value);
                        break;
                    case "customfield_11810": record.Reconciled = value; break;
                    case "customfield_11300": record.RelatedArtifacts = value; break;
                    case "customfield_11301": record.RelatedPages = value; break;
                }
            }
        }

        return new JiraParsedBaldef
        {
            Record = record,
            Comments = JiraCommentParser.ParseXmlComments(item, key),
            UserInfo = BuildUserInfo(item),
            InPersons = ExtractInPersons(item),
            Links = ExtractIssueLinks(item, key),
        };
    }

    private static JiraParsedBallot ParseBallot(JiraItem item, string key, string projectKey)
    {
        JiraBallotRecord record = new JiraBallotRecord
        {
            Id = JiraBallotRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = string.Empty,
            Description = null,
            Summary = null,
            Type = "Unknown",
            Priority = "Unknown",
            Status = "Unknown",
            Assignee = null,
            Reporter = null,
            CreatedAt = default,
            UpdatedAt = default,
            ResolvedAt = null,
        };
        PopulateBase(record, item, key, projectKey);

        if (item.CustomFields?.Items is not null)
        {
            foreach (JiraXmlCustomField cf in item.CustomFields.Items)
            {
                if (cf.Id is null || !BallotCustomFieldIds.Contains(cf.Id)) continue;
                string? value = GetCustomFieldValue(cf);
                if (value is null) continue;

                switch (cf.Id)
                {
                    case "customfield_10519": record.VoteBallot = value; break;
                    case "customfield_10521": record.VoteItem = value; break;
                    case "customfield_11707": record.ExternalId = value; break;
                    case "customfield_10601": record.Organization = value; break;
                    case "customfield_11805": record.OrganizationCategory = value; break;
                    case "customfield_11604": record.BallotCategory = value; break;
                    case "customfield_11603": record.VoteSameAs = value; break;
                    case "customfield_11302": record.Specification = value; break;
                    case "customfield_11810": record.Reconciled = value; break;
                }
            }
        }

        // Derive Voter / BallotPackageCode from <summary>; derive BallotCycle from BallotPackageCode.
        (record.Voter, record.BallotPackageCode) = JiraFieldMapper.ParseBallotSummary(item.Summary ?? item.Title);
        if (record.BallotPackageCode is not null)
        {
            (record.BallotCycle, _) = JiraFieldMapper.SplitBallotCode(record.BallotPackageCode);
        }

        // RelatedFhirIssue: first outward issue link with target ^FHIR- and type "is created from" / "relates to" / "votes on".
        List<JiraIssueLinkRecord> links = ExtractIssueLinks(item, key);
        record.RelatedFhirIssue = JiraFieldMapper.PickRelatedFhirKey(links, key);

        return new JiraParsedBallot
        {
            Record = record,
            Comments = JiraCommentParser.ParseXmlComments(item, key),
            UserInfo = BuildUserInfo(item),
            InPersons = ExtractInPersons(item),
            Links = links,
        };
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

        [XmlElement("issuelinks")]
        public JiraXmlIssueLinks? IssueLinks { get; set; }
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

        [XmlElement("label")]
        public string[]? Labels { get; set; }
    }

    public class JiraXmlIssueLinks
    {
        [XmlElement("issuelinktype")]
        public JiraXmlIssueLinkType[]? LinkTypes { get; set; }
    }

    public class JiraXmlIssueLinkType
    {
        [XmlAttribute("id")]
        public string? Id { get; set; }

        [XmlElement("name")]
        public string? Name { get; set; }

        [XmlElement("outwardlinks")]
        public JiraXmlOutwardLinks? OutwardLinks { get; set; }

        [XmlElement("inwardlinks")]
        public JiraXmlOutwardLinks? InwardLinks { get; set; }
    }

    public class JiraXmlOutwardLinks
    {
        [XmlAttribute("description")]
        public string? Description { get; set; }

        [XmlElement("issuelink")]
        public JiraXmlIssueLink[]? Links { get; set; }
    }

    public class JiraXmlIssueLink
    {
        [XmlElement("issuekey")]
        public JiraXmlKey? IssueKey { get; set; }
    }

    #endregion
}
