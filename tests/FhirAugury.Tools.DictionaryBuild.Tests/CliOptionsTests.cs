using FhirAugury.Tools.DictionaryBuild;

namespace FhirAugury.Tools.DictionaryBuild.Tests;

public class CliOptionsTests
{
    [Fact]
    public void TryParse_NoArgs_UsesDefaults()
    {
        bool ok = CliOptions.TryParse([], out BuildOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("./dictionary", options.SourcePath);
        Assert.Equal("./cache/dictionary.db", options.OutPath);
    }

    [Fact]
    public void TryParse_SourceAndOut_Override()
    {
        bool ok = CliOptions.TryParse(
            ["--source", "/tmp/dict", "--out", "/tmp/out.db"],
            out BuildOptions options,
            out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("/tmp/dict", options.SourcePath);
        Assert.Equal("/tmp/out.db", options.OutPath);
    }

    [Fact]
    public void TryParse_Force_IsAcceptedNoOp()
    {
        bool ok = CliOptions.TryParse(["--force"], out BuildOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("./dictionary", options.SourcePath);
        Assert.Equal("./cache/dictionary.db", options.OutPath);
    }

    [Fact]
    public void TryParse_MissingValue_ReturnsError()
    {
        bool ok = CliOptions.TryParse(["--source"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("--source", error);
    }

    [Fact]
    public void TryParse_UnknownFlag_ReturnsError()
    {
        bool ok = CliOptions.TryParse(["--nope"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("--nope", error);
    }
}
