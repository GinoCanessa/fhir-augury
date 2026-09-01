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

    [Fact]
    public void ExtractClassified_flags_augury_generated_block()
    {
        const string html =
            "<blockquote class=\"ballot-note\" data-augury-generated=\"true\" id=\"bn1\">gen</blockquote>";

        ClassifiedNoteBlock block = Assert.Single(BallotNoteHtmlExtractor.ExtractClassified(html));
        Assert.True(block.IsAuguryGenerated);
        Assert.Contains("gen", block.Html);
    }

    [Fact]
    public void ExtractClassified_treats_plain_ballot_note_as_hand_authored()
    {
        const string html = "<blockquote class=\"ballot-note\" id=\"bn1\">hand</blockquote>";

        ClassifiedNoteBlock block = Assert.Single(BallotNoteHtmlExtractor.ExtractClassified(html));
        Assert.False(block.IsAuguryGenerated);
    }

    [Fact]
    public void ExtractClassified_matches_stu_note_as_hand_authored()
    {
        const string html = "<blockquote class=\"stu-note\">draft STU note</blockquote>";

        ClassifiedNoteBlock block = Assert.Single(BallotNoteHtmlExtractor.ExtractClassified(html));
        Assert.False(block.IsAuguryGenerated);
        Assert.Contains("draft STU note", block.Html);
    }

    [Fact]
    public void ExtractClassified_mixed_page_returns_one_generated_and_one_preserved()
    {
        const string html = """
            <div>
              <blockquote class="ballot-note" data-augury-generated="true" id="bn1">
                <p>tool note</p>
              </blockquote>
              <blockquote class="stu-note">
                <p>hand-authored note</p>
              </blockquote>
            </div>
            """;

        IReadOnlyList<ClassifiedNoteBlock> blocks = BallotNoteHtmlExtractor.ExtractClassified(html);

        Assert.Equal(2, blocks.Count);
        Assert.Single(blocks, b => b.IsAuguryGenerated);
        ClassifiedNoteBlock preserved = Assert.Single(blocks, b => !b.IsAuguryGenerated);
        Assert.Contains("hand-authored note", preserved.Html);
    }

    [Fact]
    public void ExtractClassified_returns_empty_for_no_notes()
        => Assert.Empty(BallotNoteHtmlExtractor.ExtractClassified("<div><blockquote>plain</blockquote></div>"));
}
