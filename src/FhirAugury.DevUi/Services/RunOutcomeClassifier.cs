using FhirAugury.DevUi.Models;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.DevUi.Services;

public sealed class RunOutcomeClassifier
{
    public TicketRunOutcome Classify(
        AuthoringRunStatus run,
        ReviewSitePublication? publication = null,
        bool isPublishing = false,
        string? publicationError = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ProcessorRunOutcome processor = ClassifyProcessor(run);
        PublicationOutcome publicationOutcome = ClassifyPublication(
            run,
            processor,
            publication,
            isPublishing,
            publicationError);
        return new TicketRunOutcome(processor, publicationOutcome);
    }

    public ProcessorRunOutcome ClassifyProcessor(
        AuthoringRunStatus run)
    {
        ArgumentNullException.ThrowIfNull(run);
        AuthoringRunStateInfo state = run.State ??
            throw new InvalidOperationException(
                "The processor did not provide AuthoringRunStateInfo.");

        if (!state.IsTerminal)
        {
            return state.IsRecoverable
                ? ProcessorRunOutcome.RecoverableError
                : ProcessorRunOutcome.Active;
        }

        if (string.Equals(
                run.Status,
                "superseded",
                StringComparison.OrdinalIgnoreCase))
        {
            return ProcessorRunOutcome.Superseded;
        }
        if (string.Equals(
                run.Status,
                "completed-database-only",
                StringComparison.OrdinalIgnoreCase) ||
            run.DatabaseOnly)
        {
            return ProcessorRunOutcome.CompletedDatabaseOnly;
        }
        if (!string.Equals(
                run.Status,
                "completed",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported terminal authoring run state '{run.Status}'.");
        }

        return run.SupersededItems > 0
            ? ProcessorRunOutcome.CompletedWithSupersededItems
            : ProcessorRunOutcome.Completed;
    }

    private static PublicationOutcome ClassifyPublication(
        AuthoringRunStatus run,
        ProcessorRunOutcome processor,
        ReviewSitePublication? publication,
        bool isPublishing,
        string? publicationError)
    {
        if (processor is not (
                ProcessorRunOutcome.Completed or
                ProcessorRunOutcome.CompletedWithSupersededItems) ||
            run.DatabaseOnly)
        {
            return PublicationOutcome.Unavailable;
        }
        if (isPublishing)
        {
            return PublicationOutcome.Publishing;
        }
        if (!string.IsNullOrWhiteSpace(publicationError))
        {
            return PublicationOutcome.Failed;
        }
        return publication is null
            ? PublicationOutcome.Ready
            : PublicationOutcome.Published;
    }
}
