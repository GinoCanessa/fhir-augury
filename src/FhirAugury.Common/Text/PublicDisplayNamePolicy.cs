using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>
/// Defines the value-safety policy for display names exposed by public
/// contracts.
/// </summary>
public static partial class PublicDisplayNamePolicy
{
    /// <summary>The current public display-name policy version.</summary>
    public const int CurrentVersion = 1;

    [GeneratedRegex(@"\S+@\S+\.\S+")]
    private static partial Regex EmailAddressTokenRegex();

    /// <summary>
    /// Trims a display name and returns it only when it is not a bound account
    /// username or an email-valued string.
    /// </summary>
    public static string? Normalize(
        string? value,
        string? boundAccountUsername = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (!string.IsNullOrWhiteSpace(boundAccountUsername)
            && string.Equals(
                normalized,
                boundAccountUsername.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return EmailAddressTokenRegex().IsMatch(normalized)
            ? null
            : normalized;
    }
}
