using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Common.Tests;

public class DictionaryDatabaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger _logger = NullLogger.Instance;

    public DictionaryDatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public async Task EnsureCreated_BuildsFromWordFiles()
    {
        // Arrange
        string sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "test.words.txt"),
            """
            # Comment line
            ! Another comment
            hello
            world
            fhir

            duplicate
            duplicate
            """);

        DictionaryDatabaseOptions options = new()
        {
            SourcePath = sourceDir,
            DatabasePath = Path.Combine(_tempDir, "dict.db"),
        };

        // Act
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);

        // Assert
        Assert.True(File.Exists(options.DatabasePath));

        using SqliteConnection conn = OpenReadOnly(options.DatabasePath);
        long wordCount = ExecuteScalar(conn, "SELECT COUNT(*) FROM words;");
        Assert.Equal(4, wordCount); // hello, world, fhir, duplicate (deduped)

        // Verify specific words exist
        long helloExists = ExecuteScalar(conn, "SELECT COUNT(*) FROM words WHERE Word = 'hello';");
        Assert.Equal(1, helloExists);
    }

    [Fact]
    public async Task EnsureCreated_BuildsFromTypoFiles()
    {
        // Arrange
        string sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "test.typo.txt"),
            """
            # Typo corrections
            abandonned->abandoned
            abbout->about
            invalid line without arrow
            """);

        DictionaryDatabaseOptions options = new()
        {
            SourcePath = sourceDir,
            DatabasePath = Path.Combine(_tempDir, "dict.db"),
        };

        // Act
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);

        // Assert
        using SqliteConnection conn = OpenReadOnly(options.DatabasePath);
        long typoCount = ExecuteScalar(conn, "SELECT COUNT(*) FROM typos;");
        Assert.Equal(2, typoCount);

        // Verify specific typo
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Correction FROM typos WHERE Typo = 'abandonned';";
        string? correction = cmd.ExecuteScalar()?.ToString();
        Assert.Equal("abandoned", correction);
    }

    [Fact]
    public async Task EnsureCreated_SkipsWhenDatabaseExists()
    {
        // Arrange
        string sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "test.words.txt"), "hello\nworld\n");

        string dbPath = Path.Combine(_tempDir, "dict.db");
        DictionaryDatabaseOptions options = new()
        {
            SourcePath = sourceDir,
            DatabasePath = dbPath,
        };

        // Build once
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(dbPath);

        // Small delay to ensure timestamp difference
        await Task.Delay(50);

        // Act — should skip
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);
        DateTime secondWriteTime = File.GetLastWriteTimeUtc(dbPath);

        // Assert — file should not have been modified
        Assert.Equal(firstWriteTime, secondWriteTime);
    }

    [Fact]
    public async Task EnsureCreated_ForceRebuildOverwritesExisting()
    {
        // Arrange
        string sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "test.words.txt"), "hello\n");

        string dbPath = Path.Combine(_tempDir, "dict.db");
        DictionaryDatabaseOptions options = new()
        {
            SourcePath = sourceDir,
            DatabasePath = dbPath,
        };

        // Build once
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(dbPath);

        // Add more words
        File.WriteAllText(Path.Combine(sourceDir, "test.words.txt"), "hello\nworld\nfhir\n");

        // Small delay to ensure timestamp difference
        await Task.Delay(50);

        // Act — force rebuild
        options.ForceRebuild = true;
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);

        // Assert — file should be newer and contain new words
        DateTime secondWriteTime = File.GetLastWriteTimeUtc(dbPath);
        Assert.True(secondWriteTime > firstWriteTime);

        using SqliteConnection conn = OpenReadOnly(dbPath);
        long wordCount = ExecuteScalar(conn, "SELECT COUNT(*) FROM words;");
        Assert.Equal(3, wordCount);
    }

    [Fact]
    public async Task EnsureCreated_SkipsWhenSourceDirectoryMissing()
    {
        // Arrange
        DictionaryDatabaseOptions options = new()
        {
            SourcePath = Path.Combine(_tempDir, "nonexistent"),
            DatabasePath = Path.Combine(_tempDir, "dict.db"),
        };

        // Act — should not throw
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);

        // Assert — no database created
        Assert.False(File.Exists(options.DatabasePath));
    }

    [Fact]
    public async Task EnsureCreated_HandlesMultipleFileTypes()
    {
        // Arrange
        string sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "general.words.txt"), "hello\nworld\n");
        File.WriteAllText(Path.Combine(sourceDir, "medical.words.txt"), "aspirin\nfhir\n");
        File.WriteAllText(Path.Combine(sourceDir, "common.typo.txt"), "teh->the\nrecieve->receive\n");
        File.WriteAllText(Path.Combine(sourceDir, "README.md"), "This is not a dictionary file");
        File.WriteAllText(Path.Combine(sourceDir, "LICENSE.txt"), "MIT License");

        DictionaryDatabaseOptions options = new()
        {
            SourcePath = sourceDir,
            DatabasePath = Path.Combine(_tempDir, "dict.db"),
        };

        // Act
        await DictionaryDatabase.EnsureCreatedAsync(options, _logger);

        // Assert
        using SqliteConnection conn = OpenReadOnly(options.DatabasePath);
        long wordCount = ExecuteScalar(conn, "SELECT COUNT(*) FROM words;");
        Assert.Equal(4, wordCount); // hello, world, aspirin, fhir

        long typoCount = ExecuteScalar(conn, "SELECT COUNT(*) FROM typos;");
        Assert.Equal(2, typoCount); // teh->the, recieve->receive
    }

    [Fact]
    public async Task EnsureCreated_ConcurrentCallsAreSafe()
    {
        // Arrange
        string sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "test.words.txt"), "hello\nworld\nfhir\n");

        string dbPath = Path.Combine(_tempDir, "dict.db");

        // Act — launch multiple concurrent builds with ForceRebuild
        Task[] tasks = Enumerable.Range(0, 5).Select(_ =>
        {
            DictionaryDatabaseOptions options = new()
            {
                SourcePath = sourceDir,
                DatabasePath = dbPath,
                ForceRebuild = true,
            };
            return DictionaryDatabase.EnsureCreatedAsync(options, _logger);
        }).ToArray();

        // Should not throw
        await Task.WhenAll(tasks);

        // Assert — database should be valid
        Assert.True(File.Exists(dbPath));

        using SqliteConnection conn = OpenReadOnly(dbPath);
        long wordCount = ExecuteScalar(conn, "SELECT COUNT(*) FROM words;");
        Assert.Equal(3, wordCount);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        string cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        SqliteConnection conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static long ExecuteScalar(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
