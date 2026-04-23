using FhirAugury.Common.WorkGroups;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Jira-source implementation of <see cref="IHl7WorkGroupStore"/> over the
/// CsLightDbGen-generated <see cref="Hl7WorkGroupRecord"/>.
/// </summary>
internal sealed class JiraHl7WorkGroupStore : IHl7WorkGroupStore
{
    public IReadOnlyList<Hl7WorkGroupDto> LoadAll(SqliteConnection connection)
    {
        List<Hl7WorkGroupRecord> rows = Hl7WorkGroupRecord.SelectList(connection);
        List<Hl7WorkGroupDto> result = new(rows.Count);
        foreach (Hl7WorkGroupRecord r in rows)
        {
            result.Add(new Hl7WorkGroupDto(
                Code: r.Code,
                Name: r.Name,
                Definition: r.Definition,
                Retired: r.Retired,
                NameClean: r.NameClean));
        }
        return result;
    }

    public void ApplyChanges(
        SqliteConnection connection,
        IReadOnlyList<Hl7WorkGroupDto> toUpsert,
        IReadOnlyList<Hl7WorkGroupDto> toRetire)
    {
        Dictionary<string, Hl7WorkGroupRecord> existingByCode =
            Hl7WorkGroupRecord.SelectList(connection)
                .ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

        foreach (Hl7WorkGroupDto dto in toUpsert)
        {
            if (existingByCode.TryGetValue(dto.Code, out Hl7WorkGroupRecord? existing))
            {
                Hl7WorkGroupRecord updated = new Hl7WorkGroupRecord
                {
                    Id = existing.Id,
                    Code = dto.Code,
                    Name = dto.Name,
                    Definition = dto.Definition,
                    Retired = dto.Retired,
                    NameClean = dto.NameClean,
                };
                Hl7WorkGroupRecord.Update(connection, updated);
            }
            else
            {
                Hl7WorkGroupRecord inserted = new Hl7WorkGroupRecord
                {
                    Id = Hl7WorkGroupRecord.GetIndex(),
                    Code = dto.Code,
                    Name = dto.Name,
                    Definition = dto.Definition,
                    Retired = dto.Retired,
                    NameClean = dto.NameClean,
                };
                Hl7WorkGroupRecord.Insert(connection, inserted);
            }
        }

        foreach (Hl7WorkGroupDto dto in toRetire)
        {
            if (!existingByCode.TryGetValue(dto.Code, out Hl7WorkGroupRecord? existing))
                continue;

            Hl7WorkGroupRecord retired = new Hl7WorkGroupRecord
            {
                Id = existing.Id,
                Code = dto.Code,
                Name = dto.Name,
                Definition = dto.Definition,
                Retired = true,
                NameClean = dto.NameClean,
            };
            Hl7WorkGroupRecord.Update(connection, retired);
        }
    }

    public int Count(SqliteConnection connection)
        => Hl7WorkGroupRecord.SelectCount(connection);
}
