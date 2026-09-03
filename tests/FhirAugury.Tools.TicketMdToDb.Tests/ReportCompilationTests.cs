using FhirAugury.Tools.TicketMdToDb.Compilation;

namespace FhirAugury.Tools.TicketMdToDb.Tests;

public sealed class ReportCompilationTests
{
    [Fact]
    public void Compile_CanonicalWithEmbeddedHeading_PreservesDetailsBody()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");

        CompilationResult result = corpus.Compile(1);

        Assert.True(result.Success, JoinDiagnostics(result));
        ManifestTicket ticket = Assert.Single(result.Manifest.Tickets);
        Assert.Contains("## Issue Summary", ticket.Payload.CommentSummary, StringComparison.Ordinal);
        Assert.Contains("Closing details.", ticket.Payload.CommentSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Manifest.Diagnostics,
            diagnostic => diagnostic.Code == "source-range-unaccounted");
    }

    [Fact]
    public void Compile_Recognizers_AreMutuallyExclusive()
    {
        using CompilationTestDirectory corpus = new();
        foreach (string fixture in CompilationTestDirectory.LegacyFixtures)
        {
            corpus.CopyFixture(fixture);
        }
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");

        CompilationResult result = corpus.Compile(7);

        Assert.True(result.Success, JoinDiagnostics(result));
        Assert.Equal(7, result.Manifest.Tickets.Count);
        Assert.DoesNotContain(
            result.Manifest.Diagnostics,
            diagnostic => diagnostic.Code is "unknown-layout" or "ambiguous-layout");
        Assert.Equal(7, result.Manifest.Tickets.Select(ticket => ticket.DialectId).Distinct().Count());
    }

    [Fact]
    public void Compile_UnknownLayout_FailsClosed()
    {
        using CompilationTestDirectory corpus = new();
        corpus.WriteReport(
            "FHIR-999.md",
            "# Ticket FHIR-999\n\n## Unrecognized\n\nNo bounded dialect.");

        CompilationResult result = corpus.Compile(1);

        Assert.False(result.Success);
        Assert.Contains(
            result.Manifest.Diagnostics,
            diagnostic => diagnostic.Code == "unknown-layout" && diagnostic.IsBlocking);
        Assert.Empty(result.Manifest.Tickets);
    }

    [Fact]
    public void Compile_DuplicateKeyAndCountMismatch_ReportEveryFailure()
    {
        using CompilationTestDirectory corpus = new();
        string markdown = corpus.ReadFixture("CanonicalWithEmbeddedHeading.md")
            .Replace("FHIR-100", "FHIR-700", StringComparison.Ordinal);
        corpus.WriteReport(Path.Combine("a", "FHIR-700.md"), markdown);
        corpus.WriteReport(Path.Combine("b", "FHIR-700.md"), markdown);

        CompilationResult result = corpus.Compile(3);

        Assert.False(result.Success);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "duplicate-jira-key");
        Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "expected-count-mismatch");
    }

    [Fact]
    public void Compile_PreservesOriginalMarkdownSlices()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");

        ManifestTicket ticket = Assert.Single(corpus.Compile(1).Manifest.Tickets);

        ManifestScalar proposal = Assert.Single(
            ticket.Scalars,
            scalar => scalar.Field == PreparedTicketFieldNames.ProposalA);
        Assert.Contains("Proposal A markdown.", proposal.SourceValue, StringComparison.Ordinal);
        Assert.Contains(
            proposal.Evidence,
            evidence => evidence.Markdown.Contains("Proposal A markdown.", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_ReversedLinkSyntax_MapsZulipReference()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalReferenceVariants.md", "FHIR-101.md");

        ManifestTicket ticket = Assert.Single(corpus.Compile(1).Manifest.Tickets);

        Assert.Contains(
            ticket.Payload.RelatedZulipThreads,
            row => row.ZulipThreadId == "FHIR Infrastructure:reversed topic");
    }

    [Fact]
    public void Compile_GitHubIssueAndComments_DeduplicatesAndMergesEvidence()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalReferenceVariants.md", "FHIR-101.md");

        ManifestTicket ticket = Assert.Single(corpus.Compile(1).Manifest.Tickets);

        Assert.Single(ticket.Payload.RelatedGitHubItems, row => row.GitHubItemId == "HL7/fhir#4124");
        ManifestChildEvidence evidence = Assert.Single(
            ticket.ChildEvidence,
            child => child.Kind == "github" && child.Identity == "HL7/fhir#4124");
        Assert.Equal(2, evidence.Evidence.Count);
        Assert.Equal(2, evidence.Justifications.Count);
    }

    [Fact]
    public void Compile_GitHubCommit_PreservesAuditedUnresolvedChild()
    {
        using CompilationTestDirectory corpus = new();
        corpus.CopyFixture("CanonicalReferenceVariants.md", "FHIR-101.md");

        CompilationResult result = corpus.Compile(1);
        ManifestTicket ticket = Assert.Single(result.Manifest.Tickets);

        const string commit = "https://github.com/HL7/fhir/commit/abcdef123456";
        Assert.Contains(ticket.Payload.RelatedGitHubItems, row => row.GitHubItemId == commit);
        Assert.Contains(
            ticket.ChildEvidence,
            child => child.Identity == commit && child.Unsupported);
        Assert.Contains(
            result.Manifest.Diagnostics,
            diagnostic => diagnostic.Code == "unsupported-github-commit" && !diagnostic.IsBlocking);
    }

    private static string JoinDiagnostics(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Manifest.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}

internal sealed class CompilationTestDirectory : IDisposable
{
    public static readonly string[] LegacyFixtures =
    [
        "FHIR-10333.md",
        "FHIR-10654.md",
        "FHIR-12563.md",
        "FHIR-13634.md",
        "FHIR-16662.md",
        "FHIR-17156.md",
    ];

    private readonly string _directory = Path.Combine(
        Environment.CurrentDirectory,
        "temp",
        "ticket-md-to-db-tests",
        Guid.NewGuid().ToString("N"));

    public CompilationTestDirectory()
    {
        Directory.CreateDirectory(_directory);
    }

    public string Root => _directory;

    public string CopyFixture(string fixtureName, string? destinationName = null)
    {
        string destination = Path.Combine(_directory, destinationName ?? fixtureName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(FixturePath(fixtureName), destination);
        return destination;
    }

    public string ReadFixture(string fixtureName) => File.ReadAllText(FixturePath(fixtureName));

    public string WriteReport(string relativePath, string markdown)
    {
        string destination = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, markdown);
        return destination;
    }

    public CompilationResult Compile(int expectedCount, string? overridesPath = null) =>
        ReportCompiler.Compile(new CompilationRequest(_directory, expectedCount, overridesPath));

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_directory);

    private static string FixturePath(string fixtureName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
}
