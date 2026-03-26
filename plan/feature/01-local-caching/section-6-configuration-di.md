# Section 6: Configuration & Dependency Injection

**Goal:** Wire the cache configuration into `appsettings.json`, the service
DI container, and the source construction paths so that cache behaviour is
fully configurable at runtime.

**Dependencies:** Sections 1–5

---

## 6.1 — Extend AuguryConfiguration

### Objective

Add the top-level `Cache` section and per-source `Cache` nested objects to
the existing configuration classes.

### File to Modify: `src/FhirAugury.Service/AuguryConfiguration.cs`

Add to `AuguryConfiguration`:

```csharp
using FhirAugury.Models.Caching;

// Add to AuguryConfiguration class:
public CacheConfiguration Cache { get; set; } = new();
```

Add to `SourceConfiguration`:

```csharp
// Add to SourceConfiguration class:
public SourceCacheConfiguration Cache { get; set; } = new();
```

### Resulting JSON Structure

```json
{
  "FhirAugury": {
    "Cache": {
      "RootPath": "./cache",
      "DefaultMode": "WriteThrough"
    },
    "Sources": {
      "jira": {
        "Cache": {
          "Mode": "WriteThrough",
          "Path": null
        },
        // ... existing jira config
      }
    }
  }
}
```

### Design Notes

- `CacheConfiguration` and `SourceCacheConfiguration` are already defined in
  `FhirAugury.Models` (Section 1.4), so they're available to both Service and
  CLI projects.
- The `Cache` property on `SourceConfiguration` defaults to a new
  `SourceCacheConfiguration` instance with `Mode = null` (inherit from default).

### Acceptance Criteria

- [ ] `AuguryConfiguration.Cache` property exists with sensible default
- [ ] `SourceConfiguration.Cache` property exists
- [ ] JSON binding works end-to-end (manually verified with test config)

---

## 6.2 — Update appsettings.json

### Objective

Add the `Cache` section to the default `appsettings.json` with all documented
options and sensible defaults.

### File to Modify: `src/FhirAugury.Service/appsettings.json`

Add to the `"FhirAugury"` section:

```json
{
  "FhirAugury": {
    "Cache": {
      "RootPath": "./cache",
      "DefaultMode": "WriteThrough"
    },
    "Sources": {
      "jira": {
        "Cache": { "Mode": null, "Path": null }
      },
      "zulip": {
        "Cache": { "Mode": null, "Path": null }
      },
      "confluence": {
        "Cache": { "Mode": null, "Path": null }
      },
      "github": {
        "Cache": { "Mode": "Disabled" }
      }
    }
  }
}
```

### Design Notes

- GitHub's cache mode is explicitly `Disabled` — GitHub data comes from cloned
  repos and doesn't need API-level caching.
- Jira, Zulip, and Confluence have `Mode: null` which means "inherit from
  `DefaultMode`" (`WriteThrough`).

### Acceptance Criteria

- [ ] `appsettings.json` includes Cache section
- [ ] GitHub cache is explicitly disabled
- [ ] Other sources inherit from default

---

## 6.3 — Register IResponseCache in Service DI

### Objective

Register `IResponseCache` as a singleton in the service's DI container,
constructing either `FileSystemResponseCache` or `NullResponseCache` based
on configuration.

### File to Modify: `src/FhirAugury.Service/Program.cs`

Add after existing service registrations:

```csharp
// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    var cacheConfig = auguryConfig.Cache;
    if (cacheConfig.DefaultMode == CacheMode.Disabled)
        return NullResponseCache.Instance;

    var rootPath = Path.GetFullPath(cacheConfig.RootPath);
    return new FileSystemResponseCache(rootPath);
});
```

### Design Notes

- A single `IResponseCache` instance is shared across all sources. The
  `source` parameter in each method call provides source-level isolation
  within the shared cache root.
- The service always registers a cache; individual sources check their own
  `CacheMode` to decide whether to use it.

### Acceptance Criteria

- [ ] `IResponseCache` is registered as a singleton
- [ ] `NullResponseCache` is used when default mode is `Disabled`
- [ ] `FileSystemResponseCache` is used otherwise
- [ ] Root path is resolved to an absolute path

---

## 6.4 — Wire Cache into Source Construction (Service)

### Objective

Modify the service's source factory / ingestion worker to pass the cache and
resolved `CacheMode` when constructing data source instances.

### Files to Modify

- `src/FhirAugury.Service/Workers/IngestionWorker.cs` (or wherever sources
  are constructed in the service)
- Any source factory or builder methods

### Implementation

When constructing a source's options, resolve the effective cache mode:

```csharp
CacheMode ResolveCacheMode(string sourceName, AuguryConfiguration config)
{
    var sourceCache = config.Sources.GetValueOrDefault(sourceName)?.Cache;
    return sourceCache?.Mode ?? config.Cache.DefaultMode;
}
```

Then pass the resolved mode and the `IResponseCache` instance to the source
options:

```csharp
var cacheMode = ResolveCacheMode("jira", auguryConfig);
var jiraOptions = new JiraSourceOptions
{
    // ... existing options
    CacheMode = cacheMode,
    Cache = cacheMode != CacheMode.Disabled
        ? serviceProvider.GetRequiredService<IResponseCache>()
        : null,
};
```

### Acceptance Criteria

- [ ] Effective cache mode is resolved from per-source override → global default
- [ ] `IResponseCache` is passed to source options when caching is active
- [ ] Sources receive `null` cache when mode is `Disabled`
- [ ] GitHub source always gets `Disabled` regardless of global default

---

## 6.5 — Environment Variable Binding

### Objective

Ensure cache configuration is overridable via environment variables, following
the existing `FHIR_AUGURY_` prefix convention.

### Existing Pattern

The service already supports `FHIR_AUGURY_` prefixed environment variables via
`builder.Configuration.AddEnvironmentVariables("FHIR_AUGURY_")`. The `__`
separator maps to nested keys.

### Mapping

| Environment Variable | Config Path |
|---------------------|-------------|
| `FHIR_AUGURY_Cache__RootPath` | `FhirAugury:Cache:RootPath` |
| `FHIR_AUGURY_Cache__DefaultMode` | `FhirAugury:Cache:DefaultMode` |
| `FHIR_AUGURY_Sources__jira__Cache__Mode` | `FhirAugury:Sources:jira:Cache:Mode` |

### Acceptance Criteria

- [ ] Env vars override appsettings.json values
- [ ] Docker `environment:` section examples work as documented
- [ ] No code changes needed — ASP.NET configuration binding handles this
      automatically via existing `AddEnvironmentVariables` call
