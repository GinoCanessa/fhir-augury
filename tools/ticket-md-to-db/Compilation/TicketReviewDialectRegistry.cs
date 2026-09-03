namespace FhirAugury.Tools.TicketMdToDb.Compilation;

public sealed class TicketReviewDialectRegistry
{
    private readonly IReadOnlyList<ITicketReviewDialect> _dialects;

    public TicketReviewDialectRegistry(IEnumerable<ITicketReviewDialect>? dialects = null)
    {
        _dialects = (dialects ?? CreateDefault()).ToArray();
    }

    public DialectSelection Select(TicketReviewDocument document)
    {
        ITicketReviewDialect[] matches = _dialects
            .Where(dialect => dialect.Recognizes(document))
            .ToArray();
        return matches.Length switch
        {
            1 => new DialectSelection(matches[0], null),
            0 => new DialectSelection(
                null,
                ImportDiagnostic.Blocking(
                    "unknown-layout",
                    "No ticket-review dialect recognized this document.",
                    document.Source.RelativePath,
                    document.Source.Key)),
            _ => new DialectSelection(
                null,
                ImportDiagnostic.Blocking(
                    "ambiguous-layout",
                    $"Multiple dialects recognized this document: {string.Join(", ", matches.Select(match => match.Id))}.",
                    document.Source.RelativePath,
                    document.Source.Key)),
        };
    }

    private static IEnumerable<ITicketReviewDialect> CreateDefault()
    {
        yield return new CanonicalTicketReviewDialect();
        yield return new LegacyTicketReviewDialect10333();
        yield return new LegacyTicketReviewDialect10654();
        yield return new LegacyTicketReviewDialect12563();
        yield return new LegacyTicketReviewDialect13634();
        yield return new LegacyTicketReviewDialect16662();
        yield return new LegacyTicketReviewDialect17156();
    }
}

public sealed record DialectSelection(
    ITicketReviewDialect? Dialect,
    ImportDiagnostic? Diagnostic);
