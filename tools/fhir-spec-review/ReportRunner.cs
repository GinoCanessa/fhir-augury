using FhirAugury.Tools.FhirSpecReview.Report;

namespace FhirAugury.Tools.FhirSpecReview;

/// <summary>Orchestrates the <c>report</c> verb: validates inputs, guards the output dir, emits the site.</summary>
internal static class ReportRunner
{
    public static async Task<int> RunAsync(ReportOptions options)
    {
        string reviewDb = Path.GetFullPath(options.ReviewDbPath);
        if (!File.Exists(reviewDb))
        {
            await Console.Error.WriteLineAsync($"Review DB not found: {reviewDb}").ConfigureAwait(false);
            return 1;
        }

        string outDir = Path.GetFullPath(options.OutPath);
        if (Directory.Exists(outDir) && File.Exists(Path.Combine(outDir, "index.html")) && !options.Force)
        {
            await Console.Error.WriteLineAsync(
                $"Output directory '{outDir}' already contains a report. Pass --force to overwrite.").ConfigureAwait(false);
            return 1;
        }

        ReportEmitter emitter = new(reviewDb);
        emitter.Emit(outDir);

        Console.WriteLine($"Wrote report to {Path.Combine(outDir, "index.html")}.");
        return 0;
    }
}
