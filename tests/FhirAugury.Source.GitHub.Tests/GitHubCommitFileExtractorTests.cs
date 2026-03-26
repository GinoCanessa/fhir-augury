using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubCommitFileExtractorTests
{
    private const string Repo = "owner/repo";
    private const char NUL = '\x00';
    private const char SOH = '\x01';

    /// <summary>
    /// Builds a Pass 1 commit block using the NUL/SOH format:
    /// \x00SHA\x01author\x01email\x01date\x01cn\x01ce\x01cd\x01subject\x01body\x01refs\x01---END-HEADER---
    /// followed by name-status lines.
    /// </summary>
    private static string BuildCommitBlock(
        string sha, string author, string authorEmail, string date,
        string committerName, string committerEmail, string committerDate,
        string subject, string body, string refs, string fileLines)
    {
        return $"{NUL}{sha}{SOH}{author}{SOH}{authorEmail}{SOH}{date}{SOH}{committerName}{SOH}{committerEmail}{SOH}{committerDate}{SOH}{subject}{SOH}{body}{SOH}{refs}{SOH}---END-HEADER---\n{fileLines}";
    }

    /// <summary>Convenience overload with minimal fields.</summary>
    private static string BuildCommitBlock(string sha, string author, string date, string subject, string fileLines)
    {
        return BuildCommitBlock(sha, author, $"{author.ToLower()}@example.com", date, author, $"{author.ToLower()}@example.com", date, subject, "", "", fileLines);
    }

    [Fact]
    public void ParsePass1_NormalAMD_ParsedCorrectly()
    {
        string output = BuildCommitBlock(
            "abc1234567890abcdef1234567890abcdef123456",
            "Alice",
            "2024-06-15T10:00:00+00:00",
            "Add and modify files",
            "A\tsrc/NewFile.cs\nM\tsrc/Existing.cs\nD\tsrc/OldFile.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        (GitHubCommitRecord? commit, List<GitHubCommitFileRecord>? files) = results[0];
        Assert.Equal("abc1234567890abcdef1234567890abcdef123456", commit.Sha);
        Assert.Equal("Alice", commit.Author);
        Assert.Equal("alice@example.com", commit.AuthorEmail);
        Assert.Equal("Add and modify files", commit.Message);
        Assert.Equal(3, files.Count);

        Assert.Equal("A", files[0].ChangeType);
        Assert.Equal("src/NewFile.cs", files[0].FilePath);

        Assert.Equal("M", files[1].ChangeType);
        Assert.Equal("src/Existing.cs", files[1].FilePath);

        Assert.Equal("D", files[2].ChangeType);
        Assert.Equal("src/OldFile.cs", files[2].FilePath);
    }

    [Fact]
    public void ParsePass1_RenameRow_UsesNewPath()
    {
        string output = BuildCommitBlock(
            "def4567890abcdef1234567890abcdef1234567890",
            "Bob",
            "2024-06-16T12:00:00+00:00",
            "Rename file",
            "R100\tsrc/OldName.cs\tsrc/NewName.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        List<GitHubCommitFileRecord> files = results[0].Files;
        Assert.Single(files);
        Assert.Equal("R100", files[0].ChangeType);
        Assert.Equal("src/NewName.cs", files[0].FilePath);
    }

    [Fact]
    public void ParsePass1_CopyRow_UsesNewPath()
    {
        string output = BuildCommitBlock(
            "ccc4567890abcdef1234567890abcdef1234567890",
            "Carol",
            "2024-06-17T14:00:00+00:00",
            "Copy file",
            "C100\tsrc/Original.cs\tsrc/Copied.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        List<GitHubCommitFileRecord> files = results[0].Files;
        Assert.Single(files);
        Assert.Equal("C100", files[0].ChangeType);
        Assert.Equal("src/Copied.cs", files[0].FilePath);
    }

    [Fact]
    public void ParsePass1_MalformedLines_Skipped()
    {
        string output = BuildCommitBlock(
            "aaa4567890abcdef1234567890abcdef1234567890",
            "Dave",
            "2024-06-18T08:00:00+00:00",
            "Some commit",
            "GARBAGE LINE\nX\nA\tsrc/Good.cs\n\t\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        List<GitHubCommitFileRecord> files = results[0].Files;
        Assert.Single(files);
        Assert.Equal("src/Good.cs", files[0].FilePath);
    }

    [Fact]
    public void ParsePass1_EmptyOutput_ReturnsEmpty()
    {
        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1("", Repo);
        Assert.Empty(results);
    }

    [Fact]
    public void ParsePass1_WhitespaceOnly_ReturnsEmpty()
    {
        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1("   \n  \n  ", Repo);
        Assert.Empty(results);
    }

    [Fact]
    public void ParsePass1_MultipleCommits_ParsedCorrectly()
    {
        string commit1 = BuildCommitBlock(
            "1111111111111111111111111111111111111111",
            "Eve",
            "2024-06-19T09:00:00+00:00",
            "First commit",
            "A\tfile1.txt\nR095\told/path.cs\tnew/path.cs\n");

        string commit2 = BuildCommitBlock(
            "2222222222222222222222222222222222222222",
            "Frank",
            "2024-06-20T10:00:00+00:00",
            "Second commit",
            "M\tfile2.txt\nD\tremoved.txt\n");

        string output = commit1 + commit2;

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Equal(2, results.Count);

        Assert.Equal("1111111111111111111111111111111111111111", results[0].Commit.Sha);
        Assert.Equal(2, results[0].Files.Count);
        Assert.Equal("file1.txt", results[0].Files[0].FilePath);
        Assert.Equal("new/path.cs", results[0].Files[1].FilePath);
        Assert.Equal("R095", results[0].Files[1].ChangeType);

        Assert.Equal("2222222222222222222222222222222222222222", results[1].Commit.Sha);
        Assert.Equal(2, results[1].Files.Count);
        Assert.Equal("file2.txt", results[1].Files[0].FilePath);
        Assert.Equal("removed.txt", results[1].Files[1].FilePath);
    }

    [Fact]
    public void ParsePass1_AuthorAndCommitterInfo_Captured()
    {
        string output = BuildCommitBlock(
            "abc1234567890abcdef1234567890abcdef123456",
            "Alice Author", "alice@dev.com", "2024-06-15T10:00:00+00:00",
            "Charlie Committer", "charlie@dev.com", "2024-06-15T11:00:00+00:00",
            "Fix bug", "", "", "M\tsrc/Bug.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        GitHubCommitRecord commit = results[0].Commit;
        Assert.Equal("Alice Author", commit.Author);
        Assert.Equal("alice@dev.com", commit.AuthorEmail);
        Assert.Equal("Charlie Committer", commit.CommitterName);
        Assert.Equal("charlie@dev.com", commit.CommitterEmail);
    }

    [Fact]
    public void ParsePass1_MultiLineBody_Captured()
    {
        string body = "This is a detailed description.\n\nIt has multiple paragraphs.\n\nFixes FHIR-12345";
        string output = BuildCommitBlock(
            "bbb1234567890abcdef1234567890abcdef123456",
            "Alice", "alice@dev.com", "2024-06-15T10:00:00+00:00",
            "Alice", "alice@dev.com", "2024-06-15T10:00:00+00:00",
            "feat: add patient resource", body, "HEAD -> main, tag: v1.0",
            "A\tsrc/Patient.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        GitHubCommitRecord commit = results[0].Commit;
        Assert.Equal("feat: add patient resource", commit.Message);
        Assert.Equal(body, commit.Body);
        Assert.Equal("HEAD -> main, tag: v1.0", commit.Refs);
    }

    [Fact]
    public void ParsePass1_EmptyBody_NullStored()
    {
        string output = BuildCommitBlock(
            "ddd1234567890abcdef1234567890abcdef123456",
            "Dave", "dave@dev.com", "2024-06-18T08:00:00+00:00",
            "Dave", "dave@dev.com", "2024-06-18T08:00:00+00:00",
            "One-liner commit", "", "", "M\tfile.txt\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        Assert.Null(results[0].Commit.Body);
        Assert.Null(results[0].Commit.Refs);
    }

    // ── Pass 2 (numstat) tests ───────────────────────────────────────

    [Fact]
    public void ParsePass2_NormalStats_SumsCorrectly()
    {
        string output = """
            abc1234567890abcdef1234567890abcdef123456

            10	5	src/Parser.cs
            3	0	src/Model.cs
            0	20	src/Deprecated.cs

            """;

        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = GitHubCommitFileExtractor.ParsePass2(output);

        Assert.Single(stats);
        Assert.True(stats.ContainsKey("abc1234567890abcdef1234567890abcdef123456"));
        (int filesChanged, int insertions, int deletions) = stats["abc1234567890abcdef1234567890abcdef123456"];
        Assert.Equal(3, filesChanged);
        Assert.Equal(13, insertions);
        Assert.Equal(25, deletions);
    }

    [Fact]
    public void ParsePass2_BinaryFile_CountedButNotSummed()
    {
        string output = """
            abc1234567890abcdef1234567890abcdef123456

            5	2	src/Code.cs
            -	-	docs/logo.png

            """;

        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = GitHubCommitFileExtractor.ParsePass2(output);

        (int filesChanged, int insertions, int deletions) = stats["abc1234567890abcdef1234567890abcdef123456"];
        Assert.Equal(2, filesChanged);
        Assert.Equal(5, insertions);
        Assert.Equal(2, deletions);
    }

    [Fact]
    public void ParsePass2_EmptyCommit_ZeroStats()
    {
        string output = """
            abc1234567890abcdef1234567890abcdef123456

            """;

        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = GitHubCommitFileExtractor.ParsePass2(output);

        Assert.True(stats.ContainsKey("abc1234567890abcdef1234567890abcdef123456"));
        (int filesChanged, int insertions, int deletions) = stats["abc1234567890abcdef1234567890abcdef123456"];
        Assert.Equal(0, filesChanged);
        Assert.Equal(0, insertions);
        Assert.Equal(0, deletions);
    }

    [Fact]
    public void ParsePass2_MultipleCommits_ParsedSeparately()
    {
        string output = """
            1111111111111111111111111111111111111111

            10	5	file1.cs

            2222222222222222222222222222222222222222

            3	1	file2.cs
            7	0	file3.cs

            """;

        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = GitHubCommitFileExtractor.ParsePass2(output);

        Assert.Equal(2, stats.Count);

        (int f1, int i1, int d1) = stats["1111111111111111111111111111111111111111"];
        Assert.Equal(1, f1);
        Assert.Equal(10, i1);
        Assert.Equal(5, d1);

        (int f2, int i2, int d2) = stats["2222222222222222222222222222222222222222"];
        Assert.Equal(2, f2);
        Assert.Equal(10, i2);
        Assert.Equal(1, d2);
    }

    [Fact]
    public void ParsePass2_EmptyOutput_ReturnsEmpty()
    {
        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = GitHubCommitFileExtractor.ParsePass2("");
        Assert.Empty(stats);
    }

    // ── MergeStats tests ─────────────────────────────────────────────

    [Fact]
    public void MergeStats_MatchesBySha_PopulatesFields()
    {
        string output = BuildCommitBlock(
            "abc1234567890abcdef1234567890abcdef123456",
            "Alice",
            "2024-06-15T10:00:00+00:00",
            "Add files",
            "A\tsrc/File.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> commits = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = new()
        {
            ["abc1234567890abcdef1234567890abcdef123456"] = (3, 42, 7),
        };

        GitHubCommitFileExtractor.MergeStats(commits, stats);

        Assert.Equal(3, commits[0].Commit.FilesChanged);
        Assert.Equal(42, commits[0].Commit.Insertions);
        Assert.Equal(7, commits[0].Commit.Deletions);
    }

    [Fact]
    public void MergeStats_NoMatchingSha_LeavesZero()
    {
        string output = BuildCommitBlock(
            "abc1234567890abcdef1234567890abcdef123456",
            "Alice",
            "2024-06-15T10:00:00+00:00",
            "Add files",
            "A\tsrc/File.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> commits = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = new()
        {
            ["ffffffffffffffffffffffffffffffffffffffff"] = (1, 10, 5),
        };

        GitHubCommitFileExtractor.MergeStats(commits, stats);

        Assert.Equal(0, commits[0].Commit.FilesChanged);
        Assert.Equal(0, commits[0].Commit.Insertions);
        Assert.Equal(0, commits[0].Commit.Deletions);
    }
}
