using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Indexed content of a file from a cloned repository.</summary>
[LdgSQLiteTable("github_file_contents")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(FilePath))]
[LdgSQLiteIndex(nameof(FileExtension))]
[LdgSQLiteIndex(nameof(ParserType))]
public partial record class GitHubFileContentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    /// <summary>Relative to clone root, forward slashes.</summary>
    public required string FilePath { get; set; }

    /// <summary>Lowercase, including dot (e.g., ".xml").</summary>
    public required string FileExtension { get; set; }

    /// <summary>Parser used: "xml", "json", "markdown", "text", "code".</summary>
    public required string ParserType { get; set; }

    /// <summary>Extracted/parsed text content.</summary>
    public string? ContentText { get; set; }

    /// <summary>Original file size in bytes.</summary>
    public required int ContentLength { get; set; }

    /// <summary>Length of ContentText.</summary>
    public required int ExtractedLength { get; set; }

    /// <summary>SHA of the last commit touching this file.</summary>
    public string? LastCommitSha { get; set; }

    /// <summary>ISO 8601 timestamp of the last commit.</summary>
    public string? LastModifiedAt { get; set; }
}
