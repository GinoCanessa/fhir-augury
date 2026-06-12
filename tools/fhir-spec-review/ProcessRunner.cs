using FhirAugury.Tools.FhirSpecReview.Database;
using FhirAugury.Tools.FhirSpecReview.Readers;
using FhirAugury.Tools.FhirSpecReview.SpecReview;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Tools.FhirSpecReview;

/// <summary>Orchestrates the <c>process</c> verb: validates inputs, loads readers, runs the content review.</summary>
internal static class ProcessRunner
{
    public static async Task<int> RunAsync(ProcessOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.BaselineSitePath))
        {
            await Console.Error.WriteLineAsync("--baseline-site is required.").ConfigureAwait(false);
            return 2;
        }

        string githubDb = Path.GetFullPath(options.GitHubDbPath);
        if (!File.Exists(githubDb))
        {
            await Console.Error.WriteLineAsync($"GitHub source DB not found: {githubDb}").ConfigureAwait(false);
            return 1;
        }

        string fhirSpecDb = Path.GetFullPath(options.FhirSpecDbPath);
        if (!File.Exists(fhirSpecDb))
        {
            await Console.Error.WriteLineAsync($"fhir-spec.db not found: {fhirSpecDb}").ConfigureAwait(false);
            return 1;
        }

        string dictionaryDb = Path.GetFullPath(options.DictionaryDbPath);
        if (!File.Exists(dictionaryDb))
        {
            await Console.Error.WriteLineAsync($"dictionary.db not found: {dictionaryDb}").ConfigureAwait(false);
            return 1;
        }

        BaselineSiteReader siteReader = new(Path.GetFullPath(options.BaselineSitePath));
        if (!siteReader.Exists)
        {
            await Console.Error.WriteLineAsync($"Baseline site folder not found: {options.BaselineSitePath}").ConfigureAwait(false);
            return 1;
        }

        using GitHubCacheReader cacheReader = new(githubDb, options.GitHubCachePath, options.Repo, logger);
        if (!cacheReader.CloneRootExists)
        {
            await Console.Error.WriteLineAsync(
                $"Clone working tree not found under cache for {options.Repo} (expected {cacheReader.CloneRoot}).").ConfigureAwait(false);
            return 1;
        }

        FhirSpecDbReader specReader = new(fhirSpecDb);
        int? packageKey = specReader.ResolvePackageKey(options.BaselineRelease, out string? releaseError);
        if (packageKey is null)
        {
            await Console.Error.WriteLineAsync(releaseError).ConfigureAwait(false);
            return 1;
        }

        Console.WriteLine($"Loading baseline vocabulary ({options.BaselineRelease})...");
        SpecVocabulary baselineVocab = specReader.LoadBaselineVocabulary(packageKey.Value);

        Console.WriteLine($"Loading current-build vocabulary for {options.Repo}...");
        SpecVocabulary currentVocab = cacheReader.LoadCurrentVocabulary();

        DictionaryData dict = new DictionaryReader(dictionaryDb).Load();
        BaselinePresence presence = siteReader.Load();
        string buildVersion = cacheReader.ReadBuildVersion();

        string reviewDbPath = Path.GetFullPath(options.ReviewDbPath);
        string? reviewDir = Path.GetDirectoryName(reviewDbPath);
        if (!string.IsNullOrEmpty(reviewDir)) Directory.CreateDirectory(reviewDir);

        using ReviewDatabase reviewDb = new(reviewDbPath, logger);
        if (options.DropTables) reviewDb.DropTables();
        reviewDb.Initialize();

        Console.WriteLine($"Reviewing build {buildVersion} (baseline {options.BaselineRelease})...");
        ContentReview review = new(
            currentVocab, baselineVocab, dict, cacheReader, reviewDb, presence,
            options.Repo, options.BaselineRelease, logger);
        review.Run(buildVersion);

        Console.WriteLine($"Review written to {reviewDbPath}.");
        return 0;
    }
}
