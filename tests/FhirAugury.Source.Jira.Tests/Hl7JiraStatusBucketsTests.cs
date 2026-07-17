using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

public class Hl7JiraStatusBucketsTests
{
    [Theory]
    [InlineData("Submitted",                  nameof(JiraIndexWorkGroupRecord.IssueCountSubmitted))]
    [InlineData("Triaged",                    nameof(JiraIndexWorkGroupRecord.IssueCountTriaged))]
    [InlineData("Waiting for Input",          nameof(JiraIndexWorkGroupRecord.IssueCountWaitingForInput))]
    [InlineData("Resolved - No Change",       nameof(JiraIndexWorkGroupRecord.IssueCountNoChange))]
    [InlineData("Resolved - Change Required", nameof(JiraIndexWorkGroupRecord.IssueCountChangeRequired))]
    [InlineData("Published",                  nameof(JiraIndexWorkGroupRecord.IssueCountPublished))]
    [InlineData("Applied",                    nameof(JiraIndexWorkGroupRecord.IssueCountApplied))]
    [InlineData("Duplicate",                  nameof(JiraIndexWorkGroupRecord.IssueCountDuplicate))]
    [InlineData("Closed",                     nameof(JiraIndexWorkGroupRecord.IssueCountClosed))]
    [InlineData("Balloted",                   nameof(JiraIndexWorkGroupRecord.IssueCountBalloted))]
    [InlineData("Withdrawn",                  nameof(JiraIndexWorkGroupRecord.IssueCountWithdrawn))]
    [InlineData("Deferred",                   nameof(JiraIndexWorkGroupRecord.IssueCountDeferred))]
    public void MapToBucketColumn_KnownStatus_ReturnsExpectedColumn(string status, string expected)
    {
        Assert.Equal(expected, Hl7JiraStatusBuckets.MapToBucketColumn(status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FooBar")]
    [InlineData("submitted")] // case-sensitive: lower-case does not match
    [InlineData("Resolved")]
    public void MapToBucketColumn_UnknownStatus_FallsBackToOther(string? status)
    {
        Assert.Equal(Hl7JiraStatusBuckets.Other, Hl7JiraStatusBuckets.MapToBucketColumn(status));
        Assert.Equal(nameof(JiraIndexWorkGroupRecord.IssueCountOther), Hl7JiraStatusBuckets.Other);
    }
}
