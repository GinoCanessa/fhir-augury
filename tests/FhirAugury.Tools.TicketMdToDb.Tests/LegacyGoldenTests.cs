using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Tools.TicketMdToDb.Compilation;
using FhirAugury.Tools.TicketMdToDb.Mapping;

namespace FhirAugury.Tools.TicketMdToDb.Tests;

public sealed class LegacyGoldenTests
{
    public static TheoryData<string, string, string, string> Cases => new()
    {
        { "FHIR-10333.md", "legacy-fhir-10333", "A", "Implement all invariants." },
        { "FHIR-10654.md", "legacy-fhir-10654", "A", "Locate the authored example" },
        { "FHIR-12563.md", "legacy-fhir-12563", "existing", "Use both US Core profiles." },
        { "FHIR-13634.md", "legacy-fhir-13634", "B", "Replace the URL." },
        { "FHIR-16662.md", "legacy-fhir-16662", "B", "Add all pregnancy fields." },
        { "FHIR-17156.md", "legacy-fhir-17156", "A", "Correct the malformed links." },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compile_LegacyFixture_MatchesCompleteNormalizedPayload(
        string fixture,
        string dialect,
        string recommendation,
        string proposalASnippet)
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture(fixture);

        CompilationResult result = corpus.Compile(1);

        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Manifest.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        ManifestTicket ticket = Assert.Single(result.Manifest.Tickets);
        Assert.Equal(dialect, ticket.DialectId);
        Assert.Equal(recommendation, ticket.Payload.Recommendation);
        Assert.Contains(proposalASnippet, ticket.Payload.ProposalA, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ticket.Payload.RequestSummary));
        Assert.False(string.IsNullOrWhiteSpace(ticket.Payload.ProposalA));
        Assert.False(string.IsNullOrWhiteSpace(ticket.Payload.ProposalB));
        Assert.False(string.IsNullOrWhiteSpace(ticket.Payload.ProposalC));
        Assert.False(string.IsNullOrWhiteSpace(ticket.Payload.RecommendationJustification));
        Assert.Equal(PreparedTicketImpactValues.NotAssessed, ticket.Payload.ProposalAImpact);
        Assert.Equal(PreparedTicketImpactValues.NotAssessed, ticket.Payload.ProposalBImpact);
        Assert.Null(ticket.Payload.SavedAt);
        Assert.Equal(0, ticket.SourceRanges[0].StartOffset);
        Assert.Equal(
            File.ReadAllText(Path.Combine(corpus.Root, fixture)).Length - 1,
            ticket.SourceRanges[^1].EndOffset);
        Assert.Contains(
            ticket.SourceRanges,
            range => range.Disposition == SourceRangeDisposition.IntentionallyUnmapped);
        Assert.DoesNotContain(
            result.Manifest.Diagnostics,
            diagnostic => diagnostic.Code is "source-range-unaccounted" or "source-range-multiply-consumed");
    }

    [Fact]
    public void Compile_Fhir10654_UsesRequiredMissingMarkerForAbsentAlternatives()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("FHIR-10654.md");

        ManifestTicket ticket = Assert.Single(corpus.Compile(1).Manifest.Tickets);

        Assert.Equal(PreparedTicketImportMapper.RequiredMissingText, ticket.Payload.ProposalB);
        Assert.Equal(PreparedTicketImportMapper.RequiredMissingText, ticket.Payload.ProposalC);
    }

    [Fact]
    public void Compile_Fhir17156_RetainsImplementationAndVerificationSpans()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("FHIR-17156.md");

        ManifestTicket ticket = Assert.Single(corpus.Compile(1).Manifest.Tickets);

        Assert.Contains("### Implementation Outline", ticket.Payload.RecommendationJustification, StringComparison.Ordinal);
        Assert.Contains("### Verification", ticket.Payload.RecommendationJustification, StringComparison.Ordinal);
        ManifestScalar recommendation = Assert.Single(
            ticket.Scalars,
            scalar => scalar.Field == PreparedTicketFieldNames.RecommendationJustification);
        Assert.Contains(
            recommendation.Evidence,
            evidence => evidence.Markdown.Contains("### Verification", StringComparison.Ordinal));
    }
}
