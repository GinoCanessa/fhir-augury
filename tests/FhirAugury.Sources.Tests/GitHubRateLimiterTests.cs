using FhirAugury.Sources.GitHub;

namespace FhirAugury.Sources.Tests;

public class GitHubRateLimiterTests
{
    [Fact]
    public void CreateHttpClient_SetsHeaders()
    {
        var options = new GitHubSourceOptions
        {
            PersonalAccessToken = "test-token",
        };

        using var client = GitHubRateLimiter.CreateHttpClient(options);

        Assert.Contains(client.DefaultRequestHeaders.GetValues("accept"), v => v.Contains("github"));
        Assert.Contains(client.DefaultRequestHeaders.GetValues("user-agent"), v => v.Contains("FhirAugury"));
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("test-token", client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void CreateHttpClient_NoToken_NoAuthHeader()
    {
        var options = new GitHubSourceOptions();

        using var client = GitHubRateLimiter.CreateHttpClient(options);

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void CreateHttpClient_SetsTimeout()
    {
        var options = new GitHubSourceOptions();

        using var client = GitHubRateLimiter.CreateHttpClient(options);

        Assert.Equal(TimeSpan.FromMinutes(5), client.Timeout);
    }
}
