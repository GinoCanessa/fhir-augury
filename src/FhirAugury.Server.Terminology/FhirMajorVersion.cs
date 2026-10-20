namespace FhirAugury.Server.Terminology;

/// <summary>
/// FHIR major release tags this service supports. Used to select between
/// the R4 and R5 Firely deserializers and to tag persisted artifacts.
/// </summary>
public enum FhirMajorVersion
{
    R4,
    R5,
}

/// <summary>
/// Parses the configuration <c>FhirVersion</c> string (case-insensitive)
/// into <see cref="FhirMajorVersion"/>. Centralized so validation and
/// runtime selection agree.
/// </summary>
public static class FhirMajorVersionParser
{
    public static bool TryParse(string? raw, out FhirMajorVersion version)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            version = default;
            return false;
        }

        switch (raw.Trim().ToUpperInvariant())
        {
            case "R4":
            case "4":
            case "4.0":
            case "4.0.1":
                version = FhirMajorVersion.R4;
                return true;

            case "R5":
            case "5":
            case "5.0":
            case "5.0.0":
                version = FhirMajorVersion.R5;
                return true;
        }

        version = default;
        return false;
    }

    /// <summary>Renders the version as the canonical wire/storage tag.</summary>
    public static string ToTag(FhirMajorVersion version) => version switch
    {
        FhirMajorVersion.R4 => "R4",
        FhirMajorVersion.R5 => "R5",
        _ => version.ToString(),
    };
}
