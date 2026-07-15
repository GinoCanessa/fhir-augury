using FhirAugury.Tools.FhirXverElementDiff.Attribution;
using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Readers;
using FhirAugury.Tools.FhirXverElementDiff.Report;

namespace FhirAugury.Tools.FhirXverElementDiff;

/// <summary>
/// Entry point for <c>fhir-xver-element-diff</c> — a read-only tool that diffs FHIR
/// core-release element trees (R4→R4B→R5→R6) from the two spec SQLite DBs and emits
/// per-increment markdown change reports, each row attributed to the FHIR tickets/commits
/// that produced it. <c>--dump</c> is a per-release count smoke command.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || Array.Exists(args, a => a is "--help" or "-h" or "help"))
        {
            WriteUsage(args.Length == 0 ? Console.Error : Console.Out);
            return args.Length == 0 ? 2 : 0;
        }

        if (!CliOptions.TryParse(args, out ToolOptions options, out string? error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            WriteUsage(Console.Error);
            return 2;
        }

        if (options.DumpRelease is ReleaseId dumpRelease)
        {
            return RunDump(options, dumpRelease);
        }

        return await RunReportsAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Default action: for each selected increment, load both releases, build the report
    /// model (structure buckets + rename detection + per-element diff), attribute each
    /// changed structure's rows to the FHIR tickets/commits in its git window (unless
    /// <c>--no-attribution</c>), and write the markdown file.
    /// </summary>
    private static async Task<int> RunReportsAsync(ToolOptions options)
    {
        if (!Increments.TryResolve(options.Increment, out IReadOnlyList<IncrementDefinition> increments, out string? incError))
        {
            await Console.Error.WriteLineAsync(incError).ConfigureAwait(false);
            return 2;
        }

        bool single = increments.Count == 1;
        if (!single && (options.SinceOverride is not null || options.UntilOverride is not null))
        {
            await Console.Error.WriteLineAsync(
                "Ignoring --since/--until: they apply only when a single --increment is selected.")
                .ConfigureAwait(false);
        }

        string clonePath = Path.GetFullPath(options.ClonePath);
        GitLog git = new(clonePath);
        string? cloneHead = git.CloneAvailable
            ? await git.RevParseShortAsync("HEAD").ConfigureAwait(false)
            : null;

        FhirKeyAllowlist? allowlist = options.NoAttribution ? null : TryLoadAllowlist(options);

        ReleaseReader reader = new(ConsoleLogger.Instance);
        Dictionary<ReleaseId, ReleaseModel> cache = [];

        foreach (IncrementDefinition increment in increments)
        {
            if (!TryLoad(reader, options, increment.Earlier, cache, out ReleaseModel? earlier)
                || !TryLoad(reader, options, increment.Later, cache, out ReleaseModel? later))
            {
                return 1;
            }

            string since = single && options.SinceOverride is not null ? options.SinceOverride : increment.DefaultSince;
            string until = single && options.UntilOverride is not null ? options.UntilOverride : increment.DefaultUntil;

            ReportHeader header = new(
                GeneratedUtc: DateTimeOffset.UtcNow,
                EarlierLabel: Release.DisplayLabel(increment.Earlier),
                LaterLabel: Release.DisplayLabel(increment.Later),
                EarlierVersion: earlier!.Release.DisplayVersion,
                LaterVersion: later!.Release.DisplayVersion,
                EarlierBuilt: earlier.Release.ProcessDate,
                LaterBuilt: later.Release.ProcessDate,
                SinceSha: since,
                UntilSha: until,
                CloneHead: cloneHead,
                AttributionEnabled: !options.NoAttribution,
                HeaderNote: increment.HeaderNote);

            ReportModel model = ReportBuilder.Build(increment, earlier, later, header);
            if (allowlist is not null)
            {
                model = await Attributor.AttributeAsync(
                    model, git, since, until, allowlist, Release.IsR6(increment.Later)).ConfigureAwait(false);
            }

            string outPath = Path.GetFullPath(Path.Combine(options.OutDir, increment.Slug + ".md"));
            await MarkdownReportWriter.WriteAsync(model, outPath).ConfigureAwait(false);

            Console.WriteLine(
                $"Wrote {outPath}  (mapped {model.Mapped.Count}, removed {model.Removed.Count}, added {model.Added.Count})");
        }

        return 0;
    }

    /// <summary>
    /// Loads the FHIR-key allowlist for attribution, or null (with a note on stderr) when the
    /// Jira DB is missing/empty — attribution is best-effort enrichment, never a hard gate.
    /// </summary>
    private static FhirKeyAllowlist? TryLoadAllowlist(ToolOptions options)
    {
        string jiraDb = Path.GetFullPath(options.JiraDbPath);
        if (!File.Exists(jiraDb))
        {
            Console.Error.WriteLine($"Jira DB not found ({jiraDb}); emitting change tables without attribution.");
            return null;
        }
        try
        {
            FhirKeyAllowlist allowlist = JiraAllowlistReader.Load(jiraDb);
            if (allowlist.IsEmpty)
            {
                Console.Error.WriteLine($"No FHIR keys found in {jiraDb}; attribution will be blank.");
                return null;
            }
            return allowlist;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load the Jira allowlist from {jiraDb}: {ex.Message}");
            return null;
        }
    }

    private static bool TryLoad(
        ReleaseReader reader, ToolOptions options, ReleaseId id,
        Dictionary<ReleaseId, ReleaseModel> cache, out ReleaseModel? release)
    {
        if (cache.TryGetValue(id, out release))
        {
            return true;
        }

        string dbPath = Path.GetFullPath(options.DbPathFor(id));
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Spec DB not found for {Release.DisplayLabel(id)}: {dbPath}");
            release = null;
            return false;
        }

        release = reader.LoadRelease(reader.ResolveRelease(id, dbPath));
        cache[id] = release;
        return true;
    }

    /// <summary>
    /// Smoke command: loads a release and prints per-kind structure counts, raw vs.
    /// locally-meaningful element totals, and (for R6) the frozen build tuple plus a
    /// snapshot-completeness assertion.
    /// </summary>
    private static int RunDump(ToolOptions options, ReleaseId id)
    {
        string dbPath = Path.GetFullPath(options.DbPathFor(id));
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Spec DB not found: {dbPath}");
            return 1;
        }

        ReleaseReader readerService = new(ConsoleLogger.Instance);
        ResolvedRelease release = readerService.ResolveRelease(id, dbPath);
        ReleaseModel model = readerService.LoadRelease(release);

        Console.WriteLine($"Release {Release.DisplayLabel(id)}  [{release.PackageId} {release.DisplayVersion}]");
        Console.WriteLine($"  DB:          {dbPath} (PackageKey {release.PackageKey})");
        if (release.ProcessDate is not null)
        {
            Console.WriteLine($"  Built:       {release.ProcessDate}");
        }

        int primitive = 0, complex = 0, resource = 0;
        int rawElements = 0, meaningfulElements = 0, minSnapshot = int.MaxValue;
        foreach (StructureModel structure in model.Structures)
        {
            switch (structure.Group)
            {
                case StructureGroup.PrimitiveType: primitive++; break;
                case StructureGroup.Resource: resource++; break;
                default: complex++; break;
            }
            minSnapshot = Math.Min(minSnapshot, structure.SnapshotCount);
            foreach (ElementModel element in structure.Elements)
            {
                rawElements++;
                if (!model.IsPurelyInherited(element) && element.NormalizedKey.Length > 0)
                {
                    meaningfulElements++;
                }
            }
        }

        Console.WriteLine(
            $"  Structures:  primitive-type={primitive}, complex-type={complex}, resource={resource} " +
            $"(total {model.Structures.Count})");
        Console.WriteLine($"  Elements:    raw={rawElements}, locally-meaningful={meaningfulElements}");

        if (Release.IsR6(id))
        {
            if (minSnapshot <= 0)
            {
                Console.Error.WriteLine(
                    $"R6 snapshot-completeness assertion FAILED: a structure has SnapshotCount={minSnapshot}.");
                return 1;
            }
            Console.WriteLine($"  R6 snapshot: OK (min SnapshotCount={minSnapshot} > 0; DB is the frozen source of truth)");
        }

        return 0;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("""
            fhir-xver-element-diff — cross-version FHIR element-change analysis (R4 → R4B → R5 → R6).

            Usage:
              fhir-xver-element-diff --dump <R4|R4B|R5|R6>   Smoke command: print per-kind counts.
              fhir-xver-element-diff [report options]        Emit the three markdown change reports.
              fhir-xver-element-diff --help

            Options:
              --dump <release>          Print structure/element counts for one release and exit.
              --increment <sel>         all | r4-r4b | r4b-r5 | r5-r6 (default: all).
              --out <dir>               Output directory for reports (default: ./scratch/0714-03/reports).
              --no-attribution          Skip git/Jira attribution (change tables only).
              --since <sha>             Override the increment's git window start.
              --until <sha>             Override the increment's git window end.
              --fhir-spec-db <path>     R4/R4B/R5 spec DB (default: ./cache/fhir-spec.db).
              --fhir-r6-db <path>       R6 spec DB (default: ./cache/fhir-r6.db).
              --jira-db <path>          Jira DB for the FHIR-key allowlist (default: ./cache/jira.db).
              --clone <path>            HL7/fhir clone (default: ./cache/github/repos/HL7_fhir/clone).
            """);
    }
}
