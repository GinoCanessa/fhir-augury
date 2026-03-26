# Section 7: CLI Surface

**Goal:** Add cache-related CLI options to existing commands and create new
`cache stats` and `cache clear` commands.

**Dependencies:** Section 6 (configuration & DI)

---

## 7.1 — Add Cache Options to Download and Sync Commands

### Objective

Add `--cache-path` and `--cache-mode` options to the `download` and `sync`
commands so users can control caching from the command line.

### Files to Modify

- `src/FhirAugury.Cli/Commands/DownloadCommand.cs`
- `src/FhirAugury.Cli/Commands/SyncCommand.cs`

### New CLI Options

```
--cache-path <path>     Override the cache root directory (default: ./cache)
--cache-mode <mode>     Cache mode: Disabled, WriteThrough, CacheOnly, WriteOnly
                        (default: WriteThrough)
```

### Implementation

Add options to the `Create()` method:

```csharp
var cachePathOption = new Option<string?>(
    "--cache-path",
    "Override the cache root directory");

var cacheModeOption = new Option<CacheMode>(
    "--cache-mode",
    () => CacheMode.WriteThrough,
    "Cache mode: Disabled, WriteThrough, CacheOnly, WriteOnly");
```

In the command handler, construct the cache and pass it to source options:

```csharp
// Resolve cache
var cachePath = cachePathValue ?? "./cache";
IResponseCache cache = cacheModeValue == CacheMode.Disabled
    ? NullResponseCache.Instance
    : new FileSystemResponseCache(Path.GetFullPath(cachePath));

// Pass to source options
var jiraOptions = new JiraSourceOptions
{
    // ... existing options from CLI args
    CacheMode = cacheModeValue,
    Cache = cache,
};
```

### Design Notes

- The CLI constructs source options manually (not via DI). The cache instance
  is created inline based on the CLI arguments.
- `--cache-path` overrides the entire cache root. When used with `CacheOnly`,
  this lets users point at an arbitrary directory of pre-downloaded files:
  ```
  fhir-augury download jira --cache-only --cache-path /data/jira-exports
  ```
- When `--cache-mode CacheOnly` is specified, `--cache-path` is effectively
  required (otherwise it defaults to `./cache` which may be empty). Consider
  logging a warning if `CacheOnly` is used with the default path and the
  directory is empty.

### Acceptance Criteria

- [ ] `--cache-path` option accepted on `download` and `sync` commands
- [ ] `--cache-mode` option accepted with enum validation
- [ ] `CacheOnly` mode works end-to-end via CLI
- [ ] `WriteThrough` mode writes cache files during download
- [ ] Default behaviour (no options) continues to work unchanged
- [ ] Help text is clear and shows default values

---

## 7.2 — Create Cache Stats Command

### Objective

Add a `cache stats` command that displays cache size and file counts per source.

### File to Create: `src/FhirAugury.Cli/Commands/CacheCommand.cs`

### CLI Syntax

```
fhir-augury cache stats [--cache-path <path>]
```

### Implementation

```csharp
public static class CacheCommand
{
    public static Command Create()
    {
        var cacheCommand = new Command("cache", "Manage the response cache");

        cacheCommand.AddCommand(CreateStatsCommand());
        cacheCommand.AddCommand(CreateClearCommand());

        return cacheCommand;
    }

    private static Command CreateStatsCommand()
    {
        var statsCommand = new Command("stats", "Show cache size per source");

        var cachePathOption = new Option<string>(
            "--cache-path", () => "./cache",
            "Cache root directory");

        statsCommand.AddOption(cachePathOption);

        statsCommand.SetHandler((cachePath) =>
        {
            var cache = new FileSystemResponseCache(
                Path.GetFullPath(cachePath));

            string[] sources = ["jira", "zulip", "confluence"];
            foreach (var source in sources)
            {
                var stats = cache.GetStats(source);
                // Format and print stats
            }
        }, cachePathOption);

        return statsCommand;
    }
}
```

### Output Format

```
Source      Files    Size       Sub-paths
──────────  ─────    ─────────  ─────────
jira        542      128.5 MB   (root)
zulip       3,847    456.2 MB   s270, s412, s501, ...
confluence  1,203    89.1 MB    pages
──────────  ─────    ─────────
Total       5,592    673.8 MB
```

When `--json` is passed (global option), output as JSON:

```json
{
  "sources": [
    { "name": "jira", "fileCount": 542, "totalBytes": 134742016, "subPaths": [] },
    { "name": "zulip", "fileCount": 3847, "totalBytes": 478412800, "subPaths": ["s270", "s412"] }
  ],
  "totalFiles": 5592,
  "totalBytes": 706664448
}
```

### Acceptance Criteria

- [ ] `cache stats` shows file count, size, and sub-paths per source
- [ ] Supports `--json` output format
- [ ] Handles empty/missing cache directory gracefully
- [ ] `--cache-path` overrides the root directory

---

## 7.3 — Create Cache Clear Command

### Objective

Add a `cache clear` command that deletes cached files per-source or globally.

### CLI Syntax

```
fhir-augury cache clear [--source <name>] [--cache-path <path>]
```

### Implementation

```csharp
private static Command CreateClearCommand()
{
    var clearCommand = new Command("clear", "Clear cached responses");

    var sourceOption = new Option<string?>(
        "--source",
        "Clear only this source's cache (jira, zulip, confluence)");

    var cachePathOption = new Option<string>(
        "--cache-path", () => "./cache",
        "Cache root directory");

    clearCommand.AddOption(sourceOption);
    clearCommand.AddOption(cachePathOption);

    clearCommand.SetHandler((source, cachePath) =>
    {
        var cache = new FileSystemResponseCache(
            Path.GetFullPath(cachePath));

        if (source is not null)
        {
            cache.Clear(source);
            Console.WriteLine($"Cleared cache for {source}.");
        }
        else
        {
            cache.ClearAll();
            Console.WriteLine("Cleared all caches.");
        }
    }, sourceOption, cachePathOption);

    return clearCommand;
}
```

### Acceptance Criteria

- [ ] `cache clear` deletes all cached files and metadata
- [ ] `cache clear --source jira` deletes only Jira cache
- [ ] Confirmation message printed after clearing
- [ ] Handles empty/missing cache directory gracefully

---

## 7.4 — Register Cache Command in CLI Root

### Objective

Wire the new `cache` command into the CLI root command.

### File to Modify: `src/FhirAugury.Cli/Program.cs`

Add to the root command setup:

```csharp
rootCommand.AddCommand(CacheCommand.Create());
```

### Acceptance Criteria

- [ ] `fhir-augury cache stats` works from CLI
- [ ] `fhir-augury cache clear` works from CLI
- [ ] `fhir-augury --help` shows `cache` in the command list
