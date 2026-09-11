using System.Net;
using System.Reflection;
using System.Text.Json;

namespace FhirAugury.Publishing.Tickets;

/// <summary>
/// Emits the chooser landing page at <c>&lt;rootOut&gt;/index.html</c>.
/// The chooser is plain HTML, loads no SQL, and is unconditionally
/// regenerated every run from whichever sub-site folders are present
/// under <c>&lt;rootOut&gt;/</c>. It does not have a
/// <c>.ticket-site.meta</c> marker — it is a derived artifact.
/// </summary>
internal static class ChooserPageEmitter
{
    private const string TemplateName = "web-assets/chooser/index.template.html";
    private const string CssName = "web-assets/chooser/chooser.css";

    private const string DiscussionStateMarker = "<!-- __DISCUSSION_STATE__ -->";
    private const string ApplyingStateMarker = "<!-- __APPLYING_STATE__ -->";
    private const string DiscussionLabelMarker = "<!-- __DISCUSSION_LABEL__ -->";
    private const string AssetVersionMarker = "__ASSET_VERSION__";
    private const string DefaultDiscussionLabel = "Tickets for Discussion";

    public static async Task EmitAsync(
        string rootOut,
        Func<bool, bool, CancellationToken, Task>? beforeCommitAsync,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(rootOut);
        Directory.CreateDirectory(Path.Combine(rootOut, "assets"));

        bool discussionLive = File.Exists(Path.Combine(rootOut, PreparerSubSiteEmitter.SubSiteFolder, "index.html"));
        bool applyingLive = File.Exists(Path.Combine(rootOut, PlannerSubSiteEmitter.SubSiteFolder, "index.html"));
        string discussionLabel = discussionLive
            ? await ReadDiscussionLabelAsync(rootOut, ct).ConfigureAwait(false)
            : DefaultDiscussionLabel;

        Assembly asm = typeof(ChooserPageEmitter).Assembly;
        string template = await ReadEmbeddedAsync(asm, TemplateName, ct)
            .ConfigureAwait(false);
        string css = await ReadEmbeddedAsync(asm, CssName, ct)
            .ConfigureAwait(false);

        string html = template
            .Replace(DiscussionStateMarker, discussionLive ? "live" : "missing", StringComparison.Ordinal)
            .Replace(ApplyingStateMarker, applyingLive ? "live" : "missing", StringComparison.Ordinal)
            .Replace(
                DiscussionLabelMarker,
                WebUtility.HtmlEncode(discussionLabel),
                StringComparison.Ordinal)
            .Replace(
                AssetVersionMarker,
                PreparerSubSiteEmitter.RendererAssetsVersion,
                StringComparison.Ordinal);
        string indexPath = Path.Combine(rootOut, "index.html");
        string cssPath = Path.Combine(rootOut, "assets", "chooser.css");
        string indexTempPath = indexPath + $".tmp-{Guid.NewGuid():N}";
        string cssTempPath = cssPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(indexTempPath, html, ct)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(cssTempPath, css, ct)
                .ConfigureAwait(false);
            if (beforeCommitAsync is not null)
            {
                await beforeCommitAsync(discussionLive, applyingLive, ct)
                    .ConfigureAwait(false);
            }

            File.Move(cssTempPath, cssPath, overwrite: true);
            File.Move(indexTempPath, indexPath, overwrite: true);
        }
        finally
        {
            TryDelete(indexTempPath);
            TryDelete(cssTempPath);
        }
    }

    private static async Task<string> ReadEmbeddedAsync(
        Assembly asm,
        string name,
        CancellationToken ct)
    {
        using Stream stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource: {name}");
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadDiscussionLabelAsync(
        string rootOut,
        CancellationToken ct)
    {
        string manifestPath = Path.Combine(
            rootOut,
            PreparerSubSiteEmitter.SubSiteFolder,
            TicketSiteManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return DefaultDiscussionLabel;
        }

        try
        {
            TicketSiteManifest manifest =
                await TicketSiteManifest.ReadAsync(manifestPath, ct)
                    .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(manifest.DisplayTitle)
                ? DefaultDiscussionLabel
                : manifest.DisplayTitle;
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException)
        {
            return DefaultDiscussionLabel;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
