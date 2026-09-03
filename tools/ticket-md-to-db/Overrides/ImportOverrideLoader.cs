using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Tools.TicketMdToDb.Compilation;

namespace FhirAugury.Tools.TicketMdToDb.Overrides;

public static class ImportOverrideLoader
{
    public static ImportOverrideSet Load(
        string? path,
        IReadOnlyList<DiscoveredReport> reports)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ImportOverrideSet.Empty;
        }

        string absolutePath = Path.GetFullPath(path);
        if (!File.Exists(absolutePath))
        {
            return new ImportOverrideSet(
                new Dictionary<string, ImportOverrideEntry>(StringComparer.Ordinal),
                [ImportDiagnostic.Blocking("overrides-missing", $"Overrides file does not exist: {absolutePath}")]);
        }

        Dictionary<string, DiscoveredReport> byKey = reports
            .GroupBy(report => report.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<string, ImportOverrideEntry> entries = new(StringComparer.Ordinal);
        List<ImportDiagnostic> diagnostics = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(absolutePath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "overrides-root-invalid",
                    "Overrides root must be an object."));
                return new ImportOverrideSet(entries, diagnostics);
            }

            JsonElement? tickets = null;
            HashSet<string> rootNames = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!rootNames.Add(property.Name))
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-duplicate-property",
                        $"Duplicate root property '{property.Name}'."));
                    continue;
                }

                if (!string.Equals(property.Name, "tickets", StringComparison.Ordinal))
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-unknown-property",
                        $"Unknown root property '{property.Name}'."));
                    continue;
                }

                tickets = property.Value;
            }

            if (tickets is null || tickets.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "overrides-tickets-missing",
                    "Overrides must contain a 'tickets' object."));
                return new ImportOverrideSet(entries, diagnostics);
            }

            HashSet<string> ticketNames = new(StringComparer.Ordinal);
            foreach (JsonProperty ticketProperty in tickets.Value.EnumerateObject())
            {
                string ticketKey = ticketProperty.Name;
                int diagnosticStart = diagnostics.Count;
                if (!ticketNames.Add(ticketKey))
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-duplicate-ticket",
                        $"Duplicate override ticket '{ticketKey}'.",
                        ticketKey: ticketKey));
                    continue;
                }
                if (!byKey.TryGetValue(ticketKey, out DiscoveredReport? report))
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-unknown-ticket",
                        $"Override ticket '{ticketKey}' was not discovered.",
                        ticketKey: ticketKey));
                    continue;
                }
                if (ticketProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-ticket-invalid",
                        $"Override ticket '{ticketKey}' must be an object.",
                        report.RelativePath,
                        ticketKey));
                    continue;
                }

                string? sourceSha256 = null;
                string? reviewReason = null;
                Dictionary<string, string> fields = new(StringComparer.Ordinal);
                HashSet<string> entryNames = new(StringComparer.Ordinal);
                foreach (JsonProperty entryProperty in ticketProperty.Value.EnumerateObject())
                {
                    if (!entryNames.Add(entryProperty.Name))
                    {
                        diagnostics.Add(ImportDiagnostic.Blocking(
                            "overrides-duplicate-property",
                            $"Duplicate property '{entryProperty.Name}' for '{ticketKey}'.",
                            report.RelativePath,
                            ticketKey));
                        continue;
                    }

                    switch (entryProperty.Name)
                    {
                        case "sourceSha256":
                            sourceSha256 = ReadString(entryProperty.Value);
                            break;
                        case "reviewReason":
                            reviewReason = ReadString(entryProperty.Value);
                            break;
                        case "fields":
                            ReadFields(entryProperty.Value, report, fields, diagnostics);
                            break;
                        default:
                            diagnostics.Add(ImportDiagnostic.Blocking(
                                "overrides-unknown-property",
                                $"Unknown property '{entryProperty.Name}' for '{ticketKey}'.",
                                report.RelativePath,
                                ticketKey));
                            break;
                    }
                }

                if (!string.Equals(sourceSha256, report.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-stale-fingerprint",
                        $"Override fingerprint for '{ticketKey}' does not match the source report.",
                        report.RelativePath,
                        ticketKey));
                }
                if (string.IsNullOrWhiteSpace(reviewReason))
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-review-reason-missing",
                        $"Override ticket '{ticketKey}' requires a non-empty reviewReason.",
                        report.RelativePath,
                        ticketKey));
                }
                if (fields.Count == 0)
                {
                    diagnostics.Add(ImportDiagnostic.Blocking(
                        "overrides-fields-empty",
                        $"Override ticket '{ticketKey}' requires at least one field.",
                        report.RelativePath,
                        ticketKey));
                }

                if (diagnostics.Count == diagnosticStart)
                {
                    entries[ticketKey] = new ImportOverrideEntry(
                        ticketKey,
                        sourceSha256!,
                        reviewReason!,
                        fields);
                }
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "overrides-json-invalid",
                $"{ex.GetType().Name}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "overrides-read-failed",
                $"{ex.GetType().Name}: {ex.Message}"));
        }

        return new ImportOverrideSet(entries, diagnostics);
    }

    private static void ReadFields(
        JsonElement fieldsElement,
        DiscoveredReport report,
        Dictionary<string, string> fields,
        List<ImportDiagnostic> diagnostics)
    {
        if (fieldsElement.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "overrides-fields-invalid",
                $"Override fields for '{report.Key}' must be an object.",
                report.RelativePath,
                report.Key));
            return;
        }

        HashSet<string> fieldNames = new(StringComparer.Ordinal);
        foreach (JsonProperty fieldProperty in fieldsElement.EnumerateObject())
        {
            if (!fieldNames.Add(fieldProperty.Name))
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "overrides-duplicate-field",
                    $"Duplicate override field '{fieldProperty.Name}' for '{report.Key}'.",
                    report.RelativePath,
                    report.Key));
                continue;
            }
            if (!PreparedTicketFieldNames.Overrideable.Contains(fieldProperty.Name))
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "overrides-unknown-field",
                    $"Field '{fieldProperty.Name}' cannot be overridden.",
                    report.RelativePath,
                    report.Key));
                continue;
            }

            string? value = ReadString(fieldProperty.Value);
            if (value is null)
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "overrides-value-invalid",
                    $"Override field '{fieldProperty.Name}' for '{report.Key}' must be a string.",
                    report.RelativePath,
                    report.Key));
                continue;
            }
            if ((fieldProperty.Name is PreparedTicketFieldNames.ProposalAImpact or PreparedTicketFieldNames.ProposalBImpact)
                && !PreparedTicketImpactValues.Supported.Contains(value))
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "overrides-impact-invalid",
                    $"Override impact '{value}' is not supported.",
                    report.RelativePath,
                    report.Key));
                continue;
            }
            if (fieldProperty.Name == PreparedTicketFieldNames.Recommendation
                && !PreparedTicketRecommendationValues.Supported.Contains(value))
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "overrides-recommendation-invalid",
                    $"Override recommendation '{value}' is not supported.",
                    report.RelativePath,
                    report.Key));
                continue;
            }

            fields[fieldProperty.Name] = value;
        }
    }

    private static string? ReadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
