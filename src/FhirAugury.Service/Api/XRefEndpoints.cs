using FhirAugury.Database;
using FhirAugury.Indexing;

namespace FhirAugury.Service.Api;

/// <summary>Cross-reference endpoints.</summary>
public static class XRefEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var xref = group.MapGroup("/xref");

        xref.MapGet("/{source}/{id}", GetRelated);
    }

    private static IResult GetRelated(
        string source,
        string id,
        DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();

        var links = CrossRefQueryService.GetRelatedItems(conn, source, id);
        var similar = SimilaritySearchService.FindRelated(conn, source, id, 20);

        return Results.Ok(new
        {
            Source = source,
            Id = id,
            CrossReferences = links.Select(l => new
            {
                l.SourceType,
                l.SourceId,
                l.TargetType,
                l.TargetId,
                l.LinkType,
                l.Context,
            }),
            RelatedItems = similar,
        });
    }
}
