using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Indexing;

/// <summary>
/// Extracts Jira ticket references from text using shared <see cref="JiraTicketExtractor"/>.
/// Pure detection — text in, typed records out. No storage or DI dependencies.
/// </summary>
public static class JiraReferenceExtractor
{
    public static List<JiraXRefRecord> GetReferences(
        string sourceType, string sourceId,
        CrossRefExtractionContext? context = null,
        params string[] texts)
    {
        List<JiraXRefRecord> results = [];
        HashSet<string> seen = [];

        foreach (string text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            List<JiraTicketMatch> matches = JiraTicketExtractor.ExtractTickets(
                text, context?.ValidJiraNumbers);
            foreach (JiraTicketMatch m in matches)
            {
                if (!seen.Add(m.JiraKey)) continue;
                results.Add(new JiraXRefRecord
                {
                    Id = JiraXRefRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    LinkType = "mentions",
                    Context = m.Context,
                    JiraKey = m.JiraKey,
                });
            }
        }
        return results;
    }
}
