using System.Collections.Frozen;
using System.Xml.Linq;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Tools.FhirSpecReview.SpecReview;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Readers;

/// <summary>
/// Reads the current HL7/fhir build under review from fhir-augury's GitHub
/// source cache: sanitized current-build vocabulary + narrative-page and
/// artifact enumeration from the indexed DB, and raw page markup from the
/// clone tree (AngleSharp needs raw markup, not the extracted text).
/// </summary>
internal sealed class GitHubCacheReader : IDisposable
{
    private readonly string _repo;
    private readonly string _cloneRoot;
    private readonly ILogger _logger;
    private readonly GitHubDatabase _db;
    private readonly SqliteConnection _connection;
    private readonly FrozenDictionary<string, string> _workGroupNamesByCode;

    public GitHubCacheReader(string githubDbPath, string githubCachePath, string repo, ILogger logger)
    {
        _repo = repo;
        _logger = logger;
        _cloneRoot = Path.GetFullPath(Path.Combine(
            githubCachePath, GitHubCacheLayout.SourceName, GitHubCacheLayout.ReposSubDir,
            repo.Replace('/', '_'), GitHubCacheLayout.CloneSubDir));

        _db = new GitHubDatabase(githubDbPath, NullLogger<GitHubDatabase>.Instance, readOnly: true);
        _connection = _db.OpenConnection();
        _workGroupNamesByCode = LoadWorkGroupNames();
    }

    public string CloneRoot => _cloneRoot;

    public string RepoFullName => _repo;

    /// <summary>Reads the build version from <c>publish.ini</c> <c>[FHIR] version</c>, or "unknown".</summary>
    public string ReadBuildVersion()
    {
        string publishIniPath = Path.Combine(_cloneRoot, "publish.ini");
        if (!File.Exists(publishIniPath)) return "unknown";
        foreach ((string key, string value) in ReadIniSection(publishIniPath, "FHIR"))
        {
            if (key.Equals("version", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return "unknown";
    }

    /// <summary>True if the clone working tree (with a <c>source/</c> folder) is present.</summary>
    public bool CloneRootExists => Directory.Exists(_cloneRoot) && Directory.Exists(Path.Combine(_cloneRoot, "source"));

    /// <summary>Resolves a work-group code to its display name, or returns the code if unknown.</summary>
    public string? ResolveWorkGroupName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (_workGroupNamesByCode.TryGetValue(code, out string? name)) return name;
        if (_fallbackWorkGroupNamesByCode.TryGetValue(code, out string? fallback)) return fallback;
        return code;
    }

    /// <summary>
    /// Embedded HL7 work-group code→name map, used because the indexed
    /// <c>hl7_workgroups</c> table is empty in current caches. Covers the codes
    /// observed in the build plus common aliases. Grouping correctness depends
    /// only on the code; the display name is a nicety.
    /// </summary>
    private static readonly FrozenDictionary<string, string> _fallbackWorkGroupNamesByCode =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fhir"] = "FHIR Infrastructure",
            ["fhir-i"] = "FHIR Infrastructure",
            ["fhiri"] = "FHIR Infrastructure",
            ["inm"] = "Infrastructure And Messaging",
            ["ii"] = "Imaging Integration",
            ["oo"] = "Orders and Observations",
            ["pa"] = "Patient Administration",
            ["pc"] = "Patient Care",
            ["cds"] = "Clinical Decision Support",
            ["cqi"] = "Clinical Quality Information",
            ["cg"] = "Clinical Genomics",
            ["brr"] = "Biomedical Research and Regulation",
            ["dev"] = "Health Care Devices",
            ["devices"] = "Health Care Devices",
            ["fm"] = "Financial Management",
            ["phx"] = "Pharmacy",
            ["pher"] = "Public Health",
            ["sd"] = "FHIR Infrastructure",
            ["sec"] = "Security",
            ["security"] = "Security",
            ["cic"] = "Clinical Interoperability Council",
            ["ehr"] = "Electronic Health Records",
            ["its"] = "Implementable Technology Specifications",
            ["mnm"] = "Modeling and Methodology",
            ["us"] = "US Realm Steering Committee",
            ["v2"] = "V2 Management Group",
            ["aid"] = "Adverse Event Reporting and Patient Safety",
            ["pcwg"] = "Patient Care",
            ["mobile"] = "FHIR Infrastructure",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads the sanitized current-build vocabulary (structures, element paths, search-param names).</summary>
    public SpecVocabulary LoadCurrentVocabulary()
    {
        Dictionary<string, string> structures = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT Name, ArtifactClass FROM github_structure_definitions
                WHERE RepoFullName = $repo AND Name IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$repo", _repo);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                SanitizedKeyword key = KeywordSanitizer.Sanitize(reader.GetString(0));
                if (key.FirstLetter == '\0') continue;
                string artifactClass = reader.IsDBNull(1) ? "Resource" : reader.GetString(1);
                structures[key.Clean] = artifactClass;
            }
        }

        HashSet<string> elementPaths = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT Path FROM github_sd_elements
                WHERE RepoFullName = $repo AND Path IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$repo", _repo);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                SanitizedKeyword key = KeywordSanitizer.Sanitize(reader.GetString(0));
                if (key.Clean.Length > 0) elementPaths.Add(key.Clean);
            }
        }

        HashSet<string> searchParams = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT Name FROM github_canonical_artifacts
                WHERE RepoFullName = $repo AND ResourceType = 'SearchParameter' AND Name IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$repo", _repo);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                SanitizedKeyword key = KeywordSanitizer.Sanitize(reader.GetString(0));
                if (key.Clean.Length > 0) searchParams.Add(key.Clean);
            }
        }

        return new SpecVocabulary(
            structures.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            elementPaths.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            searchParams.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enumerates narrative pages from <c>publish.ini</c> (<c>[pages]</c> +
    /// <c>[special-pages]</c>, labels from <c>[page-titles]</c>), falling back
    /// to globbing <c>source/*.html</c> when <c>publish.ini</c> is absent.
    /// </summary>
    public List<NarrativePageInfo> EnumerateNarrativePages()
    {
        FrozenDictionary<string, string> workGroupByPath = LoadSpecFileMapWorkGroups();
        string publishIniPath = Path.Combine(_cloneRoot, "publish.ini");
        List<NarrativePageInfo> pages = [];

        if (File.Exists(publishIniPath))
        {
            Dictionary<string, string> labels = ReadIniSection(publishIniPath, "page-titles")
                .GroupBy(kvp => kvp.key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().value, StringComparer.OrdinalIgnoreCase);

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string section in (string[])["pages", "special-pages"])
            {
                foreach ((string key, string _) in ReadIniSection(publishIniPath, section))
                {
                    if (!seen.Add(key)) continue;
                    pages.Add(BuildNarrativePage(key, labels.GetValueOrDefault(key), existsInPublishIni: true, workGroupByPath));
                }
            }
            return pages;
        }

        _logger.LogWarning("publish.ini not found at {Path}; falling back to globbing source/*.html.", publishIniPath);
        string sourceDir = Path.Combine(_cloneRoot, "source");
        if (Directory.Exists(sourceDir))
        {
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*.html", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                pages.Add(BuildNarrativePage(name, null, existsInPublishIni: null, workGroupByPath));
            }
        }
        return pages;
    }

    private NarrativePageInfo BuildNarrativePage(
        string pageFileName, string? label, bool? existsInPublishIni, FrozenDictionary<string, string> workGroupByPath)
    {
        bool existsInSource = File.Exists(Path.Combine(_cloneRoot, "source", pageFileName));
        string? code = workGroupByPath.GetValueOrDefault($"source/{pageFileName}")
            ?? workGroupByPath.GetValueOrDefault(pageFileName);
        return new NarrativePageInfo(
            pageFileName, label, existsInPublishIni, existsInSource, code, ResolveWorkGroupName(code));
    }

    /// <summary>
    /// Enumerates artifacts from the current-build structure definitions,
    /// deriving expected source locations and intro/notes page existence
    /// (faithful port of <c>getExpectedLocations</c>).
    /// </summary>
    public List<ArtifactInfo> EnumerateArtifacts()
    {
        List<GitHubStructureDefinitionRecord> structures =
            GitHubStructureDefinitionRecord.SelectList(_connection, RepoFullName: _repo);

        List<ArtifactInfo> artifacts = new(structures.Count);
        foreach (GitHubStructureDefinitionRecord sd in structures)
        {
            string fhirId = LastUrlSegment(sd.Url) ?? sd.Name;
            string artifactType = (sd.ArtifactClass ?? string.Empty).ToLowerInvariant();
            string? baseShort = LastUrlSegment(sd.BaseDefinition);

            (string? dirRel, string? defRel) = GetExpectedLocations(artifactType, fhirId, baseShort);

            string? workGroupCode = ResolveArtifactWorkGroupCode(sd);

            bool? dirExists = null;
            bool? defExists = null;
            string? introFilename = null;
            string? notesFilename = null;

            if (dirRel is not null)
            {
                string dirFull = Path.Combine(_cloneRoot, dirRel.Replace('/', Path.DirectorySeparatorChar));
                dirExists = Directory.Exists(dirFull);

                if (defRel is not null && dirExists == true)
                {
                    string defFull = Path.Combine(_cloneRoot, defRel.Replace('/', Path.DirectorySeparatorChar));
                    defExists = File.Exists(defFull);

                    string shortName = Path.GetFileNameWithoutExtension(defRel).ToLowerInvariant();
                    if (shortName.StartsWith("structuredefinition-", StringComparison.Ordinal))
                    {
                        shortName = shortName["structuredefinition-".Length..];
                    }

                    string introCandidate = $"{shortName}-introduction.xml";
                    string notesCandidate = $"{shortName}-notes.xml";
                    if (File.Exists(Path.Combine(dirFull, introCandidate))) introFilename = introCandidate;
                    if (File.Exists(Path.Combine(dirFull, notesCandidate))) notesFilename = notesCandidate;
                }
                else
                {
                    defExists = false;
                }
            }
            else
            {
                dirExists = false;
                defExists = false;
            }

            artifacts.Add(new ArtifactInfo(
                fhirId, sd.Name, artifactType, dirRel, dirExists, defExists,
                introFilename, notesFilename,
                workGroupCode, ResolveWorkGroupName(workGroupCode),
                sd.Status, sd.FhirMaturity, sd.StandardsStatus, sd.Url));
        }

        return artifacts;
    }

    /// <summary>
    /// Resolves an artifact's responsible work-group code with a fallback chain
    /// that needs no github.db re-ingestion: <c>sd.WorkGroup</c> (resolved code,
    /// usually null in current caches) → <c>sd.WorkGroupRaw</c> (the indexer's
    /// captured code) → the <c>structuredefinition-wg</c> extension
    /// <c>valueCode</c> read from the artifact's authoritative definition XML at
    /// <c>sd.FilePath</c>. Returns null when none resolve (legitimately Unassigned).
    /// </summary>
    private string? ResolveArtifactWorkGroupCode(GitHubStructureDefinitionRecord sd)
    {
        if (!string.IsNullOrWhiteSpace(sd.WorkGroup)) return sd.WorkGroup;
        if (!string.IsNullOrWhiteSpace(sd.WorkGroupRaw)) return sd.WorkGroupRaw;
        return ExtractWgExtensionCode(sd.FilePath);
    }

    /// <summary>
    /// Reads the <c>structuredefinition-wg</c> extension <c>valueCode</c> from a
    /// structure-definition XML file located at <paramref name="filePathRelative"/>
    /// (relative to the clone root, forward slashes). Returns null when the path is
    /// missing, escapes the clone root, can't be parsed, or has no wg extension.
    /// </summary>
    private string? ExtractWgExtensionCode(string? filePathRelative)
    {
        if (string.IsNullOrWhiteSpace(filePathRelative)) return null;

        string combined = Path.GetFullPath(Path.Combine(
            _cloneRoot, filePathRelative.Replace('/', Path.DirectorySeparatorChar)));
        string normalizedRoot = Path.GetFullPath(_cloneRoot);
        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, normalizedRoot, StringComparison.Ordinal))
        {
            _logger.LogWarning("Refusing to read '{Path}' outside the clone root.", filePathRelative);
            return null;
        }
        if (!File.Exists(combined)) return null;

        try
        {
            XDocument doc = XDocument.Load(combined);
            XNamespace fhir = "http://hl7.org/fhir";
            foreach (XElement extension in doc.Descendants(fhir + "extension"))
            {
                string? url = (string?)extension.Attribute("url");
                if (url is null) continue;
                if (!url.EndsWith("structuredefinition-wg", StringComparison.Ordinal)) continue;

                string? code = (string?)extension.Element(fhir + "valueCode")?.Attribute("value");
                if (!string.IsNullOrWhiteSpace(code)) return code;
            }
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogDebug("Could not parse '{Path}' for wg extension: {Message}", filePathRelative, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Reads a clone-tree file's raw markup. <paramref name="relativePath"/>
    /// is relative to the clone root (forward slashes); guards against path
    /// traversal. Returns null (and logs) when the file is missing or escapes.
    /// </summary>
    public string? ReadRawMarkup(string relativePath)
    {
        string combined = Path.GetFullPath(Path.Combine(_cloneRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string normalizedRoot = Path.GetFullPath(_cloneRoot);
        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, normalizedRoot, StringComparison.Ordinal))
        {
            _logger.LogWarning("Refusing to read '{Path}' outside the clone root.", relativePath);
            return null;
        }
        if (!File.Exists(combined))
        {
            _logger.LogDebug("Markup file not found: {Path}", relativePath);
            return null;
        }
        return File.ReadAllText(combined);
    }

    private static (string? dirRel, string? defRel) GetExpectedLocations(string artifactType, string fhirId, string? baseShort)
    {
        switch (artifactType)
        {
            case "interface":
            case "resource":
                string id = fhirId.ToLowerInvariant();
                return ($"source/{id}", $"source/{id}/structuredefinition-{fhirId}.xml");

            case "profile":
                if (string.IsNullOrEmpty(baseShort)) return (null, null);
                string baseLower = baseShort.ToLowerInvariant();
                return ($"source/{baseLower}", $"source/{baseLower}/{baseLower}-{fhirId}.xml");

            default:
                return (null, null);
        }
    }

    private static string? LastUrlSegment(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        int slash = url.LastIndexOf('/');
        string segment = slash >= 0 ? url[(slash + 1)..] : url;
        return string.IsNullOrWhiteSpace(segment) ? null : segment;
    }

    private FrozenDictionary<string, string> LoadWorkGroupNames()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Code, Name FROM hl7_workgroups WHERE Code IS NOT NULL";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string code = reader.GetString(0);
                if (!reader.IsDBNull(1)) map[code] = reader.GetString(1);
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning("Could not read hl7_workgroups: {Message}", ex.Message);
        }
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private FrozenDictionary<string, string> LoadSpecFileMapWorkGroups()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT FilePath, WorkGroup FROM github_spec_file_map
                WHERE RepoFullName = $repo AND WorkGroup IS NOT NULL AND FilePath IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$repo", _repo);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                map[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning("Could not read github_spec_file_map work groups: {Message}", ex.Message);
        }
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static List<(string key, string value)> ReadIniSection(string filename, string section)
    {
        List<(string key, string value)> values = [];
        string sectionMatch = $"[{section}]";
        bool inSection = false;
        foreach (string line in File.ReadLines(filename))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = trimmed.Equals(sectionMatch, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            int eq = trimmed.IndexOf('=');
            if (eq > 0)
            {
                values.Add((trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim()));
            }
            else
            {
                values.Add((trimmed, string.Empty));
            }
        }
        return values;
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }
}
