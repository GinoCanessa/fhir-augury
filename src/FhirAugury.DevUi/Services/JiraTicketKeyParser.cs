using System.Text;
using FhirAugury.Common.Api;
using FhirAugury.DevUi.Models;

namespace FhirAugury.DevUi.Services;

public sealed class JiraTicketKeyParser
{
    public JiraTicketKeyParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new JiraTicketKeyParseResult([], []);
        }

        List<string> valid = [];
        List<string> invalid = [];
        HashSet<string> seen =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string token in Tokenize(input))
        {
            string normalized = token.ToUpperInvariant();
            if (!seen.Add(normalized))
            {
                continue;
            }
            if (ValueFormatDetector.IsJiraKey(normalized))
            {
                valid.Add(normalized);
            }
            else
            {
                invalid.Add(normalized);
            }
        }

        return new JiraTicketKeyParseResult(
            Array.AsReadOnly(valid.ToArray()),
            Array.AsReadOnly(invalid.ToArray()));
    }

    private static IEnumerable<string> Tokenize(string input)
    {
        StringBuilder current = new();
        foreach (char character in input)
        {
            if (character is ',' or ';' ||
                char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }
}
