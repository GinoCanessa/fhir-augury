using Markdig;
using Microsoft.AspNetCore.Components;

namespace FhirAugury.DevUi.Services;

/// <summary>Server-side markdown to HTML rendering via Markdig.</summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return new MarkupString("");
        return new MarkupString(Markdown.ToHtml(markdown, Pipeline));
    }
}
