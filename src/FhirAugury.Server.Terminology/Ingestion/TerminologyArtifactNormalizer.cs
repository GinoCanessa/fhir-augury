using FhirAugury.Server.Terminology.Database.Records;
using Hl7.Fhir.Model;
using System.Text.Json;

namespace FhirAugury.Server.Terminology.Ingestion;

/// <summary>
/// Converts a parsed Firely <see cref="CodeSystem"/> / <see cref="ValueSet"/>
/// POCO into <see cref="TerminologyArtifactRecord"/> + child
/// <see cref="TerminologyConceptRecord"/> rows ready for SQLite.
/// </summary>
/// <remarks>
/// <para>
/// One method per artifact kind. R4 and R5 share these POCOs (they live
/// in <c>Hl7.Fhir.Conformance</c>), so the original plan's "two
/// normalizer overloads per resource" is unnecessary — see the Phase 2
/// deviation note in <c>plan.md</c>.
/// </para>
/// <para>
/// <c>ValueSet</c> rows are produced from <c>compose.include</c> only.
/// We do not run <c>$expand</c> in v1 (intentional non-goal); valuesets
/// that lean entirely on <c>filter</c>/<c>valueSet</c> indirection will
/// have an artifact row but no concept rows. The check endpoint treats
/// the artifact's metadata (URL, name, title, description) as the only
/// signal in that case.
/// </para>
/// </remarks>
public sealed class TerminologyArtifactNormalizer
{
    private static readonly JsonWriterOptions DesignationWriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    /// <summary>
    /// Materializes the rows for a single CodeSystem.
    /// </summary>
    public (TerminologyArtifactRecord artifact, List<TerminologyConceptRecord> concepts)
        Normalize(
            CodeSystem cs,
            string packageNpmId,
            string packageVersion,
            string fhirVersion,
            string sourceJson)
    {
        string canonical = cs.Url ?? string.Empty;
        TerminologyArtifactRecord artifact = new()
        {
            Id = TerminologyArtifactRecord.GetIndex(),
            Kind = "CodeSystem",
            CanonicalUrl = canonical,
            CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(canonical),
            Version = cs.Version,
            FhirVersion = fhirVersion,
            Title = cs.Title,
            Name = cs.Name,
            Status = cs.Status?.ToString(),
            Experimental = cs.Experimental ?? false,
            Publisher = cs.Publisher,
            Description = cs.Description,
            Purpose = cs.Purpose,
            Keywords = null,
            PackageId = packageNpmId,
            PackageVersion = packageVersion,
            Json = sourceJson,
        };

        List<TerminologyConceptRecord> concepts = [];
        foreach (CodeSystem.ConceptDefinitionComponent concept in cs.Concept ?? [])
        {
            FlattenCsConcept(concept, canonical, concepts);
        }

        return (artifact, concepts);
    }

    /// <summary>
    /// Materializes the rows for a single ValueSet.
    /// </summary>
    public (TerminologyArtifactRecord artifact, List<TerminologyConceptRecord> concepts)
        Normalize(
            ValueSet vs,
            string packageNpmId,
            string packageVersion,
            string fhirVersion,
            string sourceJson)
    {
        string canonical = vs.Url ?? string.Empty;
        TerminologyArtifactRecord artifact = new()
        {
            Id = TerminologyArtifactRecord.GetIndex(),
            Kind = "ValueSet",
            CanonicalUrl = canonical,
            CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(canonical),
            Version = vs.Version,
            FhirVersion = fhirVersion,
            Title = vs.Title,
            Name = vs.Name,
            Status = vs.Status?.ToString(),
            Experimental = vs.Experimental ?? false,
            Publisher = vs.Publisher,
            Description = vs.Description,
            Purpose = vs.Purpose,
            Keywords = null,
            PackageId = packageNpmId,
            PackageVersion = packageVersion,
            Json = sourceJson,
        };

        List<TerminologyConceptRecord> concepts = [];
        if (vs.Compose is not null)
        {
            foreach (ValueSet.ConceptSetComponent include in vs.Compose.Include ?? [])
            {
                string system = include.System ?? string.Empty;
                foreach (ValueSet.ConceptReferenceComponent c in include.Concept ?? [])
                {
                    concepts.Add(BuildConcept(
                        system,
                        c.Code ?? string.Empty,
                        c.Display,
                        definition: null,
                        designations: c.Designation));
                }
            }
        }

        return (artifact, concepts);
    }

    private static void FlattenCsConcept(
        CodeSystem.ConceptDefinitionComponent concept,
        string system,
        List<TerminologyConceptRecord> sink)
    {
        sink.Add(BuildConcept(
            system,
            concept.Code ?? string.Empty,
            concept.Display,
            definition: concept.Definition,
            designations: concept.Designation));

        foreach (CodeSystem.ConceptDefinitionComponent child in concept.Concept ?? [])
        {
            FlattenCsConcept(child, system, sink);
        }
    }

    private static TerminologyConceptRecord BuildConcept(
        string system,
        string code,
        string? display,
        string? definition,
        IEnumerable<object>? designations)
    {
        return new TerminologyConceptRecord
        {
            Id = TerminologyConceptRecord.GetIndex(),
            ArtifactId = 0, // set by the pipeline after the artifact row is inserted
            SystemUrl = system,
            Code = code,
            Display = display,
            DisplayNormalized = TerminologyTextNormalizer.NormalizeDisplay(display ?? string.Empty),
            Definition = definition,
            DesignationsJson = SerializeDesignations(designations),
            IsRetired = false,
        };
    }

    private static string SerializeDesignations(IEnumerable<object>? designations)
    {
        if (designations is null) return "[]";

        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms, DesignationWriterOptions))
        {
            w.WriteStartArray();
            foreach (object d in designations)
            {
                switch (d)
                {
                    case CodeSystem.DesignationComponent csd:
                        WriteDesignation(w, csd.Language, csd.Use?.System, csd.Use?.Code, csd.Use?.Display, csd.Value);
                        break;
                    case ValueSet.DesignationComponent vsd:
                        WriteDesignation(w, vsd.Language, vsd.Use?.System, vsd.Use?.Code, vsd.Use?.Display, vsd.Value);
                        break;
                }
            }
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteDesignation(
        Utf8JsonWriter w, string? language, string? useSystem, string? useCode, string? useDisplay, string? value)
    {
        w.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(language)) w.WriteString("language", language);
        if (useSystem is not null || useCode is not null || useDisplay is not null)
        {
            w.WriteStartObject("use");
            if (!string.IsNullOrWhiteSpace(useSystem)) w.WriteString("system", useSystem);
            if (!string.IsNullOrWhiteSpace(useCode)) w.WriteString("code", useCode);
            if (!string.IsNullOrWhiteSpace(useDisplay)) w.WriteString("display", useDisplay);
            w.WriteEndObject();
        }
        if (!string.IsNullOrWhiteSpace(value)) w.WriteString("value", value);
        w.WriteEndObject();
    }
}
