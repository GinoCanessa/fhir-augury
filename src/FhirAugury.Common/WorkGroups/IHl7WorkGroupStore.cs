using Microsoft.Data.Sqlite;

namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Persistence boundary for the <c>hl7_workgroups</c> table consumed by
/// <see cref="Hl7WorkGroupIndexer"/>. Each source service implements this
/// interface over its own CsLightDbGen-generated record type so the indexer
/// itself stays free of source-specific schema knowledge.
/// </summary>
public interface IHl7WorkGroupStore
{
    /// <summary>Loads every row currently in the store.</summary>
    IReadOnlyList<Hl7WorkGroupDto> LoadAll(SqliteConnection connection);

    /// <summary>
    /// Applies a parsed XML diff to the store.
    /// <list type="bullet">
    ///   <item><paramref name="toUpsert"/> contains every concept seen in the
    ///         XML; the store inserts new ones and updates existing rows in
    ///         place (matched by <see cref="Hl7WorkGroupDto.Code"/>),
    ///         preserving surrogate IDs.</item>
    ///   <item><paramref name="toRetire"/> contains rows that exist in the
    ///         store but were absent from the XML and were not already
    ///         retired. The store updates them with <c>Retired = true</c>
    ///         while preserving every other field — the full prior DTO is
    ///         passed in so no field is silently lost.</item>
    /// </list>
    /// </summary>
    void ApplyChanges(
        SqliteConnection connection,
        IReadOnlyList<Hl7WorkGroupDto> toUpsert,
        IReadOnlyList<Hl7WorkGroupDto> toRetire);

    /// <summary>Returns the total row count currently in the store.</summary>
    int Count(SqliteConnection connection);
}
