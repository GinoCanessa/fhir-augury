using System.Security.Cryptography;
using System.Text;

namespace FhirAugury.Processing.Common.Authoring;

public static class AuthoringResultHasher
{
    public static string HashBytes(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    public static string HashNormalizedUtf8(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string normalized = payload
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        return HashBytes(Encoding.UTF8.GetBytes(normalized));
    }
}
