using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace FhirAugury.McpShared.Tests;

/// <summary>
/// Guardrail: every MCP tool in <c>src/FhirAugury.McpShared/Tools/</c> must reach its data
/// through the orchestrator, i.e. <c>httpClientFactory.CreateClient("orchestrator")</c>. In
/// production only the orchestrator (5150) is HTTP-routable; the source services
/// (jira/zulip/confluence/github) are internal, so a tool that calls
/// <c>CreateClient("jira")</c> (or any other non-orchestrator client) compiles and passes its
/// formatting tests locally but fails in production. This test scans the tool sources for any
/// string-literal <c>CreateClient("…")</c> whose name is not <c>"orchestrator"</c> and fails
/// the build if one exists.
///
/// <para>Known limitation (accepted — it matches the intended guard): the scan is textual. It
/// only catches string-literal client names, so a name held in a variable would slip through,
/// and it could in theory match the pattern inside a comment. This mirrors the existing
/// textual-guardrail style (see <see cref="McpToolNameUniquenessTests"/>).</para>
/// </summary>
public class McpClientRegistrationGuardrailTests
{
    private static readonly Regex CreateClientCall =
        new("""CreateClient\(\s*"(?<name>[^"]*)"\s*\)""", RegexOptions.Compiled);

    [Fact]
    public void AllToolsUseOrchestratorClient()
    {
        string toolsDir = GetToolsDirectory();

        Assert.True(
            Directory.Exists(toolsDir),
            $"Tools source directory not found at '{toolsDir}'. If the Tools folder moved, "
            + "update this guardrail's relative path so it scans the real tool sources.");

        List<string> allMatches = [];
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(toolsDir, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            string fileName = Path.GetFileName(file);

            foreach (Match match in CreateClientCall.Matches(source))
            {
                string name = match.Groups["name"].Value;
                string entry = $"{fileName}: CreateClient(\"{name}\")";
                allMatches.Add(entry);

                if (!string.Equals(name, "orchestrator", StringComparison.Ordinal))
                    offenders.Add(entry);
            }
        }

        // Sanity check: if this is empty the scan is broken (bad path or regex) and would
        // otherwise pass vacuously. Mirrors the Assert.NotEmpty guard in McpToolNameUniquenessTests.
        Assert.NotEmpty(allMatches);

        Assert.True(
            offenders.Count == 0,
            "Every MCP tool must use CreateClient(\"orchestrator\"); source services are not "
            + "routable in production. Offending call(s):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Resolves <c>src/FhirAugury.McpShared/Tools</c> relative to this test file via
    /// <see cref="CallerFilePathAttribute"/>, so resolution is independent of the current
    /// working directory or bin/output layout.
    /// </summary>
    private static string GetToolsDirectory([CallerFilePath] string thisFilePath = "")
    {
        string testDir = Path.GetDirectoryName(thisFilePath)!;
        return Path.GetFullPath(
            Path.Combine(testDir, "..", "..", "src", "FhirAugury.McpShared", "Tools"));
    }
}
