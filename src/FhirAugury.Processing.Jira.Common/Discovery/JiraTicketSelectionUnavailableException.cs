namespace FhirAugury.Processing.Jira.Common.Discovery;

/// <summary>Configured Jira selection could not be completed without bypassing its criteria.</summary>
public sealed class JiraTicketSelectionUnavailableException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);
