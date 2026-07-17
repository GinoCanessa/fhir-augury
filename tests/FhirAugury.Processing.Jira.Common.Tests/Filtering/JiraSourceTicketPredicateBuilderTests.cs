using FhirAugury.Processing.Jira.Common.Filtering;

namespace FhirAugury.Processing.Jira.Common.Tests.Filtering;

public class JiraSourceTicketPredicateBuilderTests
{
    [Fact]
    public void Specification_NullFilter_AcceptsAllCandidates()
    {
        ResolvedJiraProcessingFilters filters = new() { SourceTicketShape = "fhir" };

        Func<IJiraProcessingTicketFilterCandidate, bool> predicate = JiraSourceTicketPredicateBuilder.Build(filters);

        Assert.True(predicate(Candidate(specification: "fhir-core")));
        Assert.True(predicate(Candidate(specification: "fhir-extensions")));
        Assert.True(predicate(Candidate(specification: "")));
    }

    [Fact]
    public void Specification_PopulatedFilter_MatchesCaseInsensitively()
    {
        ResolvedJiraProcessingFilters filters = new()
        {
            Specifications = ["fhir-core"],
            SourceTicketShape = "fhir",
        };

        Func<IJiraProcessingTicketFilterCandidate, bool> predicate = JiraSourceTicketPredicateBuilder.Build(filters);

        Assert.True(predicate(Candidate(specification: "fhir-core")));
        Assert.True(predicate(Candidate(specification: "FHIR-CORE")));
        Assert.False(predicate(Candidate(specification: "fhir-extensions")));
        Assert.False(predicate(Candidate(specification: "")));
    }

    private static FilterCandidate Candidate(
        string project = "FHIR",
        string status = "Triaged",
        string workGroup = "Infrastructure",
        string type = "Change Request",
        string specification = "",
        string sourceTicketShape = "fhir") => new()
        {
            Project = project,
            Status = status,
            WorkGroup = workGroup,
            Type = type,
            Specification = specification,
            SourceTicketShape = sourceTicketShape,
        };

    private sealed record FilterCandidate : IJiraProcessingTicketFilterCandidate
    {
        public required string Project { get; init; }
        public required string Status { get; init; }
        public required string WorkGroup { get; init; }
        public required string Type { get; init; }
        public required string Specification { get; init; }
        public required string SourceTicketShape { get; init; }
    }
}
