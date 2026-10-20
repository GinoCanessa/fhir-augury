using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

public static class PreparedTicketSnapshotSchemaV1
{
    public const int Version = AuthoringSnapshotSchemaV1.Version;
    public const string ProcessorKind = "jira-fhir";

    public static AuthoringSnapshotSchemaCatalog Catalog { get; } = new(
        Version,
        [
            .. AuthoringSnapshotSchemaV1.CoreTables,
            new(
                "prepared_tickets",
                [
                    "RowId", "Id", "Key", "RequestSummary", "CommentSummary",
                    "LinkedTicketSummary", "RelatedTicketSummary",
                    "RelatedZulipSummary", "RelatedGitHubSummary",
                    "ExistingProposed", "ProposalA", "ProposalAJustification",
                    "ProposalAImpact", "ProposalB", "ProposalBJustification",
                    "ProposalBImpact", "ProposalC", "ProposalCJustification",
                    "Recommendation", "RecommendationJustification", "SavedAt",
                ]),
            new(
                "prepared_ticket_repos",
                [
                    "RowId", "Id", "TicketKey", "Repo", "RepoCategory",
                    "Justification",
                ]),
            new(
                "prepared_ticket_related_jira",
                [
                    "RowId", "Id", "TicketKey", "AssociatedTicketKey",
                    "LinkType", "Justification",
                ]),
            new(
                "prepared_ticket_related_zulip",
                [
                    "RowId", "Id", "TicketKey", "ZulipThreadId",
                    "Justification",
                ]),
            new(
                "prepared_ticket_related_github",
                [
                    "RowId", "Id", "TicketKey", "GitHubItemId",
                    "Justification",
                ]),
            new(
                "prepared_ticket_hydration",
                [
                    "RowId", "Id", "TicketKey", "Priority", "Resolution",
                    "ResolutionDescriptionPlain", "Specification",
                    "RaisedInVersion", "SelectedBallot", "ChangeCategory",
                    "Impact", "Labels", "CommentCount", "DescriptionPlain",
                    "DescriptionHtml", "ResolutionDescriptionHtml", "Reporter",
                    "CreatedAt", "RelatedArtifactsRaw", "RelatedPagesRaw",
                    "HydratedAt", "HydrationStatus", "HydrationReason",
                ]),
            new(
                "prepared_jira_hydration",
                [
                    "RowId", "Id", "TicketKey", "JiraKey", "Title", "Status",
                    "Type", "Priority", "Resolution",
                    "ResolutionDescriptionPlain", "WorkGroup", "WorkGroupClean",
                    "Specification", "UpdatedAt", "Url", "DescriptionHtml",
                    "ResolutionDescriptionHtml", "Reporter", "CreatedAt",
                    "RelatedArtifactsRaw", "RelatedPagesRaw", "HydratedAt",
                    "HydrationStatus", "HydrationReason",
                ]),
            new(
                "prepared_zulip_hydration",
                [
                    "RowId", "Id", "TicketKey", "ZulipThreadId", "StreamId",
                    "StreamName", "Topic", "MessageCount", "FirstMessageAt",
                    "LastMessageAt", "FirstMessageExcerpt", "Url", "HydratedAt",
                    "HydrationStatus", "HydrationReason",
                ]),
            new(
                "prepared_github_hydration",
                [
                    "RowId", "Id", "TicketKey", "GitHubItemId", "Owner", "Repo",
                    "Number", "Path", "Title", "State", "IsPullRequest",
                    "Labels", "UpdatedAt", "Url", "HydratedAt",
                    "HydrationStatus", "HydrationReason",
                ]),
            new(
                "prepared_repo_hydration",
                [
                    "RowId", "Id", "TicketKey", "Repo", "Description",
                    "WorkGroup", "Specification", "CategoryDetail", "Url",
                    "HydratedAt", "HydrationStatus", "HydrationReason",
                ]),
            new(
                "prepared_ticket_jira_xref",
                ["RowId", "Id", "TicketKey", "JiraKey", "Source"]),
            new(
                "prepared_ticket_jira_content",
                [
                    "RowId", "TicketKey", "DescriptionHtml",
                    "ResolutionDescriptionHtml",
                ]),
            new(
                "prepared_ticket_artifacts",
                ["RowId", "TicketKey", "Value"]),
            new(
                "prepared_ticket_pages",
                ["RowId", "TicketKey", "Value"]),
            new(
                "prepared_ticket_topics",
                [
                    "RowId", "Id", "WorkGroupClean", "WorkGroupDisplay",
                    "Specification", "Type", "ShortDescription",
                    "LongerDescription", "RenderOrderHint", "SavedAt",
                ]),
            new(
                "prepared_ticket_topic_groups",
                [
                    "RowId", "Id", "TopicRowId", "FirstTicketKey", "Rationale",
                    "OrderInTopic", "SavedAt",
                ]),
            new(
                "prepared_ticket_topic_members",
                [
                    "RowId", "Id", "TopicRowId", "TopicGroupRowId", "TicketKey",
                    "OrderInContainer",
                ]),
            new(
                "prepared_ticket_partition_receipts",
                [
                    "RunId", "StageId", "PartitionKey", "InputFingerprint",
                    "TopicRows", "TopicGroupRows", "MemberRows", "PersistedAt",
                ]),
            new(
                "jira_review_workgroups",
                ["RowId", "Code", "Name", "NameClean", "UpdatedAt"]),
        ],
        [
            "prepared_tickets",
            "prepared_ticket_repos",
            "prepared_ticket_related_jira",
            "prepared_ticket_related_zulip",
            "prepared_ticket_related_github",
            "prepared_ticket_hydration",
            "prepared_jira_hydration",
            "prepared_ticket_jira_content",
            "prepared_ticket_artifacts",
            "prepared_ticket_pages",
            "prepared_ticket_topics",
            "prepared_ticket_topic_groups",
            "prepared_ticket_topic_members",
            "prepared_ticket_partition_receipts",
            "jira_review_workgroups",
        ]);

    public static IReadOnlyList<AuthoringSnapshotTableSchema> Tables =>
        Catalog.Tables;

    public static IReadOnlyList<string> CountedTables =>
        Catalog.CountedTables;
}
