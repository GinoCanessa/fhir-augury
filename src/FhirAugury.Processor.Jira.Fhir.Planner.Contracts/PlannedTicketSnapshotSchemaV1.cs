using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Contracts;

public static class PlannedTicketSnapshotSchemaV1
{
    public const int Version = AuthoringSnapshotSchemaV1.Version;
    public const string ProcessorKind = "jira-fhir";

    public static AuthoringSnapshotSchemaCatalog Catalog { get; } = new(
        Version,
        [
            .. AuthoringSnapshotSchemaV1.CoreTables,
            new(
                "planned_tickets",
                [
                    "RowId", "Id", "Key", "Resolution", "ResolutionSummary",
                    "FeatureProposal", "DesignRationale", "SavedAt",
                ]),
            new(
                "planned_ticket_repos",
                [
                    "RowId", "Id", "IssueKey", "RepoKey", "RepoRevision",
                    "Justification",
                ]),
            new(
                "planned_ticket_repo_changes",
                [
                    "RowId", "Id", "IssueKey", "TicketRepoId", "RepoKey",
                    "ChangeSequence", "FilePath", "ChangeTitle",
                    "ChangeDescription", "SourceLineStart", "SourceLineEnd",
                    "ReplacementLines", "Reason",
                ]),
            new(
                "planned_ticket_repo_impacts",
                [
                    "RowId", "Id", "IssueKey", "TicketRepoId", "RepoKey",
                    "TicketRepoChangeId", "AffectedFilePath", "HowAffected",
                ]),
            new(
                "planned_ticket_change_validations",
                [
                    "RowId", "Id", "IssueKey", "TicketRepoId", "RepoKey",
                    "ValidationSequence", "Action",
                ]),
            new(
                "planned_ticket_testing_considerations",
                [
                    "RowId", "Id", "IssueKey", "TicketRepoId", "RepoKey",
                    "ConsiderationSequence", "Consideration",
                ]),
            new(
                "planned_ticket_open_questions",
                [
                    "RowId", "Id", "IssueKey", "TicketRepoId", "RepoKey",
                    "QuestionSequence", "Question",
                ]),
            new(
                "planned_ticket_related_jira",
                ["RowId", "IssueKey", "JiraKey", "Source"]),
            new(
                "planned_ticket_related_zulip",
                ["RowId", "IssueKey", "ZulipThreadId"]),
            new(
                "planned_ticket_related_github",
                ["RowId", "IssueKey", "GitHubItemId"]),
            new(
                "planned_ticket_hydration",
                [
                    "RowId", "IssueKey", "Priority", "Resolution",
                    "ResolutionDescriptionPlain", "Specification",
                    "RaisedInVersion", "SelectedBallot", "ChangeCategory",
                    "Impact", "Labels", "CommentCount", "DescriptionPlain",
                    "DescriptionHtml", "ResolutionDescriptionHtml", "Reporter",
                    "CreatedAt", "RelatedArtifactsRaw", "RelatedPagesRaw",
                    "HydratedAt", "HydrationStatus", "HydrationReason",
                ]),
            new(
                "planned_jira_hydration",
                [
                    "RowId", "IssueKey", "JiraKey", "Title", "Status", "Type",
                    "Priority", "Resolution", "ResolutionDescriptionPlain",
                    "WorkGroup", "WorkGroupClean", "Specification", "UpdatedAt",
                    "Url", "DescriptionHtml", "ResolutionDescriptionHtml",
                    "Reporter", "CreatedAt", "RelatedArtifactsRaw",
                    "RelatedPagesRaw", "HydratedAt", "HydrationStatus",
                    "HydrationReason",
                ]),
            new(
                "planned_zulip_hydration",
                [
                    "RowId", "IssueKey", "ZulipThreadId", "StreamId",
                    "StreamName", "Topic", "MessageCount", "FirstMessageAt",
                    "LastMessageAt", "FirstMessageExcerpt", "Url", "HydratedAt",
                    "HydrationStatus", "HydrationReason",
                ]),
            new(
                "planned_github_hydration",
                [
                    "RowId", "IssueKey", "GitHubItemId", "Owner", "Repo",
                    "Number", "Path", "Title", "State", "IsPullRequest",
                    "Labels", "UpdatedAt", "Url", "HydratedAt",
                    "HydrationStatus", "HydrationReason",
                ]),
            new(
                "planned_repo_hydration",
                [
                    "RowId", "IssueKey", "RepoKey", "Description", "WorkGroup",
                    "Specification", "CategoryDetail", "Url", "HydratedAt",
                    "HydrationStatus", "HydrationReason",
                ]),
            new(
                "planned_ticket_jira_xref",
                ["RowId", "IssueKey", "JiraKey", "Source"]),
            new(
                "planned_ticket_jira_content",
                [
                    "RowId", "TicketKey", "DescriptionHtml",
                    "ResolutionDescriptionHtml",
                ]),
            new(
                "planned_ticket_topics",
                [
                    "RowId", "Id", "WorkGroupClean", "WorkGroupDisplay",
                    "Specification", "Type", "ShortDescription",
                    "LongerDescription", "RenderOrderHint", "SavedAt",
                ]),
            new(
                "planned_ticket_topic_groups",
                [
                    "RowId", "Id", "TopicRowId", "FirstTicketKey", "Rationale",
                    "OrderInTopic", "SavedAt",
                ]),
            new(
                "planned_ticket_topic_members",
                [
                    "RowId", "Id", "TopicRowId", "TopicGroupRowId", "TicketKey",
                    "OrderInContainer",
                ]),
            new(
                "planned_ticket_topic_repos",
                ["RowId", "Id", "TopicRowId", "RepoKey", "OrderInTopic"]),
            new(
                "planned_ticket_partition_receipts",
                [
                    "RunId", "StageId", "PartitionKey", "InputFingerprint",
                    "TopicRows", "TopicGroupRows", "MemberRows", "PersistedAt",
                ]),
            new(
                "jira_review_workgroups",
                ["RowId", "Code", "Name", "NameClean", "UpdatedAt"]),
        ],
        [
            "planned_tickets",
            "planned_ticket_repos",
            "planned_ticket_repo_changes",
            "planned_ticket_repo_impacts",
            "planned_ticket_change_validations",
            "planned_ticket_testing_considerations",
            "planned_ticket_open_questions",
            "planned_ticket_hydration",
            "planned_jira_hydration",
            "planned_ticket_jira_content",
            "planned_ticket_topics",
            "planned_ticket_topic_groups",
            "planned_ticket_topic_members",
            "planned_ticket_topic_repos",
            "planned_ticket_partition_receipts",
            "jira_review_workgroups",
        ]);

    public static IReadOnlyList<AuthoringSnapshotTableSchema> Tables =>
        Catalog.Tables;

    public static IReadOnlyList<string> CountedTables =>
        Catalog.CountedTables;
}
