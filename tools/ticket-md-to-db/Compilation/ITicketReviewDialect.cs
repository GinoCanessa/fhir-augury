namespace FhirAugury.Tools.TicketMdToDb.Compilation;

public interface ITicketReviewDialect
{
    string Id { get; }

    bool Recognizes(TicketReviewDocument document);

    ImportedTicketReview Parse(TicketReviewDocument document);
}

internal static class TicketReviewDialectHelpers
{
    public static void SetSectionScalar(
        ImportedTicketReview review,
        TicketReviewDocument document,
        string field,
        TicketReviewSection? section,
        string ruleId)
    {
        string body = document.Body(section);
        review.Scalars[field] = string.IsNullOrWhiteSpace(body) || section is null
            ? ImportedScalar.Missing(field)
            : ImportedScalar.Present(field, body, document.Evidence(section, ruleId));
    }

    public static void SetScalar(
        ImportedTicketReview review,
        TicketReviewDocument document,
        string field,
        string? value,
        TicketReviewSection? section,
        string ruleId,
        ImportedValueState missingState = ImportedValueState.Missing)
    {
        if (!string.IsNullOrWhiteSpace(value) && section is not null)
        {
            review.Scalars[field] = ImportedScalar.Present(
                field,
                value.Trim(),
                document.Evidence(section, ruleId));
            return;
        }

        review.Scalars[field] = missingState == ImportedValueState.Ambiguous
            ? ImportedScalar.Ambiguous(field, value ?? string.Empty)
            : ImportedScalar.Missing(field);
    }
}
