using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FhirAugury.Tools.TicketMdToDb.Compilation;

public sealed record DiscoveredReport(
    string Key,
    string RelativePath,
    string AbsolutePath,
    string SourceSha256,
    [property: JsonIgnore] byte[] Bytes);

public sealed record ReportDiscoveryResult(
    string InputRoot,
    IReadOnlyList<DiscoveredReport> Reports,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public static partial class ReportDiscovery
{
    public static ReportDiscoveryResult Discover(string inputRoot, int expectedCount)
    {
        string absoluteRoot = Path.GetFullPath(inputRoot);
        List<ImportDiagnostic> diagnostics = [];
        if (!Directory.Exists(absoluteRoot))
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "input-root-missing",
                $"Input root does not exist: {absoluteRoot}"));
            return new ReportDiscoveryResult(absoluteRoot, [], diagnostics);
        }

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(
                    absoluteRoot,
                    "FHIR-*.md",
                    SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "input-enumeration-failed",
                $"{ex.GetType().Name}: {ex.Message}"));
            return new ReportDiscoveryResult(absoluteRoot, [], diagnostics);
        }

        if (paths.Length != expectedCount)
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "expected-count-mismatch",
                $"Expected {expectedCount} reports but discovered {paths.Length}."));
        }

        List<DiscoveredReport> reports = [];
        foreach (string path in paths)
        {
            string relativePath = Path.GetRelativePath(absoluteRoot, path).Replace('\\', '/');
            string fileKey = Path.GetFileNameWithoutExtension(path);
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                string markdown = Encoding.UTF8.GetString(bytes);
                Match documentKeyMatch = DocumentKeyRegex().Match(markdown);
                if (!documentKeyMatch.Success)
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "document-key-missing",
                        "No Jira key was found in the document title.",
                        relativePath,
                        fileKey));
                }
                else if (!string.Equals(fileKey, documentKeyMatch.Groups[1].Value, StringComparison.Ordinal))
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "document-key-mismatch",
                        $"Filename key {fileKey} does not match document key {documentKeyMatch.Groups[1].Value}.",
                        relativePath,
                        fileKey));
                }

                reports.Add(new DiscoveredReport(fileKey, relativePath, path, sha256, bytes));
            }
            catch (Exception ex)
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "report-unreadable",
                    $"{ex.GetType().Name}: {ex.Message}",
                    relativePath,
                    fileKey));
            }
        }

        foreach (IGrouping<string, DiscoveredReport> duplicate in reports
                     .GroupBy(report => report.Key, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "duplicate-jira-key",
                $"Jira key {duplicate.Key} appears in: {string.Join(", ", duplicate.Select(report => report.RelativePath))}.",
                ticketKey: duplicate.Key));
        }

        return new ReportDiscoveryResult(
            absoluteRoot,
            reports.OrderBy(report => report.RelativePath, StringComparer.Ordinal).ToArray(),
            diagnostics);
    }

    [GeneratedRegex(@"(?im)^\s*#.*?\b([A-Z][A-Z0-9]+-\d+)\b")]
    private static partial Regex DocumentKeyRegex();
}
