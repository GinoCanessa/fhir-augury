using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Readers;

namespace FhirAugury.Tools.FhirXverElementDiff;

/// <summary>
/// Entry point for <c>fhir-xver-element-diff</c> — a read-only tool that diffs FHIR
/// core-release element trees (R4→R4B→R5→R6) from the two spec SQLite DBs and emits
/// per-increment markdown change reports. Phase 1 exposes only the <c>--dump</c>
/// smoke command; the report-generation default action is wired in a later phase.
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

        await Console.Error.WriteLineAsync(
            "Report generation is not yet wired (Phase 4). Use --dump <RELEASE> for the smoke command.")
            .ConfigureAwait(false);
        return 2;
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
              fhir-xver-element-diff [report options]        Emit markdown reports (later phase).
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
