using FhirAugury.Common;

namespace FhirAugury.Orchestrator.Configuration;

public class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    public string DatabasePath { get; set; } = "./data/orchestrator.db";
    public PortConfiguration Ports { get; set; } = new();
    public Dictionary<string, SourceServiceConfig> Services { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public RelatedOptions Related { get; set; } = new();
    public FhirAugury.Common.Configuration.DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();

    /// <summary>
    /// Interval in seconds between reconnection attempts for offline source services.
    /// Set to 0 to disable automatic reconnection.
    /// </summary>
    public int ReconnectIntervalSeconds { get; set; } = 30;
}

public class PortConfiguration
{
    public int Http { get; set; } = 5150;
    public int Grpc { get; set; } = 5151;
}

public class SourceServiceConfig
{
    public string GrpcAddress { get; set; } = "";
    public string HttpAddress { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class SearchOptions
{
    public int DefaultLimit { get; set; } = 20;
    public int MaxLimit { get; set; } = 100;
    public Dictionary<string, double> FreshnessWeights { get; set; } = new()
    {
        [SourceSystems.Jira] = 0.5,
        [SourceSystems.Zulip] = 2.0,
    };
}

public class RelatedOptions
{
    public double CrossSourceWeight { get; set; } = 10.0;
    public double Bm25SimilarityWeight { get; set; } = 3.0;
    public double SharedMetadataWeight { get; set; } = 2.0;
    public int DefaultLimit { get; set; } = 20;
    public int MaxKeyTerms { get; set; } = 15;
    public int PerSourceTimeoutSeconds { get; set; } = 2;
}
