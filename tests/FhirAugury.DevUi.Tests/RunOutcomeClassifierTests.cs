using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.DevUi.Tests;

public sealed class RunOutcomeClassifierTests
{
    private readonly RunOutcomeClassifier _classifier = new();

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("finalizing")]
    public void NonterminalOrdinaryStatesAreActive(string status)
    {
        AuthoringRunStatus run = Run(
            status,
            terminal: false,
            recoverable: false);

        TicketRunOutcome outcome =
            _classifier.Classify(run);

        Assert.Equal(
            ProcessorRunOutcome.Active,
            outcome.Processor);
        Assert.Equal(
            PublicationOutcome.Unavailable,
            outcome.Publication);
    }

    [Fact]
    public void RecoverableErrorRemainsNonterminalAndPollable()
    {
        AuthoringRunStatus run = Run(
            "error",
            terminal: false,
            recoverable: true);

        TicketRunOutcome outcome =
            _classifier.Classify(run);

        Assert.Equal(
            ProcessorRunOutcome.RecoverableError,
            outcome.Processor);
        Assert.False(run.State!.IsTerminal);
    }

    [Fact]
    public void CompletedRunSeparatesFullAndPartialOutcomes()
    {
        TicketRunOutcome full = _classifier.Classify(
            Run("completed", terminal: true));
        TicketRunOutcome partial = _classifier.Classify(
            Run(
                "completed",
                terminal: true,
                supersededItems: 2));

        Assert.Equal(
            ProcessorRunOutcome.Completed,
            full.Processor);
        Assert.Equal(
            ProcessorRunOutcome
                .CompletedWithSupersededItems,
            partial.Processor);
        Assert.Equal(
            PublicationOutcome.Ready,
            full.Publication);
        Assert.Equal(
            PublicationOutcome.Ready,
            partial.Publication);
    }

    [Fact]
    public void DatabaseOnlyAndSupersededRunsCannotPublish()
    {
        TicketRunOutcome databaseOnly = _classifier.Classify(
            Run(
                "completed-database-only",
                terminal: true,
                databaseOnly: true));
        TicketRunOutcome superseded = _classifier.Classify(
            Run("superseded", terminal: true));

        Assert.Equal(
            ProcessorRunOutcome.CompletedDatabaseOnly,
            databaseOnly.Processor);
        Assert.Equal(
            ProcessorRunOutcome.Superseded,
            superseded.Processor);
        Assert.Equal(
            PublicationOutcome.Unavailable,
            databaseOnly.Publication);
        Assert.Equal(
            PublicationOutcome.Unavailable,
            superseded.Publication);
    }

    [Fact]
    public void AbandonedRunIsTerminalNonSuccessAndCannotPublish()
    {
        AuthoringRunStatus run =
            Run("abandoned", terminal: true);

        TicketRunOutcome outcome =
            _classifier.Classify(run);

        Assert.Equal(
            ProcessorRunOutcome.Abandoned,
            outcome.Processor);
        Assert.Equal(
            PublicationOutcome.Unavailable,
            outcome.Publication);
        Assert.False(run.State!.IsRecoverable);
    }

    [Fact]
    public void PublicationAxisIsIndependentFromProcessorSuccess()
    {
        AuthoringRunStatus run =
            Run("completed", terminal: true);
        ReviewSitePublication published = new(
            null!,
            null!,
            Reconstructed: false);

        Assert.Equal(
            PublicationOutcome.Publishing,
            _classifier.Classify(
                run,
                isPublishing: true).Publication);
        Assert.Equal(
            PublicationOutcome.Failed,
            _classifier.Classify(
                run,
                publicationError: "disk full").Publication);
        TicketRunOutcome publishedOutcome =
            _classifier.Classify(run, published);
        Assert.Equal(
            ProcessorRunOutcome.Completed,
            publishedOutcome.Processor);
        Assert.Equal(
            PublicationOutcome.Published,
            publishedOutcome.Publication);
    }

    [Fact]
    public void UnsupportedTerminalStateDoesNotInventFailedOutcome()
    {
        AuthoringRunStatus run =
            Run("failed", terminal: true);

        Assert.Throws<InvalidOperationException>(
            () => _classifier.Classify(run));
        Assert.DoesNotContain(
            Enum.GetNames<ProcessorRunOutcome>(),
            value => string.Equals(
                value,
                "Failed",
                StringComparison.Ordinal));
    }

    [Fact]
    public void MissingProcessorStateIsRejected()
    {
        AuthoringRunStatus run =
            Run("running", terminal: false) with
            {
                State = null,
            };

        Assert.Throws<InvalidOperationException>(
            () => _classifier.Classify(run));
    }

    private static AuthoringRunStatus Run(
        string status,
        bool terminal,
        bool recoverable = false,
        bool databaseOnly = false,
        int supersededItems = 0) => new(
            "run-1",
            "jira-fhir",
            1,
            status,
            databaseOnly,
            2,
            2,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            terminal ? DateTimeOffset.UtcNow : null,
            null,
            RetryableErrorItems: recoverable ? 1 : 0,
            SupersededItems: supersededItems,
            State: new AuthoringRunStateInfo(
                terminal,
                recoverable));
}
