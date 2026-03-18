namespace FhirAugury.Sources.Jira;

public enum JiraAuthMode { Cookie, ApiToken }

public record JiraSourceOptions
{
    public string BaseUrl { get; init; } = "https://jira.hl7.org";
    public JiraAuthMode AuthMode { get; init; } = JiraAuthMode.Cookie;
    public string? Cookie { get; init; }
    public string? ApiToken { get; init; }
    public string? Email { get; init; }
    public string DefaultJql { get; init; } = "project = \"FHIR Specification Feedback\"";
    public int PageSize { get; init; } = 100;
}
