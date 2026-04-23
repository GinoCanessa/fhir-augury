namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Source-system-agnostic value carrier for one HL7 work-group concept parsed
/// from the <c>CodeSystem-hl7-work-group</c> XML. The persistence layer
/// (<see cref="IHl7WorkGroupStore"/>) is responsible for mapping this DTO to
/// the source-specific record type and for assigning surrogate IDs.
/// </summary>
public sealed record Hl7WorkGroupDto(
    string Code,
    string Name,
    string? Definition,
    bool Retired,
    string NameClean);
