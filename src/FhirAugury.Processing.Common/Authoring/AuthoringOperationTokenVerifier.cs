using System.Security.Cryptography;
using System.Text;

namespace FhirAugury.Processing.Common.Authoring;

public sealed record IssuedAuthoringOperationToken(
    string Token,
    string Verifier);

public static class AuthoringOperationTokenVerifier
{
    private const int TokenByteCount = 32;

    public static IssuedAuthoringOperationToken Issue()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(TokenByteCount);
        string token = Base64UrlEncode(bytes);
        return new IssuedAuthoringOperationToken(token, CreateVerifier(token));
    }

    public static string CreateVerifier(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool Verify(string token, string verifier)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(verifier))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(verifier);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return expected.Length == actual.Length &&
            CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
