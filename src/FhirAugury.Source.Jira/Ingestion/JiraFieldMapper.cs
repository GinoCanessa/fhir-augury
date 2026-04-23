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

    private static readonly Regex AnchorHrefPattern = new(
        @"<a\b[^>]*\bhref\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BallotSummaryPattern = new(
        @"^\s*(?<vote>[^-]+?)\s+-\s+(?<voter>.+?)\s+(?:\((?<org>[^)]+)\)\s+)?:\s*(?<code>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TableCellPattern = new(
        @"<td\b[^>]*>(?<cell>.*?)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// If <paramref name="value"/> is an HTML anchor (<c>&lt;a href="…"&gt;text&lt;/a&gt;</c>),
    /// returns the href value. Otherwise returns the input cleaned of HTML
    /// (so plain URLs and plain text fall through unchanged).
    /// </summary>
    internal static string? ExtractAnchorHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        Match m = AnchorHrefPattern.Match(value);
        if (m.Success)
        {
            return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        }
        return ToPlainText(value) ?? value.Trim();
    }

    /// <summary>
    /// Strips an HTML <c>&lt;tr&gt;&lt;td&gt;…&lt;/td&gt;&lt;/tr&gt;</c> table to a comma-separated
    /// list of cell text. Falls back to plain-text strip when no
    /// <c>&lt;td&gt;</c> tags are present.
    /// </summary>
    internal static string? StripHtmlTableToCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        MatchCollection cells = TableCellPattern.Matches(value);
        if (cells.Count == 0) return ToPlainText(value);

        List<string> parts = [];
        foreach (Match m in cells)
        {
            string cellText = ToPlainText(m.Groups["cell"].Value)?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(cellText))
            {
                parts.Add(cellText);
            }
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>Parses an integer value, tolerating decimal-formatted numerics.</summary>
    internal static int? TryParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out int i))
        {
            return i;
        }
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            return (int)d;
        }
        return null;
    }

    /// <summary>
    /// Splits a BALDEF/BALLOT ballot code (e.g. <c>2019-Sep | FHIR IG LIVD R1</c>)
    /// on the first <c>|</c> into <c>(BallotCycle, BallotPackageName)</c>. When
    /// no <c>|</c> is present, the cycle is null and the full value is the
    /// package name.
    /// </summary>
    internal static (string? Cycle, string? PackageName) SplitBallotCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (null, null);
        int idx = code.IndexOf('|');
        if (idx < 0) return (null, code.Trim());
        string cycle = code[..idx].Trim();
        string name = code[(idx + 1)..].Trim();
        return (cycle.Length > 0 ? cycle : null, name.Length > 0 ? name : null);
    }

    /// <summary>
    /// Parses a BALLOT summary (<c>"&lt;vote&gt; - &lt;voter&gt; (&lt;org&gt;) : &lt;code&gt;"</c>)
    /// and returns the voter name and ballot package code. Org is optional.
    /// Returns nulls when the summary does not match.
    /// </summary>
    internal static (string? Voter, string? BallotPackageCode) ParseBallotSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return (null, null);
        Match m = BallotSummaryPattern.Match(summary.Trim());
        if (!m.Success) return (null, null);
        string voter = m.Groups["voter"].Value.Trim();
        string code = m.Groups["code"].Value.Trim();
        return (voter.Length > 0 ? voter : null, code.Length > 0 ? code : null);
    }

    /// <summary>
    /// Picks the first link whose target key starts with <c>FHIR-</c> and the
    /// link type is <c>is created from</c>, <c>relates to</c>, or <c>votes on</c>.
    /// Used by BALLOT mapping to materialise <c>RelatedFhirIssue</c> at parse time.
    /// </summary>
    internal static string? PickRelatedFhirKey(IEnumerable<JiraIssueLinkRecord> links, string sourceKey)
    {
        foreach (JiraIssueLinkRecord link in links)
        {
            if (!string.Equals(link.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!link.TargetKey.StartsWith("FHIR-", StringComparison.OrdinalIgnoreCase)) continue;
            string lt = link.LinkType ?? string.Empty;
            if (lt.Contains("created from", StringComparison.OrdinalIgnoreCase) ||
                lt.Contains("relates to", StringComparison.OrdinalIgnoreCase) ||
                lt.Contains("votes on", StringComparison.OrdinalIgnoreCase))
            {
                return link.TargetKey;
            }
        }
        return null;
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

    /// <summary>Extracts raw user reference (username + displayName) from a JSON user object field.</summary>
    public static (string? Username, string? DisplayName) ExtractUserRef(JsonElement fields, string fieldName)
    {
        if (!fields.TryGetProperty(fieldName, out JsonElement userObj) || userObj.ValueKind != JsonValueKind.Object)
            return (null, null);

        string? username = JsonElementHelper.GetString(userObj, "name")
                        ?? JsonElementHelper.GetString(userObj, "key");
        string? displayName = JsonElementHelper.GetString(userObj, "displayName");

        return (username, displayName);
    }

    /// <summary>
    /// Extracts in-person requester user references from customfield_11000 (multiuserpicker).
    /// </summary>
    public static List<JiraInPersonRef> ExtractInPersonRequesters(JsonElement fields)
    {
        List<JiraInPersonRef> results = [];

        if (!fields.TryGetProperty("customfield_11000", out JsonElement ipField) ||
            ipField.ValueKind != JsonValueKind.Array)
            return results;

        foreach (JsonElement user in ipField.EnumerateArray())
        {
            if (user.ValueKind != JsonValueKind.Object)
                continue;

            string? username = JsonElementHelper.GetString(user, "name")
                            ?? JsonElementHelper.GetString(user, "key");
            string? displayName = JsonElementHelper.GetString(user, "displayName");

            if (username is not null || displayName is not null)
                results.Add(new JiraInPersonRef(username, displayName));
        }

        return results;
    }

    private static void MapBase<T>(JsonElement issueJson, T record) where T : JiraIssueBaseRecord
    {
        JsonElement fields = issueJson.GetProperty("fields");
        string key = issueJson.GetProperty("key").GetString()!;

        record.Key = key;
        record.ProjectKey = JsonElementHelper.GetNestedString(fields, "project", "key") ?? key.Split('-')[0];
        record.Title = JsonElementHelper.GetString(fields, "summary") ?? string.Empty;
        record.Description = JsonElementHelper.GetString(fields, "description");
        record.DescriptionPlain = ToPlainText(record.Description);
        record.Summary = JsonElementHelper.GetString(fields, "summary");
        record.Type = CleanFieldValue(JsonElementHelper.GetNestedString(fields, "issuetype", "name")) ?? "Unknown";
        record.Priority = CleanFieldValue(JsonElementHelper.GetNestedString(fields, "priority", "name")) ?? "Unknown";
        record.Status = CleanFieldValue(JsonElementHelper.GetNestedString(fields, "status", "name")) ?? "Unknown";
        record.Assignee = CleanFieldValue(JsonElementHelper.GetNestedString(fields, "assignee", "displayName"));
        record.Reporter = CleanFieldValue(JsonElementHelper.GetNestedString(fields, "reporter", "displayName"));
        record.CreatedAt = ParseDate(JsonElementHelper.GetString(fields, "created"));
        record.UpdatedAt = ParseDate(JsonElementHelper.GetString(fields, "updated"));
        record.ResolvedAt = ParseNullableDate(JsonElementHelper.GetString(fields, "resolutiondate"));
        record.Labels = GetLabels(fields);
        record.CommentCount = GetCommentCount(fields);
        record.Title = CleanTitle(record.Title, record.Key);
    }

    public static JiraIssueRecord MapIssue(JsonElement issueJson)
    {
        JsonElement fields = issueJson.GetProperty("fields");

        JiraIssueRecord record = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = string.Empty,
            ProjectKey = string.Empty,
            Title = string.Empty,
            Description = null,
            Summary = null,
            Type = "Unknown",
            Priority = "Unknown",
            Status = "Unknown",
            Resolution = JsonElementHelper.GetNestedString(fields, "resolution", "name"),
            ResolutionDescription = null,
            Assignee = null,
            Reporter = null,
            CreatedAt = default,
            UpdatedAt = default,
            ResolvedAt = null,
            Specification = null,
            WorkGroup = null,
            RaisedInVersion = null,
            RelatedArtifacts = null,
            SelectedBallot = null,
            RelatedIssues = null,
            DuplicateOf = null,
            AppliedVersions = null,
            ChangeType = null,
            Impact = null,
            Vote = null,
        };
        MapBase(issueJson, record);

        foreach ((string? fieldId, string? propertyName) in CustomFieldMap)
        {
            string? value = CleanFieldValue(ExtractCustomFieldValue(fields, fieldId));
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
            }
        }

        record.Resolution = CleanFieldValue(record.Resolution);
        record.ResolutionDescriptionPlain = ToPlainText(record.ResolutionDescription);
        (record.VoteMover, record.VoteSeconder, record.VoteForCount,
         record.VoteAgainstCount, record.VoteAbstainCount) = ParseVote(record.Vote);

        return record;
    }

    public static JiraProjectScopeStatementRecord MapProjectScopeStatement(JsonElement issueJson)
    {
        JsonElement fields = issueJson.GetProperty("fields");

        JiraProjectScopeStatementRecord record = new JiraProjectScopeStatementRecord
        {
            Id = JiraProjectScopeStatementRecord.GetIndex(),
            Key = string.Empty,
            ProjectKey = string.Empty,
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
        MapBase(issueJson, record);

        record.SponsoringWorkGroup = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_14500"));
        record.SponsoringWorkGroupsLegacy = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12110"));
        string? cosp = ExtractCustomFieldValue(fields, "customfield_14501");
        record.CoSponsoringWorkGroups = StripHtmlTableToCsv(cosp) ?? CleanFieldValue(cosp);
        record.CoSponsoringWorkGroupsLegacy = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12106"));
        record.Realm = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13704"));
        record.OtherRealm = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13727"));
        record.SteeringDivision = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12109"));
        record.ManagementGroups = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12108"));
        record.ProductFamily = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12105"));
        record.BallotCycleTarget = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12801"));
        record.ApprovalDate = ParseNullableDate(ExtractCustomFieldValue(fields, "customfield_12316"));
        record.RejectionDate = ParseNullableDate(ExtractCustomFieldValue(fields, "customfield_13709"));
        record.OptOutDate = ParseNullableDate(ExtractCustomFieldValue(fields, "customfield_13710"));
        record.ProjectCommonName = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13720"));

        string? projectDesc = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12802"));
        record.ProjectDescription = projectDesc;
        record.ProjectDescriptionPlain = ToPlainText(projectDesc);

        string? projectNeed = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13707"));
        record.ProjectNeed = projectNeed;
        record.ProjectNeedPlain = ToPlainText(projectNeed);

        record.ProjectDocumentRepositoryUrl = ExtractAnchorHref(ExtractCustomFieldValue(fields, "customfield_13708"));
        record.ProjectFacilitator = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13714"));
        record.PublishingFacilitator = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13716"));
        record.VocabularyFacilitator = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13717"));
        record.OtherInterestedParties = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13715"));
        record.Implementers = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13718"));
        record.Stakeholders = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13725"));
        record.OtherStakeholders = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13726"));

        string? projectDeps = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13721"));
        record.ProjectDependencies = projectDeps;
        record.ProjectDependenciesPlain = ToPlainText(projectDeps);

        record.Accelerators = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13700"));
        record.NormativeNotification = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13701"));
        record.ProductInfo = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13702"));
        record.ExternalContentMajority = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13703"));
        record.JointCopyright = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13705"));
        record.ExternalCodeSystems = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13706"));
        record.IsoStandardToAdopt = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13711"));
        record.ExcerptText = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13712"));
        record.UnitOfMeasure = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13713"));
        record.ExternalDrivers = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13719"));
        record.BackwardsCompatibility = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13722"));
        record.ExternalProjectCollaboration = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13723"));
        record.DevelopersOfExternalContent = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_13724"));
        record.ContactEmail = ExtractAnchorHref(ExtractCustomFieldValue(fields, "customfield_12702"));

        return record;
    }

    public static JiraBaldefRecord MapBaldef(JsonElement issueJson)
    {
        JsonElement fields = issueJson.GetProperty("fields");

        JiraBaldefRecord record = new JiraBaldefRecord
        {
            Id = JiraBaldefRecord.GetIndex(),
            Key = string.Empty,
            ProjectKey = string.Empty,
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
        MapBase(issueJson, record);

        string? ballotCode = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11704"));
        record.BallotCode = ballotCode;
        (record.BallotCycle, record.BallotPackageName) = SplitBallotCode(ballotCode);

        record.BallotCategory = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11604"));
        record.Specification = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11302"));
        record.SpecificationLocation = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11706"));
        record.BallotOpens = ParseNullableDate(ExtractCustomFieldValue(fields, "customfield_10900"));
        record.BallotCloses = ParseNullableDate(ExtractCustomFieldValue(fields, "customfield_10901"));
        record.ProductFamily = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_12105"));
        record.ApprovalStatus = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11610"));
        record.VotersTotalEligible = TryParseInt(ExtractCustomFieldValue(fields, "customfield_11606"));
        record.VotersAffirmative = TryParseInt(ExtractCustomFieldValue(fields, "customfield_11607"));
        record.VotersNegative = TryParseInt(ExtractCustomFieldValue(fields, "customfield_11608"));
        record.VotersAbstain = TryParseInt(ExtractCustomFieldValue(fields, "customfield_11609"));

        string? orgPart = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11806"));
        record.OrganizationalParticipation = orgPart;
        record.OrganizationalParticipationPlain = ToPlainText(orgPart);

        record.Reconciled = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11810"));
        record.RelatedArtifacts = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11300"));
        record.RelatedPages = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11301"));

        return record;
    }

    public static JiraBallotRecord MapBallot(JsonElement issueJson)
    {
        JsonElement fields = issueJson.GetProperty("fields");
        string key = issueJson.GetProperty("key").GetString()!;

        JiraBallotRecord record = new JiraBallotRecord
        {
            Id = JiraBallotRecord.GetIndex(),
            Key = string.Empty,
            ProjectKey = string.Empty,
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
        MapBase(issueJson, record);

        record.VoteBallot = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_10519"));
        record.VoteItem = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_10521"));
        record.ExternalId = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11707"));
        record.Organization = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_10601"));
        record.OrganizationCategory = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11805"));
        record.BallotCategory = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11604"));
        record.VoteSameAs = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11603"));
        record.Specification = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11302"));
        record.Reconciled = CleanFieldValue(ExtractCustomFieldValue(fields, "customfield_11810"));

        // Parse summary for Voter / BallotPackageCode and derive cycle.
        (record.Voter, record.BallotPackageCode) = ParseBallotSummary(record.Summary);
        if (record.BallotPackageCode is not null)
        {
            (record.BallotCycle, _) = SplitBallotCode(record.BallotPackageCode);
        }

        // RelatedFhirIssue from issuelinks.
        List<JiraIssueLinkRecord> links = MapIssueLinks(issueJson, key);
        record.RelatedFhirIssue = PickRelatedFhirKey(links, key);

        return record;
    }

    /// <summary>Extracts comment author username and display name from the JSON issue for a specific comment.</summary>
    public static (string? Username, string? DisplayName) ExtractCommentAuthorRef(
        JsonElement issueJson, JiraCommentRecord comment)
    {
        JsonElement fields = issueJson.GetProperty("fields");
        if (!fields.TryGetProperty("comment", out JsonElement commentField))
            return (null, comment.Author);

        if (!commentField.TryGetProperty("comments", out JsonElement commentArray))
            return (null, comment.Author);

        foreach (JsonElement c in commentArray.EnumerateArray())
        {
            if (c.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object)
            {
                string? displayName = JsonElementHelper.GetString(author, "displayName");
                if (displayName == comment.Author || comment.Author == (JsonElementHelper.GetString(author, "name") ?? "Unknown"))
                {
                    string? username = JsonElementHelper.GetString(author, "name")
                                    ?? JsonElementHelper.GetString(author, "key");
                    return (username, displayName);
                }
            }
        }

        return (null, comment.Author);
    }

    public static List<JiraCommentRecord> MapComments(JsonElement issueJson, string issueKey)
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
