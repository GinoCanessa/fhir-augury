namespace FhirAugury.Source.Zulip.Api;

/// <summary>Request body for updating a Zulip stream's mutable properties.</summary>
public record ZulipStreamUpdateRequest(bool IncludeStream, int? BaselineValue = null);