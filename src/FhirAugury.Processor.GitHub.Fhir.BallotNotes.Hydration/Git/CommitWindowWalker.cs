namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

/// <summary>
/// One commit in the <c>since..HEAD</c> window with the paths it changed,
/// scoped to a unit's file set.
/// </summary>
public sealed record WindowCommit
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public string AuthorName { get; init; } = string.Empty;

    /// <summary>Strict ISO-8601 author date (git <c>%aI</c>), kept as text.</summary>
    public string AuthorDate { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];
}

/// <summary>
/// Walks the <c>since..HEAD</c> commit window of a local clone, modelled on
/// <c>GitHubCommitFileExtractor</c>'s NUL/SOH-delimited <c>git log</c> parse.
/// </summary>
public static class CommitWindowWalker
{
    private const char RecordSeparator = '\x00';
    private const char FieldSeparator = '\x01';
    private const string EndHeaderMarker = "---END-HEADER---";

    private const string LogFormat =
        "--format=%x00%H%x01%h%x01%an%x01%aI%x01%s%x01%b%x01" + EndHeaderMarker;

    /// <summary>
    /// Lists every path changed in <c>since..HEAD</c> repo-wide (the grouper's
    /// input), via <c>git diff --name-only</c>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListChangedFilesAsync(
        string clonePath,
        string sinceSha,
        CancellationToken ct = default)
    {
        string output = await GitRunner.RunAsync(
            clonePath,
            ["diff", "--name-only", $"{sinceSha}..HEAD"],
            ct).ConfigureAwait(false);

        List<string> paths = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) paths.Add(trimmed);
        }
        return paths;
    }

    /// <summary>
    /// Walks the window scoped to <paramref name="paths"/> (a unit's changed
    /// files). When <paramref name="paths"/> is empty the walk is repo-wide.
    /// </summary>
    public static async Task<IReadOnlyList<WindowCommit>> WalkAsync(
        string clonePath,
        string sinceSha,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        List<string> args =
        [
            "log",
            $"{sinceSha}..HEAD",
            "--no-merges",
            "--name-status",
            LogFormat,
        ];
        if (paths.Count > 0)
        {
            args.Add("--");
            args.AddRange(paths);
        }

        string output = await GitRunner.RunAsync(clonePath, args, ct).ConfigureAwait(false);
        return ParseLog(output);
    }

    /// <summary>Parses NUL-delimited <c>git log</c> blocks into <see cref="WindowCommit"/> records.</summary>
    public static IReadOnlyList<WindowCommit> ParseLog(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        List<WindowCommit> commits = [];
        string[] blocks = output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block)) continue;

            string[] fields = block.Split(FieldSeparator);
            if (fields.Length < 7) continue;

            string sha = fields[0].Trim();
            if (sha.Length < 7) continue;

            string remainder = fields[6];
            int markerIdx = remainder.IndexOf(EndHeaderMarker, StringComparison.Ordinal);
            string fileSection = markerIdx >= 0
                ? remainder[(markerIdx + EndHeaderMarker.Length)..]
                : string.Empty;

            commits.Add(new WindowCommit
            {
                Sha = sha,
                ShortSha = fields[1].Trim(),
                AuthorName = fields[2].Trim(),
                AuthorDate = fields[3].Trim(),
                Subject = fields[4].Trim(),
                Body = fields[5].Trim(),
                ChangedPaths = ParseNameStatus(fileSection),
            });
        }

        return commits;
    }

    private static IReadOnlyList<string> ParseNameStatus(string section)
    {
        List<string> paths = [];
        foreach (string rawLine in section.Split('\n', StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Length < 2) continue;
            if (line[0] is not ('A' or 'M' or 'D' or 'R' or 'C')) continue;

            string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            bool isRenameOrCopy = parts[0].StartsWith('R') || parts[0].StartsWith('C');
            string path = isRenameOrCopy && parts.Length >= 3 ? parts[2].Trim() : parts[1].Trim();
            if (path.Length > 0) paths.Add(path);
        }
        return paths;
    }
}
