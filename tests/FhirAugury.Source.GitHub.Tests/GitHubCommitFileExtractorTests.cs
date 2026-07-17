using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubCommitFileExtractorTests
{
    private const string Repo = "owner/repo";
    private const char NUL = '\x00';
    private const char SOH = '\x01';
    private const string ZeroSha = "0000000000000000000000000000000000000000";

    /// <summary>
    /// Builds a single <c>--raw --no-abbrev</c> diff line of the form
    /// <c>:&lt;om&gt; &lt;nm&gt; &lt;oldblob&gt; &lt;newblob&gt; &lt;status&gt;\t&lt;path&gt;</c>,
    /// appending <c>\t&lt;newPath&gt;</c> for rename/copy rows.
    /// </summary>
    private static string RawLine(
        string status, string path, string? newPath = null,
        string oldBlob = "1111111111111111111111111111111111111111",
        string newBlob = "2222222222222222222222222222222222222222")
    {
        string tail = newPath is not null ? $"{path}\t{newPath}" : path;
        return $":100644 100644 {oldBlob} {newBlob} {status}\t{tail}";
    }

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
            string.Join("\n",
                RawLine("A", "src/NewFile.cs", newBlob: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                RawLine("M", "src/Existing.cs", newBlob: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                RawLine("D", "src/OldFile.cs", newBlob: ZeroSha)) + "\n");

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
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", files[0].BlobSha);

        Assert.Equal("M", files[1].ChangeType);
        Assert.Equal("src/Existing.cs", files[1].FilePath);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", files[1].BlobSha);

        Assert.Equal("D", files[2].ChangeType);
        Assert.Equal("src/OldFile.cs", files[2].FilePath);
        Assert.Null(files[2].BlobSha);
    }

    [Fact]
    public void ParsePass1_RenameRow_UsesNewPath()
    {
        string output = BuildCommitBlock(
            "def4567890abcdef1234567890abcdef1234567890",
            "Bob",
            "2024-06-16T12:00:00+00:00",
            "Rename file",
            RawLine("R100", "src/OldName.cs", newPath: "src/NewName.cs",
                newBlob: "cccccccccccccccccccccccccccccccccccccccc") + "\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParsePass1(output, Repo);

        Assert.Single(results);
        List<GitHubCommitFileRecord> files = results[0].Files;
        Assert.Single(files);
        Assert.Equal("R100", files[0].ChangeType);
        Assert.Equal("src/NewName.cs", files[0].FilePath);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", files[0].BlobSha);
    }

    [Fact]
    public void ParsePass1_CopyRow_UsesNewPath()
    {
        string output = BuildCommitBlock(
            "ccc4567890abcdef1234567890abcdef1234567890",
            "Carol",
            "2024-06-17T14:00:00+00:00",
            "Copy file",
            RawLine("C100", "src/Original.cs", newPath: "src/Copied.cs") + "\n");

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
            "GARBAGE LINE\nX\n" +
            RawLine("T", "src/TypeChanged.cs") + "\n" +
            RawLine("A", "src/Good.cs") + "\n\t\n");

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
            RawLine("A", "file1.txt") + "\n" +
            RawLine("R095", "old/path.cs", newPath: "new/path.cs") + "\n");

        string commit2 = BuildCommitBlock(
            "2222222222222222222222222222222222222222",
            "Frank",
            "2024-06-20T10:00:00+00:00",
            "Second commit",
            RawLine("M", "file2.txt") + "\n" +
            RawLine("D", "removed.txt", newBlob: ZeroSha) + "\n");

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
            "Fix bug", "", "", RawLine("M", "src/Bug.cs") + "\n");

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
            RawLine("A", "src/Patient.cs") + "\n");

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
            "One-liner commit", "", "", RawLine("M", "file.txt") + "\n");

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
            RawLine("A", "src/File.cs") + "\n");

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
            RawLine("A", "src/File.cs") + "\n");

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

    // ── BuildLogRange tests ──────────────────────────────────────────

    [Fact]
    public void BuildLogRange_WithLastSha_ReturnsShaRange()
    {
        (string sinceArg, string limitArg) = GitHubCommitFileExtractor.BuildLogRange("abc123def456");

        Assert.Equal("abc123def456..HEAD", sinceArg);
        Assert.Equal("", limitArg);
    }

    [Fact]
    public void BuildLogRange_NoLastSha_ReturnsHeadWithLimit()
    {
        (string sinceArg, string limitArg) = GitHubCommitFileExtractor.BuildLogRange(null);

        Assert.Equal("HEAD", sinceArg);
        Assert.Equal(" -n 500", limitArg);
    }

    [Fact]
    public void BuildLogRange_CustomMaxCount_Honored()
    {
        (string sinceArg, string limitArg) = GitHubCommitFileExtractor.BuildLogRange(null, maxInitialCommits: 100);

        Assert.Equal("HEAD", sinceArg);
        Assert.Equal(" -n 100", limitArg);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildLogRange_NonPositiveMax_Unbounded(int max)
    {
        (string sinceArg, string limitArg) = GitHubCommitFileExtractor.BuildLogRange(null, maxInitialCommits: max);

        Assert.Equal("HEAD", sinceArg);
        Assert.Equal("", limitArg);
    }

    [Fact]
    public void BuildLogRange_Deepen_WithLastShaAndUncapped_WalksFullHistory()
    {
        (string sinceArg, string limitArg) =
            GitHubCommitFileExtractor.BuildLogRange("abc123def456", maxInitialCommits: 0, deepen: true);

        Assert.Equal("HEAD", sinceArg);
        Assert.Equal("", limitArg);
    }

    [Fact]
    public void BuildLogRange_Deepen_WithLastShaAndFiniteCap_HeadWithLimit()
    {
        (string sinceArg, string limitArg) =
            GitHubCommitFileExtractor.BuildLogRange("abc123def456", maxInitialCommits: 250, deepen: true);

        Assert.Equal("HEAD", sinceArg);
        Assert.Equal(" -n 250", limitArg);
    }

    [Fact]
    public void BuildLogRange_DeepenFalse_WithLastSha_StaysForwardOnly()
    {
        (string sinceArg, string limitArg) =
            GitHubCommitFileExtractor.BuildLogRange("abc123def456", maxInitialCommits: 0, deepen: false);

        Assert.Equal("abc123def456..HEAD", sinceArg);
        Assert.Equal("", limitArg);
    }

    // ── ParseRootShas tests ──────────────────────────────────────────

    [Fact]
    public void ParseRootShas_SingleRoot_Parsed()
    {
        List<string> roots = GitHubCommitFileExtractor.ParseRootShas(
            "1111111111111111111111111111111111111111\n");

        Assert.Equal(["1111111111111111111111111111111111111111"], roots);
    }

    [Fact]
    public void ParseRootShas_MultipleRoots_Parsed()
    {
        List<string> roots = GitHubCommitFileExtractor.ParseRootShas(
            "1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n");

        Assert.Equal(2, roots.Count);
        Assert.Contains("1111111111111111111111111111111111111111", roots);
        Assert.Contains("2222222222222222222222222222222222222222", roots);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void ParseRootShas_BlankOrWhitespace_Empty(string input)
    {
        Assert.Empty(GitHubCommitFileExtractor.ParseRootShas(input));
    }

    [Fact]
    public void ParseRootShas_ShortTokens_Filtered()
    {
        List<string> roots = GitHubCommitFileExtractor.ParseRootShas(
            "abc\n1111111111111111111111111111111111111111\n123456\n");

        Assert.Equal(["1111111111111111111111111111111111111111"], roots);
    }

    // ── ShouldDeepen decision matrix ─────────────────────────────────

    [Theory]
    // cap<=0 && prior && !rootsIngested  → deepen
    [InlineData(0, true, false, true)]
    [InlineData(-1, true, false, true)]
    // roots already ingested → no deepen (gate closed)
    [InlineData(0, true, true, false)]
    // no prior history (first-ever run) → no deepen
    [InlineData(0, false, false, false)]
    // finite cap → never deepen
    [InlineData(500, true, false, false)]
    [InlineData(500, true, true, false)]
    [InlineData(500, false, false, false)]
    public void ShouldDeepen_Matrix(int cap, bool hasPrior, bool allRootsIngested, bool expected)
    {
        Assert.Equal(expected, GitHubCommitFileExtractor.ShouldDeepen(cap, hasPrior, allRootsIngested));
    }

    // ── BuildPass1Args tests ─────────────────────────────────────────

    [Fact]
    public void BuildPass1Args_PrependsRenameLimitBeforeLog()
    {
        string args = GitHubCommitFileExtractor.BuildPass1Args("HEAD", " -n 500");

        Assert.StartsWith("-c diff.renameLimit=5000 log ", args);
        Assert.Contains("--raw --no-abbrev", args);
        Assert.DoesNotContain("--name-status", args);
        Assert.Contains("---END-HEADER---", args);
    }

    [Fact]
    public void BuildPass1Args_PassesThroughRange()
    {
        string incremental = GitHubCommitFileExtractor.BuildPass1Args("abc..HEAD", "");
        Assert.Contains("log abc..HEAD --raw --no-abbrev", incremental);

        string initial = GitHubCommitFileExtractor.BuildPass1Args("HEAD", " -n 500");
        Assert.Contains("log HEAD -n 500 --raw --no-abbrev", initial);
    }

    // ── ClassifyStderr tests ─────────────────────────────────────────

    [Fact]
    public void ClassifyStderr_PureWarning_IsBenign()
    {
        string stderr =
            "warning: exhaustive rename detection was skipped due to too many files.\n" +
            "warning: you may want to set your diff.renameLimit variable to at least 2736 and retry the command.";

        (string? benign, string? other) = GitHubCommitFileExtractor.ClassifyStderr(stderr);

        Assert.NotNull(benign);
        Assert.Contains("exhaustive rename detection was skipped", benign);
        Assert.Contains("diff.renameLimit", benign);
        Assert.Null(other);
    }

    [Fact]
    public void ClassifyStderr_MixedOutput_SplitsBuckets()
    {
        (string? benign, string? other) = GitHubCommitFileExtractor.ClassifyStderr("warning: x\nfatal-ish noise");

        Assert.Equal("warning: x", benign);
        Assert.Equal("fatal-ish noise", other);
    }

    [Fact]
    public void ClassifyStderr_NonWarning_GoesToOther()
    {
        (string? benign, string? other) = GitHubCommitFileExtractor.ClassifyStderr("some unexpected line");

        Assert.Null(benign);
        Assert.Equal("some unexpected line", other);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n \n")]
    public void ClassifyStderr_EmptyOrWhitespace_ReturnsNulls(string input)
    {
        (string? benign, string? other) = GitHubCommitFileExtractor.ClassifyStderr(input);

        Assert.Null(benign);
        Assert.Null(other);
    }
}
