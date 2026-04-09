using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Common.Database.Records;

/// <summary>A FHIR element path reference extracted from text in any source.</summary>
[LdgSQLiteTable("xref_fhir_element")]
[LdgSQLiteIndex(nameof(ContentType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(ResourceType))]
[LdgSQLiteIndex(nameof(ResourceType), nameof(ElementPath))]
public partial record class FhirElementXRefRecord : ICrossReferenceRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required string ContentType { get; set; }
    public required string SourceId { get; set; }
    public required string LinkType { get; set; }
    public required string? Context { get; set; }
    public required string ResourceType { get; set; }
    public required string ElementPath { get; set; }

    [LdgSQLiteIgnore] public string TargetType => SourceSystems.Fhir;
    [LdgSQLiteIgnore] public string TargetId => ElementPath;
}
