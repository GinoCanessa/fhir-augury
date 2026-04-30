using System.IO;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Database;

public class ApplierDatabaseTests : IDisposable
{
    private readonly string _dbPath;

    public ApplierDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"applier-db-tests-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void Initialize_CreatesAllExpectedTables()
    {
        ApplierDatabase database = new(_dbPath, NullLogger<ApplierDatabase>.Instance);
        database.Initialize();

        HashSet<string> tables = ListTables();
        Assert.Contains("applied_ticket_queue_items", tables);
        Assert.Contains("applied_tickets", tables);
        Assert.Contains("applied_ticket_repos", tables);
        Assert.Contains("applied_ticket_repo_changes", tables);
        Assert.Contains("applied_ticket_output_files", tables);
        Assert.Contains("repo_baselines", tables);
    }

    [Fact]
    public void Initialize_CreatesCompositeUniqueIndexes()
    {
        ApplierDatabase database = new(_dbPath, NullLogger<ApplierDatabase>.Instance);
        database.Initialize();

        Assert.True(HasUniqueIndexOver("applied_ticket_queue_items", ["Key", "SourceTicketShape"]));
        Assert.True(HasUniqueIndexOver("applied_ticket_repos", ["IssueKey", "RepoKey"]));
        Assert.True(HasUniqueIndexOver("applied_ticket_output_files", ["IssueKey", "RepoKey", "RelativePath"]));
    }

    [Fact]
    public void AppliedTicketQueueItem_HasCompletionIdColumn()
    {
        ApplierDatabase database = new(_dbPath, NullLogger<ApplierDatabase>.Instance);
        database.Initialize();

        Assert.Contains("CompletionId", ListColumns("applied_ticket_queue_items"));
        Assert.Contains("PlannerCompletionId", ListColumns("applied_ticket_queue_items"));
    }

    private HashSet<string> ListTables()
    {
        using SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<string> names = new();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private HashSet<string> ListColumns(string table)
    {
        using SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<string> columns = new();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private bool HasUniqueIndexOver(string table, IReadOnlyList<string> expectedColumns)
    {
        using SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        using SqliteCommand listCommand = connection.CreateCommand();
        listCommand.CommandText = $"PRAGMA index_list({table});";
        using SqliteDataReader listReader = listCommand.ExecuteReader();
        List<string> uniqueIndexes = new();
        while (listReader.Read())
        {
            string name = listReader.GetString(1);
            long unique = listReader.GetInt64(2);
            if (unique == 1)
            {
                uniqueIndexes.Add(name);
            }
        }
        listReader.Close();

        foreach (string indexName in uniqueIndexes)
        {
            using SqliteCommand infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_info({indexName});";
            using SqliteDataReader infoReader = infoCommand.ExecuteReader();
            List<string> columns = new();
            while (infoReader.Read())
            {
                columns.Add(infoReader.GetString(2));
            }
            if (columns.Count == expectedColumns.Count
                && columns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
