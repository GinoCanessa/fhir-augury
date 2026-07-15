using System.Diagnostics;

namespace FhirAugury.Tools.FhirXverElementDiff.Attribution;

/// <summary>One commit harvested from a <c>git log</c> walk.</summary>
internal sealed record CommitInfo(string Sha, string ShortSha, string Subject, string Body)
{
    /// <summary>Subject + body, joined, for ticket extraction over the whole message.</summary>
    public string Message => string.IsNullOrEmpty(Body) ? Subject : Subject + "\n" + Body;
}

/// <summary>
/// Thin async wrapper around invoking <c>git</c> against the <c>HL7/fhir</c> clone,
/// modelled on <c>...BallotNotes.Hydration/Git/GitRunner.cs</c> (argument list — no shell
/// quoting — plus a concurrent stdout/stderr drain). This tool does not reference the
/// hydration assembly, so this is a local copy of only the operations the attribution
/// walk needs. Every call is <b>best-effort</b>: a non-zero exit returns empty/null rather
/// than throwing, because attribution is enrichment layered on top of the exact change
/// tables, never a gate on them.
/// </summary>
internal sealed class GitLog
{
    // git's %x1f / %x1e format placeholders emit these control bytes; using them as the
    // field/record separators lets multi-line commit bodies parse unambiguously (a tab or
    // newline separator would collide with body content).
    private const string LogFormat = "--format=%H%x1f%h%x1f%s%x1f%b%x1e";
    private const char FieldSep = '\u001f';
    private const char RecordSep = '\u001e';

    private readonly string _clonePath;

    public GitLog(string clonePath) => _clonePath = clonePath;

    /// <summary>True when the clone directory exists on disk.</summary>
    public bool CloneAvailable => Directory.Exists(_clonePath);

    /// <summary><c>git rev-parse --short &lt;rev&gt;</c> → short SHA, or null on failure.</summary>
    public async Task<string?> RevParseShortAsync(string rev, CancellationToken ct = default)
    {
        (int exit, string stdout, _) = await RunAsync(["rev-parse", "--short", rev], ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return null;
        }
        string trimmed = stdout.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// The newest commit on <paramref name="branch"/> at or before <paramref name="date"/>
    /// (<c>git rev-list -1 --first-parent --before=&lt;date&gt; &lt;branch&gt;</c>). Used to
    /// record the R6 ballot4-snapshot commit (≈2026-06-24) for the header and Phase 6
    /// facet verification; null when unavailable.
    /// </summary>
    public async Task<string?> ResolveByDateAsync(
        DateTimeOffset date, string branch = "master", CancellationToken ct = default)
    {
        string iso = date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss");
        (int exit, string stdout, _) = await RunAsync(
            ["rev-list", "-1", "--first-parent", $"--before={iso}", branch], ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return null;
        }
        string trimmed = stdout.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// <c>git log &lt;since&gt;..&lt;until&gt; --no-merges</c> over the given paths, newest
    /// first. <c>--no-merges</c> keeps the walk on real authoring commits (decision Q3);
    /// PR-merge subjects/branches are harvested separately via <see cref="NearestMergeAsync"/>.
    /// Empty when the range/paths yield nothing or git fails.
    /// </summary>
    public async Task<IReadOnlyList<CommitInfo>> LogAsync(
        string since, string until, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
        {
            return [];
        }
        List<string> args = ["log", $"{since}..{until}", "--no-merges", LogFormat, "--"];
        args.AddRange(paths);
        (int exit, string stdout, _) = await RunAsync(args, ct).ConfigureAwait(false);
        return exit != 0 ? [] : ParseCommits(stdout);
    }

    /// <summary>
    /// The nearest enclosing merge of <paramref name="commit"/> within the window
    /// (<c>git log --merges --ancestry-path &lt;commit&gt;..&lt;until&gt; -n 1</c>), or null.
    /// </summary>
    public async Task<CommitInfo?> NearestMergeAsync(string commit, string until, CancellationToken ct = default)
    {
        (int exit, string stdout, _) = await RunAsync(
            ["log", "--merges", "--ancestry-path", $"{commit}..{until}", "-n", "1", LogFormat], ct)
            .ConfigureAwait(false);
        if (exit != 0)
        {
            return null;
        }
        IReadOnlyList<CommitInfo> commits = ParseCommits(stdout);
        return commits.Count > 0 ? commits[0] : null;
    }

    /// <summary>
    /// <c>git ls-tree -r --name-only &lt;rev&gt; -- source/</c> → the full <c>source/</c>
    /// file list at a revision, used by <see cref="SourceFileResolver"/> to resolve each
    /// structure's source file(s). Empty when git fails.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListSourceFilesAsync(string rev, CancellationToken ct = default)
    {
        (int exit, string stdout, _) = await RunAsync(
            ["ls-tree", "-r", "--name-only", rev, "--", "source/"], ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return [];
        }
        List<string> files = [];
        foreach (string line in stdout.Split('\n'))
        {
            string file = line.Trim();
            if (file.Length > 0)
            {
                files.Add(file);
            }
        }
        return files;
    }

    private static IReadOnlyList<CommitInfo> ParseCommits(string stdout)
    {
        List<CommitInfo> commits = [];
        foreach (string raw in stdout.Split(RecordSep))
        {
            string record = raw.TrimStart('\n', '\r');
            if (record.Length == 0)
            {
                continue;
            }
            string[] parts = record.Split(FieldSep);
            if (parts.Length < 3)
            {
                continue;
            }
            string sha = parts[0].Trim();
            if (sha.Length == 0)
            {
                continue;
            }
            string shortSha = parts[1].Trim();
            string subject = parts[2];
            string body = parts.Length >= 4 ? parts[3].TrimEnd('\n', '\r') : string.Empty;
            commits.Add(new CommitInfo(sha, shortSha, subject, body));
        }
        return commits;
    }

    private async Task<(int Exit, string StdOut, string StdErr)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -C targets the clone without relying on the process working directory, so a
        // missing clone simply produces a non-zero exit rather than a Start() exception.
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(_clonePath);
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return (-1, string.Empty, "failed to start git");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
