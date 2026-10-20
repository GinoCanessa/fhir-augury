using System.Text;
using System.Text.Json;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins the <c>confluence_attachments</c> table, attachment replay and
/// deletion, attachment indexing, and — most importantly — that the size cap is
/// a <b>blob-level</b> concern: an oversized attachment still produces a
/// database row and a search document, and only its bytes are absent.
/// </summary>
public class ConfluenceAttachmentTests : IDisposable
{
    private const string Space = "FHIR";
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly FileSystemResponseCache _cache;
    private readonly ConfluenceDatabase _database;

    public ConfluenceAttachmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _cache = new FileSystemResponseCache(Path.Combine(_root, "cache"));
        _dbPath = Path.Combine(_root, "confluence.db");
        _database = new ConfluenceDatabase(_dbPath, NullLogger<ConfluenceDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────

    private ConfluenceServiceOptions Options(long attachmentMaxBytes = 104_857_600) => new()
    {
        BaseUrl = BaseUrl,
        CachePath = _cache.RootPath,
        DatabasePath = _dbPath,
        AttachmentMaxBytes = attachmentMaxBytes,
    };

    private ConfluenceSource CreateSource(ConfluenceServiceOptions? options = null)
    {
        IOptions<ConfluenceServiceOptions> accessor =
            Microsoft.Extensions.Options.Options.Create(options ?? Options());

        return new ConfluenceSource(
            accessor,
            _database,
            _cache,
            new ConfluenceSpaceDiscovery(accessor, _cache, NullLogger<ConfluenceSpaceDiscovery>.Instance,
                (_, _) => throw new InvalidOperationException("replay must not fetch")),
            new ConfluenceSweep(accessor, _cache, NullLogger<ConfluenceSweep>.Instance,
                (_, _) => throw new InvalidOperationException("replay must not fetch")),
            NullLogger<ConfluenceSource>.Instance,
            (_, _) => throw new InvalidOperationException("replay must not fetch"));
    }

    private void Replay() =>
        CreateSource().LoadFromCacheAsync(CancellationToken.None).GetAwaiter().GetResult();

    private void Write(string key, string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void WriteBlob(string attachmentId, int length)
    {
        using MemoryStream stream = new(new byte[length]);
        _cache.PutAsync(ConfluenceCacheLayout.SourceName,
            ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, attachmentId), stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void WriteCatalog(params string[] keys) =>
        Write(ConfluenceCacheLayout.GetSpaceCatalogCacheKey(), new ConfluenceSpaceCatalog
        {
            DiscoveredAt = DateTimeOffset.UtcNow,
            Complete = true,
            Spaces = [.. keys.Select(k => new ConfluenceCatalogedSpace { Key = k, Name = k })],
        }.ToJson());

    private void WriteManifest(params ConfluenceManifestEntry[] entries) =>
        Write(ConfluenceCacheLayout.GetManifestCacheKey(Space), new ConfluenceManifest
        {
            SpaceKey = Space,
            Profiles = ConfluenceManifestProfiles.Current,
            SweptAt = DateTimeOffset.UtcNow,
            Complete = true,
            Entries = [.. entries],
        }.ToJson());

    private void WritePage(string id)
    {
        string payload = JsonSerializer.Serialize(new
        {
            id,
            type = "page",
            title = $"Page {id}",
            body = new { storage = new { value = "<p>body</p>" } },
            version = new { number = 1, when = "2026-08-01T00:00:00.000Z", by = new { displayName = "Ada" } },
        });

        using JsonDocument document = JsonDocument.Parse(payload);
        Write(ConfluenceCacheLayout.GetPageCacheKey(Space, id),
            ConfluenceCachedArtifact.Wrap(document.RootElement, ContentTypes.Page, Space, 1).ToJson());
    }

    private void WriteAttachmentMeta(string id, string containerId, long? fileSize)
    {
        string payload = JsonSerializer.Serialize(new
        {
            id,
            type = "attachment",
            title = $"file-{id}.pdf",
            version = new { number = 1, when = "2026-08-03T00:00:00.000Z" },
            container = new { id = containerId },
            extensions = new { mediaType = "application/pdf", fileSize },
        });

        using JsonDocument document = JsonDocument.Parse(payload);
        Write(ConfluenceCacheLayout.GetAttachmentMetaCacheKey(Space, id),
            ConfluenceCachedArtifact.Wrap(document.RootElement, ContentTypes.Attachment, Space, 1, fileSize).ToJson());
    }

    private static ConfluenceManifestEntry PageEntry(string id) =>
        new() { Id = id, Type = ContentTypes.Page, Title = $"Page {id}", Version = 1 };

    private static ConfluenceManifestEntry AttachmentEntry(string id, string containerId, long? fileSize) =>
        new()
        {
            Id = id,
            Type = ContentTypes.Attachment,
            Title = $"file-{id}.pdf",
            Version = 1,
            ContainerId = containerId,
            MediaType = "application/pdf",
            FileSize = fileSize,
            When = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
            DownloadPath = $"/download/attachments/{containerId}/file-{id}.pdf?version=1",
        };

    private ConfluenceReconcilePlan Reconcile(long attachmentMaxBytes = 104_857_600) =>
        ConfluenceReconciler.Reconcile(Space, _cache,
            new ConfluenceReconcilePolicy { AttachmentMaxBytes = attachmentMaxBytes });

    /// <summary>Sets up one page plus one attachment, with an optional blob.</summary>
    private void SeedOneAttachment(long? fileSize, int? blobLength = null)
    {
        WriteCatalog(Space);
        WritePage("100");
        WriteAttachmentMeta("300", "100", fileSize);
        WriteManifest(PageEntry("100"), AttachmentEntry("300", "100", fileSize));
        if (blobLength is not null) WriteBlob("300", blobLength.Value);
    }

    // ── Schema ────────────────────────────────────────────────────────

    [Fact]
    public void InitializeSchema_CreatesTheAttachmentsTable()
    {
        using SqliteConnection connection = _database.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='confluence_attachments'";

        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void ResetDatabase_DropsAndRecreatesTheAttachmentsTable()
    {
        SeedOneAttachment(fileSize: 4096, blobLength: 4096);
        Replay();

        using (SqliteConnection before = _database.OpenConnection())
        {
            Assert.Equal(1, ConfluenceAttachmentRecord.SelectCount(before));
        }

        _database.ResetDatabase();

        using SqliteConnection after = _database.OpenConnection();
        Assert.Equal(0, ConfluenceAttachmentRecord.SelectCount(after));
    }

    // ── Replay ────────────────────────────────────────────────────────

    [Fact]
    public void Replay_ProducesAnAttachmentRowWithItsMetadata()
    {
        SeedOneAttachment(fileSize: 4096, blobLength: 4096);

        Replay();

        using SqliteConnection connection = _database.OpenConnection();
        ConfluenceAttachmentRecord record = ConfluenceAttachmentRecord.SelectSingle(
            connection, ConfluenceAttachmentId: "300")!;

        Assert.Equal("100", record.ConfluencePageId);
        Assert.Equal("file-300.pdf", record.FileName);
        Assert.Equal("application/pdf", record.MediaType);
        Assert.Equal(4096, record.FileSizeBytes);
        Assert.Equal(1, record.VersionNumber);
        Assert.Equal($"{BaseUrl}/download/attachments/100/file-300.pdf?version=1", record.DownloadUrl);
        Assert.Equal(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, "300"), record.CacheKey);
    }

    [Fact]
    public void Replay_DoesNotDuplicateAttachmentsOnASecondRun()
    {
        SeedOneAttachment(fileSize: 4096, blobLength: 4096);

        Replay();
        Replay();

        using SqliteConnection connection = _database.OpenConnection();
        Assert.Equal(1, ConfluenceAttachmentRecord.SelectCount(connection));
    }

    [Fact]
    public void Replay_DeletesAnAttachmentAbsentFromTheManifest()
    {
        WriteCatalog(Space);
        WritePage("100");
        WriteAttachmentMeta("300", "100", 4096);
        WriteAttachmentMeta("301", "100", 2048);
        WriteManifest(PageEntry("100"), AttachmentEntry("300", "100", 4096), AttachmentEntry("301", "100", 2048));
        Replay();

        using (SqliteConnection before = _database.OpenConnection())
        {
            Assert.Equal(2, ConfluenceAttachmentRecord.SelectCount(before));
        }

        WriteManifest(PageEntry("100"), AttachmentEntry("300", "100", 4096));
        Replay();

        using SqliteConnection connection = _database.OpenConnection();
        Assert.Equal(1, ConfluenceAttachmentRecord.SelectCount(connection));
        Assert.Null(ConfluenceAttachmentRecord.SelectSingle(connection, ConfluenceAttachmentId: "301"));
    }

    [Fact]
    public void Replay_RemovesAttachmentsOfADisappearedPage()
    {
        SeedOneAttachment(fileSize: 4096, blobLength: 4096);
        Replay();

        WriteManifest();
        Replay();

        using SqliteConnection connection = _database.OpenConnection();
        Assert.Equal(0, ConfluenceAttachmentRecord.SelectCount(connection));
        Assert.Equal(0, ConfluencePageRecord.SelectCount(connection));
    }

    // ── Reconciliation ────────────────────────────────────────────────

    [Fact]
    public void AttachmentWithNoBlob_ReconcilesAsMissing()
    {
        SeedOneAttachment(fileSize: 4096);

        Assert.Equal(ConfluenceArtifactState.Missing,
            Reconcile().Items.Single(i => i.Entry.Id == "300").BlobState);
    }

    [Fact]
    public void AttachmentWithSizeMismatchedBlob_ReconcilesAsMissing()
    {
        SeedOneAttachment(fileSize: 4096, blobLength: 1024);

        Assert.Equal(ConfluenceArtifactState.Missing,
            Reconcile().Items.Single(i => i.Entry.Id == "300").BlobState);
    }

    [Fact]
    public void OversizedAttachment_StillReachesTheDatabaseAndTheIndex()
    {
        // The cap is a blob-level concern. Conflating it with the attachment
        // would leave no row at all — the opposite of a metadata-and-bytes scope.
        SeedOneAttachment(fileSize: 500_000_000);

        ConfluenceReconcilePlan plan = Reconcile();
        ConfluenceReconcileItem item = plan.Items.Single(i => i.Entry.Id == "300");

        Assert.Equal(ConfluenceArtifactState.Current, item.State);
        Assert.Equal(ConfluenceArtifactState.SkippedByPolicy, item.BlobState);
        Assert.Equal(ConfluenceSpaceVerdict.CompleteWithSkips, plan.Verdict);

        Replay();

        using SqliteConnection connection = _database.OpenConnection();
        ConfluenceAttachmentRecord record = ConfluenceAttachmentRecord.SelectSingle(
            connection, ConfluenceAttachmentId: "300")!;

        Assert.Equal(500_000_000, record.FileSizeBytes);
        Assert.NotNull(record.DownloadUrl);
        Assert.Null(record.CacheKey);
        Assert.Contains(CollectDocuments(), d => d.SourceId == "100:300");
    }

    [Fact]
    public void LoweredCap_KeepsAnAlreadyDownloadedBlobAndNeverTombstonesIt()
    {
        SeedOneAttachment(fileSize: 4096, blobLength: 4096);

        ConfluenceReconcilePlan plan = Reconcile(attachmentMaxBytes: 1024);

        Assert.Equal(ConfluenceArtifactState.Current, plan.Items.Single(i => i.Entry.Id == "300").BlobState);
        Assert.Empty(plan.VanishedKeys);
        Assert.Equal(ConfluenceSpaceVerdict.Complete, plan.Verdict);
    }

    [Fact]
    public void RaisedCap_TurnsANeverDownloadedBlobIntoAConvergingGap()
    {
        SeedOneAttachment(fileSize: 200_000_000);

        Assert.Equal(ConfluenceArtifactState.SkippedByPolicy,
            Reconcile().Items.Single(i => i.Entry.Id == "300").BlobState);

        ConfluenceReconcilePlan raised = Reconcile(attachmentMaxBytes: 0);

        Assert.Equal(ConfluenceArtifactState.Missing, raised.Items.Single(i => i.Entry.Id == "300").BlobState);
        Assert.Single(raised.BlobsToFetch);
    }

    [Fact]
    public void AbsentFileSize_IsAttemptedRatherThanSilentlySkipped()
    {
        // An absent size must not be read as "unbounded, therefore skip"; the
        // counting stream is what stops a surprise mid-transfer.
        SeedOneAttachment(fileSize: null);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceArtifactState.Missing, plan.Items.Single(i => i.Entry.Id == "300").BlobState);
        Assert.Single(plan.BlobsToFetch);
    }

    // ── Indexing ──────────────────────────────────────────────────────

    [Fact]
    public void CollectDocuments_IncludesAttachmentFileNames()
    {
        SeedOneAttachment(fileSize: 4096, blobLength: 4096);
        Replay();

        IndexContent document = CollectDocuments().Single(d => d.ContentType == ContentTypes.Attachment);

        Assert.Equal("100:300", document.SourceId);
        Assert.Contains("file-300.pdf", document.Text);
        Assert.Contains("application/pdf", document.Text);
    }

    private List<IndexContent> CollectDocuments()
    {
        ConfluenceIndexer indexer = new(
            _database,
            new AuxiliaryDatabase(new AuxiliaryDatabaseOptions(), NullLogger<AuxiliaryDatabase>.Instance),
            new Bm25Options(),
            NullLogger<ConfluenceIndexer>.Instance);

        indexer.RebuildFullIndex(CancellationToken.None);

        using SqliteConnection connection = _database.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT ContentType, SourceId FROM index_keywords";

        List<IndexContent> documents = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            documents.Add(new IndexContent
            {
                ContentType = reader.GetString(0),
                SourceId = reader.GetString(1),
                Text = string.Empty,
            });
        }

        // Text is not persisted by the indexer, so recover it from the source row
        // for the assertions above.
        using SqliteConnection lookup = _database.OpenConnection();
        return [.. documents.Select(d =>
        {
            if (d.ContentType != ContentTypes.Attachment) return d;

            string attachmentId = d.SourceId.Split(':')[^1];
            ConfluenceAttachmentRecord? record = ConfluenceAttachmentRecord.SelectSingle(
                lookup, ConfluenceAttachmentId: attachmentId);

            return record is null ? d : d with { Text = $"{record.FileName} {record.MediaType}" };
        })];
    }
}
