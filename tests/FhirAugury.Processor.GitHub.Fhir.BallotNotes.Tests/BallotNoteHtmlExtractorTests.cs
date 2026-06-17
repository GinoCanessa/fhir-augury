using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNoteHtmlExtractorTests
{
    [Fact]
    public void Extract_returns_blockquote_when_present()
    {
        const string html = """
            <div>
              <p>intro</p>
              <blockquote class="ballot-note">
                <p>This resource changed since the last ballot.</p>
              </blockquote>
              <p>more</p>
            </div>
            """;

        string extracted = BallotNoteHtmlExtractor.Extract(html);

        Assert.StartsWith("<blockquote", extracted);
        Assert.EndsWith("</blockquote>", extracted);
        Assert.Contains("changed since the last ballot", extracted);
        Assert.DoesNotContain("<p>more</p>", extracted);
    }

    [Fact]
    public void Extract_matches_regardless_of_attribute_order()
    {
        const string html =
            "<blockquote id=\"bn\" class=\"ballot-note alert\">note body</blockquote>";

        Assert.Equal(html, BallotNoteHtmlExtractor.Extract(html));
    }

    [Fact]
    public void Extract_returns_empty_when_absent()
    {
        const string html = "<div><blockquote>not a ballot note</blockquote></div>";
        Assert.Equal(string.Empty, BallotNoteHtmlExtractor.Extract(html));
    }

    [Fact]
    public void Extract_returns_empty_for_null_or_empty()
    {
        Assert.Equal(string.Empty, BallotNoteHtmlExtractor.Extract(string.Empty));
        Assert.Equal(string.Empty, BallotNoteHtmlExtractor.Extract(null!));
    }
}
