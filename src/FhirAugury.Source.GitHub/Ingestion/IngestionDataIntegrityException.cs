namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Raised when a GitHub-source ingestion pass that is <b>configured and
/// expected to produce rows</b> instead produces none — for example a
/// configured HL7 work-group refresh that materializes zero work groups, or a
/// spec-file parse that finds files on disk but indexes nothing.
/// </summary>
/// <remarks>
/// This exception signals a silent data-integrity defect, not a transient
/// per-item failure. Unlike ordinary ingestion errors (which the pipeline logs
/// and continues past), this type <b>must propagate</b>: catch blocks that
/// otherwise swallow exceptions are required to rethrow it so the ingestion run
/// fails loudly. Do not catch-and-log this without rethrowing.
/// </remarks>
public sealed class IngestionDataIntegrityException : Exception
{
    public IngestionDataIntegrityException(string message)
        : base(message)
    {
    }
}
