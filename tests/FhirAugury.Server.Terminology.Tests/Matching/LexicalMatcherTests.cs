using FhirAugury.Server.Terminology;
using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Database.Records;
using FhirAugury.Server.Terminology.Matching;
using FhirAugury.Server.Terminology.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Tests.Matching;

/// <summary>
/// Seeds a real on-disk SQLite database with a tiny THO-shaped fixture
/// (3 CodeSystems / 1 ValueSet) and exercises the lexical matcher.
/// </summary>
public sealed class LexicalMatcherTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TerminologyDatabase _db;

    public LexicalMatcherTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"server-terminology-tests-{Guid.NewGuid():N}.db");
        _db = new TerminologyDatabase(_dbPath, NullLogger<TerminologyDatabase>.Instance);
        _db.Initialize();
        SeedFixtures();
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            string walPath = _dbPath + "-wal";
            string shmPath = _dbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task Match_FindsCanonicalUrlOverlapByMetadata()
    {
        LexicalMatcher matcher = BuildMatcher();

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            CanonicalUrl = "http://example.org/cs/condition-status",
            CanonicalUrlNormalized = "http://example.org/cs/condition-status",
            Title = "Condition Status Codes",
            Name = "ConditionStatusCodes",
            Description = "A custom condition status code system.",
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default);

        Assert.NotEmpty(results);
        // The first candidate should be the THO-shaped condition-status CS.
        Assert.Equal("http://terminology.hl7.org/CodeSystem/condition-status", results[0].CanonicalUrl);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task Match_FindsCodeOverlap_AndPopulatesSampleConcepts()
    {
        LexicalMatcher matcher = BuildMatcher();

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            CanonicalUrl = "http://example.org/cs/marital",
            CanonicalUrlNormalized = "http://example.org/cs/marital",
            Title = "My Marital Status",
            Concepts =
            [
                new NormalizedConcept("http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "M", "Married", "married"),
                new NormalizedConcept("http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "S", "Single", "single"),
            ],
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default);

        Assert.NotEmpty(results);
        OverlapCandidate top = results[0];
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", top.CanonicalUrl);
        Assert.True(top.SubScores["code_jaccard"] > 0);
        Assert.NotEmpty(top.SampleConcepts);
        Assert.Contains(top.SampleConcepts, c => c.Code == "M");
    }

    [Fact]
    public async Task Match_FlagsCrossVersion_WhenFhirVersionDiffers()
    {
        LexicalMatcher matcher = BuildMatcher();

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R5", // fixture is R4
            CanonicalUrl = "http://example.org/cs/marital",
            CanonicalUrlNormalized = "http://example.org/cs/marital",
            Concepts =
            [
                new NormalizedConcept("http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "M", "Married", "married"),
            ],
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default);

        Assert.NotEmpty(results);
        Assert.True(results[0].CrossVersion);
    }

    [Fact]
    public async Task Match_ReturnsEmpty_ForUnrelatedSubmission()
    {
        LexicalMatcher matcher = BuildMatcher();

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            CanonicalUrl = "urn:internal:astronomy-catalog",
            CanonicalUrlNormalized = "urn:internal:astronomy-catalog",
            Title = "Astronomy Nebula Gamma Supernova",
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.5 }, default);

        Assert.Empty(results);
    }

    private LexicalMatcher BuildMatcher()
    {
        TerminologyServiceOptions opts = new();
        // Sanitize Packages so Validate() doesn't run (Validate is not called here).
        return new LexicalMatcher(_db, Options.Create(opts), NullLogger<LexicalMatcher>.Instance);
    }

    private void SeedFixtures()
    {
        // ── packages ────────────────────────────────────────────
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
        using (Microsoft.Data.Sqlite.SqliteConnection conn = _db.OpenConnection())
        {
            TerminologyPackageRecord.Insert(conn, pkg, insertPrimaryKey: true);

            // ── artifacts ───────────────────────────────────────
            TerminologyArtifactRecord conditionStatus = new()
            {
                Id = TerminologyArtifactRecord.GetIndex(),
                Kind = "CodeSystem",
                CanonicalUrl = "http://terminology.hl7.org/CodeSystem/condition-status",
                CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(
                    "http://terminology.hl7.org/CodeSystem/condition-status"),
                Version = "5.4.0",
                FhirVersion = "R4",
                Title = "Condition Status Codes",
                Name = "ConditionStatus",
                Status = "Active",
                Experimental = false,
                Publisher = "HL7",
                Description = "Codes describing the status of a condition.",
                Purpose = null,
                Keywords = null,
                PackageId = "hl7.terminology.r4",
                PackageVersion = "5.4.0",
                Json = "{}",
            };
            TerminologyArtifactRecord.Insert(conn, conditionStatus, insertPrimaryKey: true);

            TerminologyArtifactRecord maritalStatus = new()
            {
                Id = TerminologyArtifactRecord.GetIndex(),
                Kind = "CodeSystem",
                CanonicalUrl = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
                CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(
                    "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus"),
                Version = "5.4.0",
                FhirVersion = "R4",
                Title = "V3 Marital Status",
                Name = "MaritalStatus",
                Status = "Active",
                Experimental = false,
                Publisher = "HL7",
                Description = "Standardized marital status codes.",
                Purpose = null,
                Keywords = null,
                PackageId = "hl7.terminology.r4",
                PackageVersion = "5.4.0",
                Json = "{}",
            };
            TerminologyArtifactRecord.Insert(conn, maritalStatus, insertPrimaryKey: true);

            TerminologyArtifactRecord observationCat = new()
            {
                Id = TerminologyArtifactRecord.GetIndex(),
                Kind = "CodeSystem",
                CanonicalUrl = "http://terminology.hl7.org/CodeSystem/observation-category",
                CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(
                    "http://terminology.hl7.org/CodeSystem/observation-category"),
                Version = "5.4.0",
                FhirVersion = "R4",
                Title = "Observation Categories",
                Name = "ObservationCategory",
                Status = "Active",
                Experimental = false,
                Publisher = "HL7",
                Description = "High-level categorization of observations.",
                Purpose = null,
                Keywords = null,
                PackageId = "hl7.terminology.r4",
                PackageVersion = "5.4.0",
                Json = "{}",
            };
            TerminologyArtifactRecord.Insert(conn, observationCat, insertPrimaryKey: true);

            // ── concepts ────────────────────────────────────────
            List<TerminologyConceptRecord> concepts =
            [
                NewConcept(maritalStatus.Id, "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "M", "Married"),
                NewConcept(maritalStatus.Id, "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "S", "Single (Never Married)"),
                NewConcept(maritalStatus.Id, "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "D", "Divorced"),
                NewConcept(maritalStatus.Id, "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "W", "Widowed"),
                NewConcept(conditionStatus.Id, "http://terminology.hl7.org/CodeSystem/condition-status", "preliminary", "Preliminary"),
                NewConcept(conditionStatus.Id, "http://terminology.hl7.org/CodeSystem/condition-status", "final", "Final"),
                NewConcept(observationCat.Id, "http://terminology.hl7.org/CodeSystem/observation-category", "vital-signs", "Vital Signs"),
                NewConcept(observationCat.Id, "http://terminology.hl7.org/CodeSystem/observation-category", "social-history", "Social History"),
            ];
            concepts.Insert(conn, ignoreDuplicates: false, insertPrimaryKey: true);
        }
    }

    private static TerminologyConceptRecord NewConcept(int artifactId, string system, string code, string display)
    {
        return new TerminologyConceptRecord
        {
            Id = TerminologyConceptRecord.GetIndex(),
            ArtifactId = artifactId,
            SystemUrl = system,
            Code = code,
            Display = display,
            DisplayNormalized = TerminologyTextNormalizer.NormalizeDisplay(display),
            Definition = null,
            DesignationsJson = "[]",
            IsRetired = false,
        };
    }
}
