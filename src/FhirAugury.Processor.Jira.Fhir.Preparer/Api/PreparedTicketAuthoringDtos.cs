using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Api;

public static class PreparedTicketAuthoringDtos
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string ComputeContentHash(PreparedTicketPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return AuthoringResultHasher.HashBytes(
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
    }
}
