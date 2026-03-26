using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;

namespace FhirAugury.Common.Tests;

public class GitHubReferenceExtractorTests
{
    [Fact]
    public void ExtractsFullIssueUrl()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "page", "1", "See https://github.com/HL7/fhir/issues/4006 here");
        Assert.Single(refs);
        Assert.Equal("HL7/fhir", refs[0].RepoFullName);
        Assert.Equal(4006, refs[0].IssueNumber);
    }

    [Fact]
    public void ExtractsPrUrl()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "page", "1", "See https://github.com/HL7/fhir/pull/100 here");
        Assert.Single(refs);
        Assert.Equal("HL7/fhir", refs[0].RepoFullName);
        Assert.Equal(100, refs[0].IssueNumber);
    }

    [Fact]
    public void ExtractsShortRef()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "page", "1", "As noted in HL7/fhir#4006");
        Assert.Single(refs);
        Assert.Equal("HL7/fhir", refs[0].RepoFullName);
        Assert.Equal(4006, refs[0].IssueNumber);
    }

    [Fact]
    public void DeduplicatesUrlAndShortRef()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "page", "1",
            "https://github.com/HL7/fhir/issues/4006 and HL7/fhir#4006");
        Assert.Single(refs);
    }

    [Fact]
    public void DifferentReposReturnMultiple()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "page", "1",
            "https://github.com/HL7/fhir/issues/1 and https://github.com/HL7/US-Core/issues/2");
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void TargetIdFormat()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "page", "1", "https://github.com/HL7/fhir/issues/4006");
        Assert.Equal("HL7/fhir#4006", refs[0].TargetId);
    }

    [Fact]
    public void TargetTypeIsGitHub()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "page", "1", "https://github.com/HL7/fhir/issues/1");
        Assert.Equal("github", refs[0].TargetType);
    }

    [Fact]
    public void EmptyTextReturnsEmpty()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences("page", "1", "");
        Assert.Empty(refs);
    }

    [Fact]
    public void SourceFieldsSet()
    {
        List<GitHubXRefRecord> refs = GitHubReferenceExtractor.GetReferences(
            "message", "42", "https://github.com/HL7/fhir/issues/1");
        Assert.Equal("message", refs[0].SourceType);
        Assert.Equal("42", refs[0].SourceId);
        Assert.Equal("mentions", refs[0].LinkType);
    }
}
