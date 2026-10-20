using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Client;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets;

internal sealed record TicketSitePublisherTestHooks(
    Func<ImmutableFileSnapshot, ValueTask>? DisposeSnapshotAsync = null,
    Func<string, ValueTask>? DeleteFilteredDatabaseAsync = null,
    Func<string, CancellationToken, Task>? BeforeStageValidationAsync = null,
    Func<bool, bool, CancellationToken, Task>? BeforeChooserCommitAsync = null,
    Func<string, FileStream>? OpenOutputRootLockFile = null,
    Func<string, CancellationToken, Task>?
        AfterDiscussionRendererBytesCapturedAsync = null);

public sealed class TicketSitePublisher : ITicketSitePublisher
{
    private readonly TicketSitePublisherTestHooks? _testHooks;
    private readonly Action<string>? _warningSink;

    public TicketSitePublisher()
    {
    }

    public TicketSitePublisher(Action<string> warningSink)
    {
        _warningSink = warningSink ??
            throw new ArgumentNullException(nameof(warningSink));
    }

    internal TicketSitePublisher(
        TicketSitePublisherTestHooks testHooks,
        Action<string>? warningSink = null)
    {
        _testHooks = testHooks;
        _warningSink = warningSink;
    }

    public async Task<TicketSitePublishResult> PublishAsync(
        TicketSitePublishRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SnapshotPair);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);

        string serviceName = ExpectedService(request.SiteKind);
        if (!string.Equals(
                request.SnapshotPair.ServiceName,
                serviceName,
                StringComparison.Ordinal))
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.ServiceMismatch,
                $"A {request.SiteKind.ToString().ToLowerInvariant()} site requires a " +
                $"{serviceName} snapshot pair, but the pair is bound to " +
                $"'{request.SnapshotPair.ServiceName}'.");
        }

        ct.ThrowIfCancellationRequested();
        VerifiedAuthoringSnapshotPair pair =
            await ReverifyPairAsync(request.SnapshotPair, ct).ConfigureAwait(false);
        string outputRoot = Path.GetFullPath(request.OutputRoot);
        string siteFolder = request.SiteKind == TicketSiteKind.Discussion
            ? PreparerSubSiteEmitter.SubSiteFolder
            : PlannerSubSiteEmitter.SubSiteFolder;
        string siteOutput = Path.Combine(outputRoot, siteFolder);
        ValidatePathSeparation(outputRoot, siteOutput, pair);

        List<string> warnings = [];
        ImmutableFileSnapshot? privateSnapshot = null;
        string? filteredDatabasePath = null;
        bool ownsFilteredDatabase = false;
        TicketSitePublishResult? successResult = null;
        try
        {
            privateSnapshot = await CreatePrivateSnapshotAsync(
                pair,
                warnings,
                ct).ConfigureAwait(false);

            string siteKind = SiteKindValue(request.SiteKind);
            HydrationAssertion.SnapshotValidationResult validation =
                await ValidateSnapshotAsync(
                    privateSnapshot,
                    pair.Descriptor,
                    request.SiteKind,
                    ct).ConfigureAwait(false);

            ResolvedFilters filters = await FilterResolver.ResolveAsync(
                privateSnapshot.Path,
                request.Filters ?? TicketSiteFilters.None,
                request.SiteKind,
                ct).ConfigureAwait(false);

            TicketSiteEmbeddedDatabaseBuild embeddedBuild;
            if (request.SiteKind == TicketSiteKind.Discussion)
            {
                DiscussionSiteDatabaseBuilder.BuildResult built =
                    await DiscussionSiteDatabaseBuilder.BuildAsync(
                        privateSnapshot.Path,
                        validation.Descriptor,
                        request.Title,
                        filters,
                        ct).ConfigureAwait(false);
                filteredDatabasePath = built.TempDbPath;
                ownsFilteredDatabase = built.OwnsTempFile;
                byte[] capturedRendererBytes =
                    await ReadAllBytesWithTransientRetryAsync(
                        built.TempDbPath,
                        ct).ConfigureAwait(false);
                if (_testHooks?.AfterDiscussionRendererBytesCapturedAsync
                    is { } capturedHook)
                {
                    await capturedHook(built.TempDbPath, ct)
                        .ConfigureAwait(false);
                }
                DiscussionSiteDatabaseValidator.ValidationResult
                    rendererValidation =
                        await ValidateDiscussionRendererBytesAsync(
                            capturedRendererBytes,
                            privateSnapshot.Path,
                            validation.Descriptor,
                            request.Title,
                            filters,
                            ct).ConfigureAwait(false);
                if (!string.Equals(
                    TicketSitePresentationJson.Serialize(
                        rendererValidation.Presentation),
                    TicketSitePresentationJson.Serialize(
                        built.Presentation),
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Discussion renderer presentation changed during validation.");
                }
                embeddedBuild = new TicketSiteEmbeddedDatabaseBuild(
                    capturedRendererBytes,
                    built.SurvivingTicketCount,
                    rendererValidation.TableCounts,
                    rendererValidation.Presentation);
                if (!rendererValidation.Presentation.Readiness.IsReady)
                {
                    RecordWarning(
                        warnings,
                        "Warning: discussion publication readiness is degraded: " +
                        string.Join(
                            ", ",
                            rendererValidation.Presentation.Readiness.Reasons
                                .Select(reason => reason.Code)) +
                        ".");
                }
                DiscussionCorpusSummary corpus =
                    rendererValidation.Presentation.CorpusSummary;
                if (corpus.DateCoverage != DiscussionDateCoverage.Complete)
                {
                    RecordWarning(
                        warnings,
                        $"Warning: discussion Jira update-date coverage is {corpus.DateCoverage}: " +
                        $"{corpus.ValidJiraUpdatedAtCount}/{corpus.TicketCount} tickets have valid self-ticket dates. " +
                        "The ticket-date title suffix is omitted.");
                }
                if (corpus.TicketsWithPublicReporter < corpus.TicketCount ||
                    corpus.TicketsWithPublicAssignee < corpus.TicketCount ||
                    corpus.TicketsWithPublicRequester < corpus.TicketCount)
                {
                    RecordWarning(
                        warnings,
                        "Warning: discussion public-name coverage: " +
                        $"reporter={corpus.TicketsWithPublicReporter}/{corpus.TicketCount}, " +
                        $"assignee={corpus.TicketsWithPublicAssignee}/{corpus.TicketCount}, " +
                        $"in-person requester={corpus.TicketsWithPublicRequester}/{corpus.TicketCount}. " +
                        "Missing names do not establish that a role is absent.");
                }
                foreach (DiscussionLinkCoverage links in corpus.LinksByKind)
                {
                    if (links.UnresolvedWithRetainedSafeLinks > 0 ||
                        links.WithoutUsableUrl > 0)
                    {
                        RecordWarning(
                            warnings,
                            $"Warning: discussion {links.Kind} link coverage: " +
                            $"{links.ResolvedSafeLinks}/{links.TotalRows} resolved safe links, " +
                            $"{links.UnresolvedWithRetainedSafeLinks} unresolved with retained safe URLs, " +
                            $"{links.WithoutUsableUrl} without usable URLs. " +
                            "A safe URL alone does not establish source backing.");
                    }
                }
            }
            else
            {
                PlannerDbTrimmer.BuildResult built =
                    await PlannerDbTrimmer.BuildAsync(
                        privateSnapshot.Path,
                        filters,
                        ct).ConfigureAwait(false);
                filteredDatabasePath = built.TempDbPath;
                ownsFilteredDatabase = built.OwnsTempFile;
                IReadOnlyDictionary<string, long> tableCounts =
                    await HydrationAssertion.ReadManifestCountsAsync(
                        built.TempDbPath,
                        request.SiteKind,
                        ct).ConfigureAwait(false);
                embeddedBuild = new TicketSiteEmbeddedDatabaseBuild(
                    await ReadAllBytesWithTransientRetryAsync(
                        built.TempDbPath,
                        ct).ConfigureAwait(false),
                    built.SurvivingTicketCount,
                    tableCounts,
                    DiscussionPresentation: null);
            }

            byte[] databaseBytes = embeddedBuild.DatabaseBytes;
            string embeddedSha256 = ComputeSha256(databaseBytes, ct);
            DateTimeOffset generatedAt = DateTimeOffset.UtcNow;
            string rendererAssetsVersion =
                request.SiteKind == TicketSiteKind.Discussion
                    ? PreparerSubSiteEmitter.RendererAssetsVersion
                    : PlannerSubSiteEmitter.RendererAssetsVersion;
            TicketSiteManifest manifest = TicketSiteManifest.Create(
                siteKind,
                validation.Descriptor,
                checked((int)embeddedBuild.IncludedItemCount),
                embeddedBuild.TableCounts,
                filters,
                request.Title,
                rendererAssetsVersion,
                embeddedSha256,
                databaseBytes.LongLength,
                siteOutput,
                generatedAt,
                embeddedBuild.DiscussionPresentation);

            StagedDirectoryPublishResult stagedResult;
            TicketSiteManifest publishedManifest;
            using (FileStream outputRootLock =
                await TicketSiteOutputRootLock.AcquireAsync(
                        outputRoot,
                        ct,
                        _testHooks?.OpenOutputRootLockFile)
                    .ConfigureAwait(false))
            {
                stagedResult = await StagedDirectoryPublisher.PublishAsync(
                        siteOutput,
                        StagedDirectoryVersion.Snapshot(
                            $"ticket-site:{siteKind}",
                            validation.Descriptor.ProcessorKind,
                            validation.Descriptor.Sequence,
                            validation.Descriptor.SnapshotId,
                            manifest.BuildIdentity),
                        async (staging, renderToken) =>
                        {
                            if (request.SiteKind == TicketSiteKind.Discussion)
                            {
                                await PreparerSubSiteEmitter.EmitAsync(
                                    staging,
                                    embeddedBuild.DiscussionPresentation
                                    ?? throw new InvalidOperationException(
                                        "Discussion presentation is unavailable."),
                                    databaseBytes,
                                    renderToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await PlannerSubSiteEmitter.EmitAsync(
                                    staging,
                                    request.Title,
                                    filters,
                                    databaseBytes,
                                    renderToken).ConfigureAwait(false);
                            }
                            await OutputDirGuard.WriteMarkerAsync(
                                staging,
                                siteKind,
                                filters,
                                generatedAt,
                                validation.Descriptor,
                                renderToken).ConfigureAwait(false);
                            await TicketSiteManifest.WriteAsync(
                                staging,
                                manifest,
                                renderToken).ConfigureAwait(false);
                        },
                        async (staging, validationToken) =>
                        {
                            if (_testHooks?.BeforeStageValidationAsync is { } hook)
                            {
                                await hook(staging, validationToken)
                                    .ConfigureAwait(false);
                            }
                            await OutputDirGuard.ValidateSnapshotStageAsync(
                                staging,
                                siteKind,
                                manifest,
                                validationToken).ConfigureAwait(false);
                        },
                        new StagedDirectoryPublishOptions(Force: request.Force),
                        ct).ConfigureAwait(false);

                // The sub-site is committed once PublishAsync returns. From
                // this point onward, caller cancellation or a chooser refresh
                // failure must not report that durable commit as a failure.
                publishedManifest =
                    stagedResult.Outcome == StagedDirectoryPublishOutcome.Idempotent
                        ? await TicketSiteManifest.ReadAsync(
                            Path.Combine(siteOutput, TicketSiteManifest.FileName),
                            CancellationToken.None).ConfigureAwait(false)
                        : manifest;
                try
                {
                    await ChooserPageEmitter.EmitAsync(
                            outputRoot,
                            _testHooks?.BeforeChooserCommitAsync,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException or IOException or
                    UnauthorizedAccessException or InvalidOperationException or
                    ArgumentException or NotSupportedException)
                {
                    RecordWarning(
                        warnings,
                        "Warning: the ticket sub-site was committed, but the " +
                        $"chooser could not be refreshed: {ex.Message}");
                }
            }

            successResult = new TicketSitePublishResult(
                stagedResult.Outcome == StagedDirectoryPublishOutcome.Idempotent
                    ? TicketSitePublishOutcome.Idempotent
                    : TicketSitePublishOutcome.Published,
                publishedManifest,
                outputRoot,
                siteOutput,
                filters.ToPublic(),
                Array.Empty<string>());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TicketSitePublishException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or
            NotSupportedException or JsonException or SqliteException or
            CryptographicException or OverflowException)
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.Publication,
                ex.Message,
                ex);
        }
        finally
        {
            if (ownsFilteredDatabase && filteredDatabasePath is not null)
            {
                await CleanupFilteredDatabaseBestEffortAsync(
                    filteredDatabasePath,
                    warnings).ConfigureAwait(false);
            }
            if (privateSnapshot is not null)
            {
                await CleanupSnapshotBestEffortAsync(
                    privateSnapshot,
                    warnings).ConfigureAwait(false);
            }
        }

        if (successResult is null)
        {
            throw new InvalidOperationException(
                "Ticket site publication completed without a result.");
        }
        return successResult with
        {
            Warnings = Array.AsReadOnly(warnings.ToArray()),
        };
    }

    private static async Task<VerifiedAuthoringSnapshotPair> ReverifyPairAsync(
        VerifiedAuthoringSnapshotPair pair,
        CancellationToken ct)
    {
        if (!pair.IsDurable)
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.SnapshotValidation,
                "Ticket site publication requires a durable verified snapshot pair.");
        }

        try
        {
            return await new AuthoringSnapshotPairVerifier()
                .VerifyReadyPairAsync(
                    pair.ServiceName,
                    pair.RunId,
                    pair.DirectoryPath,
                    ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.SnapshotValidation,
                ex.Message,
                ex);
        }
    }

    private async Task<ImmutableFileSnapshot> CreatePrivateSnapshotAsync(
        VerifiedAuthoringSnapshotPair pair,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        try
        {
            return await ImmutableFileSnapshot.CreateAsync(
                pair.DatabasePath,
                ct,
                warning => RecordWarning(warnings, $"Warning: {warning}"))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.SnapshotValidation,
                ex.Message,
                ex);
        }
    }

    private static async Task<HydrationAssertion.SnapshotValidationResult>
        ValidateSnapshotAsync(
            ImmutableFileSnapshot snapshot,
            FhirAugury.Processing.Contracts.AuthoringSnapshotDescriptor descriptor,
            TicketSiteKind siteKind,
            CancellationToken ct)
    {
        try
        {
            return await HydrationAssertion.ValidateSnapshotAsync(
                snapshot,
                descriptor,
                siteKind,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            SqliteException or InvalidOperationException or FormatException or
            InvalidCastException or OverflowException)
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.SnapshotValidation,
                ex.Message,
                ex);
        }
    }

    private static void ValidatePathSeparation(
        string outputRoot,
        string siteOutput,
        VerifiedAuthoringSnapshotPair pair)
    {
        string[] sourcePaths =
        [
            pair.DatabasePath,
            pair.DescriptorPath,
            pair.ReadyManifestPath!,
        ];
        try
        {
            StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                outputRoot,
                sourcePaths);
            StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                siteOutput,
                sourcePaths);
        }
        catch (InvalidOperationException ex)
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.InvalidRequest,
                ex.Message,
                ex);
        }

        if (IsSameOrDescendant(outputRoot, pair.DirectoryPath))
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.InvalidRequest,
                $"Output root '{outputRoot}' cannot be inside snapshot pair directory " +
                $"'{pair.DirectoryPath}'.");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string directory)
    {
        string relative = Path.GetRelativePath(
            Path.GetFullPath(directory),
            Path.GetFullPath(candidate));
        return string.Equals(relative, ".", StringComparison.Ordinal) ||
            (!Path.IsPathRooted(relative) &&
             !string.Equals(relative, "..", StringComparison.Ordinal) &&
             !relative.StartsWith(
                 $"..{Path.DirectorySeparatorChar}",
                 StringComparison.Ordinal) &&
             !relative.StartsWith(
                 $"..{Path.AltDirectorySeparatorChar}",
                 StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadAllBytesWithTransientRetryAsync(
        string path,
        CancellationToken ct)
    {
        const int maxAttempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                long length = stream.Length;
                if (length > int.MaxValue)
                {
                    throw new IOException(
                        $"Temp DB at '{path}' is too large to inline ({length} bytes).");
                }

                byte[] buffer = new byte[length];
                await stream.ReadExactlyAsync(buffer.AsMemory(), ct)
                    .ConfigureAwait(false);
                return buffer;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(50 * attempt),
                    ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(50 * attempt),
                    ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<
        DiscussionSiteDatabaseValidator.ValidationResult>
        ValidateDiscussionRendererBytesAsync(
            byte[] databaseBytes,
            string sourceDatabasePath,
            FhirAugury.Processing.Contracts.AuthoringSnapshotDescriptor descriptor,
            string baseTitle,
            ResolvedFilters filters,
            CancellationToken ct)
    {
        string validationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-renderer-validation-{Guid.NewGuid():N}");
        string validationPath = Path.Combine(
            validationDirectory,
            "discussion-renderer.db");
        Directory.CreateDirectory(validationDirectory);
        try
        {
            await using (FileStream validationGuard = new(
                validationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await validationGuard.WriteAsync(databaseBytes, ct)
                    .ConfigureAwait(false);
                await validationGuard.FlushAsync(ct).ConfigureAwait(false);
                return await DiscussionSiteDatabaseValidator.ValidateAsync(
                    validationPath,
                    sourceDatabasePath,
                    descriptor,
                    baseTitle,
                    filters,
                    ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(validationPath))
            {
                File.Delete(validationPath);
            }
            Directory.Delete(validationDirectory);
        }
    }

    private static string ComputeSha256(
        byte[] bytes,
        CancellationToken ct)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunkSize = 128 * 1024;
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(chunkSize, bytes.Length - offset);
            hash.AppendData(bytes, offset, count);
        }
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private async ValueTask CleanupFilteredDatabaseBestEffortAsync(
        string path,
        ICollection<string> warnings)
    {
        try
        {
            if (_testHooks?.DeleteFilteredDatabaseAsync is { } cleanup)
            {
                await cleanup(path).ConfigureAwait(false);
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            RecordWarning(
                warnings,
                $"Warning: deferred cleanup of filtered snapshot database '{path}': {ex.Message}");
        }
    }

    private async ValueTask CleanupSnapshotBestEffortAsync(
        ImmutableFileSnapshot snapshot,
        ICollection<string> warnings)
    {
        try
        {
            if (_testHooks?.DisposeSnapshotAsync is { } cleanup)
            {
                await cleanup(snapshot).ConfigureAwait(false);
            }
            else
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            RecordWarning(
                warnings,
                $"Warning: deferred cleanup of private snapshot '{snapshot.Path}': {ex.Message}");
        }
    }

    private void RecordWarning(
        ICollection<string> warnings,
        string warning)
    {
        warnings.Add(warning);
        try
        {
            _warningSink?.Invoke(warning);
        }
        catch (Exception)
        {
            // Warning observation must not change the publication outcome.
        }
    }

    private static string ExpectedService(TicketSiteKind siteKind)
        => siteKind switch
        {
            TicketSiteKind.Discussion => "Preparer",
            TicketSiteKind.Applying => "Planner",
            _ => throw new TicketSitePublishException(
                TicketSitePublishFailure.InvalidRequest,
                $"Unknown ticket site kind '{siteKind}'."),
        };

    private static string SiteKindValue(TicketSiteKind siteKind)
        => siteKind switch
        {
            TicketSiteKind.Discussion => PreparerSubSiteEmitter.Kind,
            TicketSiteKind.Applying => PlannerSubSiteEmitter.Kind,
            _ => throw new TicketSitePublishException(
                TicketSitePublishFailure.InvalidRequest,
                $"Unknown ticket site kind '{siteKind}'."),
        };
}
