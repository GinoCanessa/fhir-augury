using FhirAugury.Tools.TicketMdToDb.Audit;
using FhirAugury.Tools.TicketMdToDb.Compilation;

namespace FhirAugury.Tools.TicketMdToDb;

public static class Program
{
    public static Task<int> Main(string[] args) => ProgramEntry.RunAsync(args);
}

public static class ProgramEntry
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken ct = default)
    {
        if (!CliOptions.TryParse(args, out CliOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }

        CompilationResult compilation = ReportCompiler.Compile(new CompilationRequest(
            options!.InputRoot,
            options.ExpectedCount,
            options.OverridesPath));
        ImportAudit audit = ImportAudit.ForCompilation(
            compilation,
            options.DatabasePath,
            options.Orchestrator,
            options.OverridesPath,
            options.AuditPath,
            options.OverrideTemplatePath,
            options.DryRun,
            options.ReplaceExisting,
            options.AcceptUnresolvedHydration);
        try
        {
            await ImportAuditWriter.WriteAuditAsync(options.AuditPath, audit, ct);
            await ImportAuditWriter.WriteOverrideTemplateAsync(
                options.OverrideTemplatePath,
                compilation.OverrideTemplate,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Could not write import evidence: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        foreach (ImportDiagnostic diagnostic in compilation.Manifest.Diagnostics)
        {
            TextWriter output = diagnostic.IsBlocking ? Console.Error : Console.Out;
            output.WriteLine(
                $"{(diagnostic.IsBlocking ? "ERROR" : "WARN")} {diagnostic.Code}: " +
                $"{diagnostic.SourcePath ?? diagnostic.TicketKey ?? string.Empty} {diagnostic.Message}".Trim());
        }

        Console.WriteLine(
            $"Manifest {compilation.Manifest.ManifestId}: " +
            $"{compilation.Manifest.Files.Count} files, " +
            $"{compilation.Manifest.Tickets.Count} tickets, " +
            $"{compilation.Manifest.Diagnostics.Count(diagnostic => diagnostic.IsBlocking)} errors.");
        if (!options.DryRun && compilation.Success)
        {
            Console.Error.WriteLine("Write mode is unavailable until staging and promotion have been configured.");
            return 1;
        }

        return compilation.Success ? 0 : 1;
    }
}
