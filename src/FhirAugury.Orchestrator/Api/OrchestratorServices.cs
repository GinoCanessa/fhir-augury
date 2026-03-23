using FhirAugury.Orchestrator.CrossRef;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using FhirAugury.Orchestrator.Workers;

namespace FhirAugury.Orchestrator.Api;

/// <summary>
/// Bundles dependencies used by <see cref="OrchestratorGrpcService"/> to reduce constructor parameter count.
/// </summary>
public record OrchestratorServices(
    UnifiedSearchService SearchService,
    RelatedItemFinder RelatedFinder,
    OrchestratorDatabase Database,
    SourceRouter Router,
    ServiceHealthMonitor HealthMonitor,
    CrossRefLinker CrossRefLinker,
    XRefScanWorker XRefScanWorker);
