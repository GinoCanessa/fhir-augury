using System.Text;

namespace FhirAugury.Common.Text;

/// <summary>
/// Shared formatting helpers used across CLI, MCP, and HTTP output layers.
/// </summary>
public static class FormatHelpers
{
    /// <summary>Formats a byte count into a human-readable string (B, KB, MB, GB).</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    /// <summary>Formats a camelCase or snake_case key into a human-readable label.</summary>
    public static string FormatKey(string key)
    {
        StringBuilder sb = new StringBuilder(key.Length + 4);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (c == '_')
            {
                sb.Append(' ');
            }
            else
            {
                if (i > 0 && char.IsUpper(c))
                    sb.Append(' ');
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}
