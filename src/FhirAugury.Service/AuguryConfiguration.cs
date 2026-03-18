namespace FhirAugury.Service;

/// <summary>Strongly-typed configuration binding to the FhirAugury config section.</summary>
public class AuguryConfiguration
{
    public const string SectionName = "FhirAugury";

    public string DatabasePath { get; set; } = "fhir-augury.db";
    public Dictionary<string, SourceConfiguration> Sources { get; set; } = [];
    public Bm25Configuration Bm25 { get; set; } = new();
    public ApiConfiguration Api { get; set; } = new();
}

public class SourceConfiguration
{
    public bool Enabled { get; set; } = true;
    public TimeSpan? SyncSchedule { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string? AuthMode { get; set; }

    // Jira-specific
    public string? Cookie { get; set; }
    public string? ApiToken { get; set; }
    public string? Email { get; set; }
    public string? DefaultJql { get; set; }

    // Zulip-specific
    public string? ApiKey { get; set; }
    public string? CredentialFile { get; set; }
    public bool OnlyWebPublic { get; set; } = true;
}

public class Bm25Configuration
{
    public double K1 { get; set; } = 1.2;
    public double B { get; set; } = 0.75;
}

public class ApiConfiguration
{
    public int Port { get; set; } = 5100;
    public string[] CorsOrigins { get; set; } = ["*"];
}
