namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

/// <summary>
/// Thrown by the <c>ticket-topics-clean-v1</c> migration when re-deriving
/// <see cref="FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(string?)"/>
/// over <c>prepared_ticket_topics.WorkGroupDisplay</c> would collapse two
/// or more rows onto a single
/// <c>(WorkGroupClean, Specification, Type, ShortDescription)</c> tuple,
/// violating the
/// <c>idx_prepared_ticket_topics_partition_short</c> UNIQUE index. The
/// sentinel is left un-written so re-running the migration after the
/// operator resolves the duplicate picks up where it stopped.
/// </summary>
public sealed class WorkGroupCleanReslugAbortedException : Exception
{
    public WorkGroupCleanReslugAbortedException(string message)
        : base(message) { }

    public WorkGroupCleanReslugAbortedException(string message, Exception innerException)
        : base(message, innerException) { }
}
