using FhirAugury.Source.Jira.Database.Records;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Maps Jira issue <c>Status</c> strings to the corresponding
/// <see cref="JiraIndexWorkGroupRecord"/> bucket column name. Any status that
/// is not explicitly recognized falls into <see cref="Other"/> so we never
/// silently drop a count.
/// </summary>
public static class Hl7JiraStatusBuckets
{
    public const string Other = nameof(JiraIndexWorkGroupRecord.IssueCountOther);

    public static string MapToBucketColumn(string? status) => status switch
    {
        "Submitted"                  => nameof(JiraIndexWorkGroupRecord.IssueCountSubmitted),
        "Triaged"                    => nameof(JiraIndexWorkGroupRecord.IssueCountTriaged),
        "Waiting for Input"          => nameof(JiraIndexWorkGroupRecord.IssueCountWaitingForInput),
        "Resolved - No Change"       => nameof(JiraIndexWorkGroupRecord.IssueCountNoChange),
        "Resolved - Change Required" => nameof(JiraIndexWorkGroupRecord.IssueCountChangeRequired),
        "Published"                  => nameof(JiraIndexWorkGroupRecord.IssueCountPublished),
        "Applied"                    => nameof(JiraIndexWorkGroupRecord.IssueCountApplied),
        "Duplicate"                  => nameof(JiraIndexWorkGroupRecord.IssueCountDuplicate),
        "Closed"                     => nameof(JiraIndexWorkGroupRecord.IssueCountClosed),
        "Balloted"                   => nameof(JiraIndexWorkGroupRecord.IssueCountBalloted),
        "Withdrawn"                  => nameof(JiraIndexWorkGroupRecord.IssueCountWithdrawn),
        "Deferred"                   => nameof(JiraIndexWorkGroupRecord.IssueCountDeferred),
        _                            => Other,
    };
}
