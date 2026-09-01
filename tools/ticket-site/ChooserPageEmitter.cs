using System.Reflection;

namespace FhirAugury.Tools.TicketSite;

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

    public static void Emit(string rootOut)
    {
        Directory.CreateDirectory(rootOut);
        Directory.CreateDirectory(Path.Combine(rootOut, "assets"));

        bool discussionLive = File.Exists(Path.Combine(rootOut, PreparerSubSiteEmitter.SubSiteFolder, "index.html"));
        bool applyingLive = File.Exists(Path.Combine(rootOut, PlannerSubSiteEmitter.SubSiteFolder, "index.html"));

        Assembly asm = typeof(ChooserPageEmitter).Assembly;
        string template = ReadEmbedded(asm, TemplateName);
        string css = ReadEmbedded(asm, CssName);

        string html = template
            .Replace(DiscussionStateMarker, discussionLive ? "live" : "missing", StringComparison.Ordinal)
            .Replace(ApplyingStateMarker, applyingLive ? "live" : "missing", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(rootOut, "index.html"), html);
        File.WriteAllText(Path.Combine(rootOut, "assets", "chooser.css"), css);
    }

    private static string ReadEmbedded(Assembly asm, string name)
    {
        using Stream stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource: {name}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
