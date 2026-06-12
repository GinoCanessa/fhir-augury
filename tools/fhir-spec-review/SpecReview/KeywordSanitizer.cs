using System.Globalization;
using System.Text;

namespace FhirAugury.Tools.FhirSpecReview.SpecReview;

/// <summary>Result of sanitizing a candidate token into a comparison keyword.</summary>
/// <param name="Clean">Lower-cased, punctuation-stripped token (letters + digits only).</param>
/// <param name="FirstLetter">First alphabetic character of the original token, or <c>'\0'</c> if none.</param>
/// <param name="PrefixSymbol">Leading symbol/punctuation char seen before any letter (e.g. <c>'_'</c>, <c>'$'</c>), or null.</param>
internal readonly record struct SanitizedKeyword(string Clean, char FirstLetter, char? PrefixSymbol);

/// <summary>
/// Faithful port of the legacy <c>sanitizeAsKeyword()</c>
/// (<c>fmg-r6-review/SpecReview/ContentReview.cs</c>). Strips punctuation so
/// <c>Patient.contact</c> → <c>patientcontact</c>, lower-cases letters, keeps
/// digits, and exposes the first letter and any leading prefix symbol (so
/// <c>_id</c> exposes prefix <c>'_'</c>). Load-bearing: every loaded
/// vocabulary value AND every candidate token must pass through this so real
/// artifacts are not flagged.
/// </summary>
internal static class KeywordSanitizer
{
    public static SanitizedKeyword Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SanitizedKeyword(string.Empty, '\0', null);
        }

        char? firstLetter = null;
        char? prefixSymbol = null;

        StringBuilder sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(c);

            switch (uc)
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                    sb.Append(char.ToLower(c));
                    firstLetter ??= c;
                    break;

                case UnicodeCategory.LowercaseLetter:
                    firstLetter ??= c;
                    sb.Append(c);
                    break;

                case UnicodeCategory.DecimalDigitNumber:
                    sb.Append(c);
                    break;

                case UnicodeCategory.ConnectorPunctuation:
                case UnicodeCategory.DashPunctuation:
                case UnicodeCategory.OpenPunctuation:
                case UnicodeCategory.ClosePunctuation:
                case UnicodeCategory.InitialQuotePunctuation:
                case UnicodeCategory.FinalQuotePunctuation:
                case UnicodeCategory.OtherPunctuation:
                case UnicodeCategory.MathSymbol:
                case UnicodeCategory.CurrencySymbol:
                case UnicodeCategory.OtherSymbol:
                    if (firstLetter == null)
                    {
                        prefixSymbol ??= c;
                    }
                    break;

                default:
                    break;
            }
        }

        return new SanitizedKeyword(sb.ToString(), firstLetter ?? '\0', prefixSymbol);
    }
}
