using System.Reflection;
using FhirAugury.McpShared.Tools;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tests;

/// <summary>
/// Guardrail: every MCP tool name discovered by <c>WithToolsFromAssembly</c> must be unique.
/// The MCP SDK registers duplicates first-wins (<c>ConcurrentDictionary.TryAdd</c>), so a
/// colliding name silently shadows all but one implementation with no startup error. This
/// test converts such a collision into a visible failure.
/// </summary>
public class McpToolNameUniquenessTests
{
    [Fact]
    public void AllMcpToolNames_AreUnique()
    {
        List<string> toolNames = DiscoverToolNames();

        Assert.NotEmpty(toolNames);

        List<string> duplicates = toolNames
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Duplicate MCP tool name(s) found: {string.Join(", ", duplicates)}. "
            + "Each [McpServerTool] name must be unique across [McpServerToolType] classes; "
            + "the MCP SDK registers duplicates first-wins, silently shadowing the rest.");
    }

    private static List<string> DiscoverToolNames()
    {
        Assembly assembly = typeof(UnifiedTools).Assembly;
        List<string> names = [];

        foreach (Type type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
                continue;

            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

            foreach (MethodInfo method in methods)
            {
                McpServerToolAttribute? attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null)
                    continue;

                names.Add(attribute.Name ?? method.Name);
            }
        }

        return names;
    }
}
