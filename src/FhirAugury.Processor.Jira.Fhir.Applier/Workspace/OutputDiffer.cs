using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

public sealed record OutputDiffEntry(string RelativePath, string DiffSummary, long ByteSize, string Sha256);

public sealed record OutputDiffSummary(IReadOnlyList<OutputDiffEntry> Entries);

/// <summary>
/// Diffs the post-build worktree against the per-repo baseline snapshot, scoped to the
/// repo's <see cref="ApplierRepoOptions.OutputRoots"/> globs only. Files identical by
/// SHA-256 are skipped; text files are normalised against
/// <see cref="ApplierOptions.VolatileOutputPatterns"/> before comparison so generation
/// timestamps / build IDs / similar volatile content do not register as real changes.
/// Surviving differences are copied into
/// <c>{OutputDirectory}/{ticketKey}/{safeName}/&lt;relative path&gt;</c> after the prior
/// per-(ticket, repo) subtree has been wiped.
/// </summary>
public sealed class OutputDiffer(
    IOptions<ApplierOptions> applierOptions,
    ILogger<OutputDiffer> logger)
{
    private readonly ApplierOptions _options = applierOptions.Value;
    private const long TextNormalizationMaxBytes = 16 * 1024 * 1024;

    public async Task<OutputDiffSummary> ComputeAndCopyAsync(
        ApplierRepoOptions repo,
        string worktreePath,
        string baselinePath,
        string ticketKey,
        CancellationToken ct)
    {
        IReadOnlyList<string> outputRoots = OutputRootResolver.GetEffectiveOutputRoots(repo);

        IReadOnlyList<string> currentFiles = OutputRootResolver.ResolveFiles(worktreePath, outputRoots);
        IReadOnlyList<string> baselineFiles = Directory.Exists(baselinePath)
            ? EnumerateAllFiles(baselinePath)
            : [];

        HashSet<string> currentSet = new(currentFiles, StringComparer.Ordinal);
        HashSet<string> baselineSet = new(baselineFiles, StringComparer.Ordinal);

        List<Regex> volatilePatterns = _options.VolatileOutputPatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase))
            .ToList();

        List<OutputDiffEntry> entries = [];

        // Removed: in baseline but not in current
        foreach (string relative in baselineSet.Except(currentSet))
        {
            string baselineFile = Path.Combine(baselinePath, relative);
            FileInfo info = new(baselineFile);
            entries.Add(new OutputDiffEntry(
                RelativePath: relative,
                DiffSummary: "removed",
                ByteSize: info.Exists ? info.Length : 0,
                Sha256: info.Exists ? await ComputeSha256Async(baselineFile, ct) : string.Empty));
        }

        // Added or modified
        foreach (string relative in currentSet)
        {
            ct.ThrowIfCancellationRequested();
            string currentFile = Path.Combine(worktreePath, relative);
            string sha = await ComputeSha256Async(currentFile, ct);
            long size = new FileInfo(currentFile).Length;

            if (!baselineSet.Contains(relative))
            {
                entries.Add(new OutputDiffEntry(relative, "added", size, sha));
                continue;
            }

            string baselineFile = Path.Combine(baselinePath, relative);
            string baselineSha = await ComputeSha256Async(baselineFile, ct);
            if (string.Equals(sha, baselineSha, StringComparison.Ordinal))
            {
                continue;
            }

            if (volatilePatterns.Count > 0 && size <= TextNormalizationMaxBytes && new FileInfo(baselineFile).Length <= TextNormalizationMaxBytes)
            {
                if (await TextEqualAfterNormalisationAsync(currentFile, baselineFile, volatilePatterns, ct))
                {
                    continue;
                }
            }

            entries.Add(new OutputDiffEntry(relative, "modified", size, sha));
        }

        // Sort for stable output
        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));

        // Copy survivors
        string outputDir = RepoWorkspaceLayout.OutputPath(_options.OutputDirectory, ticketKey, repo.Owner, repo.Name);
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }
        if (entries.Count > 0)
        {
            Directory.CreateDirectory(outputDir);
            foreach (OutputDiffEntry entry in entries)
            {
                if (entry.DiffSummary == "removed")
                {
                    continue;
                }
                string source = Path.Combine(worktreePath, entry.RelativePath);
                string target = Path.Combine(outputDir, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }

        logger.LogInformation("Output diff for {Repo}/{Ticket}: {Count} entries", repo.FullName, ticketKey, entries.Count);
        return new OutputDiffSummary(entries);
    }

    private static IReadOnlyList<string> EnumerateAllFiles(string root)
    {
        List<string> result = [];
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<bool> TextEqualAfterNormalisationAsync(string a, string b, IReadOnlyList<Regex> patterns, CancellationToken ct)
    {
        try
        {
            string textA = await File.ReadAllTextAsync(a, Encoding.UTF8, ct);
            string textB = await File.ReadAllTextAsync(b, Encoding.UTF8, ct);
            foreach (Regex pattern in patterns)
            {
                textA = pattern.Replace(textA, string.Empty);
                textB = pattern.Replace(textB, string.Empty);
            }
            return string.Equals(textA, textB, StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
