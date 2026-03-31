using System.Collections.Frozen;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Classifies repository files by extension to determine whether to skip, and which parser to use.
/// </summary>
public static class FileTypeClassifier
{
    /// <summary>Action to take for a given file.</summary>
    public enum FileAction
    {
        Skip,
        ParseXml,
        ParseJson,
        ParseMarkdown,
        ParseText,
        ParseCode,
        ParseFallback,
    }

    private static readonly FrozenSet<string> SkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp", ".tiff", ".tif",
        // Video
        ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".flv", ".webm",
        // Audio
        ".mp3", ".wav", ".ogg", ".flac", ".aac", ".wma",
        // Executable / binary
        ".exe", ".dll", ".so", ".dylib", ".bin", ".com", ".msi",
        // Archive / compressed
        ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".jar", ".war", ".ear", ".nupkg",
        // Compiled / bytecode
        ".class", ".pyc", ".pyo", ".o", ".obj", ".wasm",
        // Font
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        // Database
        ".db", ".sqlite", ".mdb",
        // PDF / office
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SkipFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json",
        "yarn.lock",
        "pnpm-lock.yaml",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", "dist", "build", "packages",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> XmlExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".xhtml", ".html", ".htm", ".csproj", ".fsproj", ".props", ".targets",
        ".resx", ".config", ".xsd", ".xsl", ".xslt", ".wsdl", ".xaml",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> JsonExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> MarkdownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".mdx",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".bat", ".sh", ".cmd", ".ps1", ".bash", ".zsh",
        ".ini", ".cfg", ".conf", ".env", ".properties",
        ".toml", ".yml", ".yaml",
        ".csv", ".tsv", ".log",
        ".rst", ".adoc", ".tex",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".go", ".rs", ".rb", ".php",
        ".c", ".cpp", ".h", ".hpp",
        ".swift", ".kt", ".scala",
        ".sql", ".r", ".m", ".pl", ".lua",
        ".groovy", ".gradle", ".cmake",
        ".makefile", ".dockerfile",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Classifies a file by its path and extension.</summary>
    public static FileAction Classify(string filePath, IReadOnlySet<string>? additionalSkipExtensions = null)
    {
        string fileName = Path.GetFileName(filePath);

        if (SkipFileNames.Contains(fileName))
            return FileAction.Skip;

        // Check for minified files
        if (fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
            return FileAction.Skip;

        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return FileAction.ParseFallback;

        if (SkipExtensions.Contains(ext))
            return FileAction.Skip;

        if (additionalSkipExtensions is not null && additionalSkipExtensions.Contains(ext))
            return FileAction.Skip;

        if (XmlExtensions.Contains(ext))
            return FileAction.ParseXml;

        if (JsonExtensions.Contains(ext))
            return FileAction.ParseJson;

        if (MarkdownExtensions.Contains(ext))
            return FileAction.ParseMarkdown;

        if (TextExtensions.Contains(ext))
            return FileAction.ParseText;

        if (CodeExtensions.Contains(ext))
            return FileAction.ParseCode;

        return FileAction.ParseFallback;
    }

    /// <summary>Returns true if the directory should be skipped entirely.</summary>
    public static bool IsSkippedDirectory(string dirName, IReadOnlySet<string>? additionalSkipDirs = null)
    {
        if (SkipDirectories.Contains(dirName))
            return true;

        if (additionalSkipDirs is not null && additionalSkipDirs.Contains(dirName))
            return true;

        return false;
    }
}
