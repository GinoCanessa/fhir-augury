using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FhirAugury.Common.Tests;

public sealed partial class DockerfileProjectClosureTests
{
    private static readonly string ProcessingCommonProject =
        Normalize("src/FhirAugury.Processing.Common/FhirAugury.Processing.Common.csproj");

    [Fact]
    public void ProcessingDockerfiles_CopyCompleteTransitiveProjectClosure()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] dockerfiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "src"),
            "Dockerfile",
            SearchOption.AllDirectories);
        List<string> failures = [];
        int inspected = 0;

        foreach (string dockerfile in dockerfiles)
        {
            string text = File.ReadAllText(dockerfile);
            Match restore = RestoreProjectRegex().Match(text);
            if (!restore.Success)
            {
                continue;
            }

            string targetProject = Normalize(restore.Groups["project"].Value);
            HashSet<string> closure = GetProjectClosure(repositoryRoot, targetProject);
            if (!closure.Contains(ProcessingCommonProject))
            {
                continue;
            }

            inspected++;
            HashSet<string> copiedProjects = ProjectCopyRegex()
                .Matches(text)
                .Select(match => Normalize(match.Groups["project"].Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> copiedSourceDirectories = SourceDirectoryCopyRegex()
                .Matches(text)
                .Select(match => Normalize(match.Groups["directory"].Value).TrimEnd('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string[] missingProjects = closure
                .Where(project => !copiedProjects.Contains(project))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] missingSources = closure
                .Select(project => project[..project.LastIndexOf('/')])
                .Where(directory => !copiedSourceDirectories.Contains(directory))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingProjects.Length > 0 || missingSources.Length > 0)
            {
                failures.Add(
                    $"{Path.GetRelativePath(repositoryRoot, dockerfile)}: " +
                    $"missing project copies [{string.Join(", ", missingProjects)}]; " +
                    $"missing source copies [{string.Join(", ", missingSources)}]");
            }

            string targetProjectText = File.ReadAllText(ToFileSystemPath(repositoryRoot, targetProject));
            if (targetProjectText.Contains(@"<Import Project=""..\sqlite.props""", StringComparison.Ordinal) &&
                !text.Contains(
                    "COPY src/SqliteProviderModuleInitializer.cs src/SqliteProviderModuleInitializer.cs",
                    StringComparison.Ordinal))
            {
                failures.Add(
                    $"{Path.GetRelativePath(repositoryRoot, dockerfile)}: " +
                    "missing src/SqliteProviderModuleInitializer.cs required by src/sqlite.props");
            }
        }

        Assert.True(inspected > 0, "No Dockerfile restore graph included FhirAugury.Processing.Common.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static HashSet<string> GetProjectClosure(string repositoryRoot, string targetProject)
    {
        HashSet<string> closure = new(StringComparer.OrdinalIgnoreCase);
        AddProjectAndReferences(repositoryRoot, targetProject, closure);
        return closure;
    }

    private static void AddProjectAndReferences(
        string repositoryRoot,
        string project,
        HashSet<string> closure)
    {
        project = Normalize(project);
        if (!closure.Add(project))
        {
            return;
        }

        string fullPath = ToFileSystemPath(repositoryRoot, project);
        XDocument document = XDocument.Load(fullPath);
        string projectDirectory = Path.GetDirectoryName(fullPath)!;
        foreach (XElement reference in document.Descendants("ProjectReference"))
        {
            string include = reference.Attribute("Include")?.Value
                ?? throw new InvalidOperationException($"{project} has a ProjectReference without Include.");
            string referencedPath = Path.GetFullPath(
                Path.Combine(
                    projectDirectory,
                    include
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar)));
            string relative = Path.GetRelativePath(repositoryRoot, referencedPath);
            AddProjectAndReferences(repositoryRoot, relative, closure);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "fhir-augury.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate fhir-augury.slnx.");
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/');

    private static string ToFileSystemPath(string repositoryRoot, string relativePath)
        => Path.Combine(
            repositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    [GeneratedRegex(@"RUN\s+dotnet\s+restore\s+(?<project>src/[^\s\\]+\.csproj)", RegexOptions.IgnoreCase)]
    private static partial Regex RestoreProjectRegex();

    [GeneratedRegex(@"COPY\s+(?<project>src/[^\s]+\.csproj)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectCopyRegex();

    [GeneratedRegex(@"COPY\s+(?<directory>src/[^\s]+/)\s+src/[^\s]+/", RegexOptions.IgnoreCase)]
    private static partial Regex SourceDirectoryCopyRegex();
}
