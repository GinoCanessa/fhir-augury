using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using FhirAugury.Source.GitHub.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Categories;

public class StubStrategyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;
    private readonly ArtifactFileMapper _mapper;

    public StubStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "stub-strat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
        _mapper = new ArtifactFileMapper(_database, NullLogger<ArtifactFileMapper>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ── UTG Strategy ──────────────────────────────────────

    [Fact]
    public void Utg_Category_IsCorrect()
    {
        UtgStrategy strategy = new(_mapper, NullLogger<UtgStrategy>.Instance);
        Assert.Equal(RepoCategory.Utg, strategy.Category);
    }

    [Fact]
    public void Utg_DiscoverTags_ReturnsEmpty()
    {
        UtgStrategy strategy = new(_mapper, NullLogger<UtgStrategy>.Instance);
        Assert.Empty(strategy.DiscoverTags("HL7/UTG", _tempDir, CancellationToken.None));
    }

    [Fact]
    public void Utg_GetPriorityPaths_ReturnsNull()
    {
        UtgStrategy strategy = new(_mapper, NullLogger<UtgStrategy>.Instance);
        Assert.Null(strategy.GetPriorityPaths("HL7/UTG", _tempDir));
    }

    [Fact]
    public void Utg_GetAdditionalIgnorePatterns_ReturnsEmpty()
    {
        UtgStrategy strategy = new(_mapper, NullLogger<UtgStrategy>.Instance);
        Assert.Empty(strategy.GetAdditionalIgnorePatterns());
    }

    [Fact]
    public void Utg_Validate_TrueWhenSourceOfTruthExists()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "input", "sourceOfTruth"));
        UtgStrategy strategy = new(_mapper, NullLogger<UtgStrategy>.Instance);
        Assert.True(strategy.Validate("HL7/UTG", _tempDir));
    }

    [Fact]
    public void Utg_Validate_FalseWhenNoSourceOfTruth()
    {
        UtgStrategy strategy = new(_mapper, NullLogger<UtgStrategy>.Instance);
        Assert.False(strategy.Validate("HL7/UTG", _tempDir));
    }

    // ── FhirExtensionsPack Strategy ───────────────────────

    [Fact]
    public void ExtPack_Category_IsCorrect()
    {
        FhirExtensionsPackStrategy strategy = new(_mapper, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.Equal(RepoCategory.FhirExtensionsPack, strategy.Category);
    }

    [Fact]
    public void ExtPack_DiscoverTags_ReturnsEmpty()
    {
        FhirExtensionsPackStrategy strategy = new(_mapper, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.Empty(strategy.DiscoverTags("HL7/fhir-extensions", _tempDir, CancellationToken.None));
    }

    [Fact]
    public void ExtPack_GetPriorityPaths_ReturnsNull()
    {
        FhirExtensionsPackStrategy strategy = new(_mapper, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.Null(strategy.GetPriorityPaths("HL7/fhir-extensions", _tempDir));
    }

    [Fact]
    public void ExtPack_GetAdditionalIgnorePatterns_ReturnsEmpty()
    {
        FhirExtensionsPackStrategy strategy = new(_mapper, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.Empty(strategy.GetAdditionalIgnorePatterns());
    }

    // ── Incubator Strategy ────────────────────────────────

    [Fact]
    public void Incubator_Category_IsCorrect()
    {
        IncubatorStrategy strategy = new(_mapper, NullLogger<IncubatorStrategy>.Instance);
        Assert.Equal(RepoCategory.Incubator, strategy.Category);
    }

    [Fact]
    public void Incubator_DiscoverTags_ReturnsEmpty()
    {
        IncubatorStrategy strategy = new(_mapper, NullLogger<IncubatorStrategy>.Instance);
        Assert.Empty(strategy.DiscoverTags("HL7/test", _tempDir, CancellationToken.None));
    }

    [Fact]
    public void Incubator_GetPriorityPaths_ReturnsNull()
    {
        IncubatorStrategy strategy = new(_mapper, NullLogger<IncubatorStrategy>.Instance);
        Assert.Null(strategy.GetPriorityPaths("HL7/test", _tempDir));
    }

    [Fact]
    public void Incubator_GetAdditionalIgnorePatterns_ReturnsEmpty()
    {
        IncubatorStrategy strategy = new(_mapper, NullLogger<IncubatorStrategy>.Instance);
        Assert.Empty(strategy.GetAdditionalIgnorePatterns());
    }

    [Fact]
    public void Incubator_Validate_TrueWhenSushiConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sushi-config.yaml"), "id: test");
        IncubatorStrategy strategy = new(_mapper, NullLogger<IncubatorStrategy>.Instance);
        Assert.True(strategy.Validate("HL7/test", _tempDir));
    }

    [Fact]
    public void Incubator_Validate_TrueWhenIgIni()
    {
        File.WriteAllText(Path.Combine(_tempDir, "ig.ini"), "[ig]");
        IncubatorStrategy strategy = new(_mapper, NullLogger<IncubatorStrategy>.Instance);
        Assert.True(strategy.Validate("HL7/test", _tempDir));
    }

    [Fact]
    public void Incubator_Validate_FalseWhenNoMarkers()
    {
        IncubatorStrategy strategy = new(_mapper, NullLogger<IncubatorStrategy>.Instance);
        Assert.False(strategy.Validate("HL7/test", _tempDir));
    }

    // ── IG Strategy ───────────────────────────────────────

    [Fact]
    public void Ig_Category_IsCorrect()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Equal(RepoCategory.Ig, strategy.Category);
    }

    [Fact]
    public void Ig_DiscoverTags_ReturnsEmpty()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Empty(strategy.DiscoverTags("test/ig", _tempDir, CancellationToken.None));
    }

    [Fact]
    public void Ig_GetPriorityPaths_ReturnsNull()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Null(strategy.GetPriorityPaths("test/ig", _tempDir));
    }

    [Fact]
    public void Ig_GetAdditionalIgnorePatterns_ReturnsEmpty()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Empty(strategy.GetAdditionalIgnorePatterns());
    }

    [Fact]
    public void Ig_Validate_TrueWhenSushiConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sushi-config.yaml"), "id: test");
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.True(strategy.Validate("test/ig", _tempDir));
    }

    [Fact]
    public void Ig_Validate_FalseWhenNoMarkers()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.False(strategy.Validate("test/ig", _tempDir));
    }
}
