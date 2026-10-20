namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// The one place that decides "this run cannot continue". Every network catch
/// site routes its exception through here before recording a per-item failure.
/// </summary>
/// <remarks>
/// Two conditions end a run rather than an item: a credential the instance no
/// longer accepts (401/403), and an edge appliance challenging us (a WAF
/// <c>405</c>). <see cref="ConfluenceAuthFailure"/> keeps its narrow 401/403
/// meaning and is still the authority on credentials; this guard sits above it
/// so a second run-ending condition did not require re-teaching four catch
/// sites what "fatal" means.
/// </remarks>
public static class ConfluenceRunStop
{
    /// <summary>
    /// Rethrows when the exception means the whole run must stop, so callers can
    /// express "record this item and carry on, unless the run is over".
    /// </summary>
    public static void ThrowIfRunMustStop(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfluenceHumanInterventionRequiredException challenge)
            {
                throw challenge;
            }
        }

        ConfluenceAuthFailure.ThrowIfAuthFailure(exception);
    }
}
