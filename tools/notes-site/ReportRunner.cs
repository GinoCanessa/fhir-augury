using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Tools.NotesSite.Report;

namespace FhirAugury.Tools.NotesSite;

/// <summary>Orchestrates the <c>report</c> verb: validates inputs, guards the output dir, emits the site.</summary>
internal static class ReportRunner
{
    public static async Task<int> RunAsync(ReportOptions options)
    {
        string notesDb = Path.GetFullPath(options.DbPath);
        if (!File.Exists(notesDb))
        {
            await Console.Error.WriteLineAsync(
                $"Notes DB not found: {notesDb}. Run the BallotNotes processor 'hydrate' first.").ConfigureAwait(false);
            return 1;
        }

        int noteCount;
        try
        {
            using BallotNotesDatabase db = new(notesDb, ConsoleLogger.Instance, readOnly: true);
            noteCount = db.CountNotes();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            await Console.Error.WriteLineAsync(
                $"Notes DB schema error reading {notesDb}: {ex.Message}. Re-hydrate it with the BallotNotes processor.").ConfigureAwait(false);
            return 1;
        }

        string outDir = Path.GetFullPath(options.OutPath);
        if (Directory.Exists(outDir) && File.Exists(Path.Combine(outDir, "index.html")) && !options.Force)
        {
            await Console.Error.WriteLineAsync(
                $"Output directory '{outDir}' already contains a report. Pass --force to overwrite.").ConfigureAwait(false);
            return 1;
        }

        NotesSpaEmitter emitter = new(notesDb, options.Title);
        emitter.Emit(outDir);

        Console.WriteLine($"Wrote {noteCount} note(s) to {Path.Combine(outDir, "index.html")}.");
        return 0;
    }
}
