extern alias FhirR4;
extern alias FhirR5;

using Hl7.Fhir.Model;
using System.Text.Json;

namespace FhirAugury.Server.Terminology.Ingestion;

/// <summary>
/// Per-FHIR-version JSON-to-POCO parser for CodeSystem / ValueSet.
/// </summary>
/// <remarks>
/// <para>
/// The Firely R4 and R5 packages both expose
/// <c>Hl7.Fhir.Serialization.FhirJsonDeserializer</c> from version-
/// specific assemblies; referencing both in the same project requires
/// <c>extern alias</c>. This wrapper centralizes that boilerplate so
/// the rest of the ingestion layer can stay alias-free.
/// </para>
/// <para>
/// <c>CodeSystem</c> and <c>ValueSet</c> themselves live in the shared
/// <c>Hl7.Fhir.Conformance</c> assembly — a single POCO type covers both
/// R4 and R5. Only the parser table-of-types differs, which is why we
/// dispatch on <see cref="FhirMajorVersion"/> here and return the
/// shared base type.
/// </para>
/// </remarks>
public sealed class TerminologyResourceParser
{
    private readonly FhirR4::Hl7.Fhir.Serialization.FhirJsonDeserializer _r4 = new();
    private readonly FhirR5::Hl7.Fhir.Serialization.FhirJsonDeserializer _r5 = new();

    /// <summary>
    /// Parses the supplied FHIR JSON into a <see cref="Resource"/> using
    /// the deserializer appropriate for <paramref name="version"/>.
    /// Returns <c>null</c> when the JSON is empty or the resource type
    /// is not one of CodeSystem / ValueSet.
    /// </summary>
    public Resource? TryParse(string json, FhirMajorVersion version)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (!reader.Read()) return null;

        Resource? parsed;
        IEnumerable<Hl7.Fhir.Utility.CodedException> issues;
        _ = version switch
        {
            FhirMajorVersion.R4 => _r4.TryDeserializeResource(ref reader, out parsed!, out issues!),
            FhirMajorVersion.R5 => _r5.TryDeserializeResource(ref reader, out parsed!, out issues!),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null),
        };

        // Accept the typed object even when TryDeserializeResource flagged
        // non-fatal issues (e.g. missing CodeSystem.status). Submissions
        // routed through /check may be partial drafts on purpose — the
        // matcher's job is to score them, not validate them.
        if (parsed is null) return null;
        if (parsed is CodeSystem or ValueSet) return parsed;
        return null;
    }
}
