using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using FhirAugury.Tools.FhirSpecReview.Database;
using FhirAugury.Tools.FhirSpecReview.Database.Records;
using FhirAugury.Tools.FhirSpecReview.Readers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Tools.FhirSpecReview.SpecReview;

/// <summary>
/// Faithful port of the legacy <c>ContentReview</c> check engine. Drives the
/// per-page and per-artifact content-quality checks over the current build
/// (raw markup from the clone tree), comparing against the current-build
/// vocabulary (GitHub cache) and the published baseline vocabulary
/// (<c>fhir-spec.db</c>), with dictionary-based spell-check and baseline-site
/// presence tracking. Writes results into the review DB.
/// </summary>
internal sealed class ContentReview
{
    private static readonly char[] s_wordSplitChars = [' ', '\t', '\r', '\n', '"'];

    private static readonly char[] s_extendedSplitChars =
    [
        ' ', '\t', '\r', '\n',
        '.',
        ':', '\\', '/',
        '"', '\'', ';',
        '+', '-', '_', '#', '*', '&', '^', '%', '@', '!',
        ';', ',', '|', '?', '=',
        '{', '}', '(', ')', '[', ']',
    ];

    private static readonly char[] s_wgSplitChars = [' ', '[', ']', '%'];

    private static readonly Regex s_incompleteMarkerRegex = new(
        @"\b(to-do|todo|to\s+do|will\s+consider|\.\.\.|future\s+versions)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_readerReviewRegex = new(
        @"\b(" +
        @"\[%stu-note%\]|stu-note|stu\s+note" +
        @"|\[%impl-note%\]|implementation\s+note|implementer\s+note|note\s+to\s+implementers" +
        @"|\[%feedback-note%\]|feedback" +
        @"|\[%dragons-start%\]|dragon" +
        @"|balloters|voters" +
        @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_trialUseTagRegex = new(
        @"(>\s*Trial\s+Use\s*<|class\s*=\s*['""]stu['""])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_zulipLinkRegex = new(
        @"http[s?]:\/\/chat.fhir.org/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_confluenceLinkRegex = new(
        @"http[s?]://confluence.hl7.org/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_thoCodeSystemRegex = new(
        @"(http:\/\/|https:\/\/)?terminology.hl7.org(\/CodeSystem[^\s]*|\/temporary/CodeSystem[^\s]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_htmlStripRegex = new("<.*?>", RegexOptions.Compiled);

    private static readonly Regex s_urlRegex = new(
        @"(http|https|ftp|sftp):\/\/[^\s\/$.?#].[^\s]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_baseFhirRegex = new(@"\[base\]\/[^\s]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_typeFhirRegex = new(@"\[type\]\/(\[type\]\/?)?[^\s]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_urnRegex = new(@"urn:[a-zA-Z0-9][a-zA-Z0-9-]{1,31}:[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_xsdRegex = new(@"xs[d]?:[a-zA-Z0-9._%+-]+(\/[a-zA-Z0-9._%+-]+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_fhirShexRegex = new(@"fhir:[a-zA-Z0-9._%+-]+(\/[a-zA-Z0-9._%+-]+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_emailAddressRegex = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_dateTimeRegex = new(
        @"([0-9]([0-9]([0-9][1-9]|[1-9]0)|[1-9]00)|[1-9]000)(-(0[1-9]|1[0-2])(-(0[1-9]|[1-2][0-9]|3[0-1])(T([01][0-9]|2[0-3]):[0-5][0-9]:([0-5][0-9]|60)(\.[0-9]{1,9})?)?)?(Z|(\+|-)((0[0-9]|1[0-3]):[0-5][0-9]|14:00)?)?)?",
        RegexOptions.Compiled);

    private static readonly Regex s_fileTargetRegex = new(
        @"[^\s]+\.(png|jpg|jpeg|gif|svg|htm|html|diagram|xsd|json|xml|sch|zip|shex|ttl)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_conformantShallRegex = new(@"\bSHALL\b(?!\s+NOT)", RegexOptions.Compiled);
    private static readonly Regex s_totalShallRegex = new(@"\bSHALL\b(?!\s+NOT)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_conformantShallNotRegex = new(@"\bSHALL\s+NOT\b", RegexOptions.Compiled);
    private static readonly Regex s_totalShallNotRegex = new(@"\bSHALL\s+NOT\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_conformantShouldRegex = new(@"\bSHOULD\b(?!\s+NOT)", RegexOptions.Compiled);
    private static readonly Regex s_totalShouldRegex = new(@"\bSHOULD\b(?!\s+NOT)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_conformantShouldNotRegex = new(@"\bSHOULD\s+NOT\b", RegexOptions.Compiled);
    private static readonly Regex s_totalShouldNotRegex = new(@"\bSHOULD\s+NOT\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_conformantMayRegex = new(@"\bMAY\b(?!\s+NOT)", RegexOptions.Compiled);
    private static readonly Regex s_totalMayRegex = new(@"\bMAY\b(?!\s+NOT)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_conformantMayNotRegex = new(@"\bMAY\s+NOT\b", RegexOptions.Compiled);
    private static readonly Regex s_totalMayNotRegex = new(@"\bMAY\s+NOT\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> s_priorFhirVersionKeywords = new(
    [
        "dstu1",
        "dstu2", "r2", "hl7.fhir.r2.core", "fhirVersion=1.0",
        "stu3", "r3", "hl7.fhir.r3.core", "fhirVersion=3.0",
        "r4", "hl7.fhir.r4.core", "fhirVersion=4.0",
        "r4b", "hl7.fhir.r4b.core", "fhirVersion=4.3",
        "r5", "hl7.fhir.r5.core", "hl7.fhir.r5.corexml", "hl7.fhir.r5.examples", "hl7.fhir.r5.expansions", "hl7.fhir.r5.search", "fhirVersion=5.0",
    ], StringComparer.OrdinalIgnoreCase);

    private readonly SpecVocabulary _current;
    private readonly SpecVocabulary _baseline;
    private readonly Readers.DictionaryData _dict;
    private readonly GitHubCacheReader _cache;
    private readonly ReviewDatabase _reviewDb;
    private readonly BaselinePresence _baselinePresence;
    private readonly string _repo;
    private readonly string _baselineRelease;
    private readonly ILogger _logger;
    private readonly HtmlParser _htmlParser = new();

    private readonly bool _haveCurrent;
    private readonly bool _haveBaseline;
    private readonly bool _haveDict;

    public ContentReview(
        SpecVocabulary current,
        SpecVocabulary baseline,
        Readers.DictionaryData dict,
        GitHubCacheReader cache,
        ReviewDatabase reviewDb,
        BaselinePresence baselinePresence,
        string repo,
        string baselineRelease,
        ILogger logger)
    {
        _current = current;
        _baseline = baseline;
        _dict = dict;
        _cache = cache;
        _reviewDb = reviewDb;
        _baselinePresence = baselinePresence;
        _repo = repo;
        _baselineRelease = baselineRelease;
        _logger = logger;

        _haveCurrent = !current.IsEmpty;
        _haveBaseline = !baseline.IsEmpty;
        _haveDict = dict.Words.Count > 0 || dict.Typos.Count > 0;
    }

    public void Run(string buildVersion)
    {
        using SqliteConnection conn = _reviewDb.OpenConnection();

        List<ArtifactInfo> artifacts = _cache.EnumerateArtifacts();

        // Impose a stable order so the kept-vs-skipped choice on a duplicate
        // FhirId is deterministic (EnumerateArtifacts/SelectList has no ORDER BY).
        artifacts.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        HashSet<string> currentArtifactSanitized = new(StringComparer.OrdinalIgnoreCase);

        // Dedup on (RepoFullName, FhirId) before insert: the artifacts table has a
        // UNIQUE index on that key, so a duplicate FhirId (e.g. two extensions
        // sharing one canonical URL) would otherwise crash the run. Keep the first,
        // skip the rest, and record each collision as a finding. Ordinal comparison
        // matches SQLite's default BINARY collation on the UNIQUE index.
        Dictionary<string, ArtifactInfo> firstByFhirId = new(StringComparer.Ordinal);
        foreach (ArtifactInfo artifact in artifacts)
        {
            if (firstByFhirId.TryGetValue(artifact.FhirId, out ArtifactInfo? kept))
            {
                RecordDuplicateArtifactKey(conn, kept, artifact);
                continue;
            }

            firstByFhirId[artifact.FhirId] = artifact;
            ProcessArtifact(conn, artifact, currentArtifactSanitized);
        }

        List<NarrativePageInfo> pages = _cache.EnumerateNarrativePages();
        HashSet<string> currentPageFileNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (NarrativePageInfo page in pages)
        {
            currentPageFileNames.Add(page.PageFileName);
            ProcessNarrativePage(conn, page);
        }

        RecordRemovedBaselineEntities(conn, currentArtifactSanitized, currentPageFileNames);

        ReviewRunRecord run = new()
        {
            Id = ReviewRunRecord.GetIndex(),
            RepoFullName = _repo,
            BuildVersion = buildVersion,
            BaselineRelease = _baselineRelease,
            RunAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        conn.Insert(run, insertPrimaryKey: true);
    }

    private void ProcessArtifact(SqliteConnection conn, ArtifactInfo info, HashSet<string> currentArtifactSanitized)
    {
        SanitizedKeyword nameKey = KeywordSanitizer.Sanitize(info.FhirId);
        if (nameKey.Clean.Length > 0) currentArtifactSanitized.Add(nameKey.Clean);

        bool? existsInBaselineSite = _baselinePresence.SanitizedEntities.Count == 0
            ? null
            : _baselinePresence.SanitizedEntities.Contains(nameKey.Clean);

        ArtifactRecord record = new()
        {
            Id = ArtifactRecord.GetIndex(),
            RepoFullName = _repo,
            FhirId = info.FhirId,
            Name = info.Name,
            ArtifactType = info.ArtifactType,
            SourceDirectoryExists = info.SourceDirectoryExists,
            SourceDefinitionExists = info.SourceDefinitionExists,
            IntroPageFilename = info.IntroPageFilename,
            NotesPageFilename = info.NotesPageFilename,
            ExistsInBaselineSite = existsInBaselineSite,
            ResponsibleWorkGroupCode = info.WorkGroupCode,
            ResponsibleWorkGroupName = info.WorkGroupName,
            Status = info.Status,
            MaturityLevel = info.MaturityLevel,
            StandardsStatus = info.StandardsStatus,
        };
        conn.Insert(record, insertPrimaryKey: true);

        if (info.SourceDirRelative is null) return;

        if (info.IntroPageFilename is not null)
        {
            ReviewArtifactPage(conn, record, info, info.IntroPageFilename);
        }
        if (info.NotesPageFilename is not null)
        {
            ReviewArtifactPage(conn, record, info, info.NotesPageFilename);
        }
    }

    /// <summary>
    /// Records a <c>(RepoFullName, FhirId)</c> collision: <paramref name="kept"/>
    /// is the first artifact for the FhirId (retained), <paramref name="duplicate"/>
    /// is a subsequent one (skipped — not inserted, pages not reviewed). Each
    /// skipped duplicate becomes its own finding row. A plain insert is used (no
    /// <c>ignoreDuplicates</c>): the table has no semantic UNIQUE index, so
    /// <c>INSERT OR IGNORE</c> would have no useful target.
    /// </summary>
    private void RecordDuplicateArtifactKey(SqliteConnection conn, ArtifactInfo kept, ArtifactInfo duplicate)
    {
        _logger.LogWarning(
            "Duplicate artifact FhirId '{FhirId}': keeping '{KeptName}' ({KeptUrl}), skipping '{DuplicateName}' ({DuplicateUrl}).",
            duplicate.FhirId, kept.Name, kept.CanonicalUrl, duplicate.Name, duplicate.CanonicalUrl);

        DuplicateArtifactKeyRecord record = new()
        {
            Id = DuplicateArtifactKeyRecord.GetIndex(),
            RepoFullName = _repo,
            FhirId = duplicate.FhirId,
            KeptName = kept.Name,
            DuplicateName = duplicate.Name,
            KeptCanonicalUrl = kept.CanonicalUrl,
            DuplicateCanonicalUrl = duplicate.CanonicalUrl,
            ArtifactType = duplicate.ArtifactType,
            WorkGroupCode = duplicate.WorkGroupCode,
        };
        conn.Insert(record, insertPrimaryKey: true);
    }

    private void ReviewArtifactPage(SqliteConnection conn, ArtifactRecord artifact, ArtifactInfo info, string pageFileName)
    {
        string relativePath = $"{info.SourceDirRelative}/{pageFileName}";
        string? markup = _cache.ReadRawMarkup(relativePath);

        SpecPageRecord page = new()
        {
            Id = SpecPageRecord.GetIndex(),
            RepoFullName = _repo,
            ArtifactId = artifact.Id,
            FhirArtifactId = artifact.FhirId,
            PageFileName = pageFileName,
            ExistsInPublishIni = null,
            ExistsInSource = markup is not null,
            ExistsInBaselineSite = artifact.ExistsInBaselineSite,
            ResponsibleWorkGroupCode = artifact.ResponsibleWorkGroupCode,
            ResponsibleWorkGroupName = artifact.ResponsibleWorkGroupName,
            MaturityLabel = artifact.Status,
            MaturityLevel = artifact.MaturityLevel,
            StandardsStatus = artifact.StandardsStatus,
        };
        conn.Insert(page, insertPrimaryKey: true);

        if (markup is not null)
        {
            ReviewPageContent(conn, page, markup, isNarrative: false);
        }
    }

    private void ProcessNarrativePage(SqliteConnection conn, NarrativePageInfo info)
    {
        string? markup = info.ExistsInSource ? _cache.ReadRawMarkup($"source/{info.PageFileName}") : null;

        SpecPageRecord page = new()
        {
            Id = SpecPageRecord.GetIndex(),
            RepoFullName = _repo,
            ArtifactId = null,
            FhirArtifactId = null,
            PageFileName = info.PageFileName,
            ExistsInPublishIni = info.ExistsInPublishIni,
            ExistsInSource = info.ExistsInSource,
            ExistsInBaselineSite = _baselinePresence.PageFileNames.Count == 0
                ? null
                : _baselinePresence.PageFileNames.Contains(info.PageFileName),
            ResponsibleWorkGroupCode = info.WorkGroupCode,
            ResponsibleWorkGroupName = info.WorkGroupName,
        };
        conn.Insert(page, insertPrimaryKey: true);

        if (markup is not null)
        {
            ReviewPageContent(conn, page, markup, isNarrative: true);
        }
    }

    private void ReviewPageContent(SqliteConnection conn, SpecPageRecord page, string htmlContent, bool isNarrative)
    {
        IDocument doc;
        try
        {
            doc = _htmlParser.ParseDocument(htmlContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error parsing HTML for page {Page}: {Message}", page.PageFileName, ex.Message);
            return;
        }

        string visibleText = ExtractVisibleText(doc);
        int footerLoc = visibleText.IndexOf("[%file newfooter%]", StringComparison.Ordinal);
        if (footerLoc != -1) visibleText = visibleText[0..footerLoc];

        if (isNarrative)
        {
            string? inPageWg = ExtractWorkGroup(doc);
            if (inPageWg is not null && page.ResponsibleWorkGroupCode is null)
            {
                page.ResponsibleWorkGroupCode = inPageWg;
                page.ResponsibleWorkGroupName = _cache.ResolveWorkGroupName(inPageWg);
            }

            (bool parseable, string? maturityLabel, int? maturityLevel, string? standardsStatus) = ExtractStatusInfo(doc);
            page.MaturityLabel = maturityLabel;
            page.MaturityLevel = maturityLevel;
            page.StandardsStatus = standardsStatus;
            if (!parseable)
            {
                page.Update(conn);
                return;
            }
        }

        (page.ConformantShallCount, page.NonConformantShallCount) = GetConformanceCounts(visibleText, s_conformantShallRegex, s_totalShallRegex);
        (page.ConformantShallNotCount, page.NonConformantShallNotCount) = GetConformanceCounts(visibleText, s_conformantShallNotRegex, s_totalShallNotRegex);
        (page.ConformantShouldCount, page.NonConformantShouldCount) = GetConformanceCounts(visibleText, s_conformantShouldRegex, s_totalShouldRegex);
        (page.ConformantShouldNotCount, page.NonConformantShouldNotCount) = GetConformanceCounts(visibleText, s_conformantShouldNotRegex, s_totalShouldNotRegex);
        (page.ConformantMayCount, page.NonConformantMayCount) = GetConformanceCounts(visibleText, s_conformantMayRegex, s_totalMayRegex);
        (page.ConformantMayNotCount, page.NonConformantMayNotCount) = GetConformanceCounts(visibleText, s_conformantMayNotRegex, s_totalMayNotRegex);

        page.ConformantTotalCount = (page.ConformantShallCount ?? 0) + (page.ConformantShallNotCount ?? 0)
            + (page.ConformantShouldCount ?? 0) + (page.ConformantShouldNotCount ?? 0)
            + (page.ConformantMayCount ?? 0) + (page.ConformantMayNotCount ?? 0);
        page.NonConformantTotalCount = (page.NonConformantShallCount ?? 0) + (page.NonConformantShallNotCount ?? 0)
            + (page.NonConformantShouldCount ?? 0) + (page.NonConformantShouldNotCount ?? 0)
            + (page.NonConformantMayCount ?? 0) + (page.NonConformantMayNotCount ?? 0);

        PageCheckState state = new(conn, page.Id);
        if (page.PageFileName != "credits.html")
        {
            ProcessWords(state, visibleText);
        }

        page.RemovedFhirArtifactCount = state.RemovedWords.Count;
        page.UnknownWordCount = state.UnknownWordCount;
        page.TypoWordCount = state.TypoWordCount;
        page.PriorFhirVersionReferenceCount = state.PriorFhirVersionCount;
        page.DeprecatedLiteralCount = state.DeprecatedLiteralCount;

        page.ImagesWithIssuesCount = CheckPageImages(conn, doc, page.Id);

        page.PossibleIncompleteMarkers = SerializeMatches(s_incompleteMarkerRegex, visibleText);
        page.ReaderReviewNotes = SerializeMatches(s_readerReviewRegex, visibleText);
        page.StuLiteralsCount = s_trialUseTagRegex.Matches(htmlContent).Count;
        page.ZulipLinkCount = s_zulipLinkRegex.Matches(visibleText).Count;
        page.ConfluenceLinkCount = s_confluenceLinkRegex.Matches(visibleText).Count;

        page.Update(conn);
    }

    private sealed class PageCheckState
    {
        public PageCheckState(SqliteConnection conn, int pageId)
        {
            Conn = conn;
            PageId = pageId;
        }

        public SqliteConnection Conn { get; }
        public int PageId { get; }
        public int PriorFhirVersionCount;
        public int DeprecatedLiteralCount;
        public int UnknownWordCount;
        public int TypoWordCount;
        public HashSet<string> UnknownWords { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Typos { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RemovedWords { get; } = new(StringComparer.Ordinal);
    }

    private void ProcessWords(PageCheckState state, string visibleText)
    {
        string? lastArtifactName = null;
        string[] words = visibleText.Split(s_wordSplitChars, StringSplitOptions.RemoveEmptyEntries);

        foreach (string word in words)
        {
            if (s_urlRegex.IsMatch(word) || s_baseFhirRegex.IsMatch(word) || s_typeFhirRegex.IsMatch(word)
                || s_emailAddressRegex.IsMatch(word) || s_urnRegex.IsMatch(word) || s_xsdRegex.IsMatch(word)
                || s_fhirShexRegex.IsMatch(word) || s_thoCodeSystemRegex.IsMatch(word)
                || s_fileTargetRegex.IsMatch(word) || s_dateTimeRegex.IsMatch(word))
            {
                continue;
            }

            if (word.StartsWith("[%", StringComparison.Ordinal) || word.EndsWith("%]", StringComparison.Ordinal))
            {
                continue;
            }

            SanitizedKeyword key = KeywordSanitizer.Sanitize(word);
            if (key.PrefixSymbol == '%' || key.PrefixSymbol == '#') continue;
            if (key.PrefixSymbol == '/' && word.StartsWith('/')) continue;
            if (key.FirstLetter == '\0') continue;

            (bool hasDisposition, string? artifactName) = ProcessWord(state, word, key, lastArtifactName);
            if (hasDisposition)
            {
                lastArtifactName = artifactName ?? lastArtifactName;
                continue;
            }

            string[] subWords = word.Split(s_extendedSplitChars,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string subWord in subWords)
            {
                SanitizedKeyword subKey = KeywordSanitizer.Sanitize(subWord);
                if (subKey.FirstLetter == '\0') continue;

                (hasDisposition, artifactName) = ProcessWord(state, subWord, subKey, lastArtifactName);
                if (hasDisposition)
                {
                    lastArtifactName = artifactName ?? lastArtifactName;
                    continue;
                }

                if (state.UnknownWords.Add(word))
                {
                    state.UnknownWordCount++;
                    conn_InsertUnknownWord(state, word, isTypo: false, correction: null);
                }
            }
        }
    }

    private (bool hasDisposition, string? resourceName) ProcessWord(
        PageCheckState state, string word, SanitizedKeyword key, string? lastArtifactName)
    {
        string sanitized = key.Clean;

        if (s_priorFhirVersionKeywords.Contains(sanitized))
        {
            state.PriorFhirVersionCount++;
            return (true, null);
        }

        if (string.Equals(sanitized, "deprecated", StringComparison.Ordinal))
        {
            state.DeprecatedLiteralCount++;
            return (true, null);
        }

        bool inCurrent = false;
        string? currentName = null;
        bool inBaseline = false;
        string? baselineClass = null;
        string? baselineName = null;

        string? lastArtifactWord = lastArtifactName is not null && key.PrefixSymbol == '.'
            ? lastArtifactName + word
            : null;
        string? lastArtifactSanitized = lastArtifactWord is null
            ? null
            : KeywordSanitizer.Sanitize(lastArtifactWord).Clean;

        if (_haveCurrent)
        {
            (inCurrent, _, currentName) = TestWordAgainstFhir(_current, word, sanitized, key.FirstLetter, key.PrefixSymbol);
            if (inCurrent) return (true, currentName);

            if (lastArtifactWord is not null && !string.IsNullOrEmpty(lastArtifactSanitized))
            {
                (inCurrent, _, currentName) = TestWordAgainstFhir(_current, lastArtifactWord, lastArtifactSanitized, key.FirstLetter, null);
                if (inCurrent) return (true, currentName);
            }

            if (sanitized.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                (inCurrent, _, currentName) = TestWordAgainstFhir(_current, word[..^1], sanitized[..^1], key.FirstLetter, key.PrefixSymbol);
                if (inCurrent) return (true, currentName);
            }
        }

        if (_haveBaseline)
        {
            (inBaseline, baselineClass, baselineName) = TestWordAgainstFhir(_baseline, word, sanitized, key.FirstLetter, key.PrefixSymbol);

            if (!inBaseline && lastArtifactWord is not null && !string.IsNullOrEmpty(lastArtifactSanitized))
            {
                (inBaseline, baselineClass, baselineName) = TestWordAgainstFhir(_baseline, lastArtifactWord, lastArtifactSanitized, key.FirstLetter, null);
            }

            if (!inBaseline && sanitized.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                (inBaseline, baselineClass, baselineName) = TestWordAgainstFhir(_baseline, word[..^1], sanitized[..^1], key.FirstLetter, key.PrefixSymbol);
            }
        }

        // a term that exists in the published baseline but not the current build = removed
        if (inBaseline && !inCurrent)
        {
            if (_dict.Words.Contains(sanitized))
            {
                return (true, null);
            }

            if (state.RemovedWords.Add(word))
            {
                SpecPageRemovedFhirArtifactRecord removed = new()
                {
                    Id = SpecPageRemovedFhirArtifactRecord.GetIndex(),
                    PageId = state.PageId,
                    Word = word,
                    ArtifactClass = baselineClass ?? "Unknown",
                };
                state.Conn.Insert(removed, insertPrimaryKey: true);
            }
            return (true, baselineName);
        }

        if (!inCurrent && !inBaseline && _haveDict)
        {
            if (_dict.Typos.TryGetValue(word, out string? correctionByWord) || _dict.Typos.TryGetValue(sanitized, out correctionByWord))
            {
                if (state.Typos.Add(word))
                {
                    state.TypoWordCount++;
                    conn_InsertUnknownWord(state, word, isTypo: true, correction: correctionByWord);
                }
                return (true, null);
            }

            if (_dict.Words.Contains(sanitized))
            {
                return (true, null);
            }
        }

        return (false, null);
    }

    private static (bool found, string? artifactClass, string? artifactName) TestWordAgainstFhir(
        SpecVocabulary vocab, string word, string sanitized, char firstLetter, char? prefixSymbol)
    {
        if (firstLetter == '\0') return (false, null, null);

        if (vocab.Structures.TryGetValue(sanitized, out string? artifactClass))
        {
            return (true, artifactClass, word.Split('.', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[0].Trim() : word);
        }

        if (sanitized.StartsWith("value", StringComparison.Ordinal)
            && vocab.Structures.TryGetValue(sanitized[5..], out artifactClass))
        {
            return (true, artifactClass, null);
        }

        if (vocab.ElementPaths.Contains(sanitized))
        {
            return (true, "Element", word.Split('.', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } p ? p[0].Trim() : word);
        }

        if (prefixSymbol == '_' && vocab.SearchParameterNames.Contains(sanitized))
        {
            return (true, "SearchParameter", null);
        }

        return (false, null, null);
    }

    private static (int conformant, int nonConformant) GetConformanceCounts(string text, Regex conformantRegex, Regex totalRegex)
    {
        int conformant = conformantRegex.Matches(text).Count;
        int total = totalRegex.Matches(text).Count;
        return (conformant, total - conformant);
    }

    private int CheckPageImages(SqliteConnection conn, IDocument doc, int pageId)
    {
        int issues = 0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (IElement img in doc.QuerySelectorAll("img"))
        {
            string? src = img.GetAttribute("src");
            if (src is null) continue;

            bool missingAlt = !img.HasAttribute("alt") || string.IsNullOrWhiteSpace(img.GetAttribute("alt"));
            bool notInFigure = img.ParentElement is null
                || !string.Equals(img.ParentElement.TagName, "figure", StringComparison.OrdinalIgnoreCase);

            if (missingAlt || notInFigure)
            {
                issues++;
                if (seen.Add(src))
                {
                    SpecPageImageRecord record = new()
                    {
                        Id = SpecPageImageRecord.GetIndex(),
                        PageId = pageId,
                        Source = src,
                        MissingAlt = missingAlt,
                        NotInFigure = notInFigure,
                    };
                    conn.Insert(record, insertPrimaryKey: true);
                }
            }
        }
        return issues;
    }

    private void RecordRemovedBaselineEntities(
        SqliteConnection conn, HashSet<string> currentArtifactSanitized, HashSet<string> currentPageFileNames)
    {
        foreach (string pageFileName in _baselinePresence.PageFileNames)
        {
            if (currentPageFileNames.Contains(pageFileName)) continue;
            RemovedBaselineEntityRecord record = new()
            {
                Id = RemovedBaselineEntityRecord.GetIndex(),
                EntityKind = "page",
                Name = pageFileName,
                BaselineRelease = _baselineRelease,
                WorkGroupCode = null,
            };
            conn.Insert(record, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        foreach (string dirName in _baselinePresence.ArtifactDirNames)
        {
            string sanitized = KeywordSanitizer.Sanitize(dirName).Clean;
            if (sanitized.Length == 0 || currentArtifactSanitized.Contains(sanitized)) continue;
            RemovedBaselineEntityRecord record = new()
            {
                Id = RemovedBaselineEntityRecord.GetIndex(),
                EntityKind = "artifact",
                Name = dirName,
                BaselineRelease = _baselineRelease,
                WorkGroupCode = null,
            };
            conn.Insert(record, ignoreDuplicates: true, insertPrimaryKey: true);
        }
    }

    private static void conn_InsertUnknownWord(PageCheckState state, string word, bool isTypo, string? correction)
    {
        SpecPageUnknownWordRecord record = new()
        {
            Id = SpecPageUnknownWordRecord.GetIndex(),
            PageId = state.PageId,
            Word = word,
            IsTypo = isTypo,
            Correction = correction,
        };
        state.Conn.Insert(record, insertPrimaryKey: true);
    }

    private static string? SerializeMatches(Regex regex, string text)
    {
        List<string> matches = regex.Matches(text)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.Count == 0 ? null : JsonSerializer.Serialize(matches);
    }

    private string? ExtractWorkGroup(IDocument doc)
    {
        if (doc.QuerySelector("td[id='wg']") is IHtmlTableCellElement cell)
        {
            string? content = cell.TextContent?.Trim();
            if (string.IsNullOrEmpty(content)) return null;

            string[] parts = content.Split(s_wgSplitChars, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int idx = Array.FindIndex(parts, p =>
                p.Equals("wg", StringComparison.OrdinalIgnoreCase) || p.Equals("wgt", StringComparison.OrdinalIgnoreCase));
            if (idx != -1 && parts.Length > idx + 1)
            {
                return parts[idx + 1];
            }
        }
        return null;
    }

    private (bool parseable, string? maturityLabel, int? maturityLevel, string? standardsStatus) ExtractStatusInfo(IDocument doc)
    {
        IElement? table;
        string? maturityLabel;
        string? standardsStatus;

        if (doc.QuerySelector("table[class='colsd']") is IHtmlTableElement draftTable)
        {
            table = draftTable; maturityLabel = "Draft"; standardsStatus = "Draft";
        }
        else if (doc.QuerySelector("table[class='colstu']") is IHtmlTableElement stuTable)
        {
            table = stuTable; maturityLabel = "STU"; standardsStatus = "Trial Use";
        }
        else if (doc.QuerySelector("table[class='colsi']") is IHtmlTableElement informativeTable)
        {
            table = informativeTable; maturityLabel = "Informative"; standardsStatus = "Informative";
        }
        else if (doc.QuerySelector("table[class='colsn']") is IHtmlTableElement normativeTable)
        {
            table = normativeTable; maturityLabel = "Normative"; standardsStatus = "Normative";
        }
        else
        {
            return (false, null, null, null);
        }

        int? maturityLevel = null;
        if (table.QuerySelector("td[id='fmm']") is IHtmlTableCellElement fmmCell)
        {
            string? content = fmmCell.TextContent?.Trim();
            if (!string.IsNullOrEmpty(content))
            {
                int colonIndex = content.LastIndexOf(':');
                if (colonIndex >= 0 && colonIndex < content.Length - 1
                    && int.TryParse(content[(colonIndex + 1)..].Trim(), out int level))
                {
                    maturityLevel = level;
                }
            }
        }

        if (table.QuerySelector("td[id='ballot']") is IHtmlTableCellElement ballotCell)
        {
            string? content = ballotCell.TextContent?.Trim();
            if (!string.IsNullOrEmpty(content))
            {
                int colonIndex = content.LastIndexOf(':');
                standardsStatus = colonIndex >= 0 ? content[(colonIndex + 1)..].Trim() : content.Trim();
            }
        }

        return (true, maturityLabel, maturityLevel, standardsStatus);
    }

    private static string ExtractVisibleText(IDocument doc)
    {
        StringBuilder sb = new();
        foreach (INode node in doc.Body?.ChildNodes ?? (IEnumerable<INode>)[])
        {
            AddNodeText(node, sb);
        }
        return s_htmlStripRegex.Replace(sb.ToString(), " ");
    }

    private static void AddNodeText(INode node, StringBuilder sb)
    {
        if (node is IElement { TagName: "SCRIPT" or "STYLE" or "NAV" or "HEADER" or "FOOTER" })
        {
            return;
        }

        if (node.HasChildNodes)
        {
            foreach (INode child in node.ChildNodes)
            {
                AddNodeText(child, sb);
            }
            return;
        }

        sb.AppendLine(System.Net.WebUtility.UrlDecode(node.TextContent));
    }
}
