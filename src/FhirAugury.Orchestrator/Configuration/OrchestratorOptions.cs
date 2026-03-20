namespace FhirAugury.Orchestrator.Configuration;

public class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    public string DatabasePath { get; set; } = "./data/orchestrator.db";
    public PortConfiguration Ports { get; set; } = new();
    public Dictionary<string, SourceServiceConfig> Services { get; set; } = new();
    public CrossRefOptions CrossRef { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public RelatedOptions Related { get; set; } = new();
}

public class PortConfiguration
{
    public int Http { get; set; } = 5150;
    public int Grpc { get; set; } = 5151;
}

public class SourceServiceConfig
{
    public string GrpcAddress { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class CrossRefOptions
{
    public int ScanIntervalMinutes { get; set; } = 30;
    public bool ValidateTargets { get; set; } = true;
}

public class SearchOptions
{
    public int DefaultLimit { get; set; } = 20;
    public int MaxLimit { get; set; } = 100;
    public double CrossRefBoostFactor { get; set; } = 0.5;
    public Dictionary<string, double> FreshnessWeights { get; set; } = new()
    {
        ["jira"] = 0.5,
        ["zulip"] = 2.0,
    };
}

public class RelatedOptions
{
    public double ExplicitXrefWeight { get; set; } = 10.0;
    public double ReverseXrefWeight { get; set; } = 8.0;
    public double Bm25SimilarityWeight { get; set; } = 3.0;
    public double SharedMetadataWeight { get; set; } = 2.0;
    public int DefaultLimit { get; set; } = 20;
    public int MaxKeyTerms { get; set; } = 15;
}
