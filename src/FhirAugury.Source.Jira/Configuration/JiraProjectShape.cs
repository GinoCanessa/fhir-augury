namespace FhirAugury.Source.Jira.Configuration;

/// <summary>
/// Discriminator that selects which typed table a Jira project's items
/// are routed into during parsing/upsert. Defaults to
/// <see cref="FhirChangeRequest"/> for unknown projects.
/// </summary>
public enum JiraProjectShape
{
    /// <summary>FHIR-style change request (FHIR, FHIR-I, GCR, HTA, TSC, UP, UPSM, …) → <c>jira_issues</c>.</summary>
    FhirChangeRequest,

    /// <summary>Project Scope Statement (PSS) → <c>jira_pss</c>.</summary>
    ProjectScopeStatement,

    /// <summary>Ballot Definition (BALDEF) → <c>jira_baldef</c>.</summary>
    BallotDefinition,

    /// <summary>Ballot vote tracking row (BALLOT) → <c>jira_ballot</c>.</summary>
    BallotVote,
}
