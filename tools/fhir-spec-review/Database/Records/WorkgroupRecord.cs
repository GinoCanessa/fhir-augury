using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>Work group lookup (code, display name, URL) used to group the report.</summary>
[LdgSQLiteTable("workgroups")]
public partial record class WorkgroupRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? Url { get; set; } = null;
}
