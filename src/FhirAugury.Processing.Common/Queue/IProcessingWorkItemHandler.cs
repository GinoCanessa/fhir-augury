namespace FhirAugury.Processing.Common.Queue;

/// <summary>
/// Processor-specific handler invoked for claimed work items. Pending work is <c>ProcessingStatus IS NULL</c>;
/// in-flight is <c>in-progress</c>, complete is <c>complete</c>, and failed is <c>error</c>.
/// </summary>
public interface IProcessingWorkItemHandler<TItem>
{
    Task ProcessAsync(TItem item, CancellationToken ct);
}
