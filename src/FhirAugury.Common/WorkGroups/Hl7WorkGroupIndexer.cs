using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Parses a FHIR <c>CodeSystem-hl7-work-group</c> XML document and applies the
/// resulting concepts to an <see cref="IHl7WorkGroupStore"/>.
/// </summary>
/// <remarks>
/// Re-load semantics:
/// <list type="bullet">
///   <item>Existing rows are matched by <see cref="Hl7WorkGroupDto.Code"/>
///         (case-insensitive) and updated in place; their surrogate IDs are
///         preserved by the store so foreign keys stay valid.</item>
///   <item>Codes present in the store but absent from the XML are kept and
///         marked <c>Retired = true</c> rather than deleted.</item>
/// </list>
/// </remarks>
public sealed class Hl7WorkGroupIndexer
{
    private static readonly XNamespace FhirNs = "http://hl7.org/fhir";

    private readonly IHl7WorkGroupStore _store;
    private readonly ILogger _logger;

    public Hl7WorkGroupIndexer(IHl7WorkGroupStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Parses <paramref name="xmlPath"/> and applies the contained work
    /// groups to <paramref name="connection"/>. Returns the total row count
    /// in the store after the call. If <paramref name="xmlPath"/> is
    /// null/empty or missing, the store is left untouched and the current
    /// row count is returned.
    /// </summary>
    public int Rebuild(string? xmlPath, SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
        {
            _logger.LogInformation(
                "hl7 workgroup XML not present (path={Path}); skipping indexer",
                xmlPath ?? "<null>");
            return _store.Count(connection);
        }

        XDocument doc = XDocument.Load(xmlPath, LoadOptions.None);
        XElement? root = doc.Root;
        if (root is null)
        {
            _logger.LogWarning("hl7 workgroup XML at {Path} has no root element", xmlPath);
            return _store.Count(connection);
        }

        List<Hl7WorkGroupDto> candidates = [];
        foreach (XElement concept in root.Elements(FhirNs + "concept"))
            candidates.AddRange(Walk(concept));

        Dictionary<string, Hl7WorkGroupDto> existingByCode =
            _store.LoadAll(connection)
                .ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

        List<Hl7WorkGroupDto> upsert = new(candidates.Count);
        List<Hl7WorkGroupDto> retire = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (Hl7WorkGroupDto candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(candidate.Code);
            upsert.Add(candidate);
        }

        foreach (KeyValuePair<string, Hl7WorkGroupDto> kv in existingByCode)
        {
            if (seen.Contains(kv.Key)) continue;
            if (kv.Value.Retired) continue;

            _logger.LogInformation(
                "hl7 workgroup {Code} no longer present in XML; marking retired",
                kv.Value.Code);

            retire.Add(kv.Value with { Retired = true });
        }

        _store.ApplyChanges(connection, upsert, retire);

        int total = _store.Count(connection);
        int inserted = upsert.Count(c => !existingByCode.ContainsKey(c.Code));
        int updated = upsert.Count - inserted;

        if (retire.Count > 0)
            _logger.LogInformation("marked {Count} workgroups retired by omission", retire.Count);

        _logger.LogInformation(
            "hl7_workgroups indexed: {Total} rows ({Inserted} inserted, {Updated} updated, {Retired} retired)",
            total, inserted, updated, retire.Count);
        return total;
    }

    private static IEnumerable<Hl7WorkGroupDto> Walk(XElement concept)
    {
        Hl7WorkGroupDto? mapped = Map(concept);
        if (mapped is not null)
            yield return mapped;

        foreach (XElement child in concept.Elements(FhirNs + "concept"))
            foreach (Hl7WorkGroupDto descendant in Walk(child))
                yield return descendant;
    }

    private static Hl7WorkGroupDto? Map(XElement concept)
    {
        string? code = concept.Element(FhirNs + "code")?.Attribute("value")?.Value;
        if (string.IsNullOrWhiteSpace(code)) return null;

        string? display = concept.Element(FhirNs + "display")?.Attribute("value")?.Value;
        string name = string.IsNullOrWhiteSpace(display) ? code : display;
        string? definition = concept.Element(FhirNs + "definition")?.Attribute("value")?.Value;

        bool retired = false;
        foreach (XElement prop in concept.Elements(FhirNs + "property"))
        {
            string? propCode = prop.Element(FhirNs + "code")?.Attribute("value")?.Value;
            string? valueCode = prop.Element(FhirNs + "valueCode")?.Attribute("value")?.Value;
            if (string.Equals(propCode, "status", StringComparison.Ordinal)
                && string.Equals(valueCode, "retired", StringComparison.Ordinal))
            {
                retired = true;
                break;
            }
        }

        return new Hl7WorkGroupDto(
            Code: code,
            Name: name,
            Definition: definition,
            Retired: retired,
            NameClean: Hl7WorkGroupNameCleaner.Clean(name));
    }
}
