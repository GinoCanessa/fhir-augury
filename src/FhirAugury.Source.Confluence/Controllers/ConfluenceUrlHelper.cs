using System.Globalization;
using System.Text;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database.Records;

namespace FhirAugury.Source.Confluence.Controllers;

internal static class ConfluenceUrlHelper
{
    internal static string BuildPageUrl(ConfluenceServiceOptions options, string pageId, string? existingUrl)
    {
        return existingUrl ?? $"{options.BaseUrl}/pages/{pageId}";
    }

    internal static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (value is null) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? dt
            : null;
    }

    internal static string BuildMarkdownSnapshot(ConfluencePageRecord page, List<ConfluenceCommentRecord> comments)
    {
        StringBuilder md = new StringBuilder();
        md.AppendLine($"# {page.Title}");
        md.AppendLine();
        md.AppendLine($"**Space:** {page.SpaceKey}  ");
        md.AppendLine($"**Version:** {page.VersionNumber}  ");
        if (page.LastModifiedBy is not null) md.AppendLine($"**Last Modified By:** {page.LastModifiedBy}  ");
        md.AppendLine($"**Last Modified:** {page.LastModifiedAt:yyyy-MM-dd}  ");
        if (page.Labels is not null) md.AppendLine($"**Labels:** {page.Labels}  ");
        md.AppendLine();
        if (!string.IsNullOrEmpty(page.BodyPlain))
        {
            md.AppendLine("## Content");
            md.AppendLine();
            md.AppendLine(page.BodyPlain);
            md.AppendLine();
        }

        if (comments.Count > 0)
        {
            md.AppendLine("## Comments");
            foreach (ConfluenceCommentRecord c in comments)
            {
                md.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}): {c.Body}");
                md.AppendLine();
            }
        }

        return md.ToString();
    }
}
