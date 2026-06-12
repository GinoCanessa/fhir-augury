using FhirAugury.Tools.FhirSpecReview.Database;
using FhirAugury.Tools.FhirSpecReview.Report;
using Microsoft.Extensions.Logging.Abstractions;

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

        using (ReviewDatabase db = new(reviewDb, NullLogger.Instance, readOnly: true))
        {
            List<(string Table, string Column)> missingColumns = db.FindMissingRequiredColumns();
            if (missingColumns.Count > 0)
            {
                string cols = string.Join(", ", missingColumns.Select(c => $"{c.Table}.{c.Column}"));
                await Console.Error.WriteLineAsync(
                    $"Review DB schema is out of date (missing: {cols}). " +
                    "Regenerate the review DB with this build's process.").ConfigureAwait(false);
                return 1;
            }
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
