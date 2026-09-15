using System.Globalization;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Tests;

public sealed class ZulipReferenceResolverTests : IDisposable
{
    private readonly ZulipReferenceTestDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("321987")]
    [InlineData("000321987")]
    [InlineData(" 321987 ")]
    public void Resolve_NumericMessageUsesStoredContextAndNearUrl(string reference)
    {
        const string topic = "entry/request: réponse 100% & 漢字";
        _fixture.AddStream(17, 9876, "fhir/infrastructure-wg");
        _fixture.AddStream(9876, 123, "not-the-owning-stream");
        _fixture.AddMessage(321987, 17, "old-stream-name", topic, "2026-05-01 10:00:00+00:00", "original content");
        _fixture.AddMessage(321988, 17, "old-stream-name", topic, "2026-05-02T12:00:00Z");
        _fixture.AddMessage(321989, 9876, "not-the-owning-stream", topic, "2026-05-03T12:00:00Z");

        ZulipReferenceResolutionResponse response = _fixture.Resolver.Resolve(reference);

        Assert.Equal(reference, response.Reference);
        Assert.Equal(ZulipReferenceLookupOutcome.Resolved, response.Outcome);
        Assert.Equal(ZulipReferenceKind.Message, response.Kind);
        Assert.Equal(321987, response.MessageId);
        Assert.Equal(9876, response.StreamId);
        Assert.Equal("fhir/infrastructure-wg", response.StreamName);
        Assert.Equal(topic, response.Topic);
        Assert.Equal(
            $"https://chat.example.com/#narrow/stream/fhir%2Finfrastructure-wg/topic/{Uri.EscapeDataString(topic)}/near/321987",
            response.Url);
        Assert.Equal(2, response.MessageCount);
        Assert.Equal("original content", response.FirstMessageExcerpt);
        Assert.Equal(Utc(2026, 5, 1, 10), response.FirstMessageAt);
        Assert.Equal(Utc(2026, 5, 2, 12), response.LastMessageAt);
        Assert.True(ZulipReferenceContract.IsCoherentResolution(response, reference));
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public void Resolve_ThreadRequiresIndexedBacking()
    {
        const string reference = "implementers:plausible topic";
        _fixture.AddStream(1, 42, "implementers");

        ZulipReferenceResolutionResponse missing = _fixture.Resolver.Resolve(reference);

        Assert.Equal(ZulipReferenceLookupOutcome.NotFound, missing.Outcome);
        Assert.Equal(404, ZulipReferenceContract.HttpStatus(Assert.IsType<ZulipReferenceLookupOutcome>(missing.Outcome)));
        Assert.Null(missing.Url);
        Assert.Equal(reference, missing.Reference);

        _fixture.AddMessage(100, 1, "implementers", "plausible topic", "2026-05-01 10:00:00+00:00");
        ZulipReferenceResolutionResponse resolved = _fixture.Resolver.Resolve(reference);

        Assert.Equal(ZulipReferenceLookupOutcome.Resolved, resolved.Outcome);
        Assert.Equal(ZulipReferenceKind.Thread, resolved.Kind);
        Assert.Null(resolved.MessageId);
        Assert.Equal(1, resolved.MessageCount);
        Assert.True(ZulipReferenceContract.IsCoherentResolution(resolved, reference));
    }

    [Fact]
    public void Resolve_AmbiguousDelimiterRefuses()
    {
        _fixture.AddStream(1, 41, "a");
        _fixture.AddStream(2, 42, "a:b");
        _fixture.AddMessage(100, 2, "a:b", "c", "2026-05-01T10:00:00Z");

        ZulipReferenceResolutionResponse unique = _fixture.Resolver.Resolve("a:b:c");
        Assert.Equal(ZulipReferenceLookupOutcome.Resolved, unique.Outcome);
        Assert.Equal("a:b", unique.StreamName);
        Assert.Equal("c", unique.Topic);

        _fixture.AddMessage(101, 1, "a", "b:c", "2026-05-01T10:00:00Z");
        ZulipReferenceResolutionResponse ambiguous = _fixture.Resolver.Resolve("a:b:c");

        Assert.Equal(ZulipReferenceLookupOutcome.AmbiguousReference, ambiguous.Outcome);
        Assert.Equal(409, ZulipReferenceContract.HttpStatus(Assert.IsType<ZulipReferenceLookupOutcome>(ambiguous.Outcome)));
        Assert.Null(ambiguous.Url);
        Assert.Null(ambiguous.StreamName);
    }

    [Fact]
    public void Resolve_DuplicateIndexedStreamNamesRefuseRatherThanPickOne()
    {
        _fixture.AddStream(1, 41, "same");
        _fixture.AddStream(2, 42, "same");
        _fixture.AddMessage(100, 1, "same", "topic", "2026-05-01T10:00:00Z");
        _fixture.AddMessage(101, 2, "same", "topic", "2026-05-01T10:00:00Z");

        ZulipReferenceResolutionResponse response = _fixture.Resolver.Resolve("same:topic");

        Assert.Equal(ZulipReferenceLookupOutcome.AmbiguousReference, response.Outcome);
        Assert.Null(response.Url);
    }

    [Theory]
    [InlineData("entry/request: response")]
    [InlineData("100% literal %2F")]
    [InlineData("réponse 日本語 🩺")]
    [InlineData(" topic with surrounding spaces ")]
    public void Resolve_ReservedTopicCharactersAreNotDecodedOrSplitAway(string topic)
    {
        const string stream = "fhir/core:stream";
        string reference = $"{stream}:{topic}";
        _fixture.AddStream(1, 42, stream);
        _fixture.AddMessage(100, 1, stream, topic, "2026-05-01T10:00:00Z");

        ZulipReferenceResolutionResponse response = _fixture.Resolver.Resolve(reference);

        Assert.Equal(reference, response.Reference);
        Assert.Equal(stream, response.StreamName);
        Assert.Equal(topic, response.Topic);
        Assert.Equal(
            $"https://chat.example.com/#narrow/stream/{Uri.EscapeDataString(stream)}/topic/{Uri.EscapeDataString(topic)}",
            response.Url);
        Assert.True(ZulipReferenceContract.IsCoherentResolution(response, reference));
    }

    [Fact]
    public void Resolve_MalformedOptionalTimestampRetainsLink()
    {
        _fixture.AddStream(1, 42, "implementers");
        _fixture.AddMessage(100, 1, "implementers", "topic", "not a timestamp");
        _fixture.AddMessage(101, 1, "implementers", "topic", "2026-05-01T10:00:00Z");

        ZulipReferenceResolutionResponse response = _fixture.Resolver.Resolve("100");

        Assert.Equal(ZulipReferenceLookupOutcome.Resolved, response.Outcome);
        Assert.EndsWith("/near/100", response.Url);
        Assert.Equal(2, response.MessageCount);
        Assert.Null(response.FirstMessageAt);
        Assert.Null(response.LastMessageAt);
        Assert.Equal([ZulipReferenceDiagnosticCode.InvalidTimestamp], response.Diagnostics);
        Assert.DoesNotContain("not a timestamp", JsonSerializer.Serialize(response));
        Assert.True(ZulipReferenceContract.IsCoherentResolution(response, "100"));
    }

    [Fact]
    public void Resolve_MixedOffsetAggregatesUseInstants()
    {
        _fixture.AddStream(1, 42, "implementers");
        _fixture.AddMessage(100, 1, "implementers", "topic", "2026-05-01T00:30:00+02:00");
        _fixture.AddMessage(101, 1, "implementers", "topic", "2026-04-30 23:00:00-02:00");
        _fixture.AddMessage(102, 1, "implementers", "topic", "2026-04-30T23:00:00Z");

        ZulipReferenceResolutionResponse response = _fixture.Resolver.Resolve("implementers:topic");

        Assert.Equal(new DateTimeOffset(2026, 4, 30, 22, 30, 0, TimeSpan.Zero), response.FirstMessageAt);
        Assert.Equal(Utc(2026, 5, 1, 1), response.LastMessageAt);
        Assert.Equal(TimeSpan.Zero, response.FirstMessageAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, response.LastMessageAt!.Value.Offset);
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [InlineData("2026-05-01 10:00:00+03:00", "2026-05-01T07:00:00Z")]
    [InlineData("2026-05-01 10:00:00.1234567-02:00", "2026-05-01T12:00:00.1234567Z")]
    [InlineData("2026-05-01 10:00:00", "2026-05-01T10:00:00Z")]
    [InlineData("2026-05-01 10:00:00.123", "2026-05-01T10:00:00.123Z")]
    [InlineData("2026-05-01T10:00:00+03:00", "2026-05-01T07:00:00Z")]
    [InlineData("2026-05-01T10:00:00.1234567Z", "2026-05-01T10:00:00.1234567Z")]
    public void Resolve_StorageTimestampsAreInvariantUtc(string storage, string expected)
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            DateTimeOffset? parsed = ZulipReferenceResolver.ParseTimestamp(storage);
            Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), parsed);
            Assert.Equal(TimeSpan.Zero, parsed!.Value.Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(null, ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData(" ", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("0", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("-1", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("+1", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("99999999999999999999", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData(":", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData(":topic", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("stream:", ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("unsupported", ZulipReferenceLookupOutcome.UnsupportedReference)]
    [InlineData("999", ZulipReferenceLookupOutcome.NotFound)]
    [InlineData("unindexed:topic", ZulipReferenceLookupOutcome.NotFound)]
    public void Resolve_InputAndAbsenceFailuresAreDistinct(string? reference, ZulipReferenceLookupOutcome expected)
    {
        ZulipReferenceResolutionResponse response = _fixture.Resolver.Resolve(reference);

        Assert.Equal(reference, response.Reference);
        Assert.Equal(expected, response.Outcome);
        Assert.Null(response.Url);
    }

    [Fact]
    public void Resolve_MissingOwningStreamCannotCertifyContext()
    {
        _fixture.AddMessage(100, 77, "guessed", "topic", "2026-05-01T10:00:00Z");

        ZulipReferenceResolutionResponse response = _fixture.Resolver.Resolve("100");

        Assert.Equal(ZulipReferenceLookupOutcome.InvalidSourceContext, response.Outcome);
        Assert.Equal([ZulipReferenceDiagnosticCode.MissingStreamContext], response.Diagnostics);
        Assert.Null(response.Url);
    }

    [Fact]
    public void Resolve_UnavailableDatabaseIsNotNotFound()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"zulip_missing_{Guid.NewGuid():N}", "missing.db");
        using ZulipDatabase database = new(missingPath, NullLogger<ZulipDatabase>.Instance, readOnly: true);
        ZulipReferenceResolver resolver = new(database, ZulipReferenceTestDatabase.Options, NullLogger<ZulipReferenceResolver>.Instance);

        ZulipReferenceResolutionResponse response = resolver.Resolve("100");

        Assert.Equal(ZulipReferenceLookupOutcome.SourceUnavailable, response.Outcome);
        Assert.Equal(503, ZulipReferenceContract.HttpStatus(Assert.IsType<ZulipReferenceLookupOutcome>(response.Outcome)));
        Assert.DoesNotContain(missingPath, JsonSerializer.Serialize(response));
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public void Resolve_CallerCancellationPropagates()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _fixture.Resolver.Resolve("100", cancellation.Token));
    }

    [Fact]
    public void Resolve_SchemaProgrammingErrorsAreNotSourceUnavailable()
    {
        using SqliteConnection connection = _fixture.Database.OpenConnection();
        using SqliteCommand command = new("DROP TABLE zulip_messages", connection);
        command.ExecuteNonQuery();

        Assert.Throws<SqliteException>(() => _fixture.Resolver.Resolve("100"));
    }

    [Fact]
    public void Resolve_ReadTransactionKeepsContextConsistent()
    {
        _fixture.AddStream(1, 42, "before");
        _fixture.AddMessage(100, 1, "before", "old topic", "2026-05-01T10:00:00Z", "before content");
        using SqliteConnection reader = _fixture.Database.OpenConnection();
        using SqliteTransaction snapshot = reader.BeginTransaction(deferred: true);
        using (SqliteCommand establishSnapshot = new("SELECT COUNT(*) FROM zulip_messages", reader, snapshot))
            Assert.Equal(1L, establishSnapshot.ExecuteScalar());

        using (SqliteConnection writer = _fixture.Database.OpenConnection())
        using (SqliteTransaction change = writer.BeginTransaction())
        {
            using SqliteCommand update = new("""
                UPDATE zulip_streams SET Name = 'after', ZulipStreamId = 99 WHERE Id = 1;
                UPDATE zulip_messages SET StreamName = 'after', Topic = 'new topic',
                    Timestamp = '2026-06-01T10:00:00Z', ContentPlain = 'after content' WHERE ZulipMessageId = 100;
                """, writer, change);
            update.ExecuteNonQuery();
            change.Commit();
        }
        _fixture.AddMessage(101, 1, "after", "new topic", "2026-06-02T10:00:00Z");

        ZulipReferenceResolutionResponse prior = _fixture.Resolver.ResolveIndexed(reader, snapshot, "100", 100, default);
        ZulipReferenceResolutionResponse current = _fixture.Resolver.Resolve("100");

        Assert.Equal("before", prior.StreamName);
        Assert.Equal(42, prior.StreamId);
        Assert.Equal("old topic", prior.Topic);
        Assert.Equal(1, prior.MessageCount);
        Assert.Equal("before content", prior.FirstMessageExcerpt);
        Assert.Equal(Utc(2026, 5, 1, 10), prior.LastMessageAt);
        Assert.Equal("after", current.StreamName);
        Assert.Equal(99, current.StreamId);
        Assert.Equal("new topic", current.Topic);
        Assert.Equal(2, current.MessageCount);
        Assert.Equal(Utc(2026, 6, 2, 10), current.LastMessageAt);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}

internal sealed class ZulipReferenceTestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"zulip_reference_{Guid.NewGuid():N}.db");
    internal static IOptions<ZulipServiceOptions> Options { get; } =
        Microsoft.Extensions.Options.Options.Create(new ZulipServiceOptions { BaseUrl = "https://chat.example.com" });

    public ZulipDatabase Database { get; }
    public ZulipReferenceResolver Resolver { get; }

    public ZulipReferenceTestDatabase()
    {
        Database = new ZulipDatabase(_path, NullLogger<ZulipDatabase>.Instance);
        Database.Initialize();
        Resolver = new ZulipReferenceResolver(Database, Options, NullLogger<ZulipReferenceResolver>.Instance);
    }

    public void AddStream(int localId, int publicId, string name)
    {
        using SqliteConnection connection = Database.OpenConnection();
        ZulipStreamRecord.Insert(connection, new ZulipStreamRecord
        {
            Id = localId,
            ZulipStreamId = publicId,
            Name = name,
            Description = null,
            IsWebPublic = true,
            MessageCount = 0,
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UnixEpoch,
        });
        // Generated Insert assigns the integer primary key. Pin the test's
        // local coordinate explicitly so it cannot accidentally equal the public ID.
        using SqliteCommand command = new("UPDATE zulip_streams SET Id = @localId WHERE ZulipStreamId = @publicId", connection);
        command.Parameters.AddWithValue("@localId", localId);
        command.Parameters.AddWithValue("@publicId", publicId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    public void AddMessage(int messageId, int localStreamId, string streamName, string topic, string timestamp, string content = "message body")
    {
        using SqliteConnection connection = Database.OpenConnection();
        ZulipMessageRecord.Insert(connection, new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = messageId,
            StreamId = localStreamId,
            StreamName = streamName,
            Topic = topic,
            SenderId = 1,
            SenderName = "Example Sender",
            SenderEmail = null,
            ContentPlain = content,
            ContentHtml = $"<p>{content}</p>",
            Timestamp = DateTimeOffset.UnixEpoch,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Reactions = null,
        });
        using SqliteCommand command = new("UPDATE zulip_messages SET Timestamp = @timestamp WHERE ZulipMessageId = @id", connection);
        command.Parameters.AddWithValue("@timestamp", timestamp);
        command.Parameters.AddWithValue("@id", messageId);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Database.Dispose();
        TestFileCleanup.SafeDeleteFile(_path);
    }
}
