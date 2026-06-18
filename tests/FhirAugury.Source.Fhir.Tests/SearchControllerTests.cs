using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Controllers;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Indexing;
using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Fhir.Tests;

public class SearchControllerTests
{
    private sealed class Harness : IDisposable
    {
        public SearchController Controller { get; }
        private readonly FhirSpecFixture _spec = new();
        private readonly FhirSearchDatabase _search;
        private readonly string _searchPath;

        public Harness()
        {
            _searchPath = Path.Combine(Path.GetTempPath(), $"fhir-fts-{Guid.NewGuid():N}.db");
            _search = new FhirSearchDatabase(_searchPath, NullLogger<FhirSearchDatabase>.Instance);
            _search.Initialize();

            FhirSpecDatabase specDb = _spec.CreateDatabase();
            new FhirSearchIndexBuilder(specDb, _search, NullLogger<FhirSearchIndexBuilder>.Instance).Build();

            FhirReleaseResolver resolver = new(specDb);
            Controller = new SearchController(resolver, new FhirSearchReader(_search));
        }

        public void Dispose()
        {
            _search.Dispose();
            TestFileCleanup.SafeDeleteFile(_searchPath);
            _spec.Dispose();
        }
    }

    private static FhirReleaseResponse<FhirSearchResponse> OkBody(IActionResult result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<FhirReleaseResponse<FhirSearchResponse>>(ok.Value);
    }

    [Fact]
    public void Search_ReturnsRankedHits_ForRelease()
    {
        using Harness h = new();

        FhirReleaseResponse<FhirSearchResponse> body = OkBody(h.Controller.Search("R5", "observation"));

        Assert.Equal("R5", body.Release.ShortName);
        Assert.NotEmpty(body.Result.Hits);
        Assert.Contains(body.Result.Hits, hit => hit is { Kind: "structure", Name: "Observation" });
        // Every hit belongs to the requested release.
        Assert.All(body.Result.Hits, hit => Assert.Equal("R5", hit.Release));
        // Hits are ordered by descending score.
        double[] scores = body.Result.Hits.Select(hit => hit.Score).ToArray();
        Assert.True(scores.SequenceEqual(scores.OrderByDescending(s => s)));
    }

    [Fact]
    public void Search_TypesFilter_RestrictsKind()
    {
        using Harness h = new();

        FhirReleaseResponse<FhirSearchResponse> body =
            OkBody(h.Controller.Search("R5", "observation", types: "searchparameter"));

        Assert.NotEmpty(body.Result.Hits);
        Assert.All(body.Result.Hits, hit => Assert.Equal("searchparameter", hit.Kind));
    }

    [Fact]
    public void Search_MissingQuery_BadRequest()
    {
        using Harness h = new();
        Assert.IsType<BadRequestObjectResult>(h.Controller.Search("R5", null));
    }

    [Fact]
    public void Search_UnknownRelease_NotFound()
    {
        using Harness h = new();
        Assert.IsType<NotFoundObjectResult>(h.Controller.Search("R99", "observation"));
    }

    [Fact]
    public void Search_NoMatches_ReturnsEmpty()
    {
        using Harness h = new();

        FhirReleaseResponse<FhirSearchResponse> body =
            OkBody(h.Controller.Search("R5", "zzzznomatch"));

        Assert.Equal(0, body.Result.Count);
        Assert.Empty(body.Result.Hits);
    }
}
