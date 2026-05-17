using System.Reflection;
using FhirAugury.Orchestrator.Controllers.Proxies;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Tests;

/// <summary>
/// Pins the route shape of the GitHub proxy controller after the
/// single-repo passthrough was added (slot 0517-02, Phase 2).
/// Mirrors the spirit of the existing proxy-route conventions: the
/// orchestrator exposes the source's <c>GET repos/{owner}/{name}</c>
/// as a 1:1 passthrough on the same path.
/// </summary>
public class GitHubProxyControllerTests
{
    [Fact]
    public void GetRepo_IsRegisteredAt_ReposOwnerName()
    {
        MethodInfo method = typeof(GitHubProxyController).GetMethod(nameof(GitHubProxyController.GetRepo))
            ?? throw new InvalidOperationException("GetRepo method not found");

        HttpGetAttribute? attribute = method.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("repos/{owner}/{name}", attribute!.Template);
    }

    [Fact]
    public void GetRepo_AcceptsOwnerAndNameParameters()
    {
        MethodInfo method = typeof(GitHubProxyController).GetMethod(nameof(GitHubProxyController.GetRepo))
            ?? throw new InvalidOperationException("GetRepo method not found");

        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal("owner", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("name", parameters[1].Name);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[2].ParameterType);
    }
}
