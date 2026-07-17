using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_tickets")]
[LdgSQLiteIndex(nameof(Recommendation))]
[LdgSQLiteIndex(nameof(ProposalAImpact))]
[LdgSQLiteIndex(nameof(ProposalBImpact))]
public partial record class PreparedTicketRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [LdgSQLiteUnique]
    public required string Key { get; set; }
    public required string RequestSummary { get; set; }
    public required string CommentSummary { get; set; }
    public required string LinkedTicketSummary { get; set; }
    public required string RelatedTicketSummary { get; set; }
    public required string RelatedZulipSummary { get; set; }
    public required string RelatedGitHubSummary { get; set; }
    public required string ExistingProposed { get; set; }
    public required string ProposalA { get; set; }
    public required string ProposalAJustification { get; set; }
    public required string ProposalAImpact { get; set; }
    public required string ProposalB { get; set; }
    public required string ProposalBJustification { get; set; }
    public required string ProposalBImpact { get; set; }
    public required string ProposalC { get; set; }
    public required string ProposalCJustification { get; set; }
    public required string Recommendation { get; set; }
    public required string RecommendationJustification { get; set; }
    public required DateTimeOffset SavedAt { get; set; }
}

