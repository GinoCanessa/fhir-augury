using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class CommitWindowWalkerTests
{
    private const char Nul = '\u0000';
    private const char Soh = '\u0001';
    private const string Marker = "---END-HEADER---";

    private static string Block(
        string sha, string shortSha, string author, string date, string subject, string body, string nameStatus)
        => $"{Nul}{sha}{Soh}{shortSha}{Soh}{author}{Soh}{date}{Soh}{subject}{Soh}{body}{Soh}{Marker}\n{nameStatus}\n";

    [Fact]
    public void ParseLog_parses_fields_and_changed_paths()
    {
        string output =
            Block(
                "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
                "a1b2c3d",
                "Jane Dev",
                "2026-06-10T12:00:00+00:00",
                "FHIR-12345 Fix Observation",
                "Detailed body text",
                "M\tsource/observation/observation.xml\nA\tsource/observation/observation-notes.xml")
            + Block(
                "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3",
                "b2c3d4e",
                "John Dev",
                "2026-06-11T09:30:00+00:00",
                "Update security page",
                "",
                "M\tsource/security.html");

        IReadOnlyList<WindowCommit> commits = CommitWindowWalker.ParseLog(output);

        Assert.Equal(2, commits.Count);

        WindowCommit first = commits[0];
        Assert.Equal("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2", first.Sha);
        Assert.Equal("a1b2c3d", first.ShortSha);
        Assert.Equal("Jane Dev", first.AuthorName);
        Assert.Equal("2026-06-10T12:00:00+00:00", first.AuthorDate);
        Assert.Equal("FHIR-12345 Fix Observation", first.Subject);
        Assert.Equal("Detailed body text", first.Body);
        Assert.Equal(
            ["source/observation/observation.xml", "source/observation/observation-notes.xml"],
            first.ChangedPaths);

        WindowCommit second = commits[1];
        Assert.Equal(["source/security.html"], second.ChangedPaths);
    }

    [Fact]
    public void ParseLog_uses_new_path_for_renames()
    {
        string output = Block(
            "c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4",
            "c3d4e5f",
            "Dev",
            "2026-06-12T00:00:00+00:00",
            "Rename page",
            "",
            "R100\tsource/old-name.html\tsource/new-name.html");

        IReadOnlyList<WindowCommit> commits = CommitWindowWalker.ParseLog(output);

        WindowCommit commit = Assert.Single(commits);
        Assert.Equal(["source/new-name.html"], commit.ChangedPaths);
    }

    [Fact]
    public void ParseLog_returns_empty_for_blank_output()
        => Assert.Empty(CommitWindowWalker.ParseLog(string.Empty));
}
