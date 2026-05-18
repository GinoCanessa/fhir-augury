using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.PreparerSite.Tests;

public sealed class HydrationAssertionTests
{
    [Fact]
    public async Task Assert_ReturnsTrue_WhenHydrationRowsPresent()
    {
        string dbPath = NewDbPath();
        await PreparerTestDb.SeedAsync(
            dbPath,
            [new("FHIR-1")],
            specByKey: new Dictionary<string, string?> { ["FHIR-1"] = "fhir-core" });

        using StringWriter stderr = new();
        bool ok = await HydrationAssertion.AssertHydratedAsync(dbPath, stderr, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task Assert_ReturnsFalse_WithActionableMessage_WhenHydrationEmpty()
    {
        string dbPath = NewDbPath();
        // Create the table (preparer schema) but seed no hydration rows.
        using PreparerDatabase preparer = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        preparer.Initialize();

        using StringWriter stderr = new();
        bool ok = await HydrationAssertion.AssertHydratedAsync(dbPath, stderr, CancellationToken.None);

        Assert.False(ok);
        string text = stderr.ToString();
        Assert.Contains("not hydrated", text, StringComparison.Ordinal);
        Assert.Contains("FhirAugury.Processor.Jira.Fhir.Preparer", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assert_ReturnsFalse_WhenHydrationTableAbsent()
    {
        string dbPath = NewDbPath();
        // Empty SQLite file, no preparer tables at all.
        await using (SqliteConnection cn = new($"Data Source={dbPath}"))
        {
            await cn.OpenAsync();
            await using SqliteCommand cmd = cn.CreateCommand();
            cmd.CommandText = "CREATE TABLE _placeholder (x INTEGER)";
            await cmd.ExecuteNonQueryAsync();
        }

        using StringWriter stderr = new();
        bool ok = await HydrationAssertion.AssertHydratedAsync(dbPath, stderr, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("not hydrated", stderr.ToString(), StringComparison.Ordinal);
    }

    private static string NewDbPath()
        => Path.Combine(AppContext.BaseDirectory, $"hydration-assert-{Guid.NewGuid():N}.db");
}
