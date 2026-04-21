using System.Xml.Linq;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Parses a FHIR <c>CodeSystem-hl7-work-group</c> XML document and upserts
/// rows into the <c>hl7_workgroups</c> table.
/// </summary>
/// <remarks>
/// Re-load semantics:
/// <list type="bullet">
///   <item>Existing rows are matched by <c>Code</c> and updated in place;
///         their <c>Id</c> is preserved so foreign keys stay valid.</item>
///   <item>Codes present in the database but absent from the XML are kept
///         and marked <c>Retired = true</c> rather than deleted.</item>
/// </list>
/// </remarks>
public sealed class Hl7WorkGroupIndexer(
    JiraDatabase database,
    ILogger<Hl7WorkGroupIndexer> logger)
{
    private static readonly XNamespace FhirNs = "http://hl7.org/fhir";

    /// <summary>
    /// Parses <paramref name="xmlPath"/> and upserts the contained work
    /// groups. Returns the total row count in <c>hl7_workgroups</c> after the
    /// call. If <paramref name="xmlPath"/> is null/empty or missing, the
    /// table is left untouched and the current row count is returned.
    /// </summary>
    public int Rebuild(string? xmlPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using SqliteConnection connection = database.OpenConnection();

        if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
        {
            logger.LogInformation(
                "hl7 workgroup XML not present (path={Path}); skipping indexer",
                xmlPath ?? "<null>");
            return Hl7WorkGroupRecord.SelectCount(connection);
        }

        XDocument doc = XDocument.Load(xmlPath, LoadOptions.None);
        XElement? root = doc.Root;
        if (root is null)
        {
            logger.LogWarning("hl7 workgroup XML at {Path} has no root element", xmlPath);
            return Hl7WorkGroupRecord.SelectCount(connection);
        }

        List<Hl7WorkGroupRecord> candidates = [];
        foreach (XElement concept in root.Elements(FhirNs + "concept"))
            candidates.AddRange(Walk(concept));

        Dictionary<string, Hl7WorkGroupRecord> existingByCode =
            Hl7WorkGroupRecord.SelectList(connection)
                .ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

        int inserted = 0;
        int updated = 0;

        foreach (Hl7WorkGroupRecord candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (existingByCode.TryGetValue(candidate.Code, out Hl7WorkGroupRecord? existing))
            {
                candidate.Id = existing.Id;
                Hl7WorkGroupRecord.Update(connection, candidate);
                existingByCode.Remove(candidate.Code);
                updated++;
            }
            else
            {
                candidate.Id = Hl7WorkGroupRecord.GetIndex();
                Hl7WorkGroupRecord.Insert(connection, candidate);
                inserted++;
            }
        }

        int retired = 0;
        foreach (Hl7WorkGroupRecord leftover in existingByCode.Values)
        {
            if (leftover.Retired) continue;

            logger.LogInformation(
                "hl7 workgroup {Code} no longer present in XML; marking retired",
                leftover.Code);

            Hl7WorkGroupRecord retiredRow = new Hl7WorkGroupRecord
            {
                Id = leftover.Id,
                Code = leftover.Code,
                Name = leftover.Name,
                Definition = leftover.Definition,
                Retired = true,
                NameClean = leftover.NameClean,
            };
            Hl7WorkGroupRecord.Update(connection, retiredRow);
            retired++;
        }

        if (retired > 0)
            logger.LogInformation("marked {Count} workgroups retired by omission", retired);

        int total = Hl7WorkGroupRecord.SelectCount(connection);
        logger.LogInformation(
            "hl7_workgroups indexed: {Total} rows ({Inserted} inserted, {Updated} updated, {Retired} retired)",
            total, inserted, updated, retired);
        return total;
    }

    private static IEnumerable<Hl7WorkGroupRecord> Walk(XElement concept)
    {
        Hl7WorkGroupRecord? mapped = Map(concept);
        if (mapped is not null)
            yield return mapped;

        foreach (XElement child in concept.Elements(FhirNs + "concept"))
            foreach (Hl7WorkGroupRecord descendant in Walk(child))
                yield return descendant;
    }

    private static Hl7WorkGroupRecord? Map(XElement concept)
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

        return new Hl7WorkGroupRecord
        {
            Id = 0,
            Code = code,
            Name = name,
            Definition = definition,
            Retired = retired,
            NameClean = Hl7WorkGroupNameCleaner.Clean(name),
        };
    }
}
