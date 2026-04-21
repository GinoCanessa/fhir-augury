namespace FhirAugury.Source.Jira.Configuration;

/// <summary>
/// Configuration for the HL7 work-group CodeSystem XML support file used by
/// the Jira source service. The file is materialized into
/// <c>cache/jira/_support/&lt;Filename&gt;</c> on startup and at the start of
/// every scheduled sync.
/// </summary>
public class WorkGroupSourceXmlOptions
{
    /// <summary>
    /// Filename, relative to <c>cache/jira/_support/</c>, where the XML file
    /// is materialized. Defaults to <c>CodeSystem-hl7-work-group.xml</c>.
    /// </summary>
    public string Filename { get; set; } = "CodeSystem-hl7-work-group.xml";

    /// <summary>
    /// Optional absolute or relative local file path to copy into
    /// <c>_support/</c> on startup / sync. Takes precedence over
    /// <see cref="Url"/> when set. If the local file is missing the
    /// acquirer warns and does <b>not</b> fall back to <see cref="Url"/>.
    /// </summary>
    public string? LocalFile { get; set; }

    /// <summary>
    /// Optional URL to download the XML from when no local file is configured
    /// and the file is missing from <c>_support/</c>.
    /// </summary>
    public string? Url { get; set; }
}
