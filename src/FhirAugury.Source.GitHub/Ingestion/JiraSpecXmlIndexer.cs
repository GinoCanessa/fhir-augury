using System.Data;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FhirAugury.Parsing.Xml;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Parses JIRA-Spec-Artifacts XML files and indexes them into the database.
/// Handles _families.xml, SPECS-*.xml, _workgroups.xml, and individual spec files.
/// </summary>
public class JiraSpecXmlIndexer(ILogger<JiraSpecXmlIndexer> logger, WorkGroupResolver workGroupResolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Indexes all JIRA-Spec-Artifacts XML files from the given clone path into the database.
    /// </summary>
    public void IndexRepository(
        string repoFullName,
        string clonePath,
        SqliteConnection connection,
        CancellationToken ct)
    {
        // Clean up existing data for this repo
        CleanupExistingData(repoFullName, connection);

        // Step 1: Parse _families.xml to get known family keys
        HashSet<string> knownFamilies = ParseFamilies(clonePath);

        // Step 2: Parse SPECS-[FAMILY].xml files for spec list metadata
        Dictionary<string, string> specNameLookup = ParseSpecLists(repoFullName, clonePath, knownFamilies, connection, ct);

        // Step 3: Parse _workgroups.xml
        ParseWorkgroups(repoFullName, clonePath, connection, ct);

        // Step 4: Parse individual specification XML files
        ParseSpecificationFiles(repoFullName, clonePath, knownFamilies, specNameLookup, connection, ct);

        logger.LogInformation("Indexed JIRA-Spec-Artifacts for {Repo}", repoFullName);
    }

    private static void CleanupExistingData(string repoFullName, SqliteConnection connection)
    {
        string[] tables =
        [
            "jira_spec_versions",
            "jira_spec_artifacts",
            "jira_spec_pages",
            "jira_spec_families",
            "jira_workgroups",
            "jira_specs",
        ];

        foreach (string table in tables)
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Parses _families.xml to extract known product family keys.</summary>
    private HashSet<string> ParseFamilies(string clonePath)
    {
        HashSet<string> families = new(StringComparer.OrdinalIgnoreCase);
        string familiesPath = Path.Combine(clonePath, "xml", "_families.xml");
        if (!File.Exists(familiesPath))
        {
            // Try root level
            familiesPath = Path.Combine(clonePath, "_families.xml");
        }

        if (!File.Exists(familiesPath))
        {
            logger.LogWarning("_families.xml not found in {ClonePath}", clonePath);
            return families;
        }

        try
        {
            using XmlReader xr = XmlDowngradeReader.Create(familiesPath);
            XDocument doc = XDocument.Load(xr);
            foreach (XElement family in doc.Descendants("family"))
            {
                string? key = (string?)family.Attribute("key");
                if (!string.IsNullOrEmpty(key))
                    families.Add(key);
            }

            logger.LogDebug("Parsed {Count} families from _families.xml", families.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse _families.xml");
        }

        return families;
    }

    /// <summary>
    /// Parses SPECS-[FAMILY].xml files and inserts JiraSpecFamilyRecord rows.
    /// Returns a lookup of specKey → specName for use when parsing individual spec files.
    /// </summary>
    private Dictionary<string, string> ParseSpecLists(
        string repoFullName,
        string clonePath,
        HashSet<string> knownFamilies,
        SqliteConnection connection,
        CancellationToken ct)
    {
        Dictionary<string, string> specNameLookup = new(StringComparer.OrdinalIgnoreCase);

        List<JiraSpecFamilyRecord> records = [];

        foreach (string specsFile in FindSpecsFiles(clonePath))
        {
            ct.ThrowIfCancellationRequested();

            string fileName = Path.GetFileNameWithoutExtension(specsFile);
            string family = fileName.StartsWith("SPECS-", StringComparison.OrdinalIgnoreCase)
                ? fileName["SPECS-".Length..]
                : fileName;

            try
            {
                using XmlReader xr = XmlDowngradeReader.Create(specsFile);
                XDocument doc = XDocument.Load(xr);

                foreach (XElement spec in doc.Descendants("specification"))
                {
                    string? specKey = (string?)spec.Attribute("key");
                    string? name = (string?)spec.Attribute("name");
                    if (string.IsNullOrEmpty(specKey) || string.IsNullOrEmpty(name))
                        continue;

                    specNameLookup.TryAdd(specKey, name);

                    records.Add(new()
                    {
                        Id = JiraSpecFamilyRecord.GetIndex(),
                        RepoFullName = repoFullName,
                        Family = family,
                        SpecKey = specKey,
                        Name = name,
                        Accelerator = (string?)spec.Attribute("accelerator"),
                        Deprecated = string.Equals((string?)spec.Attribute("deprecated"), "true", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse SPECS file {File}", specsFile);
            }

            JiraSpecFamilyRecord.Insert(connection, records, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        logger.LogDebug("Parsed {Count} spec-family entries", specNameLookup.Count);
        return specNameLookup;
    }

    /// <summary>Parses _workgroups.xml and inserts JiraWorkgroupRecord rows.</summary>
    private void ParseWorkgroups(
        string repoFullName,
        string clonePath,
        SqliteConnection connection,
        CancellationToken ct)
    {
        string wgPath = Path.Combine(clonePath, "xml", "_workgroups.xml");
        if (!File.Exists(wgPath))
        {
            wgPath = Path.Combine(clonePath, "_workgroups.xml");
        }

        if (!File.Exists(wgPath))
        {
            logger.LogDebug("_workgroups.xml not found");
            return;
        }

        try
        {
            using XmlReader xr = XmlDowngradeReader.Create(wgPath);
            XDocument doc = XDocument.Load(xr);
            int count = 0;

            List<JiraWorkgroupRecord> records = [];

            foreach (XElement wg in doc.Descendants("workgroup"))
            {
                ct.ThrowIfCancellationRequested();

                string? key = (string?)wg.Attribute("key");
                string? name = (string?)wg.Attribute("name");
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name))
                    continue;

                records.Add(new()
                {
                    Id = JiraWorkgroupRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    WorkgroupKey = key,
                    Name = name,
                    Webcode = (string?)wg.Attribute("webcode"),
                    Listserv = (string?)wg.Attribute("listserv"),
                    Deprecated = string.Equals((string?)wg.Attribute("deprecated"), "true", StringComparison.OrdinalIgnoreCase),
                    WorkGroupCode = workGroupResolver.Resolve(name),
                });
                count++;
            }

            JiraWorkgroupRecord.Insert(connection, records, ignoreDuplicates: true, insertPrimaryKey: true);

            logger.LogDebug("Parsed {Count} workgroups", count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse _workgroups.xml");
        }
    }

    /// <summary>
    /// Discovers and parses individual specification XML files from both xml/ and root.
    /// </summary>
    private void ParseSpecificationFiles(
        string repoFullName,
        string clonePath,
        HashSet<string> knownFamilies,
        Dictionary<string, string> specNameLookup,
        SqliteConnection connection,
        CancellationToken ct)
    {
        int specCount = 0;

        foreach (string file in FindSpecFiles(clonePath, knownFamilies))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                ParseSingleSpecFile(repoFullName, clonePath, file, knownFamilies, specNameLookup, connection, ct);
                specCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to parse spec file {File}, skipping", file);
            }
        }

        logger.LogInformation("Parsed {Count} specification files for {Repo}", specCount, repoFullName);
    }

    private void ParseSingleSpecFile(
        string repoFullName,
        string clonePath,
        string filePath,
        HashSet<string> knownFamilies,
        Dictionary<string, string> specNameLookup,
        SqliteConnection connection,
        CancellationToken ct)
    {
        using XmlReader xr = XmlDowngradeReader.Create(filePath);
        XDocument doc = XDocument.Load(xr);
        XElement? root = doc.Root;
        if (root is null)
            return;

        string relativePath = Path.GetRelativePath(clonePath, filePath).Replace('\\', '/');
        string family = DetectFamily(filePath, knownFamilies);
        string? specKey = (string?)root.Attribute("key");

        if (string.IsNullOrEmpty(specKey))
        {
            logger.LogWarning("Spec file {File} missing 'key' attribute, skipping", relativePath);
            return;
        }

        // Parse artifact page extensions
        string? artifactPageExtensions = null;
        List<string> extensions = root.Elements("artifactPageExtension")
            .Select(e => (string?)e.Attribute("value") ?? e.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();
        if (extensions.Count > 0)
            artifactPageExtensions = JsonSerializer.Serialize(extensions, JsonOptions);

        // Resolve spec name from SPECS-*.xml data
        specNameLookup.TryGetValue(specKey, out string? specName);

        JiraSpecRecord specRecord = new()
        {
            Id = JiraSpecRecord.GetIndex(),
            RepoFullName = repoFullName,
            FilePath = relativePath,
            Family = family,
            SpecKey = specKey,
            SpecName = specName,
            CanonicalUrl = (string?)root.Attribute("url"),
            CiUrl = (string?)root.Attribute("ciUrl"),
            BallotUrl = (string?)root.Attribute("ballotUrl"),
            GitUrl = (string?)root.Attribute("gitUrl"),
            DefaultWorkgroup = (string?)root.Attribute("defaultWorkgroup"),
            DefaultVersion = (string?)root.Attribute("defaultVersion") ?? "STU1",
            ArtifactPageExtensions = artifactPageExtensions,
        };

        JiraSpecRecord.Insert(connection, specRecord, ignoreDuplicates: true);

        // Parse child elements
        ParseVersions(repoFullName, specKey, specRecord.Id, root, connection, ct);
        ParseArtifacts(repoFullName, specKey, specRecord.Id, root, connection, ct);
        ParsePages(repoFullName, specKey, specRecord.Id, root, connection, ct);
    }

    private void ParseVersions(
        string repoFullName,
        string specKey,
        int jiraSpecId,
        XElement root,
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<JiraSpecVersionRecord> records = [];

        foreach (XElement version in root.Elements("version"))
        {
            ct.ThrowIfCancellationRequested();

            string? code = (string?)version.Attribute("code");
            if (string.IsNullOrEmpty(code))
                continue;

            records.Add(new()
            {
                Id = JiraSpecVersionRecord.GetIndex(),
                RepoFullName = repoFullName,
                SpecKey = specKey,
                JiraSpecId = jiraSpecId,
                Code = code,
                Url = (string?)version.Attribute("url"),
                Deprecated = string.Equals((string?)version.Attribute("deprecated"), "true", StringComparison.OrdinalIgnoreCase),
            });
        }

        JiraSpecVersionRecord.Insert(connection, records, ignoreDuplicates: true, insertPrimaryKey: true);
    }

    private void ParseArtifacts(
        string repoFullName,
        string specKey,
        int jiraSpecId,
        XElement root,
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<JiraSpecArtifactRecord> records = [];

        foreach (XElement artifact in root.Elements("artifact"))
        {
            ct.ThrowIfCancellationRequested();

            string? artifactKey = (string?)artifact.Attribute("key");
            string? name = (string?)artifact.Attribute("name");
            if (string.IsNullOrEmpty(artifactKey) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            string? artifactId = (string?)artifact.Attribute("id");
            string? resourceType = null;
            if (!string.IsNullOrEmpty(artifactId))
            {
                int slashIndex = artifactId.IndexOf('/');
                if (slashIndex > 0)
                {
                    resourceType = artifactId[..slashIndex];
                }
            }

            // Serialize other artifact IDs
            string? otherArtifactIds = null;
            List<string> others = artifact.Elements("otherArtifact")
                .Select(e => (string?)e.Attribute("id") ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
            if (others.Count > 0)
            {
                otherArtifactIds = JsonSerializer.Serialize(others, JsonOptions);
            }

            records.Add(new()
            {
                Id = JiraSpecArtifactRecord.GetIndex(),
                RepoFullName = repoFullName,
                SpecKey = specKey,
                JiraSpecId = jiraSpecId,
                ArtifactKey = artifactKey,
                Name = name,
                ArtifactId = artifactId,
                ResourceType = resourceType,
                Workgroup = (string?)artifact.Attribute("workgroup"),
                Deprecated = string.Equals((string?)artifact.Attribute("deprecated"), "true", StringComparison.OrdinalIgnoreCase),
                OtherArtifactIds = otherArtifactIds,
            });
        }

        JiraSpecArtifactRecord.Insert(connection, records, ignoreDuplicates: true, insertPrimaryKey: true);
    }

    private void ParsePages(
        string repoFullName,
        string specKey,
        int jiraSpecId,
        XElement root,
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<JiraSpecPageRecord> records = [];

        foreach (XElement page in root.Elements("page"))
        {
            ct.ThrowIfCancellationRequested();

            string? pageKey = (string?)page.Attribute("key");
            string? name = (string?)page.Attribute("name");
            if (string.IsNullOrEmpty(pageKey) || string.IsNullOrEmpty(name))
                continue;

            // Serialize other page URLs
            string? otherPageUrls = null;
            List<string> others = page.Elements("otherpage")
                .Select(e => (string?)e.Attribute("url") ?? "")
                .Where(url => !string.IsNullOrEmpty(url))
                .ToList();
            if (others.Count > 0)
            {
                otherPageUrls = JsonSerializer.Serialize(others, JsonOptions);
            }

            records.Add(new()
            {
                Id = JiraSpecPageRecord.GetIndex(),
                RepoFullName = repoFullName,
                SpecKey = specKey,
                JiraSpecId = jiraSpecId,
                PageKey = pageKey,
                Name = name,
                Url = (string?)page.Attribute("url"),
                Workgroup = (string?)page.Attribute("workgroup"),
                Deprecated = string.Equals((string?)page.Attribute("deprecated"), "true", StringComparison.OrdinalIgnoreCase),
                OtherPageUrls = otherPageUrls,
            });
        }

        JiraSpecPageRecord.Insert(connection, records, ignoreDuplicates: true, insertPrimaryKey: true);
    }

    // ── File discovery helpers ──────────────────────────────────────

    /// <summary>Finds SPECS-*.xml files in both xml/ and root.</summary>
    private static IEnumerable<string> FindSpecsFiles(string clonePath)
    {
        string xmlDir = Path.Combine(clonePath, "xml");
        if (Directory.Exists(xmlDir))
        {
            foreach (string file in Directory.EnumerateFiles(xmlDir, "SPECS-*.xml"))
                yield return file;
        }

        foreach (string file in Directory.EnumerateFiles(clonePath, "SPECS-*.xml"))
        {
            // Skip if already found in xml/
            if (!file.StartsWith(xmlDir, StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    /// <summary>
    /// Finds individual specification XML files (e.g., FHIR-core.xml, CDA-ccda.xml).
    /// Scans both xml/ directory and root level. Excludes meta files.
    /// </summary>
    private static IEnumerable<string> FindSpecFiles(string clonePath, HashSet<string> knownFamilies)
    {
        HashSet<string> excludeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "_families.xml", "_workgroups.xml", "build.xml",
        };

        IEnumerable<string> allXmlFiles = Enumerable.Empty<string>();

        string xmlDir = Path.Combine(clonePath, "xml");
        if (Directory.Exists(xmlDir))
            allXmlFiles = allXmlFiles.Concat(Directory.EnumerateFiles(xmlDir, "*.xml"));

        allXmlFiles = allXmlFiles.Concat(Directory.EnumerateFiles(clonePath, "*.xml"));

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in allXmlFiles)
        {
            string fileName = Path.GetFileName(file);

            // Skip meta files
            if (excludeNames.Contains(fileName))
                continue;

            // Skip SPECS-*.xml files
            if (fileName.StartsWith("SPECS-", StringComparison.OrdinalIgnoreCase))
                continue;

            // Must match [FAMILY]-*.xml pattern
            int hyphenIndex = fileName.IndexOf('-');
            if (hyphenIndex <= 0)
                continue;

            string prefix = fileName[..hyphenIndex];
            if (!knownFamilies.Contains(prefix))
                continue;

            // Deduplicate (xml/ vs root)
            if (!seen.Add(fileName))
                continue;

            yield return file;
        }
    }

    /// <summary>Detects the product family from the file name prefix.</summary>
    private static string DetectFamily(string filePath, HashSet<string> knownFamilies)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        int hyphenIndex = fileName.IndexOf('-');
        if (hyphenIndex <= 0)
            return "UNKNOWN";

        string prefix = fileName[..hyphenIndex];
        // Return the canonical casing from the known families set if available
        foreach (string family in knownFamilies)
        {
            if (string.Equals(family, prefix, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return prefix.ToUpperInvariant();
    }
}
