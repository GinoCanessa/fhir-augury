using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubCommitFileExtractorTests
{
    private const string Repo = "owner/repo";

    private static string BuildCommitBlock(string sha, string author, string date, string subject, string fileLines)
    {
        return $"{sha}\n{author}\n{date}\n{subject}\n---END-HEADER---\n{fileLines}";
    }

    [Fact]
    public void ParseGitLogOutput_NormalAMD_ParsedCorrectly()
    {
        string output = BuildCommitBlock(
            "abc1234567890abcdef1234567890abcdef123456",
            "Alice",
            "2024-06-15T10:00:00+00:00",
            "Add and modify files",
            "A\tsrc/NewFile.cs\nM\tsrc/Existing.cs\nD\tsrc/OldFile.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParseGitLogOutput(output, Repo);

        Assert.Single(results);
        (GitHubCommitRecord? commit, List<GitHubCommitFileRecord>? files) = results[0];
        Assert.Equal("abc1234567890abcdef1234567890abcdef123456", commit.Sha);
        Assert.Equal("Alice", commit.Author);
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
    public void ParseGitLogOutput_RenameRow_UsesNewPath()
    {
        string output = BuildCommitBlock(
            "def4567890abcdef1234567890abcdef1234567890",
            "Bob",
            "2024-06-16T12:00:00+00:00",
            "Rename file",
            "R100\tsrc/OldName.cs\tsrc/NewName.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParseGitLogOutput(output, Repo);

        Assert.Single(results);
        List<GitHubCommitFileRecord> files = results[0].Files;
        Assert.Single(files);
        Assert.Equal("R100", files[0].ChangeType);
        Assert.Equal("src/NewName.cs", files[0].FilePath);
    }

    [Fact]
    public void ParseGitLogOutput_CopyRow_UsesNewPath()
    {
        string output = BuildCommitBlock(
            "ccc4567890abcdef1234567890abcdef1234567890",
            "Carol",
            "2024-06-17T14:00:00+00:00",
            "Copy file",
            "C100\tsrc/Original.cs\tsrc/Copied.cs\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParseGitLogOutput(output, Repo);

        Assert.Single(results);
        List<GitHubCommitFileRecord> files = results[0].Files;
        Assert.Single(files);
        Assert.Equal("C100", files[0].ChangeType);
        Assert.Equal("src/Copied.cs", files[0].FilePath);
    }

    [Fact]
    public void ParseGitLogOutput_MalformedLines_Skipped()
    {
        string output = BuildCommitBlock(
            "aaa4567890abcdef1234567890abcdef1234567890",
            "Dave",
            "2024-06-18T08:00:00+00:00",
            "Some commit",
            "GARBAGE LINE\nX\nA\tsrc/Good.cs\n\t\n");

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParseGitLogOutput(output, Repo);

        Assert.Single(results);
        List<GitHubCommitFileRecord> files = results[0].Files;
        Assert.Single(files);
        Assert.Equal("src/Good.cs", files[0].FilePath);
    }

    [Fact]
    public void ParseGitLogOutput_EmptyOutput_ReturnsEmpty()
    {
        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParseGitLogOutput("", Repo);
        Assert.Empty(results);
    }

    [Fact]
    public void ParseGitLogOutput_WhitespaceOnly_ReturnsEmpty()
    {
        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParseGitLogOutput("   \n  \n  ", Repo);
        Assert.Empty(results);
    }

    [Fact]
    public void ParseGitLogOutput_MultipleCommits_ParsedCorrectly()
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

        string output = commit1 + "\n" + commit2;

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> results = GitHubCommitFileExtractor.ParseGitLogOutput(output, Repo);

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
}
