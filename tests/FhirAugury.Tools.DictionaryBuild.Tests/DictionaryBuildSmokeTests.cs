using FhirAugury.Tools.DictionaryBuild;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.DictionaryBuild.Tests;

public class DictionaryBuildSmokeTests
{
    [Fact]
    public async Task Main_BuildsDatabase_WithExpectedCounts()
    {
        string root = CreateTempRoot();
        string sourceDir = Path.Combine(root, "dictionary");
        string dbPath = Path.Combine(root, "dictionary.db");

        try
        {
            WriteSource(sourceDir);

            int exit = await Program.Main(["--source", sourceDir, "--out", dbPath]);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(dbPath), "Output DB should exist after a successful build.");
            Assert.Equal(3, CountRows(dbPath, "words"));
            Assert.Equal(2, CountRows(dbPath, "typos"));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Main_RerunOverwrites_FullRebuild()
    {
        string root = CreateTempRoot();
        string sourceDir = Path.Combine(root, "dictionary");
        string dbPath = Path.Combine(root, "dictionary.db");

        try
        {
            WriteSource(sourceDir);
            int firstExit = await Program.Main(["--source", sourceDir, "--out", dbPath]);
            Assert.Equal(0, firstExit);
            Assert.Equal(3, CountRows(dbPath, "words"));

            // Shrink the source and rebuild — a full rebuild must reflect the new
            // contents (not append to / skip the existing DB).
            File.WriteAllText(
                Path.Combine(sourceDir, "extra.words.txt"),
                string.Empty);
            File.WriteAllText(
                Path.Combine(sourceDir, "test.words.txt"),
                "solo\n");
            File.Delete(Path.Combine(sourceDir, "test.typo.txt"));

            int secondExit = await Program.Main(["--source", sourceDir, "--out", dbPath]);

            Assert.Equal(0, secondExit);
            Assert.Equal(1, CountRows(dbPath, "words"));
            Assert.Equal(0, CountRows(dbPath, "typos"));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Main_MissingSource_ReturnsNonZero_NoOutput()
    {
        string root = CreateTempRoot();
        string sourceDir = Path.Combine(root, "missing");
        string dbPath = Path.Combine(root, "dictionary.db");

        try
        {
            int exit = await Program.Main(["--source", sourceDir, "--out", dbPath]);

            Assert.NotEqual(0, exit);
            Assert.False(File.Exists(dbPath), "No DB should be created when the source is missing.");
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dictbuild-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteSource(string sourceDir)
    {
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            Path.Combine(sourceDir, "test.words.txt"),
            "# comment line\nalpha\nbeta\ngamma\n");
        File.WriteAllText(
            Path.Combine(sourceDir, "test.typo.txt"),
            "# comment\nteh -> the\nrecieve -> receive\n");
    }

    private static long CountRows(string dbPath, string table)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using SqliteConnection connection = new(connectionString);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
