using System.Text.Json;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 2 (slot 0625-02): both repo mappers persist the repository default
/// branch so the deterministic primary-PR rule has a base-branch anchor that
/// survives a full sync.
/// </summary>
public class GitHubRepoDefaultBranchTests
{
    [Fact]
    public void GhCliMapRepo_PopulatesDefaultBranch_FromDefaultBranchRef()
    {
        string json = """
        {
            "name": "fhir",
            "nameWithOwner": "HL7/fhir",
            "description": "FHIR specification",
            "hasIssuesEnabled": true,
            "owner": { "login": "HL7" },
            "defaultBranchRef": { "name": "master" }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        GitHubRepoRecord record = GhCliIssueMapper.MapRepo(doc.RootElement);

        Assert.Equal("master", record.DefaultBranch);
    }

    [Fact]
    public void GhCliMapRepo_NullDefaultBranch_WhenRefMissing()
    {
        string json = """
        {
            "name": "fhir",
            "nameWithOwner": "HL7/fhir",
            "description": null,
            "hasIssuesEnabled": true,
            "owner": { "login": "HL7" }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        GitHubRepoRecord record = GhCliIssueMapper.MapRepo(doc.RootElement);

        Assert.Null(record.DefaultBranch);
    }

    [Fact]
    public void RestMapRepo_PopulatesDefaultBranch_FromDefaultBranchField()
    {
        string json = """
        {
            "name": "fhir",
            "full_name": "HL7/fhir",
            "description": "FHIR specification",
            "has_issues": true,
            "owner": { "login": "HL7" },
            "default_branch": "main"
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        GitHubRepoRecord record = GitHubIssueMapper.MapRepo(doc.RootElement);

        Assert.Equal("main", record.DefaultBranch);
    }
}
