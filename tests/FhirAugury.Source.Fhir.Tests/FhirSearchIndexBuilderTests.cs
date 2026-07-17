using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Indexing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Fhir.Tests;

public class FhirSearchIndexBuilderTests
{
    // 7 structures + 1 code system + 1 value set + 1 operation + 4 search params.
    private const int ExpectedArtifacts = 14;

    private sealed class Harness : IDisposable
    {
        public FhirSpecFixture Spec { get; } = new();
        public FhirSearchDatabase Search { get; }
        public FhirSearchIndexBuilder Builder { get; }
        private readonly string _searchPath;

        public Harness()
        {
            _searchPath = Path.Combine(Path.GetTempPath(), $"fhir-fts-{Guid.NewGuid():N}.db");
            Search = new FhirSearchDatabase(_searchPath, NullLogger<FhirSearchDatabase>.Instance);
            Search.Initialize();
            Builder = new FhirSearchIndexBuilder(
                Spec.CreateDatabase(), Search, NullLogger<FhirSearchIndexBuilder>.Instance);
        }

        public void Dispose()
        {
            Search.Dispose();
            TestFileCleanup.SafeDeleteFile(_searchPath);
            Spec.Dispose();
        }
    }

    [Fact]
    public void Build_IndexesAllArtifacts()
    {
        using Harness h = new();

        int count = h.Builder.Build();

        Assert.Equal(ExpectedArtifacts, count);
        Assert.Equal(ExpectedArtifacts, h.Search.ArtifactCount());
    }

    [Fact]
    public void NeedsRebuild_TrueWhenEmpty()
    {
        using Harness h = new();
        Assert.True(h.Builder.NeedsRebuild());
    }

    [Fact]
    public void NeedsRebuild_FalseAfterBuild_FingerprintUnchanged()
    {
        using Harness h = new();
        h.Builder.Build();
        Assert.False(h.Builder.NeedsRebuild());
    }

    [Fact]
    public void NeedsRebuild_TrueAfterSourceFingerprintChanges()
    {
        using Harness h = new();
        h.Builder.Build();
        Assert.False(h.Builder.NeedsRebuild());

        // Simulate a fresh upstream build of the spec database.
        File.SetLastWriteTimeUtc(h.Spec.DatabasePath, DateTime.UtcNow.AddMinutes(10));

        Assert.True(h.Builder.NeedsRebuild());
    }

    [Fact]
    public void Build_IsIdempotent_NoDuplicateRows()
    {
        using Harness h = new();

        h.Builder.Build();
        int second = h.Builder.Build();

        Assert.Equal(ExpectedArtifacts, second);
        Assert.Equal(ExpectedArtifacts, h.Search.ArtifactCount());
    }
}
