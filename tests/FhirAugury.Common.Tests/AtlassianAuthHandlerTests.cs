using System.Net;
using FhirAugury.Common.Http;

namespace FhirAugury.Common.Tests;

public class AtlassianAuthHandlerTests
{
    /// <summary>Concrete handler exposing the abstract credential members for testing.</summary>
    private sealed class TestAuthHandler : AtlassianAuthHandler
    {
        protected override string AuthMode { get; }
        protected override string? Email { get; }
        protected override string? Username { get; }
        protected override string? ApiToken { get; }
        protected override string? Cookie { get; }

        internal TestAuthHandler(
            string authMode,
            string? email = null,
            string? username = null,
            string? apiToken = null,
            string? cookie = null)
        {
            AuthMode = authMode;
            Email = email;
            Username = username;
            ApiToken = apiToken;
            Cookie = cookie;
            InnerHandler = new CapturingHandler();
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
    }

    private static async Task<HttpRequestMessage> SendAsync(AtlassianAuthHandler handler)
    {
        using HttpClient client = new(handler);
        HttpResponseMessage response = await client.GetAsync("https://jira.example.org/rest/api/2/serverInfo");
        return response.RequestMessage!;
    }

    [Theory]
    [InlineData("pat")]
    [InlineData("bearer")]
    public async Task PatAndBearerModes_SendTokenAsBearer(string authMode)
    {
        HttpRequestMessage request = await SendAsync(new TestAuthHandler(authMode, apiToken: "token-123"));

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("token-123", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task BearerMode_WithoutToken_SendsNoAuthorization()
    {
        HttpRequestMessage request = await SendAsync(new TestAuthHandler("bearer"));

        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("apitoken")]
    public async Task BasicModes_SendBase64EmailAndToken(string authMode)
    {
        HttpRequestMessage request = await SendAsync(
            new TestAuthHandler(authMode, email: "user@example.org", apiToken: "token-123"));

        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(
            Convert.ToBase64String("user@example.org:token-123"u8.ToArray()),
            request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task BasicMode_FallsBackToUsernameWhenEmailIsEmpty()
    {
        HttpRequestMessage request = await SendAsync(
            new TestAuthHandler("basic", username: "jdoe", apiToken: "token-123"));

        Assert.Equal(
            Convert.ToBase64String("jdoe:token-123"u8.ToArray()),
            request.Headers.Authorization?.Parameter);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("none")]
    [InlineData("")]
    public async Task UnrecognisedMode_SendsNoCredentialsAtAll(string authMode)
    {
        // The switch has no default branch, so an unknown mode falls through
        // and the request goes out unauthenticated rather than failing.
        HttpRequestMessage request = await SendAsync(
            new TestAuthHandler(authMode, email: "user@example.org", apiToken: "token-123", cookie: "JSESSIONID=abc"));

        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("cookie"));
    }

    [Fact]
    public async Task CookieMode_SendsCookieHeaderAndNoAuthorization()
    {
        HttpRequestMessage request = await SendAsync(
            new TestAuthHandler("cookie", cookie: "JSESSIONID=abc"));

        Assert.Equal("JSESSIONID=abc", Assert.Single(request.Headers.GetValues("cookie")));
        Assert.Null(request.Headers.Authorization);
    }
}
