using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class IgnorePatternMatcherTests
{
    [Fact]
    public void EmptyMatcher_ExcludesNothing()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(null);
        Assert.False(matcher.IsExcluded("src/file.cs"));
        Assert.False(matcher.IsExcluded("README.md"));
    }

    [Fact]
    public void SimpleExtensionPattern_MatchesFiles()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(["*.generated.cs"]);
        Assert.True(matcher.IsExcluded("src/Models/Foo.generated.cs"));
        Assert.False(matcher.IsExcluded("src/Models/Foo.cs"));
    }

    [Fact]
    public void DoubleStarPattern_MatchesAtAnyDepth()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(["**/test-data/**"]);
        Assert.True(matcher.IsExcluded("test-data/file.xml"));
        Assert.True(matcher.IsExcluded("src/test-data/file.xml"));
        Assert.True(matcher.IsExcluded("src/deep/test-data/nested/file.xml"));
        Assert.False(matcher.IsExcluded("src/file.xml"));
    }

    [Fact]
    public void DirectoryPattern_MatchesDirectoryContents()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(["output/"]);
        Assert.True(matcher.IsExcluded("output/file.txt"));
        Assert.True(matcher.IsExcluded("output/sub/file.txt"));
        Assert.False(matcher.IsExcluded("src/output_file.txt"));
    }

    [Fact]
    public void NegationPattern_ReIncludesFiles()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher([
            "**/test-data/**",
            "!test-data/important.xml",
        ]);

        Assert.True(matcher.IsExcluded("test-data/junk.xml"));
        Assert.False(matcher.IsExcluded("test-data/important.xml"));
    }

    [Fact]
    public void LastMatchWins()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher([
            "*.xml",
            "!important.xml",
            "*.xml",
        ]);

        // Last pattern (*.xml) wins for all XML files
        Assert.True(matcher.IsExcluded("important.xml"));
        Assert.True(matcher.IsExcluded("other.xml"));
    }

    [Fact]
    public void CommentAndBlankLines_AreIgnored()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher([
            "# This is a comment",
            "",
            "  ",
            "*.tmp",
        ]);

        Assert.True(matcher.IsExcluded("file.tmp"));
        Assert.False(matcher.IsExcluded("file.txt"));
    }

    [Fact]
    public void PathSeparators_AreNormalized()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(["**/vendor/**"]);
        Assert.True(matcher.IsExcluded("src\\vendor\\lib\\file.cs"));
    }

    [Fact]
    public void SlashInPattern_MatchesFullPath()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(["build-output/*.xml"]);
        Assert.True(matcher.IsExcluded("build-output/results.xml"));
        Assert.False(matcher.IsExcluded("src/results.xml"));
    }

    [Fact]
    public void DirectoryPatternWithoutSlash_MatchesAtAnyLevel()
    {
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(["vendor/"]);
        Assert.True(matcher.IsExcluded("vendor/lib.js"));
        Assert.True(matcher.IsExcluded("src/vendor/lib.js"));
    }

    [Fact]
    public void LoadsFromFile()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, [
                "# Repo-level ignores",
                "fsh-generated/",
                "!fsh-generated/important.json",
            ]);

            IgnorePatternMatcher matcher = new IgnorePatternMatcher([], tempFile);
            Assert.True(matcher.IsExcluded("fsh-generated/output.json"));
            Assert.False(matcher.IsExcluded("fsh-generated/important.json"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ConfigAndFilePatternsAreMerged()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, ["docs/"]);

            IgnorePatternMatcher matcher = new IgnorePatternMatcher(["*.tmp"], tempFile);
            Assert.True(matcher.IsExcluded("file.tmp"));
            Assert.True(matcher.IsExcluded("docs/readme.md"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void MissingFileIsIgnored()
    {
        // Should not throw
        IgnorePatternMatcher matcher = new IgnorePatternMatcher(["*.tmp"], "/nonexistent/path/.augury-index-ignore");
        Assert.True(matcher.IsExcluded("file.tmp"));
    }
}
