using System.Net;
using System.Text;
using NSubstitute;

namespace FhirAugury.McpShared.Tests;

/// <summary>
/// Test infrastructure for mocking HTTP calls made by MCP tools.
/// </summary>
internal static class McpTestHelper
{
    /// <summary>
    /// Creates a mock <see cref="IHttpClientFactory"/> that returns an <see cref="HttpClient"/>
    /// backed by a <see cref="MockHttpHandler"/> returning the given JSON for any request.
    /// </summary>
    internal static IHttpClientFactory CreateFactory(string clientName, string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        MockHttpHandler handler = new(responseJson, statusCode);
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(clientName).Returns(client);
        return factory;
    }

    /// <summary>
    /// Creates a mock <see cref="IHttpClientFactory"/> that serves multiple named HTTP clients,
    /// each returning its own preconfigured JSON response.
    /// </summary>
    internal static IHttpClientFactory CreateFactory(params (string Name, string ResponseJson)[] clients)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        foreach ((string name, string json) in clients)
        {
            MockHttpHandler handler = new(json);
            HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
            factory.CreateClient(name).Returns(client);
        }
        return factory;
    }
}

/// <summary>
/// A mock <see cref="HttpMessageHandler"/> that returns a preconfigured JSON response
/// for every request and records each request for later verification.
/// </summary>
internal class MockHttpHandler : HttpMessageHandler
{
    private readonly string _responseJson;
    private readonly HttpStatusCode _statusCode;

    public HttpRequestMessage? LastRequest { get; private set; }
    public List<HttpRequestMessage> AllRequests { get; } = [];

    public MockHttpHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseJson = responseJson;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        AllRequests.Add(request);

        HttpResponseMessage response = new(_statusCode)
        {
            Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}
