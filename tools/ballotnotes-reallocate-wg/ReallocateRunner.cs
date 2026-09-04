using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using Microsoft.Data.Sqlite;
using System.Net.Http.Json;

namespace FhirAugury.Tools.BallotNotesReallocateWg;

/// <summary>
/// Orchestrates the <c>reallocate</c> verb: preflights the reference DBs and
/// clone, reconstructs each note's resolver inputs from already-persisted rows,
/// calls the merged <c>OwningWorkGroupResolver.Resolve</c> (the single source of
/// truth), and re-stamps the four owning-WG columns. <c>--dry-run</c> previews
/// changes and writes nothing.
/// </summary>
internal static class ReallocateRunner
{
    /// <summary>A single note whose resolved owning WG differs from what is stored.</summary>
    private readonly record struct WgChange(
        string NoteId,
        string ExpectedEvidenceRevision,
        string FromPrimary,
        string ToPrimary,
        string FromNames,
        string ToNames,
        string ToPrimaryCode,
        string ToCodes);

    public static async Task<int> RunAsync(
        ReallocateOptions options,
        CancellationToken ct = default,
        HttpMessageHandler? processorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        string notesDb = Path.GetFullPath(options.DbPath);
        if (!File.Exists(notesDb))
        {
            await Console.Error.WriteLineAsync(
                $"Notes DB not found: {notesDb}. Run the BallotNotes processor 'hydrate' first.").ConfigureAwait(false);
            return 1;
        }

        string clonePath = Path.GetFullPath(options.ClonePath);
        if (!Directory.Exists(clonePath))
        {
            await Console.Error.WriteLineAsync($"Clone directory not found: {clonePath}.").ConfigureAwait(false);
            return 1;
        }

        string cloneHead;
        try
        {
            cloneHead = (await GitRunner.RunAsync(clonePath, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Console.Error.WriteLineAsync(
                $"Clone directory is not a git work tree (git rev-parse HEAD failed): {clonePath}. {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        if (!await PreflightReferenceDbsAsync(options).ConfigureAwait(false))
        {
            return 1;
        }

        BallotNotesHydrationOptions hydrationOptions = new()
        {
            GitHubDbPath = options.GitHubDbPath,
            FhirR6DbPath = options.FhirR6DbPath,
            FhirSpecDbPath = options.FhirSpecDbPath,
        };

        bool processorBatch = options.ProcessorBaseUrl is not null;
        if (!options.DryRun &&
            !processorBatch &&
            !await DirectWriteModeAllowedAsync(notesDb, ct))
        {
            return 1;
        }

        using BallotNotesDatabase db = new(
            notesDb,
            ConsoleLogger.Instance,
            readOnly: options.DryRun || processorBatch);

        // Migrate legacy notes DBs (additive columns added since the DB was first
        // created) before reading: ReadNote selects every current column, so a
        // pre-migration DB would otherwise fail with "no such column". EnsureSchema
        // requires write access, so it is skipped for the read-only --dry-run path
        // (a stale DB there must first be migrated by a real hydrate/reallocate run).
        if (!options.DryRun && !processorBatch)
        {
            db.Initialize();
        }

        IReadOnlyList<string> noteIds = db.ListNoteIds(options.Repo);
        if (noteIds.Count == 0)
        {
            Console.WriteLine(options.Repo is null
                ? "No notes in the database."
                : $"No notes for repo '{options.Repo}'.");
            return 0;
        }

        List<NoteDetail> details = new(noteIds.Count);
        foreach (string id in noteIds)
        {
            NoteDetail? detail = db.GetNote(id);
            if (detail is not null) details.Add(detail);
        }

        // Multi-repo guard: one --clone serves one repo.
        string[] distinctRepos = [.. details
            .Select(d => $"{d.Note.RepoOwner}/{d.Note.RepoName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (distinctRepos.Length > 1)
        {
            await Console.Error.WriteLineAsync(
                $"Notes span {distinctRepos.Length} repositories ({string.Join(", ", distinctRepos)}); a single --clone " +
                "can't serve multiple repos. Pass --repo <owner/name> to scope the run.").ConfigureAwait(false);
            return 1;
        }

        // Clone-fidelity guard: the resolver reads live clone files / git ls-tree HEAD,
        // so a divergent checkout silently changes datatype membership and markers.
        string[] distinctHeads = [.. details
            .Select(d => d.Note.HeadSha)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (distinctHeads.Length > 1 && !options.AllowMixedHeads)
        {
            await Console.Error.WriteLineAsync(
                $"Selected notes span {distinctHeads.Length} distinct HeadSha values; one clone is a single checkout. " +
                "Scope with --repo, or pass --allow-mixed-heads to proceed anyway.").ConfigureAwait(false);
            return 1;
        }

        if (!options.AllowStaleClone)
        {
            if (distinctHeads.Length == 1
                && !string.Equals(distinctHeads[0], cloneHead, StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync(
                    $"Clone HEAD ({cloneHead}) does not match the notes' HeadSha ({distinctHeads[0]}). " +
                    "Check out the matching commit, or pass --allow-stale-clone to proceed anyway.").ConfigureAwait(false);
                return 1;
            }
            if (distinctHeads.Length > 1)
            {
                await Console.Error.WriteLineAsync(
                    "[Warning] --allow-mixed-heads set: cannot verify clone HEAD against multiple note HeadSha values; " +
                    "re-stamps use the current checkout.").ConfigureAwait(false);
            }
        }

        string? hint = string.IsNullOrEmpty(options.WorkGroupHint) ? null : options.WorkGroupHint;
        List<WgChange> changes = [];

        foreach (NoteDetail detail in details)
        {
            HydrationUnit unit = new()
            {
                Type = detail.Note.Type,
                Name = detail.Note.Name,
                ChangedPaths = [.. detail.SourceFiles.Select(f => f.Path)],
            };

            List<ResolvedSourceFile> resolvedFiles = [.. detail.SourceFiles.Select(f => new ResolvedSourceFile
            {
                Path = f.Path,
                Role = f.Role,
                TouchedInWindow = f.TouchedInWindow,
            })];

            UnitAttribution attribution = new()
            {
                Tickets = [.. detail.Tickets.Select(t => new AttributedTicket
                {
                    Key = t.TicketKey,
                    Title = t.Title,
                    Resolution = t.Resolution,
                    WorkGroup = t.WorkGroup,
                    Specification = t.Specification,
                    Url = t.Url,
                    ChangeImpact = t.ChangeImpact,
                    ChangeCategory = t.ChangeCategory,
                    IssueType = t.IssueType,
                    RelatedTicketKeys = t.RelatedTicketKeys,
                    CommitCount = t.CommitCount,
                })],
                CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            };

            IReadOnlyList<string> headDatatypeNames =
                string.Equals(detail.Note.Type, "DataType", StringComparison.OrdinalIgnoreCase)
                    ? await HeadDatatypeLister.ListAsync(clonePath, ct).ConfigureAwait(false)
                    : [];

            IReadOnlyList<WorkGroupRef> refs = OwningWorkGroupResolver.Resolve(
                unit, clonePath, detail.Note.RepoOwner, detail.Note.RepoName, attribution,
                resolvedFiles, headDatatypeNames, hint, hydrationOptions, ConsoleLogger.Instance);

            WorkGroupRef primary = refs.Count > 0 ? refs[0] : WorkGroupRef.Unknown;
            string toPrimary = primary.DisplayName;
            string toPrimaryCode = primary.Code;
            string toNames = WorkGroupRef.JoinNames(refs);
            string toCodes = WorkGroupRef.JoinCodes(refs);

            bool unchanged =
                string.Equals(detail.Note.WorkGroup, toPrimary, StringComparison.Ordinal)
                && string.Equals(detail.Note.WorkGroupCode, toPrimaryCode, StringComparison.Ordinal)
                && string.Equals(detail.Note.WorkGroupNames, toNames, StringComparison.Ordinal)
                && string.Equals(detail.Note.WorkGroupCodes, toCodes, StringComparison.Ordinal);

            if (unchanged) continue;

            changes.Add(new WgChange(
                detail.Note.NoteId,
                detail.Note.CurrentEvidenceRevision,
                Display(detail.Note.WorkGroup), Display(toPrimary),
                Display(detail.Note.WorkGroupNames), Display(toNames),
                toPrimaryCode,
                toCodes));
        }

        if (!options.DryRun && changes.Count > 0)
        {
            BallotNotesWorkGroupReallocationRequest request =
                CreateBatchRequest(changes);
            if (processorBatch)
            {
                int submitted = await SubmitProcessorBatchAsync(
                    options.ProcessorBaseUrl!,
                    request,
                    processorHandler,
                    ct);
                if (submitted != changes.Count)
                {
                    await Console.Error.WriteLineAsync(
                        $"Processor reported {submitted} updates for a {changes.Count}-change batch.");
                    return 1;
                }
            }
            else
            {
                try
                {
                    BallotNotesWorkGroupReallocationResult result =
                        await db.ReallocateWorkGroupsLegacyAsync(request, ct);
                    if (result.UpdatedCount != changes.Count)
                    {
                        await Console.Error.WriteLineAsync(
                            $"Direct reallocation reported {result.UpdatedCount} updates for a {changes.Count}-change batch.");
                        return 1;
                    }
                }
                catch (AuthoringConflictException ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Direct reallocation batch was rejected: {ex.Message}");
                    return 1;
                }
            }
        }

        foreach (WgChange change in changes)
        {
            Console.WriteLine(
                $"  {change.NoteId}: {change.FromPrimary} -> {change.ToPrimary} | names: {change.FromNames} -> {change.ToNames}");
        }

        string mode = options.DryRun ? " (dry-run, nothing written)" : string.Empty;
        Console.WriteLine($"inspected: {details.Count}, changed: {changes.Count}{mode}");
        return 0;
    }

    private static async Task<int> SubmitProcessorBatchAsync(
        Uri processorBaseUrl,
        BallotNotesWorkGroupReallocationRequest request,
        HttpMessageHandler? handler,
        CancellationToken ct)
    {
        using HttpClient client = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(
            processorBaseUrl.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? processorBaseUrl.AbsoluteUri
                : $"{processorBaseUrl.AbsoluteUri}/");
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/v1/ballot-notes/maintenance/workgroups/reallocate",
            request,
            ct);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(ct);
            await Console.Error.WriteLineAsync(
                $"Processor reallocation batch failed with {(int)response.StatusCode}: {detail}");
            return -1;
        }
        BallotNotesWorkGroupReallocationResult? result =
            await response.Content.ReadFromJsonAsync<
                BallotNotesWorkGroupReallocationResult>(
                cancellationToken: ct);
        return result?.UpdatedCount ?? -1;
    }

    private static BallotNotesWorkGroupReallocationRequest CreateBatchRequest(
        IReadOnlyList<WgChange> changes) =>
        new(
            changes.Select(change => new BallotNoteWorkGroupReallocation(
                change.NoteId,
                change.ExpectedEvidenceRevision,
                change.ToPrimary == "(none)" ? string.Empty : change.ToPrimary,
                change.ToPrimaryCode,
                change.ToNames == "(none)" ? string.Empty : change.ToNames,
                change.ToCodes)).ToArray());

    private static async Task<bool> DirectWriteModeAllowedAsync(
        string dbPath,
        CancellationToken ct)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(ct);
        await using SqliteCommand table = connection.CreateCommand();
        table.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'authoring_processor_modes'";
        if (await table.ExecuteScalarAsync(ct) is null)
        {
            return true;
        }

        await using SqliteCommand mode = connection.CreateCommand();
        mode.CommandText =
            """
            SELECT Mode
            FROM authoring_processor_modes
            WHERE Mode IN ('run-backed', 'cutting-over')
            LIMIT 1
            """;
        string? blockedMode = Convert.ToString(await mode.ExecuteScalarAsync(ct));
        if (string.IsNullOrWhiteSpace(blockedMode))
        {
            return true;
        }

        await Console.Error.WriteLineAsync(
            $"Direct reallocation writes are unavailable while processor mode is '{blockedMode}'; use --processor.");
        return false;
    }

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "(none)" : value;

    /// <summary>
    /// Opens the reference DBs read-only and confirms the tables the resolver
    /// relies on are present, so a missing/drifted DB cannot silently downgrade
    /// output to <c>(unknown)</c>/raw codes. Fails loudly before any restamp.
    /// </summary>
    private static async Task<bool> PreflightReferenceDbsAsync(ReallocateOptions options)
    {
        string githubDb = Path.GetFullPath(options.GitHubDbPath);
        if (!File.Exists(githubDb))
        {
            await Console.Error.WriteLineAsync(
                $"GitHub source DB not found: {githubDb} (resolver registry/WG tables). " +
                "Pass --github-db, or run ingestion first.").ConfigureAwait(false);
            return false;
        }

        if (!TryGetTables(githubDb, out HashSet<string> githubTables, out string? githubError))
        {
            await Console.Error.WriteLineAsync($"Could not open GitHub source DB {githubDb}: {githubError}").ConfigureAwait(false);
            return false;
        }

        foreach (string required in new[] { "hl7_workgroups", "jira_workgroups" })
        {
            if (!githubTables.Contains(required))
            {
                await Console.Error.WriteLineAsync(
                    $"GitHub source DB {githubDb} is missing required table '{required}'. " +
                    "Output would silently downgrade; aborting.").ConfigureAwait(false);
                return false;
            }
        }
        if (!githubTables.Contains("jira_spec_artifacts") && !githubTables.Contains("jira_spec_pages"))
        {
            await Console.Error.WriteLineAsync(
                $"GitHub source DB {githubDb} has neither 'jira_spec_artifacts' nor 'jira_spec_pages' (the registry). " +
                "Output would silently downgrade; aborting.").ConfigureAwait(false);
            return false;
        }

        // At least one spec DB with a Structures table must be available.
        bool anySpec = false;
        foreach (string specPath in new[] { options.FhirR6DbPath, options.FhirSpecDbPath })
        {
            string full = Path.GetFullPath(specPath);
            if (!File.Exists(full)) continue;
            if (TryGetTables(full, out HashSet<string> tables, out _) && tables.Contains("Structures"))
            {
                anySpec = true;
                break;
            }
        }
        if (!anySpec)
        {
            await Console.Error.WriteLineAsync(
                $"No usable spec reference DB with a 'Structures' table found at --fhir-r6-db " +
                $"({options.FhirR6DbPath}) or --fhir-spec-db ({options.FhirSpecDbPath}). " +
                "Output would silently downgrade; aborting.").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static bool TryGetTables(string dbPath, out HashSet<string> tables, out string? error)
    {
        tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        try
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString;

            using SqliteConnection connection = new(connectionString);
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
            return true;
        }
        catch (SqliteException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
