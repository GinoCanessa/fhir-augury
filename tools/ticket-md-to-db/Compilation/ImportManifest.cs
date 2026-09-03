using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Tools.TicketMdToDb.Mapping;
using FhirAugury.Tools.TicketMdToDb.Overrides;

namespace FhirAugury.Tools.TicketMdToDb.Compilation;

public sealed record ImportDiagnostic(
    string Code,
    string Message,
    bool IsBlocking,
    string? SourcePath,
    string? TicketKey)
{
    public static ImportDiagnostic Blocking(
        string code,
        string message,
        string? sourcePath = null,
        string? ticketKey = null) =>
        new(code, message, true, sourcePath, ticketKey);

    public static ImportDiagnostic NonBlocking(
        string code,
        string message,
        string? sourcePath = null,
        string? ticketKey = null) =>
        new(code, message, false, sourcePath, ticketKey);
}

public sealed record ManifestSourceFile(
    string Key,
    string RelativePath,
    string SourceSha256);

public sealed record ManifestScalar(
    string Field,
    ImportedValueState SourceState,
    string? SourceValue,
    string AppliedValue,
    IReadOnlyList<SourceEvidence> Evidence);

public sealed record ManifestChildEvidence(
    string Kind,
    string Identity,
    string? LinkType,
    bool Unsupported,
    IReadOnlyList<string> Justifications,
    IReadOnlyList<SourceEvidence> Evidence);

public sealed record ManifestTicket(
    string Key,
    string DialectId,
    string SourcePath,
    string SourceSha256,
    PreparedTicketPayload Payload,
    IReadOnlyList<ManifestScalar> Scalars,
    IReadOnlyList<ManifestChildEvidence> ChildEvidence,
    IReadOnlyList<ClassifiedSourceRange> SourceRanges,
    IReadOnlyList<AppliedImportOverride> Overrides);

public sealed class ImportManifest
{
    public const string CurrentParserSchemaVersion = "ticket-md-to-db/1";

    public required string ParserSchemaVersion { get; init; }
    public required string ManifestId { get; init; }
    public required IReadOnlyList<ManifestSourceFile> Files { get; init; }
    public required IReadOnlyList<ManifestTicket> Tickets { get; init; }
    public required IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; }

    public bool IsValid => Diagnostics.All(diagnostic => !diagnostic.IsBlocking);

    public static ImportManifest Create(
        IReadOnlyList<ManifestSourceFile> files,
        IReadOnlyList<ManifestTicket> tickets,
        IReadOnlyList<ImportDiagnostic> diagnostics)
    {
        ManifestSourceFile[] sortedFiles = files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ThenBy(file => file.Key, StringComparer.Ordinal)
            .ToArray();
        ManifestTicket[] sortedTickets = tickets
            .OrderBy(ticket => ticket.Key, StringComparer.Ordinal)
            .ThenBy(ticket => ticket.SourcePath, StringComparer.Ordinal)
            .ToArray();
        ImportDiagnostic[] sortedDiagnostics = diagnostics
            .OrderBy(diagnostic => diagnostic.SourcePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.TicketKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();
        var identityInput = new
        {
            parserSchemaVersion = CurrentParserSchemaVersion,
            files = sortedFiles,
            tickets = sortedTickets,
            diagnostics = sortedDiagnostics,
        };
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            identityInput,
            DeterministicJson.Options);
        string id = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        return new ImportManifest
        {
            ParserSchemaVersion = CurrentParserSchemaVersion,
            ManifestId = id,
            Files = sortedFiles,
            Tickets = sortedTickets,
            Diagnostics = sortedDiagnostics,
        };
    }
}

public sealed record CompilationRequest(
    string InputRoot,
    int ExpectedCount,
    string? OverridesPath = null);

public sealed record CompilationResult(
    string InputRoot,
    ImportManifest Manifest,
    ImportOverrideTemplate OverrideTemplate)
{
    public bool Success => Manifest.IsValid;
}

public static class ReportCompiler
{
    public static CompilationResult Compile(CompilationRequest request)
    {
        ReportDiscoveryResult discovery = ReportDiscovery.Discover(
            request.InputRoot,
            request.ExpectedCount);
        ImportOverrideSet overrides = ImportOverrideLoader.Load(
            request.OverridesPath,
            discovery.Reports);
        List<ImportDiagnostic> diagnostics =
        [
            .. discovery.Diagnostics,
            .. overrides.Diagnostics,
        ];
        List<ManifestTicket> tickets = [];
        TicketReviewDialectRegistry registry = new();

        foreach (DiscoveredReport report in discovery.Reports)
        {
            try
            {
                TicketReviewDocument document = TicketReviewDocument.Parse(report);
                DialectSelection selection = registry.Select(document);
                if (selection.Diagnostic is not null || selection.Dialect is null)
                {
                    diagnostics.Add(selection.Diagnostic!);
                    continue;
                }

                ImportedTicketReview review = selection.Dialect.Parse(document);
                diagnostics.AddRange(review.Diagnostics);
                ImportOverrideEntry? ticketOverride = overrides.Entries.GetValueOrDefault(report.Key);
                PreparedTicketMappingResult mapped = PreparedTicketImportMapper.Map(
                    review,
                    ticketOverride,
                    document.Markdown.Length);
                diagnostics.AddRange(mapped.Diagnostics);
                tickets.Add(mapped.Ticket);
            }
            catch (Exception ex)
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "report-compile-failed",
                    $"{ex.GetType().Name}: {ex.Message}",
                    report.RelativePath,
                    report.Key));
            }
        }

        ImportManifest manifest = ImportManifest.Create(
            discovery.Reports.Select(report => new ManifestSourceFile(
                    report.Key,
                    report.RelativePath,
                    report.SourceSha256))
                .ToArray(),
            tickets,
            diagnostics);
        return new CompilationResult(
            discovery.InputRoot,
            manifest,
            BuildOverrideTemplate(manifest));
    }

    private static ImportOverrideTemplate BuildOverrideTemplate(ImportManifest manifest)
    {
        Dictionary<string, ImportOverrideTemplateEntry> tickets = new(StringComparer.Ordinal);
        foreach (ManifestTicket ticket in manifest.Tickets.OrderBy(ticket => ticket.Key, StringComparer.Ordinal))
        {
            Dictionary<string, string?> fields = ticket.Scalars
                .Where(scalar => scalar.SourceState is not ImportedValueState.Present)
                .OrderBy(scalar => scalar.Field, StringComparer.Ordinal)
                .ToDictionary(
                    scalar => scalar.Field,
                    scalar => (string?)scalar.AppliedValue,
                    StringComparer.Ordinal);
            if (fields.Count == 0)
            {
                continue;
            }

            tickets[ticket.Key] = new ImportOverrideTemplateEntry(
                ticket.SourceSha256,
                string.Empty,
                fields);
        }

        return new ImportOverrideTemplate(tickets);
    }
}

internal static class DeterministicJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
