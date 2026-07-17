using FhirAugury.Cli.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class PreparedTicketWriteHandler
{
    public static async Task<object> HandleAsync(PreparedTicketWriteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DbPath))
        {
            throw new ArgumentException("DbPath is required.");
        }

        if (request.Payload is null)
        {
            throw new ArgumentException("Payload is required.");
        }

        PreparedTicketPayloadValidator.ThrowIfInvalid(request.Payload);
        string dbPath = Path.GetFullPath(request.DbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using PreparerDatabase database = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        PreparedTicketSaveResult result = await database.SavePreparedTicketAsync(request.Payload, ct);
        return new
        {
            key = result.Key,
            preparedTicketRows = result.PreparedTicketRows,
            repoRows = result.RepoRows,
            relatedJiraRows = result.RelatedJiraRows,
            relatedZulipRows = result.RelatedZulipRows,
            relatedGitHubRows = result.RelatedGitHubRows,
        };
    }
}
