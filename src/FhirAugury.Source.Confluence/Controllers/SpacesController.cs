using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Confluence.Controllers;

[ApiController]
[Route("api/v1")]
public class SpacesController(ConfluenceDatabase db) : ControllerBase
{
    [HttpGet("spaces")]
    public IActionResult GetSpaces()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<ConfluenceSpaceRecord> spaces = ConfluenceSpaceRecord.SelectList(connection);

        return Ok(new
        {
            total = spaces.Count,
            spaces = spaces.Select(s => new { s.Key, s.Name, s.Description, s.Url, s.LastFetchedAt }),
        });
    }
}