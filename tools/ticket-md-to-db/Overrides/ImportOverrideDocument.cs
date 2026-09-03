using FhirAugury.Tools.TicketMdToDb.Compilation;

namespace FhirAugury.Tools.TicketMdToDb.Overrides;

public sealed record ImportOverrideEntry(
    string TicketKey,
    string SourceSha256,
    string ReviewReason,
    IReadOnlyDictionary<string, string> Fields);

public sealed class ImportOverrideSet
{
    public static ImportOverrideSet Empty { get; } = new(
        new Dictionary<string, ImportOverrideEntry>(StringComparer.Ordinal),
        []);

    public ImportOverrideSet(
        IReadOnlyDictionary<string, ImportOverrideEntry> entries,
        IReadOnlyList<ImportDiagnostic> diagnostics)
    {
        Entries = entries;
        Diagnostics = diagnostics;
    }

    public IReadOnlyDictionary<string, ImportOverrideEntry> Entries { get; }
    public IReadOnlyList<ImportDiagnostic> Diagnostics { get; }
}

public sealed record AppliedImportOverride(
    string TicketKey,
    string Field,
    string? PreviousValue,
    string NewValue,
    string SourceSha256,
    string ReviewReason);

public sealed record ImportOverrideTemplate(
    IReadOnlyDictionary<string, ImportOverrideTemplateEntry> Tickets);

public sealed record ImportOverrideTemplateEntry(
    string SourceSha256,
    string ReviewReason,
    IReadOnlyDictionary<string, string?> Fields);
