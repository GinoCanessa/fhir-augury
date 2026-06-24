using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration;

/// <summary>Inputs for a single hydration run.</summary>
public sealed record BallotNotesHydrationRequest
{
    public required string RepoOwner { get; init; }
    public required string RepoName { get; init; }
    public required string SinceSha { get; init; }
    public required string RunKey { get; init; }
    public string RepoCategory { get; init; } = string.Empty;

    /// <summary>Human-readable window label (e.g. <c>R6 Ballot 4</c>); empty when not supplied.</summary>
    public string WindowLabel { get; init; } = string.Empty;

    /// <summary>Owning work-group fallback when a unit has no attributed tickets.</summary>
    public string? WorkGroupHint { get; init; }
}

/// <summary>The outcome of a hydration run, recorded on the run row.</summary>
public sealed record HydrationResult
{
    public required string RunKey { get; init; }
    public int UnitsHydrated { get; init; }
    public int CommitsInWindow { get; init; }
    public int TicketsAttributed { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Orchestrates server-side ballot-note hydration for a repo + since-commit
/// window: groups changed files into units and, per unit (in parallel), walks
/// the window, attributes tickets, resolves source files, captures the current
/// ballot-note HTML, and upserts the evidence. Designed to be invoked
/// fire-and-forget by the controller; it never throws past
/// <see cref="BallotNotesDatabase.FinishRun"/>.
/// </summary>
public sealed class BallotNotesHydrator(
    BallotNotesDatabase database,
    TicketAttributor attributor,
    IOptions<BallotNotesHydrationOptions> options,
    ILogger<BallotNotesHydrator> logger)
{
    private readonly BallotNotesHydrationOptions _options = options.Value;

    public async Task<HydrationResult> HydrateAsync(BallotNotesHydrationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string owner = request.RepoOwner.Trim();
        string name = request.RepoName.Trim();
        string clonePath = Path.Combine(_options.CloneRoot, $"{owner}_{name}", "clone");

        int totalCommits = 0;
        int totalTickets = 0;
        int hydrated = 0;
        object gate = new();

        try
        {
            string headSha = (await GitRunner.RunAsync(clonePath, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim();
            string headShort = ShortSha(headSha);
            string sinceFull = (await GitRunner.RunAsync(clonePath, ["rev-parse", request.SinceSha], ct).ConfigureAwait(false)).Trim();
            string sinceShort = ShortSha(sinceFull);

            IReadOnlyList<string> changed = await CommitWindowWalker
                .ListChangedFilesAsync(clonePath, request.SinceSha, ct).ConfigureAwait(false);

            bool isFhirCore = IsFhirCore(owner, name);
            IReadOnlySet<string> ownedPages = isFhirCore
                ? await ComputeDatatypeOwnedPagesAsync(clonePath, ct).ConfigureAwait(false)
                : new HashSet<string>();

            IReadOnlyList<HydrationUnit> units = BallotNotesUnitGrouper.Group(changed, isFhirCore, ownedPages);

            database.UpdateRunPlan(request.RunKey, units.Count, headSha, headShort);

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism),
                CancellationToken = ct,
            };

            await Parallel.ForEachAsync(units, parallelOptions, async (unit, token) =>
            {
                (int commits, int tickets) = await HydrateUnitAsync(
                    clonePath, owner, name, request, sinceFull, sinceShort, headSha, headShort, unit, token)
                    .ConfigureAwait(false);

                lock (gate)
                {
                    totalCommits += commits;
                    totalTickets += tickets;
                    hydrated++;
                    database.BumpRunProgress(request.RunKey, hydrated, totalCommits, totalTickets);
                }
            }).ConfigureAwait(false);

            database.FinishRun(request.RunKey, "completed", null);
            return new HydrationResult
            {
                RunKey = request.RunKey,
                UnitsHydrated = hydrated,
                CommitsInWindow = totalCommits,
                TicketsAttributed = totalTickets,
                Status = "completed",
            };
        }
        catch (Exception ex)
        {
            string reason = ex is OperationCanceledException ? "Hydration cancelled." : ex.Message;
            logger.LogError(ex, "BallotNotes hydration failed for {Owner}/{Name}", owner, name);
            database.FinishRun(request.RunKey, "failed", reason);
            return new HydrationResult
            {
                RunKey = request.RunKey,
                UnitsHydrated = hydrated,
                CommitsInWindow = totalCommits,
                TicketsAttributed = totalTickets,
                Status = "failed",
                Error = reason,
            };
        }
    }

    private async Task<(int Commits, int Tickets)> HydrateUnitAsync(
        string clonePath,
        string owner,
        string name,
        BallotNotesHydrationRequest request,
        string sinceFull,
        string sinceShort,
        string headSha,
        string headShort,
        HydrationUnit unit,
        CancellationToken ct)
    {
        IReadOnlyList<WindowCommit> commits = await CommitWindowWalker
            .WalkAsync(clonePath, sinceFull, unit.ChangedPaths, ct).ConfigureAwait(false);

        HashSet<string> touched = new(unit.ChangedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (WindowCommit commit in commits)
        {
            foreach (string path in commit.ChangedPaths) touched.Add(path);
        }

        UnitAttribution attribution = await attributor
            .AttributeAsync(commits, request.WorkGroupHint, ct).ConfigureAwait(false);

        SourceFileResolution resolution = await SourceFileResolver
            .ResolveAsync(clonePath, unit, touched, ct).ConfigureAwait(false);

        CurrentNoteResolution currentNote = await ResolveCurrentNoteAsync(clonePath, unit, ct).ConfigureAwait(false);
        string noteId = Slugify($"{owner}-{name}-{unit.Type}-{unit.Name}");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        NoteRecord note = new()
        {
            NoteId = noteId,
            Type = unit.Type,
            Name = unit.Name,
            RepoOwner = owner,
            RepoName = name,
            RepoCategory = request.RepoCategory.Trim(),
            WorkGroup = attribution.WorkGroup,
            WorkGroupCode = attribution.WorkGroupCode,
            SinceSha = sinceFull,
            SinceShortSha = sinceShort,
            HeadSha = headSha,
            HeadShortSha = headShort,
            WindowLabel = request.WindowLabel,
            CommitsInWindow = commits.Count,
            TicketsAttributed = attribution.Tickets.Count,
            NeedsNote = "unknown",
            CurrentBallotNoteHtml = currentNote.CurrentHtml,
            CurrentNoteIsAuguryGenerated = currentNote.IsAuguryGenerated,
            PreservedHandAuthoredHtml = currentNote.PreservedHandAuthoredHtml,
            SourceFilesNote = resolution.Note,
            GeneratedAt = now,
            SavedAt = now,
        };

        List<NoteSourceFileRecord> files = [];
        int fileOrder = 0;
        foreach (ResolvedSourceFile file in resolution.Files)
        {
            files.Add(new NoteSourceFileRecord
            {
                NoteId = noteId,
                Path = file.Path,
                Role = file.Role,
                TouchedInWindow = file.TouchedInWindow,
                FileOrder = fileOrder++,
            });
        }

        List<NoteCommitRecord> commitRecords = [];
        int commitOrder = 0;
        foreach (WindowCommit commit in commits)
        {
            attribution.CommitTicketKeys.TryGetValue(commit.Sha, out IReadOnlyList<string>? keys);
            commitRecords.Add(new NoteCommitRecord
            {
                NoteId = noteId,
                Sha = commit.Sha,
                ShortSha = string.IsNullOrEmpty(commit.ShortSha) ? ShortSha(commit.Sha) : commit.ShortSha,
                AuthorName = commit.AuthorName,
                AuthorDate = commit.AuthorDate,
                Subject = commit.Subject,
                WebUrl = $"https://github.com/{owner}/{name}/commit/{commit.Sha}",
                TicketKeys = keys is null ? string.Empty : string.Join(", ", keys),
                CommitOrder = commitOrder++,
            });
        }

        List<NoteTicketRecord> ticketRecords = [];
        int ticketOrder = 0;
        foreach (AttributedTicket ticket in attribution.Tickets)
        {
            ticketRecords.Add(new NoteTicketRecord
            {
                NoteId = noteId,
                TicketKey = ticket.Key,
                Title = ticket.Title,
                Resolution = ticket.Resolution,
                WorkGroup = ticket.WorkGroup,
                Specification = ticket.Specification,
                Url = ticket.Url,
                ChangeImpact = ticket.ChangeImpact,
                ChangeCategory = ticket.ChangeCategory,
                CommitCount = ticket.CommitCount,
                TicketOrder = ticketOrder++,
            });
        }

        database.UpsertUnitEvidence(note, files, commitRecords, ticketRecords);
        return (commits.Count, attribution.Tickets.Count);
    }

    private static async Task<CurrentNoteResolution> ResolveCurrentNoteAsync(string clonePath, HydrationUnit unit, CancellationToken ct)
    {
        foreach (string candidate in CurrentNoteCandidates(unit))
        {
            IReadOnlyList<ClassifiedNoteBlock> blocks = await BallotNoteHtmlExtractor
                .ExtractClassifiedAtHeadAsync(clonePath, candidate, ct).ConfigureAwait(false);
            if (blocks.Count == 0) continue;

            ClassifiedNoteBlock? generated = blocks.FirstOrDefault(b => b.IsAuguryGenerated);
            // The replace-target is the augury-generated block specifically; fall
            // back to the first block as revision context when none is marked.
            string current = generated?.Html ?? blocks[0].Html;
            string preserved = string.Join(
                "\n",
                blocks.Where(b => !b.IsAuguryGenerated).Select(b => b.Html));
            return new CurrentNoteResolution(current, generated is not null, preserved);
        }
        return CurrentNoteResolution.Empty;
    }

    /// <summary>The classified current-note evidence for a unit at HEAD.</summary>
    private readonly record struct CurrentNoteResolution(string CurrentHtml, bool IsAuguryGenerated, string PreservedHandAuthoredHtml)
    {
        public static CurrentNoteResolution Empty { get; } = new(string.Empty, false, string.Empty);
    }

    private static IEnumerable<string> CurrentNoteCandidates(HydrationUnit unit) => unit.Type switch
    {
        "Page" => [$"source/{unit.Name}.html"],
        "DataType" => ["source/datatypes.html"],
        _ =>
        [
            $"source/{unit.Name}/{unit.Name}-introduction.xml",
            $"source/{unit.Name}/{unit.Name}-notes.xml",
            $"source/{unit.Name}/{unit.Name}-introduction.md",
            $"source/{unit.Name}/{unit.Name}.html",
        ],
    };

    private async Task<IReadOnlySet<string>> ComputeDatatypeOwnedPagesAsync(string clonePath, CancellationToken ct)
    {
        IReadOnlyList<string> topLevel = await ListTreeAsync(clonePath, "source/", ct).ConfigureAwait(false);
        HashSet<string> htmlPages = new(StringComparer.OrdinalIgnoreCase);
        List<string> datatypeNames = [];

        IReadOnlyList<string> datatypeTree = await ListTreeAsync(clonePath, "source/datatypes/", ct).ConfigureAwait(false);
        foreach (string path in datatypeTree)
        {
            // Top-level datatype SD files: source/datatypes/<stem>.xml (no nesting).
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            string remainder = path["source/datatypes/".Length..];
            if (remainder.Contains('/')) continue;
            string stem = remainder[..^".xml".Length];
            if (stem.Contains('-')) continue; // skip intro/example variants
            datatypeNames.Add(stem);
        }

        foreach (string path in topLevel)
        {
            if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
            string remainder = path["source/".Length..];
            if (remainder.Contains('/')) continue;
            htmlPages.Add(path);
        }

        return DatatypePageMap.ComputeOwnedPages(datatypeNames, htmlPages.Contains);
    }

    private static async Task<IReadOnlyList<string>> ListTreeAsync(string clonePath, string pathspec, CancellationToken ct)
    {
        GitRunner.GitResult result = await GitRunner.TryRunAsync(
            clonePath, ["ls-tree", "-r", "--name-only", "HEAD", "--", pathspec], ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return [];

        List<string> files = [];
        foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) files.Add(trimmed);
        }
        return files;
    }

    private static bool IsFhirCore(string owner, string name)
        => string.Equals(owner, "HL7", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "fhir", StringComparison.OrdinalIgnoreCase);

    private static string ShortSha(string fullSha)
    {
        string full = fullSha.Trim();
        return full.Length > 12 ? full[..12] : full;
    }

    /// <summary>Lowercase slug; non-alphanumeric runs collapse to a single hyphen.</summary>
    private static string Slugify(string value)
    {
        System.Text.StringBuilder sb = new(value.Length);
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
