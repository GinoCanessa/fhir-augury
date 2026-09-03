using FhirAugury.Tools.TicketMdToDb.Compilation;
using FhirAugury.Tools.TicketMdToDb.Import;

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

        TicketImportRunResult result = await new TicketImportRunner().RunAsync(
            options!,
            ct);
        CompilationResult compilation = result.Compilation;

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
        foreach (string message in result.Messages)
        {
            Console.Error.WriteLine(message);
        }
        Console.WriteLine(
            $"Run {result.Audit.Run.RunId}: {result.Audit.Run.Status}" +
            (result.EvidencePath is null ? string.Empty : $"; evidence {result.EvidencePath}"));
        return result.ExitCode;
    }
}
