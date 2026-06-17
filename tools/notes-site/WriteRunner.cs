using System.Text;
using System.Text.Json;
using FhirAugury.Tools.NotesSite.Contracts;
using FhirAugury.Tools.NotesSite.Database;
using FhirAugury.Tools.NotesSite.Database.Records;

namespace FhirAugury.Tools.NotesSite;

/// <summary>
/// Orchestrates the <c>write</c> verb: reads a <see cref="NoteWritePayload"/>
/// (from <c>--in</c> or stdin), maps it to records, and idempotently upserts the
/// note (plus children and the owning run row) into the notes DB.
/// </summary>
internal static class WriteRunner
{
    private static readonly HashSet<string> s_validTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Artifact", "Page", "DataType" };

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<int> RunAsync(WriteOptions options)
    {
        string json;
        try
        {
            json = options.InPath is null
                ? await Console.In.ReadToEndAsync().ConfigureAwait(false)
                : await File.ReadAllTextAsync(options.InPath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"Failed to read payload: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            await Console.Error.WriteLineAsync("No payload provided (empty --in file or stdin).").ConfigureAwait(false);
            return 2;
        }

        NoteWritePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<NoteWritePayload>(json, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"Invalid JSON payload: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        if (payload is null)
        {
            await Console.Error.WriteLineAsync("Payload deserialized to null.").ConfigureAwait(false);
            return 2;
        }

        if (!Validate(payload, out string? validationError))
        {
            await Console.Error.WriteLineAsync(validationError).ConfigureAwait(false);
            return 2;
        }

        string dbPath = Path.GetFullPath(options.DbPath);
        string? dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

        using NotesDatabase db = new(dbPath, ConsoleLogger.Instance);
        if (options.DropTables) db.DropTables();
        db.Initialize();

        (NoteRecord note,
         List<NoteSourceFileRecord> files,
         List<NoteCommitRecord> commits,
         List<NoteTicketRecord> tickets,
         NotesRunRecord run) = MapToRecords(payload);

        db.SaveNote(note, files, commits, tickets, run);

        Console.WriteLine(
            $"Wrote note '{note.Name}' ({note.Type}) for {note.RepoOwner}/{note.RepoName} " +
            $"[{files.Count} files, {commits.Count} commits, {tickets.Count} tickets] to {dbPath}.");
        return 0;
    }

    private static bool Validate(NoteWritePayload payload, out string? error)
    {
        if (string.IsNullOrWhiteSpace(payload.Type) || !s_validTypes.Contains(payload.Type.Trim()))
        {
            error = $"Invalid 'type' (expected Artifact, Page, or DataType): '{payload.Type}'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            error = "Missing required 'name'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(payload.RepoOwner) || string.IsNullOrWhiteSpace(payload.RepoName))
        {
            error = "Missing required 'repoOwner' / 'repoName'.";
            return false;
        }
        error = null;
        return true;
    }

    private static (NoteRecord, List<NoteSourceFileRecord>, List<NoteCommitRecord>, List<NoteTicketRecord>, NotesRunRecord)
        MapToRecords(NoteWritePayload p)
    {
        string type = NormalizeType(p.Type);
        string owner = p.RepoOwner.Trim();
        string name = p.RepoName.Trim();
        string unitName = p.Name.Trim();
        string noteId = Slugify($"{owner}-{name}-{type}-{unitName}");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset generatedAt = p.GeneratedAt ?? now;

        NoteRecord note = new()
        {
            NoteId = noteId,
            Type = type,
            Name = unitName,
            RepoOwner = owner,
            RepoName = name,
            RepoCategory = p.RepoCategory.Trim(),
            WorkGroup = p.WorkGroup.Trim(),
            WorkGroupCode = p.WorkGroupCode.Trim(),
            SinceSha = p.SinceSha.Trim(),
            SinceShortSha = ShortSha(p.SinceShortSha, p.SinceSha),
            HeadSha = p.HeadSha.Trim(),
            HeadShortSha = ShortSha(p.HeadShortSha, p.HeadSha),
            CommitsInWindow = p.CommitsInWindow,
            TicketsAttributed = p.TicketsAttributed,
            NeedsNote = NormalizeNeedsNote(p.NeedsNote),
            CurrentBallotNoteHtml = p.CurrentBallotNoteHtml,
            ProposedBallotNoteHtml = p.ProposedBallotNoteHtml,
            RollupSummaryMarkdown = p.RollupSummaryMarkdown,
            NotesForReviewerMarkdown = p.NotesForReviewerMarkdown,
            SourceFilesNote = p.SourceFilesNote,
            GeneratedAt = generatedAt,
            SavedAt = now,
        };

        List<NoteSourceFileRecord> files = [];
        int order = 0;
        foreach (NoteSourceFilePayload f in p.SourceFiles)
        {
            if (string.IsNullOrWhiteSpace(f.Path)) continue;
            files.Add(new NoteSourceFileRecord
            {
                NoteId = noteId,
                Path = f.Path.Trim(),
                Role = f.Role.Trim(),
                TouchedInWindow = f.TouchedInWindow,
                FileOrder = order++,
            });
        }

        List<NoteCommitRecord> commits = [];
        order = 0;
        foreach (NoteCommitPayload c in p.Commits)
        {
            if (string.IsNullOrWhiteSpace(c.Sha)) continue;
            commits.Add(new NoteCommitRecord
            {
                NoteId = noteId,
                Sha = c.Sha.Trim(),
                ShortSha = ShortSha(c.ShortSha, c.Sha),
                AuthorName = c.AuthorName.Trim(),
                AuthorDate = c.AuthorDate.Trim(),
                Subject = c.Subject.Trim(),
                WebUrl = c.WebUrl.Trim(),
                TicketKeys = string.Join(", ", c.TicketKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())),
                CommitOrder = order++,
            });
        }

        List<NoteTicketRecord> tickets = [];
        order = 0;
        foreach (NoteTicketPayload t in p.Tickets)
        {
            if (string.IsNullOrWhiteSpace(t.Key)) continue;
            tickets.Add(new NoteTicketRecord
            {
                NoteId = noteId,
                TicketKey = t.Key.Trim(),
                Title = t.Title.Trim(),
                Resolution = t.Resolution.Trim(),
                WorkGroup = t.WorkGroup.Trim(),
                Specification = t.Specification.Trim(),
                Url = t.Url.Trim(),
                CommitCount = t.CommitCount,
                TicketOrder = order++,
            });
        }

        NotesRunRecord run = new()
        {
            RunKey = $"{owner}/{name}@{note.SinceSha}..{note.HeadSha}",
            RepoOwner = owner,
            RepoName = name,
            RepoCategory = note.RepoCategory,
            SinceSha = note.SinceSha,
            SinceShortSha = note.SinceShortSha,
            HeadSha = note.HeadSha,
            HeadShortSha = note.HeadShortSha,
            RunAt = now,
        };

        return (note, files, commits, tickets, run);
    }

    private static string NormalizeType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "artifact" => "Artifact",
        "page" => "Page",
        "datatype" => "DataType",
        _ => type.Trim(),
    };

    private static string NormalizeNeedsNote(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" => "yes",
        "no" or "false" => "no",
        _ => "unknown",
    };

    private static string ShortSha(string shortSha, string fullSha)
    {
        if (!string.IsNullOrWhiteSpace(shortSha)) return shortSha.Trim();
        string full = fullSha.Trim();
        return full.Length > 12 ? full[..12] : full;
    }

    /// <summary>Lowercase slug; non-alphanumeric runs collapse to a single hyphen.</summary>
    private static string Slugify(string value)
    {
        StringBuilder sb = new(value.Length);
        bool lastHyphen = false;
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastHyphen = false;
            }
            else if (!lastHyphen)
            {
                sb.Append('-');
                lastHyphen = true;
            }
        }
        return sb.ToString().Trim('-');
    }
}
