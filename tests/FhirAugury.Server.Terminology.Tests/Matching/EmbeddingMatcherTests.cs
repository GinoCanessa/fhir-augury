using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Database.Records;
using FhirAugury.Server.Terminology.Matching;
using FhirAugury.Server.Terminology.Matching.Embeddings;
using FhirAugury.Server.Terminology.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Tests.Matching;

/// <summary>
/// Seeds a real on-disk SQLite database with a tiny THO-shaped fixture
/// and exercises <see cref="EmbeddingMatcher"/> against a deterministic
/// fake embedding provider.
/// </summary>
public sealed class EmbeddingMatcherTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TerminologyDatabase _db;

    public EmbeddingMatcherTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"server-terminology-embed-{Guid.NewGuid():N}.db");
        _db = new TerminologyDatabase(_dbPath, NullLogger<TerminologyDatabase>.Instance);
        _db.Initialize();
        SeedFixtures();
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (string p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Match_RanksClosestVector_ForMaritalStatusSubmission()
    {
        FakeEmbeddingProvider provider = new();
        EmbeddingMatcher matcher = BuildMatcher(provider);

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            CanonicalUrl = "http://example.org/cs/marital",
            Title = "My Marital Status",
            Description = "Marital status codes for our app.",
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default);

        Assert.NotEmpty(results);
        Assert.Equal(
            "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            results[0].CanonicalUrl);
        Assert.True(results[0].SubScores["cosine"] > 0.9);
        Assert.Contains(results[0].Reasons, r => r.StartsWith("semantic similarity"));
    }

    [Fact]
    public async Task Match_FlagsCrossVersion_WhenFhirVersionDiffers()
    {
        FakeEmbeddingProvider provider = new();
        EmbeddingMatcher matcher = BuildMatcher(provider);

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R5",
            Title = "Marital Status",
            Description = "Marital status codes.",
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default);

        Assert.NotEmpty(results);
        Assert.True(results[0].CrossVersion);
    }

    [Fact]
    public async Task Match_Throws_WhenProviderDisabled()
    {
        NullEmbeddingProvider disabled = new();
        EmbeddingMatcher matcher = BuildMatcher(disabled);

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            Title = "Anything",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            matcher.MatchAsync(submission,
                new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default));
    }

    [Fact]
    public async Task Match_FiltersByMinScore()
    {
        FakeEmbeddingProvider provider = new();
        EmbeddingMatcher matcher = BuildMatcher(provider);

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            Title = "Marital Status",
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.99 }, default);

        // Only the (almost) identical artifact should clear 0.99.
        Assert.All(results, r => Assert.True(r.Score >= 0.99));
    }

    private EmbeddingMatcher BuildMatcher(IEmbeddingProvider provider)
    {
        TerminologyServiceOptions opts = new();
        return new EmbeddingMatcher(_db, provider, Options.Create(opts),
            NullLogger<EmbeddingMatcher>.Instance);
    }

    private void SeedFixtures()
    {
        TerminologyPackageRecord pkg = new()
        {
            Id = TerminologyPackageRecord.GetIndex(),
            PackageId = "hl7.terminology.r4",
            RequestedVersionTag = "latest",
            ResolvedVersion = "5.4.0",
            FhirVersion = "R4",
            IngestedAt = DateTimeOffset.UtcNow,
            ArtifactCount = 0,
            ConceptCount = 0,
        };
        using SqliteConnection conn = _db.OpenConnection();
        TerminologyPackageRecord.Insert(conn, pkg, insertPrimaryKey: true);

        SeedArtifact(conn,
            "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            "V3 Marital Status", "MaritalStatus",
            "Standardized marital status codes.",
            [("M", "Married"), ("S", "Single"), ("D", "Divorced"), ("W", "Widowed")]);

        SeedArtifact(conn,
            "http://terminology.hl7.org/CodeSystem/condition-status",
            "Condition Status Codes", "ConditionStatus",
            "Codes describing the status of a condition.",
            [("preliminary", "Preliminary"), ("final", "Final")]);

        SeedArtifact(conn,
            "http://terminology.hl7.org/CodeSystem/observation-category",
            "Observation Categories", "ObservationCategory",
            "High-level categorization of observations.",
            [("vital-signs", "Vital Signs"), ("social-history", "Social History")]);
    }

    private static void SeedArtifact(
        SqliteConnection conn, string url, string title, string name, string desc,
        (string Code, string Display)[] concepts)
    {
        TerminologyArtifactRecord artifact = new()
        {
            Id = TerminologyArtifactRecord.GetIndex(),
            Kind = "CodeSystem",
            CanonicalUrl = url,
            CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(url),
            Version = "5.4.0",
            FhirVersion = "R4",
            Title = title,
            Name = name,
            Status = "Active",
            Experimental = false,
            Publisher = "HL7",
            Description = desc,
            Purpose = null,
            Keywords = null,
            PackageId = "hl7.terminology.r4",
            PackageVersion = "5.4.0",
            Json = "{}",
        };
        TerminologyArtifactRecord.Insert(conn, artifact, insertPrimaryKey: true);

        List<TerminologyConceptRecord> rows = [];
        foreach ((string code, string display) in concepts)
        {
            rows.Add(new TerminologyConceptRecord
            {
                Id = TerminologyConceptRecord.GetIndex(),
                ArtifactId = artifact.Id,
                SystemUrl = url,
                Code = code,
                Display = display,
                DisplayNormalized = TerminologyTextNormalizer.NormalizeDisplay(display),
                Definition = null,
                DesignationsJson = "[]",
                IsRetired = false,
            });
        }
        rows.Insert(conn, ignoreDuplicates: false, insertPrimaryKey: true);
    }

    /// <summary>
    /// Deterministic fake: assigns each input text a 3-d vector based on
    /// the presence of keywords ("marital", "condition", "observation").
    /// </summary>
    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsEnabled => true;
        public int Dimensions => 3;
        public string ModelName => "fake-3d";

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
            Task.FromResult(Vector(text));

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(Vector).ToArray());

        private static float[] Vector(string text)
        {
            string t = text.ToLowerInvariant();
            float marital = t.Contains("marital") ? 1f : 0f;
            float condition = t.Contains("condition") ? 1f : 0f;
            float observation = t.Contains("observ") ? 1f : 0f;
            // Add tiny noise to avoid all-zero vectors triggering cosine=0.
            return [marital + 0.01f, condition + 0.01f, observation + 0.01f];
        }
    }
}
