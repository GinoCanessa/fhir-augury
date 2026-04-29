namespace FhirAugury.Processing.Jira.Common.Filtering;

public interface IJiraProcessingTicketFilterCandidate
{
    string Project { get; }
    string Status { get; }
    string WorkGroup { get; }
    string Type { get; }
    string SourceTicketShape { get; }
}
