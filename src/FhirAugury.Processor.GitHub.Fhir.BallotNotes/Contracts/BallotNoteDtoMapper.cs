using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;

/// <summary>Maps persistence models to API DTOs.</summary>
internal static class BallotNoteDtoMapper
{
    public static BallotNoteDetailDto ToDetailDto(NoteDetail detail)
    {
        NoteRecord note = detail.Note;
        return new BallotNoteDetailDto
        {
            NoteId = note.NoteId,
            Type = note.Type,
            Name = note.Name,
            Status = detail.Status,
            RepoOwner = note.RepoOwner,
            RepoName = note.RepoName,
            RepoCategory = note.RepoCategory,
            WorkGroup = note.WorkGroup,
            WorkGroupCode = note.WorkGroupCode,
            SinceSha = note.SinceSha,
            SinceShortSha = note.SinceShortSha,
            HeadSha = note.HeadSha,
            HeadShortSha = note.HeadShortSha,
            WindowLabel = note.WindowLabel,
            CommitsInWindow = note.CommitsInWindow,
            TicketsAttributed = note.TicketsAttributed,
            NeedsNote = note.NeedsNote,
            CurrentBallotNoteHtml = note.CurrentBallotNoteHtml,
            CurrentNoteIsAuguryGenerated = note.CurrentNoteIsAuguryGenerated,
            PreservedHandAuthoredHtml = note.PreservedHandAuthoredHtml,
            ProposedBallotNoteHtml = note.ProposedBallotNoteHtml,
            RollupSummaryMarkdown = note.RollupSummaryMarkdown,
            NotesForReviewerMarkdown = note.NotesForReviewerMarkdown,
            SourceFilesNote = note.SourceFilesNote,
            HydratedAt = note.HydratedAt,
            AuthoredAt = note.AuthoredAt,
            GeneratedAt = note.GeneratedAt,
            SourceFiles = [.. detail.SourceFiles.Select(f => new NoteSourceFileDto
            {
                Path = f.Path,
                Role = f.Role,
                TouchedInWindow = f.TouchedInWindow,
            })],
            Commits = [.. detail.Commits.Select(c => new NoteCommitDto
            {
                Sha = c.Sha,
                ShortSha = c.ShortSha,
                AuthorName = c.AuthorName,
                AuthorDate = c.AuthorDate,
                Subject = c.Subject,
                WebUrl = c.WebUrl,
                TicketKeys = c.TicketKeys,
            })],
            Tickets = [.. detail.Tickets.Select(t => new NoteTicketDto
            {
                TicketKey = t.TicketKey,
                Title = t.Title,
                Resolution = t.Resolution,
                WorkGroup = t.WorkGroup,
                Specification = t.Specification,
                Url = t.Url,
                CommitCount = t.CommitCount,
                ChangeImpact = t.ChangeImpact,
                ChangeCategory = t.ChangeCategory,
                RelatedTicketKeys = string.IsNullOrEmpty(t.RelatedTicketKeys)
                    ? []
                    : [.. t.RelatedTicketKeys.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            })],
        };
    }

    public static HydrationStatusDto ToStatusDto(NotesRunRecord run) => new()
    {
        RunKey = run.RunKey,
        Status = run.Status,
        UnitsTotal = run.UnitsTotal,
        UnitsHydrated = run.UnitsHydrated,
        CommitsInWindow = run.CommitsInWindow,
        TicketsAttributed = run.TicketsAttributed,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        Error = run.Error,
    };
}
