using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

public class FileTypeClassifierTests
{
    [Theory]
    [InlineData("file.png", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.jpg", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.exe", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.dll", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.zip", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.pdf", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.db", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.wasm", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.ttf", FileTypeClassifier.FileAction.Skip)]
    [InlineData("file.nupkg", FileTypeClassifier.FileAction.Skip)]
    public void Skip_BinaryExtensions(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Theory]
    [InlineData("package-lock.json", FileTypeClassifier.FileAction.Skip)]
    [InlineData("yarn.lock", FileTypeClassifier.FileAction.Skip)]
    [InlineData("pnpm-lock.yaml", FileTypeClassifier.FileAction.Skip)]
    [InlineData("app.min.js", FileTypeClassifier.FileAction.Skip)]
    [InlineData("styles.min.css", FileTypeClassifier.FileAction.Skip)]
    public void Skip_SpecificFiles(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Theory]
    [InlineData("file.xml", FileTypeClassifier.FileAction.ParseXml)]
    [InlineData("file.html", FileTypeClassifier.FileAction.ParseXml)]
    [InlineData("file.csproj", FileTypeClassifier.FileAction.ParseXml)]
    [InlineData("file.props", FileTypeClassifier.FileAction.ParseXml)]
    [InlineData("file.xsd", FileTypeClassifier.FileAction.ParseXml)]
    public void ParseXml_Extensions(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Theory]
    [InlineData("file.json", FileTypeClassifier.FileAction.ParseJson)]
    public void ParseJson_Extensions(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Theory]
    [InlineData("file.md", FileTypeClassifier.FileAction.ParseMarkdown)]
    [InlineData("file.mdx", FileTypeClassifier.FileAction.ParseMarkdown)]
    public void ParseMarkdown_Extensions(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Theory]
    [InlineData("file.txt", FileTypeClassifier.FileAction.ParseText)]
    [InlineData("file.yml", FileTypeClassifier.FileAction.ParseText)]
    [InlineData("file.yaml", FileTypeClassifier.FileAction.ParseText)]
    [InlineData("file.sh", FileTypeClassifier.FileAction.ParseText)]
    [InlineData("file.toml", FileTypeClassifier.FileAction.ParseText)]
    [InlineData("file.env", FileTypeClassifier.FileAction.ParseText)]
    public void ParseText_Extensions(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Theory]
    [InlineData("file.cs", FileTypeClassifier.FileAction.ParseCode)]
    [InlineData("file.java", FileTypeClassifier.FileAction.ParseCode)]
    [InlineData("file.py", FileTypeClassifier.FileAction.ParseCode)]
    [InlineData("file.ts", FileTypeClassifier.FileAction.ParseCode)]
    [InlineData("file.go", FileTypeClassifier.FileAction.ParseCode)]
    [InlineData("file.rs", FileTypeClassifier.FileAction.ParseCode)]
    public void ParseCode_Extensions(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Theory]
    [InlineData("file.xyz", FileTypeClassifier.FileAction.ParseFallback)]
    [InlineData("file.unknown", FileTypeClassifier.FileAction.ParseFallback)]
    [InlineData("noextension", FileTypeClassifier.FileAction.ParseFallback)]
    public void Fallback_UnknownExtensions(string path, FileTypeClassifier.FileAction expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(path));
    }

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        Assert.Equal(FileTypeClassifier.FileAction.ParseXml, FileTypeClassifier.Classify("FILE.XML"));
        Assert.Equal(FileTypeClassifier.FileAction.ParseJson, FileTypeClassifier.Classify("data.JSON"));
        Assert.Equal(FileTypeClassifier.FileAction.Skip, FileTypeClassifier.Classify("IMAGE.PNG"));
    }

    [Fact]
    public void AdditionalSkipExtensions_AreRespected()
    {
        HashSet<string> additionalSkips = new(StringComparer.OrdinalIgnoreCase) { ".custom" };
        Assert.Equal(FileTypeClassifier.FileAction.Skip, FileTypeClassifier.Classify("file.custom", additionalSkips));
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData("node_modules", true)]
    [InlineData("bin", true)]
    [InlineData("obj", true)]
    [InlineData(".vs", true)]
    [InlineData("__pycache__", true)]
    [InlineData("src", false)]
    [InlineData("lib", false)]
    public void IsSkippedDirectory(string dirName, bool expected)
    {
        Assert.Equal(expected, FileTypeClassifier.IsSkippedDirectory(dirName));
    }

    [Fact]
    public void AdditionalSkipDirectories_AreRespected()
    {
        HashSet<string> additionalSkips = new(StringComparer.OrdinalIgnoreCase) { "custom_dir" };
        Assert.True(FileTypeClassifier.IsSkippedDirectory("custom_dir", additionalSkips));
        Assert.False(FileTypeClassifier.IsSkippedDirectory("other_dir", additionalSkips));
    }
}
