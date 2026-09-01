using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Dispatch.Handlers;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Tests;

public class GitHubWorkGroupsDispatchTests
{
    [Fact]
    public void KnownCommands_IncludesGitHubWorkGroups()
    {
        Assert.Contains("github-workgroups", CommandDispatcher.KnownCommands);
    }

    [Fact]
    public async Task GitHubReposGet_MissingOwnerName_RoutesToHandlerAndErrors()
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            """{"command":"github-repos","action":"get"}""");

        Assert.False(env.Success);
        Assert.Contains("owner", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUrl_List_NoQuery()
    {
        string url = GitHubWorkGroupsHandler.BuildUrl(new GitHubWorkGroupsRequest { Action = "list" });
        Assert.Equal("/api/v1/github/workgroups", url);
    }

    [Fact]
    public void BuildUrl_Files_EscapesRepoAndWorkgroup()
    {
        string url = GitHubWorkGroupsHandler.BuildUrl(new GitHubWorkGroupsRequest
        {
            Action = "files",
            Repo = "HL7/fhir",
            Workgroup = "fhir-i",
            Limit = 25,
        });

        Assert.Equal("/api/v1/github/workgroups/files?repo=HL7%2Ffhir&workgroup=fhir-i&limit=25", url);
    }

    [Fact]
    public void BuildUrl_Artifacts_EscapesRepoAndWorkgroup()
    {
        string url = GitHubWorkGroupsHandler.BuildUrl(new GitHubWorkGroupsRequest
        {
            Action = "artifacts",
            Repo = "HL7/fhir",
            Workgroup = "fhir-i",
        });

        Assert.Equal("/api/v1/github/workgroups/artifacts?repo=HL7%2Ffhir&workgroup=fhir-i", url);
    }

    [Fact]
    public void BuildUrl_Unresolved_OptionalRepo()
    {
        string url = GitHubWorkGroupsHandler.BuildUrl(new GitHubWorkGroupsRequest
        {
            Action = "unresolved",
            Repo = "HL7/fhir",
            Offset = 10,
        });

        Assert.Equal("/api/v1/github/workgroups/unresolved?repo=HL7%2Ffhir&offset=10", url);
    }

    [Fact]
    public void BuildUrl_Resolve_RepoAndPath()
    {
        string url = GitHubWorkGroupsHandler.BuildUrl(new GitHubWorkGroupsRequest
        {
            Action = "resolve",
            Repo = "HL7/fhir",
            Path = "source/patient/patient-introduction.md",
        });

        Assert.StartsWith("/api/v1/github/workgroups/resolve?", url);
        Assert.Contains("repo=HL7%2Ffhir", url);
        Assert.Contains("path=source%2Fpatient%2Fpatient-introduction.md", url);
    }

    [Fact]
    public void BuildUrl_UnknownAction_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            GitHubWorkGroupsHandler.BuildUrl(new GitHubWorkGroupsRequest { Action = "frobnicate" }));
    }
}
