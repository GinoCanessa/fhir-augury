using System.Security.Cryptography;
using FhirAugury.Cli.Dispatch.Handlers;
using FhirAugury.Cli.Models;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;

namespace FhirAugury.Cli.Tests.Authoring;

[Collection(AuthoringEnvironmentCollection.Name)]
public sealed class BallotNoteAuthoringHandlerTests
{
    [Fact]
    public async Task SupersedeUsesTypedOrchestratorRequest()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            Assert.Equal(
                "/api/v1/processing-services/BallotNotes/authoring/runs/run-1/items/item-1/supersede",
                request.RequestUri!.AbsolutePath);
            System.Text.Json.JsonElement body =
                System.Text.Json.JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Assert.Equal(
                "duplicate",
                body.GetProperty("reason").GetString());
            return DelegateHttpHandler.Json(
                new AuthoringItemSupersedeResult(
                    "run-1",
                    "item-1",
                    "superseded",
                    "duplicate"));
        });

        object result = await BallotNoteAuthoringHandler.HandleAsync(
            new BallotNoteAuthoringRequest
            {
                Action = "supersede",
                RunId = "run-1",
                ItemId = "item-1",
                Reason = "duplicate",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal(
            "superseded",
            Assert.IsType<AuthoringItemSupersedeResult>(result).Status);
    }

    [Fact]
    public async Task SupersedeRequiresReason()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new InvalidOperationException("HTTP should not be called."));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => BallotNoteAuthoringHandler.HandleAsync(
                new BallotNoteAuthoringRequest
                {
                    Action = "supersede",
                    RunId = "run-1",
                    ItemId = "item-1",
                },
                "http://orchestrator",
                CancellationToken.None,
                handler));

        Assert.Contains("reason", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SnapshotDownloadsVerifiedBytesAndDescriptorTogether()
    {
        AuthoringHttpClient.EnsureOuterMode();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-cli-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string snapshotPath = Path.Combine(directory, "friendly-name.db");
        string descriptorPath = Path.Combine(directory, "notes.json");
        string expectedSnapshotPath = Path.Combine(
            directory,
            "ballot-notes-generated.db");
        byte[] bytes = [1, 2, 3, 4, 5];
        string hash = Convert.ToHexString(
            SHA256.HashData(bytes)).ToLowerInvariant();
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                "/bytes",
                StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(
                    System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
            }
            return Task.FromResult(DelegateHttpHandler.Json(
                new AuthoringSnapshotDescriptor(
                    "github-fhir-ballot-notes",
                    "run-1",
                    "snapshot-1",
                    1,
                    4,
                    1,
                    hash,
                    bytes.Length,
                    1,
                    1,
                    new Dictionary<string, long>(),
                    "ballot-notes-generated.db",
                    DateTimeOffset.Parse("2026-09-04T00:00:00Z"))));
        });

        object result = await BallotNoteAuthoringHandler.HandleAsync(
            new BallotNoteAuthoringRequest
            {
                Action = "snapshot",
                RunId = "run-1",
                SnapshotPath = snapshotPath,
                DescriptorPath = descriptorPath,
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal(
            expectedSnapshotPath,
            System.Text.Json.JsonSerializer.SerializeToElement(result)
                .GetProperty("snapshotPath")
                .GetString());
        Assert.False(File.Exists(snapshotPath));
        Assert.Equal(
            bytes,
            await File.ReadAllBytesAsync(expectedSnapshotPath));
        Assert.Contains(
            "snapshot-1",
            await File.ReadAllTextAsync(descriptorPath));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task SnapshotCannotOverwritePairControlManifestPath()
    {
        AuthoringHttpClient.EnsureOuterMode();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-cli-snapshot-control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string snapshotPath = Path.Combine(directory, "notes.db");
            string descriptorPath = Path.Combine(
                directory,
                AuthoringSnapshotPairManifest.ReadyFileName);
            await File.WriteAllTextAsync(
                descriptorPath,
                "operator-owned");
            byte[] bytes = [1, 2, 3];
            string hash = Convert.ToHexString(
                    SHA256.HashData(bytes))
                .ToLowerInvariant();
            DelegateHttpHandler handler = new((request, _, _) =>
                Task.FromResult(
                    request.RequestUri!.AbsolutePath.EndsWith(
                        "/bytes",
                        StringComparison.Ordinal)
                        ? new HttpResponseMessage(
                            System.Net.HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(bytes),
                        }
                        : DelegateHttpHandler.Json(
                            new AuthoringSnapshotDescriptor(
                                "github-fhir-ballot-notes",
                                "run-1",
                                "snapshot-1",
                                1,
                                4,
                                1,
                                hash,
                                bytes.Length,
                                1,
                                1,
                                new Dictionary<string, long>(),
                                "notes.db",
                                DateTimeOffset.Parse(
                                    "2026-09-04T00:00:00Z")))));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                BallotNoteAuthoringHandler.HandleAsync(
                    new BallotNoteAuthoringRequest
                    {
                        Action = "snapshot",
                        RunId = "run-1",
                        SnapshotPath = snapshotPath,
                        DescriptorPath = descriptorPath,
                    },
                    "http://orchestrator",
                    CancellationToken.None,
                    handler));

            Assert.Equal(
                "operator-owned",
                await File.ReadAllTextAsync(descriptorPath));
            Assert.False(File.Exists(snapshotPath));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotPublicationRestoresPriorSnapshotWhenPairCannotComplete()
    {
        AuthoringHttpClient.EnsureOuterMode();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-cli-snapshot-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string snapshotPath = Path.Combine(directory, "notes.db");
        string descriptorPath = Path.Combine(directory, "descriptor-directory");
        Directory.CreateDirectory(descriptorPath);
        byte[] priorBytes = [9, 9, 9];
        await File.WriteAllBytesAsync(snapshotPath, priorBytes);
        byte[] bytes = [1, 2, 3, 4, 5];
        string hash = Convert.ToHexString(
            SHA256.HashData(bytes)).ToLowerInvariant();
        DelegateHttpHandler handler = new((request, _, _) =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/bytes",
                    StringComparison.Ordinal)
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes),
                    }
                    : DelegateHttpHandler.Json(
                        new AuthoringSnapshotDescriptor(
                            "github-fhir-ballot-notes",
                            "run-1",
                            "snapshot-1",
                            1,
                            4,
                            1,
                            hash,
                            bytes.Length,
                            1,
                            1,
                            new Dictionary<string, long>(),
                            "notes.db",
                            DateTimeOffset.Parse("2026-09-04T00:00:00Z")))));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            BallotNoteAuthoringHandler.HandleAsync(
                new BallotNoteAuthoringRequest
                {
                    Action = "snapshot",
                    RunId = "run-1",
                    SnapshotPath = snapshotPath,
                    DescriptorPath = descriptorPath,
                },
                "http://orchestrator",
                CancellationToken.None,
                handler));

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(snapshotPath));
        Assert.True(Directory.Exists(descriptorPath));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task SnapshotPublicationWaitsForExclusiveDestinationLocks()
    {
        AuthoringHttpClient.EnsureOuterMode();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-cli-snapshot-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string snapshotPath = Path.Combine(directory, "notes.db");
        string descriptorPath = Path.Combine(directory, "notes.json");
        byte[] bytes = [1, 2, 3];
        string hash = Convert.ToHexString(
            SHA256.HashData(bytes)).ToLowerInvariant();
        DelegateHttpHandler handler = new((request, _, _) =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/bytes",
                    StringComparison.Ordinal)
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes),
                    }
                    : DelegateHttpHandler.Json(
                        new AuthoringSnapshotDescriptor(
                            "github-fhir-ballot-notes",
                            "run-1",
                            "snapshot-1",
                            1,
                            4,
                            1,
                            hash,
                            bytes.Length,
                            1,
                            1,
                            new Dictionary<string, long>(),
                            "notes.db",
                            DateTimeOffset.Parse("2026-09-04T00:00:00Z")))));
        using (FileStream heldLock = new(
                   $"{snapshotPath}.fhir-augury.publish.lock",
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        using (CancellationTokenSource cancellation =
               new(TimeSpan.FromMilliseconds(250)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BallotNoteAuthoringHandler.HandleAsync(
                    new BallotNoteAuthoringRequest
                    {
                        Action = "snapshot",
                        RunId = "run-1",
                        SnapshotPath = snapshotPath,
                        DescriptorPath = descriptorPath,
                    },
                    "http://orchestrator",
                    cancellation.Token,
                    handler));
        }

        Assert.False(File.Exists(snapshotPath));
        Assert.False(File.Exists(descriptorPath));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task WorkerSubmitUsesTypedProseHash()
    {
        using AuthoringEnvironmentScope environment = new();
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(body);
            System.Text.Json.JsonElement submission =
                document.RootElement.GetProperty("submission");
            return DelegateHttpHandler.Json(new
            {
                receipt = new AuthoringResultReceipt(
                    "receipt-1",
                    "run-1",
                    "item-1",
                    "operation-1",
                    "note-a",
                    submission.GetProperty("contentHash").GetString()!,
                    "revision-1",
                    "revision-1",
                    1,
                    DateTimeOffset.Parse("2026-09-04T00:00:00Z")),
                isReplay = false,
            });
        });

        object result = await BallotNoteAuthoringHandler.HandleAsync(
            new BallotNoteAuthoringRequest
            {
                Action = "submit",
                ObservedSourceRevision = "revision-1",
                Prose = new BallotNoteProsePutRequest
                {
                    NeedsNote = "true",
                    ProposedBallotNoteHtml = "<blockquote>note</blockquote>",
                },
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.False(Assert.IsType<AuthoringSubmitResponse>(result).IsReplay);
    }
}
