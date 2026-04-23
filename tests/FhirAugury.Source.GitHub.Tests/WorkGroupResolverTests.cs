using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Tests for <see cref="WorkGroupResolver"/>: exact <c>Code</c> match,
/// exact <c>Name</c> match, <c>NameClean</c> fallback, retired rows still
/// resolve, unmatched returns null, and <c>Reload()</c> picks up new rows.
/// </summary>
public sealed class WorkGroupResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public WorkGroupResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wg_resolver_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private void Seed(params (string Code, string Name, bool Retired)[] rows)
    {
        using SqliteConnection conn = _db.OpenConnection();
        foreach ((string code, string name, bool retired) in rows)
        {
            Hl7WorkGroupRecord rec = new()
            {
                Id = Hl7WorkGroupRecord.GetIndex(),
                Code = code,
                Name = name,
                Definition = null,
                Retired = retired,
                NameClean = FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(name),
            };
            Hl7WorkGroupRecord.Insert(conn, rec);
        }
    }

    private WorkGroupResolver NewResolver()
    {
        WorkGroupResolver r = new(_db, NullLogger<WorkGroupResolver>.Instance);
        r.Reload();
        return r;
    }

    [Fact]
    public void Resolve_ExactCode_ReturnsCode()
    {
        Seed(("fhir", "FHIR Infrastructure", false));
        Assert.Equal("fhir", NewResolver().Resolve("fhir"));
    }

    [Fact]
    public void Resolve_ExactCode_CaseInsensitive()
    {
        Seed(("fhir", "FHIR Infrastructure", false));
        Assert.Equal("fhir", NewResolver().Resolve("FHIR"));
    }

    [Fact]
    public void Resolve_ExactName_ReturnsCode()
    {
        Seed(("fhir", "FHIR Infrastructure", false));
        Assert.Equal("fhir", NewResolver().Resolve("FHIR Infrastructure"));
    }

    [Fact]
    public void Resolve_NameClean_Fallback()
    {
        Seed(("oo", "Orders & Observations", false));
        // Punctuation/whitespace differences should still resolve via NameClean.
        Assert.Equal("oo", NewResolver().Resolve("orders   and observations"));
    }

    [Fact]
    public void Resolve_RetiredRow_StillResolves()
    {
        Seed(("legacy", "Legacy Group", true));
        Assert.Equal("legacy", NewResolver().Resolve("legacy"));
        Assert.Equal("legacy", NewResolver().Resolve("Legacy Group"));
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNull()
    {
        Seed(("fhir", "FHIR Infrastructure", false));
        Assert.Null(NewResolver().Resolve("totally unknown wg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrEmpty_ReturnsNull(string? input)
    {
        Seed(("fhir", "FHIR Infrastructure", false));
        Assert.Null(NewResolver().Resolve(input));
    }

    [Fact]
    public void Reload_PicksUpNewRows()
    {
        Seed(("fhir", "FHIR Infrastructure", false));
        WorkGroupResolver resolver = NewResolver();
        Assert.Null(resolver.Resolve("oo"));

        Seed(("oo", "Orders & Observations", false));
        resolver.Reload();
        Assert.Equal("oo", resolver.Resolve("oo"));
        Assert.Equal(2, resolver.Count);
    }
}
