using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 5 (slot 0826-01): the three separate <c>gh pr view</c> calls collapse into one
/// combined <c>--json</c> request. This is the regression guard for the quota win — a full
/// <c>HL7/fhir</c> backfill drops from ~12,855 GraphQL points to ~4,285, inside a single
/// 5,000-point window.
/// </summary>
public class GhCliPrDetailArgsTests
{
    [Fact]
    public void BuildPrDetailArgs_RequestsAllFiveFieldsInOneCall()
    {
        string args = GitHubCliProvider.BuildPrDetailArgs(3000, "--repo HL7/fhir");

        Assert.Contains("--json comments,reviews,commits,baseRefName,mergedAt", args);

        string[] fields = GitHubCliProvider.PrDetailFields.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, fields.Length);
        Assert.Equal(["comments", "reviews", "commits", "baseRefName", "mergedAt"], fields);
    }

    [Fact]
    public void BuildPrDetailArgs_IncludesRepoArgs()
    {
        string args = GitHubCliProvider.BuildPrDetailArgs(42, "--repo HL7/fhir --hostname example.org");

        Assert.StartsWith("pr view 42 ", args);
        Assert.Contains("--repo HL7/fhir", args);
        Assert.Contains("--hostname example.org", args);
    }
}
