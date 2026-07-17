using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

/// <summary>
/// Handles the <c>github-workgroups</c> verb (canonical HL7 work-group reads
/// served by the GitHub source). Forwards to the orchestrator at
/// <c>/api/v1/github/workgroups*</c>.
/// </summary>
public static class GitHubWorkGroupsHandler
{
    public static async Task<object> HandleAsync(GitHubWorkGroupsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        string url = BuildUrl(request);
        using HttpServiceClient client = new(orchestratorAddr);
        return new { data = await client.GetFromOrchestratorAsync(url, ct) };
    }

    /// <summary>
    /// Builds the orchestrator URL for a <c>github-workgroups</c> request.
    /// Extracted for unit-testing the route/query construction without an HTTP
    /// call.
    /// </summary>
    internal static string BuildUrl(GitHubWorkGroupsRequest request)
    {
        string action = request.Action.ToLowerInvariant();
        switch (action)
        {
            case "list":
                return "/api/v1/github/workgroups";

            case "files":
                return "/api/v1/github/workgroups/files" + RepoWorkGroupQuery(request);

            case "artifacts":
                return "/api/v1/github/workgroups/artifacts" + RepoWorkGroupQuery(request);

            case "unresolved":
                List<string> uq = [];
                if (!string.IsNullOrWhiteSpace(request.Repo)) uq.Add($"repo={Uri.EscapeDataString(request.Repo)}");
                if (request.Limit is int ul) uq.Add($"limit={ul}");
                if (request.Offset is int uo) uq.Add($"offset={uo}");
                return "/api/v1/github/workgroups/unresolved" + ToQuery(uq);

            case "resolve":
                List<string> rq = [];
                if (!string.IsNullOrWhiteSpace(request.Repo)) rq.Add($"repo={Uri.EscapeDataString(request.Repo)}");
                if (!string.IsNullOrWhiteSpace(request.Path)) rq.Add($"path={Uri.EscapeDataString(request.Path)}");
                return "/api/v1/github/workgroups/resolve" + ToQuery(rq);

            default:
                throw new ArgumentException(
                    $"Unknown action: {request.Action}. Valid: list, files, artifacts, unresolved, resolve");
        }
    }

    private static string RepoWorkGroupQuery(GitHubWorkGroupsRequest request)
    {
        List<string> q = [];
        if (!string.IsNullOrWhiteSpace(request.Repo)) q.Add($"repo={Uri.EscapeDataString(request.Repo)}");
        if (!string.IsNullOrWhiteSpace(request.Workgroup)) q.Add($"workgroup={Uri.EscapeDataString(request.Workgroup)}");
        if (request.Limit is int limit) q.Add($"limit={limit}");
        if (request.Offset is int offset) q.Add($"offset={offset}");
        return ToQuery(q);
    }

    private static string ToQuery(List<string> parts)
        => parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
}
