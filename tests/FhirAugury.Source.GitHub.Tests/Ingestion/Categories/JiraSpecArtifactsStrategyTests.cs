using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Categories;

public class JiraSpecArtifactsStrategyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;
    private readonly JiraSpecXmlIndexer _indexer;
    private readonly JiraSpecArtifactsStrategy _strategy;
    private const string RepoName = "HL7/JIRA-Spec-Artifacts";

    public JiraSpecArtifactsStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jiraspec-strat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
        _indexer = new JiraSpecXmlIndexer(NullLogger<JiraSpecXmlIndexer>.Instance);
        _strategy = new JiraSpecArtifactsStrategy(_indexer, NullLogger<JiraSpecArtifactsStrategy>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    private string CreateCloneDir()
    {
        string cloneDir = Path.Combine(_tempDir, "clone");
        Directory.CreateDirectory(Path.Combine(cloneDir, "xml"));
        return cloneDir;
    }

    private static void WriteFile(string dir, string relativePath, string content)
    {
        string fullPath = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void SetupValidRepo(string cloneDir)
    {
        WriteFile(cloneDir, "xml/_families.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <families>
              <family key="FHIR"/>
              <family key="CDA"/>
            </families>
            """);

        WriteFile(cloneDir, "xml/SPECS-FHIR.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specifications>
              <specification key="core" name="FHIR Core"/>
            </specifications>
            """);
    }

    [Fact]
    public void Category_IsCorrect()
    {
        Assert.Equal(RepoCategory.JiraSpecArtifacts, _strategy.Category);
    }

    [Fact]
    public void StrategyName_IsCorrect()
    {
        Assert.Equal("jira-spec-artifacts", _strategy.StrategyName);
    }

    [Fact]
    public void Validate_TrueForWellFormedStructure()
    {
        string cloneDir = CreateCloneDir();
        SetupValidRepo(cloneDir);
        Assert.True(_strategy.Validate(RepoName, cloneDir));
    }

    [Fact]
    public void Validate_FalseWhenXmlDirMissing()
    {
        string cloneDir = Path.Combine(_tempDir, "no-xml");
        Directory.CreateDirectory(cloneDir);
        Assert.False(_strategy.Validate(RepoName, cloneDir));
    }

    [Fact]
    public void Validate_FalseWhenNoFamiliesFile()
    {
        string cloneDir = Path.Combine(_tempDir, "no-families");
        Directory.CreateDirectory(Path.Combine(cloneDir, "xml"));
        WriteFile(cloneDir, "xml/SPECS-FHIR.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specifications/>
            """);
        Assert.False(_strategy.Validate(RepoName, cloneDir));
    }

    [Fact]
    public void Validate_FalseWhenNoSpecsFiles()
    {
        string cloneDir = Path.Combine(_tempDir, "no-specs");
        Directory.CreateDirectory(Path.Combine(cloneDir, "xml"));
        WriteFile(cloneDir, "xml/_families.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <families><family key="FHIR"/></families>
            """);
        Assert.False(_strategy.Validate(RepoName, cloneDir));
    }

    [Fact]
    public void GetPriorityPaths_ReturnsXml()
    {
        List<string>? paths = _strategy.GetPriorityPaths(RepoName, _tempDir);
        Assert.NotNull(paths);
        Assert.Single(paths);
        Assert.Contains("xml/", paths);
    }

    [Fact]
    public void GetAdditionalIgnorePatterns_ExcludesNonContent()
    {
        List<string> patterns = _strategy.GetAdditionalIgnorePatterns();
        Assert.Contains("tools/", patterns);
        Assert.Contains("schemas/", patterns);
        Assert.Contains("images/", patterns);
        Assert.Contains("json/", patterns);
        Assert.Contains(".github/", patterns);
    }

    [Fact]
    public void DiscoverTags_TagsByTypeAndFamily()
    {
        string cloneDir = CreateCloneDir();
        SetupValidRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3"/>
            """);
        WriteFile(cloneDir, "xml/_workgroups.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workgroups/>
            """);

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags(RepoName, cloneDir, CancellationToken.None);

        // Expect type tags for: _families.xml, _workgroups.xml, SPECS-FHIR.xml, FHIR-core.xml
        // And a spec-family tag for FHIR-core.xml
        Assert.Contains(tags, t => t.TagCategory == "type" && t.TagName == "jira-family-list");
        Assert.Contains(tags, t => t.TagCategory == "type" && t.TagName == "jira-workgroup-list");
        Assert.Contains(tags, t => t.TagCategory == "type" && t.TagName == "jira-spec-list");
        Assert.Contains(tags, t => t.TagCategory == "type" && t.TagName == "jira-spec-definition");
        Assert.Contains(tags, t => t.TagCategory == "spec-family" && t.TagName == "FHIR");
    }

    [Fact]
    public void DiscoverTags_IncludesRootLevelFiles()
    {
        string cloneDir = CreateCloneDir();
        SetupValidRepo(cloneDir);
        // Root-level spec file
        WriteFile(cloneDir, "FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3"/>
            """);

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags(RepoName, cloneDir, CancellationToken.None);
        Assert.Contains(tags, t =>
            t.TagCategory == "type" &&
            t.TagName == "jira-spec-definition" &&
            t.FilePath == "FHIR-core.xml");
    }

    [Fact]
    public void BuildArtifactMappings_DelegatesToIndexer()
    {
        string cloneDir = CreateCloneDir();
        SetupValidRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
              <artifact key="patient" name="Patient" id="StructureDefinition/Patient"/>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _strategy.BuildArtifactMappings(RepoName, cloneDir, connection, CancellationToken.None);

        // Verify the indexer was called and populated data
        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: "core");
        Assert.Single(specs);
        List<JiraSpecArtifactRecord> artifacts = JiraSpecArtifactRecord.SelectList(connection, SpecKey: "core");
        Assert.Single(artifacts);
        Assert.Equal("patient", artifacts[0].ArtifactKey);
    }
}
