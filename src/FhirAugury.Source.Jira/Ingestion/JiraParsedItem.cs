using FhirAugury.Source.Jira.Database.Records;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Discriminated parsed-issue payload produced by <see cref="JiraXmlParser"/>
/// or the JSON parser path in <see cref="JiraSource"/>. The concrete subtype
/// determines which typed Jira table the record lands in
/// (<c>jira_issues</c>, <c>jira_pss</c>, <c>jira_baldef</c>, <c>jira_ballot</c>).
/// All shapes carry the same side-table payload (comments, in-persons, links);
/// related-issue keys are FHIR-only.
/// </summary>
public abstract record JiraParsedItem
{
    /// <summary>Base columns of the parsed record (Key, ProjectKey, etc.).</summary>
    public abstract JiraIssueBaseRecord BaseRecord { get; }

    public string Key => BaseRecord.Key;
    public string ProjectKey => BaseRecord.ProjectKey;

    public List<JiraCommentRecord> Comments { get; init; } = [];
    public JiraXmlUserInfo UserInfo { get; init; } = new();
    public List<JiraInPersonRef> InPersons { get; init; } = [];
    public List<JiraIssueLinkRecord> Links { get; init; } = [];

    /// <summary>FHIR-shaped records expose related-issue keys for the
    /// <c>jira_issue_related</c> side table. Other shapes return empty.</summary>
    public virtual IReadOnlyList<string> RelatedIssueKeys => [];

    /// <summary>FHIR-only vote attribution; returns null on other shapes.</summary>
    public virtual string? VoteMover => null;

    /// <summary>FHIR-only vote attribution; returns null on other shapes.</summary>
    public virtual string? VoteSeconder => null;
}

/// <summary>FHIR change-request issue → <c>jira_issues</c>.</summary>
public sealed record JiraParsedFhirIssue : JiraParsedItem
{
    public required JiraIssueRecord Record { get; init; }
    public override JiraIssueBaseRecord BaseRecord => Record;
    public override string? VoteMover => Record.VoteMover;
    public override string? VoteSeconder => Record.VoteSeconder;

    public override IReadOnlyList<string> RelatedIssueKeys =>
        string.IsNullOrWhiteSpace(Record.RelatedIssues)
            ? []
            : Record.RelatedIssues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Project Scope Statement → <c>jira_pss</c>.</summary>
public sealed record JiraParsedProjectScopeStatement : JiraParsedItem
{
    public required JiraProjectScopeStatementRecord Record { get; init; }
    public override JiraIssueBaseRecord BaseRecord => Record;
}

/// <summary>Ballot Definition → <c>jira_baldef</c>.</summary>
public sealed record JiraParsedBaldef : JiraParsedItem
{
    public required JiraBaldefRecord Record { get; init; }
    public override JiraIssueBaseRecord BaseRecord => Record;
}

/// <summary>Ballot vote-tracking row → <c>jira_ballot</c>.</summary>
public sealed record JiraParsedBallot : JiraParsedItem
{
    public required JiraBallotRecord Record { get; init; }
    public override JiraIssueBaseRecord BaseRecord => Record;
}
