using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

public class OutputDifferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"differ-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (OutputDiffer Differ, ApplierRepoOptions Repo, string Worktree, string Baseline, string OutputDir) NewSut(
        List<string>? volatilePatterns = null)
    {
        ApplierOptions options = new()
        {
            WorkingDirectory = Path.Combine(_root, "ws"),
            OutputDirectory = Path.Combine(_root, "out"),
            PlannerDatabasePath = "ignored",
            VolatileOutputPatterns = volatilePatterns ?? [],
        };
        ApplierRepoOptions repo = new()
        {
            Owner = "HL7",
            Name = "fhir",
            BuildCommand = "/bin/true",
            OutputRoots = ["output/**"],
        };
        OutputDiffer differ = new(Options.Create(options), NullLogger<OutputDiffer>.Instance);
        string worktree = Path.Combine(_root, "wt");
        string baseline = Path.Combine(_root, "bl");
        string outputDir = RepoWorkspaceLayout.OutputPath(options.OutputDirectory, "FHIR-1", repo.Owner, repo.Name);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(baseline);
        return (differ, repo, worktree, baseline, outputDir);
    }

    [Fact]
    public async Task IgnoresFilesOutsideOutputRoots()
    {
        var (differ, repo, worktree, baseline, _) = NewSut();
        Directory.CreateDirectory(Path.Combine(worktree, "output"));
        File.WriteAllText(Path.Combine(worktree, "output", "a.txt"), "new");
        File.WriteAllText(Path.Combine(worktree, "source.txt"), "edited");

        OutputDiffSummary result = await differ.ComputeAndCopyAsync(repo, worktree, baseline, "FHIR-1", default);

        Assert.Single(result.Entries);
        Assert.Equal("output/a.txt", result.Entries[0].RelativePath);
        Assert.Equal("added", result.Entries[0].DiffSummary);
    }

    [Fact]
    public async Task SkipsShaIdenticalFiles()
    {
        var (differ, repo, worktree, baseline, _) = NewSut();
        Directory.CreateDirectory(Path.Combine(worktree, "output"));
        Directory.CreateDirectory(Path.Combine(baseline, "output"));
        File.WriteAllText(Path.Combine(worktree, "output", "same.txt"), "hello");
        File.WriteAllText(Path.Combine(baseline, "output", "same.txt"), "hello");

        OutputDiffSummary result = await differ.ComputeAndCopyAsync(repo, worktree, baseline, "FHIR-1", default);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DetectsAddRemoveModified()
    {
        var (differ, repo, worktree, baseline, _) = NewSut();
        Directory.CreateDirectory(Path.Combine(worktree, "output"));
        Directory.CreateDirectory(Path.Combine(baseline, "output"));

        File.WriteAllText(Path.Combine(worktree, "output", "added.txt"), "x");
        File.WriteAllText(Path.Combine(baseline, "output", "removed.txt"), "y");
        File.WriteAllText(Path.Combine(worktree, "output", "mod.txt"), "v2");
        File.WriteAllText(Path.Combine(baseline, "output", "mod.txt"), "v1");

        OutputDiffSummary result = await differ.ComputeAndCopyAsync(repo, worktree, baseline, "FHIR-1", default);

        Assert.Equal(3, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.RelativePath == "output/added.txt" && e.DiffSummary == "added");
        Assert.Contains(result.Entries, e => e.RelativePath == "output/removed.txt" && e.DiffSummary == "removed");
        Assert.Contains(result.Entries, e => e.RelativePath == "output/mod.txt" && e.DiffSummary == "modified");
    }

    [Fact]
    public async Task VolatilePatternsNormaliseAwayDifferences()
    {
        var (differ, repo, worktree, baseline, _) = NewSut(volatilePatterns: [@"Generated at \d{4}-\d{2}-\d{2}"]);
        Directory.CreateDirectory(Path.Combine(worktree, "output"));
        Directory.CreateDirectory(Path.Combine(baseline, "output"));
        File.WriteAllText(Path.Combine(worktree, "output", "page.html"), "<html>Generated at 2026-04-29 hello</html>");
        File.WriteAllText(Path.Combine(baseline, "output", "page.html"), "<html>Generated at 2026-04-28 hello</html>");

        OutputDiffSummary result = await differ.ComputeAndCopyAsync(repo, worktree, baseline, "FHIR-1", default);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task CopiesAddedAndModifiedToOutputSubtree()
    {
        var (differ, repo, worktree, baseline, outputDir) = NewSut();
        Directory.CreateDirectory(Path.Combine(worktree, "output", "nested"));
        File.WriteAllText(Path.Combine(worktree, "output", "added.txt"), "added-content");
        File.WriteAllText(Path.Combine(worktree, "output", "nested", "deep.txt"), "deep");

        await differ.ComputeAndCopyAsync(repo, worktree, baseline, "FHIR-1", default);

        Assert.True(File.Exists(Path.Combine(outputDir, "output", "added.txt")));
        Assert.Equal("added-content", File.ReadAllText(Path.Combine(outputDir, "output", "added.txt")));
        Assert.True(File.Exists(Path.Combine(outputDir, "output", "nested", "deep.txt")));
    }

    [Fact]
    public async Task ReplacesPriorOutputSubtreeOnRerun()
    {
        var (differ, repo, worktree, baseline, outputDir) = NewSut();
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "stale.txt"), "leftover");

        Directory.CreateDirectory(Path.Combine(worktree, "output"));
        File.WriteAllText(Path.Combine(worktree, "output", "fresh.txt"), "new");

        await differ.ComputeAndCopyAsync(repo, worktree, baseline, "FHIR-1", default);

        Assert.False(File.Exists(Path.Combine(outputDir, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(outputDir, "output", "fresh.txt")));
    }

    [Fact]
    public async Task EmptyDiff_DoesNotCreateOutputDir()
    {
        var (differ, repo, worktree, baseline, outputDir) = NewSut();
        Directory.CreateDirectory(Path.Combine(worktree, "output"));
        Directory.CreateDirectory(Path.Combine(baseline, "output"));
        File.WriteAllText(Path.Combine(worktree, "output", "x.txt"), "same");
        File.WriteAllText(Path.Combine(baseline, "output", "x.txt"), "same");

        OutputDiffSummary result = await differ.ComputeAndCopyAsync(repo, worktree, baseline, "FHIR-1", default);

        Assert.Empty(result.Entries);
        Assert.False(Directory.Exists(outputDir));
    }
}
