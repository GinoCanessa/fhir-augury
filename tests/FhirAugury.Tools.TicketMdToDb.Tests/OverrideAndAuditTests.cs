using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Tools.TicketMdToDb.Audit;
using FhirAugury.Tools.TicketMdToDb.Compilation;
using FhirAugury.Tools.TicketMdToDb.Mapping;

namespace FhirAugury.Tools.TicketMdToDb.Tests;

public sealed class OverrideAndAuditTests
{
    [Fact]
    public void Overrides_RequireMatchingFingerprintAndReason()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        CompilationResult baseline = corpus.Compile(1);
        string sha = Assert.Single(baseline.Manifest.Files).SourceSha256;
        string validPath = WriteOverrides(
            corpus,
            sha,
            "reviewed against source",
            new Dictionary<string, string>
            {
                [PreparedTicketFieldNames.ProposalAImpact] = PreparedTicketImpactValues.NonSubstantive,
            });

        CompilationResult valid = corpus.Compile(1, validPath);

        Assert.True(valid.Success);
        ManifestTicket ticket = Assert.Single(valid.Manifest.Tickets);
        Assert.Equal(PreparedTicketImpactValues.NonSubstantive, ticket.Payload.ProposalAImpact);
        Assert.Single(ticket.Overrides);

        string stalePath = WriteOverrides(
            corpus,
            new string('0', 64),
            string.Empty,
            new Dictionary<string, string>
            {
                [PreparedTicketFieldNames.ProposalAImpact] = PreparedTicketImpactValues.NonSubstantive,
            },
            "stale.json");
        CompilationResult stale = corpus.Compile(1, stalePath);
        Assert.Contains(stale.Manifest.Diagnostics, item => item.Code == "overrides-stale-fingerprint");
        Assert.Contains(stale.Manifest.Diagnostics, item => item.Code == "overrides-review-reason-missing");
    }

    [Fact]
    public void Overrides_RejectDuplicateUnknownAndInvalidFields()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        string sha = Assert.Single(corpus.Compile(1).Manifest.Files).SourceSha256;
        string path = Path.Combine(corpus.Root, "invalid.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "tickets": {
                "FHIR-100": {
                  "sourceSha256": "{{sha}}",
                  "reviewReason": "reviewed",
                  "fields": {
                    "ProposalAImpact": "Non-substantive",
                    "ProposalAImpact": "Compatible, substantive",
                    "Unknown": "value",
                    "Recommendation": "maybe"
                  }
                }
              }
            }
            """);

        CompilationResult result = corpus.Compile(1, path);

        Assert.False(result.Success);
        Assert.Contains(result.Manifest.Diagnostics, item => item.Code == "overrides-duplicate-field");
        Assert.Contains(result.Manifest.Diagnostics, item => item.Code == "overrides-unknown-field");
        Assert.Contains(result.Manifest.Diagnostics, item => item.Code == "overrides-recommendation-invalid");
    }

    [Theory]
    [InlineData(PreparedTicketRecommendationValues.Existing)]
    [InlineData(PreparedTicketRecommendationValues.ProposalA)]
    [InlineData(PreparedTicketRecommendationValues.ProposalB)]
    [InlineData(PreparedTicketRecommendationValues.ProposalC)]
    public void Overrides_UseSharedRecommendationVocabulary(string recommendation)
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        string sha = Assert.Single(corpus.Compile(1).Manifest.Files).SourceSha256;
        string path = WriteOverrides(
            corpus,
            sha,
            "reviewed",
            new Dictionary<string, string>
            {
                [PreparedTicketFieldNames.Recommendation] = recommendation,
            });

        CompilationResult result = corpus.Compile(1, path);

        Assert.True(result.Success);
        Assert.Equal(recommendation, Assert.Single(result.Manifest.Tickets).Payload.Recommendation);
    }

    [Fact]
    public void MissingPolicy_UsesNotAssessedAndRequiredTextMarker()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("FHIR-10654.md");

        ManifestTicket ticket = Assert.Single(corpus.Compile(1).Manifest.Tickets);

        Assert.Equal(PreparedTicketImpactValues.NotAssessed, ticket.Payload.ProposalAImpact);
        Assert.Equal(PreparedTicketImpactValues.NotAssessed, ticket.Payload.ProposalBImpact);
        Assert.Equal(PreparedTicketImportMapper.RequiredMissingText, ticket.Payload.ProposalB);
        Assert.Equal(PreparedTicketImportMapper.RequiredMissingText, ticket.Payload.ProposalC);
    }

    [Fact]
    public void MissingRecommendation_RemainsBlocking()
    {
        using CompilationTestDirectory corpus = new();
        string markdown = corpus.ReadFixture("CanonicalWithEmbeddedHeading.md")
            .Replace("**Recommended disposition:** A", "No recommendation was recorded.", StringComparison.Ordinal);
        corpus.WriteReport("FHIR-100.md", markdown);

        CompilationResult result = corpus.Compile(1);

        Assert.False(result.Success);
        Assert.Contains(
            result.Manifest.Diagnostics,
            item => item.Code is "recommendation-missing" or "recommendation-ambiguous");
    }

    [Fact]
    public void Manifest_IsStableAcrossEquivalentRootsAndRunTimes()
    {
        using CompilationTestDirectory first = new();
        using CompilationTestDirectory second = new();
        first.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        second.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");

        CompilationResult firstResult = first.Compile(1);
        CompilationResult secondResult = second.Compile(1);

        Assert.Equal(firstResult.Manifest.ManifestId, secondResult.Manifest.ManifestId);
        Assert.Equal(
            JsonSerializer.Serialize(firstResult.Manifest),
            JsonSerializer.Serialize(secondResult.Manifest));
    }

    [Fact]
    public void RunEnvelope_RecordsNondeterministicContextSeparately()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        CompilationResult compilation = corpus.Compile(1);
        string db = Path.Combine(corpus.Root, "output.db");
        ImportAudit first = ImportAudit.ForCompilation(
            compilation,
            db,
            new Uri("http://localhost:5150"),
            null,
            $"{db}.audit.json",
            $"{db}.template.json",
            true,
            false,
            false,
            DateTimeOffset.Parse("2026-09-03T10:00:00Z"),
            "run-a");
        ImportAudit second = ImportAudit.ForCompilation(
            compilation,
            db,
            new Uri("http://localhost:5150"),
            null,
            $"{db}.audit.json",
            $"{db}.template.json",
            true,
            false,
            false,
            DateTimeOffset.Parse("2026-09-03T11:00:00Z"),
            "run-b");

        Assert.Equal(first.Manifest.ManifestId, second.Manifest.ManifestId);
        Assert.NotEqual(first.Run.RunId, second.Run.RunId);
        Assert.NotEqual(first.Run.StartedAt, second.Run.StartedAt);
    }

    [Fact]
    public async Task DryRun_DoesNotCreateDestination()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        string db = Path.Combine(corpus.Root, "output.db");
        string audit = Path.Combine(corpus.Root, "audit.json");
        string template = Path.Combine(corpus.Root, "template.json");

        int exitCode = await ProgramEntry.RunAsync(
        [
            "--input", corpus.Root,
            "--db", db,
            "--orchestrator", "http://localhost:5150",
            "--expected-count", "1",
            "--audit", audit,
            "--override-template", template,
            "--dry-run",
        ]);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(db));
        Assert.True(File.Exists(audit));
        Assert.True(File.Exists(template));
    }

    private static string WriteOverrides(
        CompilationTestDirectory corpus,
        string sha,
        string reviewReason,
        IReadOnlyDictionary<string, string> fields,
        string fileName = "overrides.json")
    {
        string path = Path.Combine(corpus.Root, fileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                tickets = new Dictionary<string, object>
                {
                    ["FHIR-100"] = new
                    {
                        sourceSha256 = sha,
                        reviewReason,
                        fields,
                    },
                },
            }));
        return path;
    }
}
