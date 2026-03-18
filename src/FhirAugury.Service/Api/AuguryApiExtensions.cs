namespace FhirAugury.Service.Api;

/// <summary>Registers all API endpoint groups on the WebApplication.</summary>
public static class AuguryApiExtensions
{
    public static WebApplication MapAuguryApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        IngestEndpoints.Map(api);
        SearchEndpoints.Map(api);
        JiraEndpoints.Map(api);
        ZulipEndpoints.Map(api);
        XRefEndpoints.Map(api);
        StatsEndpoints.Map(api);

        return app;
    }
}
