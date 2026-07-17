using System.Text;
using System.Text.RegularExpressions;
using FhirAugury.Processing.Jira.Common.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Agent;

public sealed partial class JiraAgentCommandRenderer(IOptions<JiraProcessingOptions> optionsAccessor)
{
    private readonly JiraProcessingOptions _options = optionsAccessor.Value;

    public JiraAgentCommand Render(JiraAgentCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string template = _options.AgentCliCommand;
        if (!template.Contains("{ticketKey}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AgentCliCommand must include the {ticketKey} token.");
        }

        Dictionary<string, string> tokens = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ticketKey"] = context.TicketKey,
            ["dbPath"] = context.DatabasePath,
            ["sourceTicketId"] = context.SourceTicketId,
            ["sourceTicketShape"] = context.SourceTicketShape,
        };
        foreach (KeyValuePair<string, string> token in context.ExtensionTokens)
        {
            tokens[token.Key] = token.Value;
        }

        string rendered = TokenRegex().Replace(template, match =>
        {
            string name = match.Groups[1].Value;
            if (!tokens.TryGetValue(name, out string? value))
            {
                throw new InvalidOperationException($"AgentCliCommand contains unresolved token '{{{name}}}'.");
            }

            return value;
        });

        List<string> parts = SplitCommandLine(rendered);
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("AgentCliCommand rendered to an empty command.");
        }

        return new JiraAgentCommand(parts[0], parts.Skip(1).ToArray());
    }

    public static List<string> SplitCommandLine(string commandLine)
    {
        List<string> args = [];
        StringBuilder current = new();
        bool inQuotes = false;
        char quoteChar = '\0';
        for (int index = 0; index < commandLine.Length; index++)
        {
            char ch = commandLine[index];
            if ((ch is '"' or '\'') && (!inQuotes || ch == quoteChar))
            {
                inQuotes = !inQuotes;
                quoteChar = inQuotes ? ch : '\0';
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (ch == '\\' && index + 1 < commandLine.Length && (commandLine[index + 1] == '"' || commandLine[index + 1] == '\\'))
            {
                index++;
                current.Append(commandLine[index]);
                continue;
            }

            current.Append(ch);
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("AgentCliCommand contains an unterminated quoted argument.");
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    [GeneratedRegex("\\{([A-Za-z0-9_]+)\\}")]
    private static partial Regex TokenRegex();
}
