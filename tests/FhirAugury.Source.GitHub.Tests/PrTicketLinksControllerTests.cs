using System.Text.Json;
using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 4 (slot 0626-02): the PR↔ticket read surface. <c>pr-tickets/{*key}</c>
/// returns the tickets linked to a PR; <c>ticket-prs/{jiraKey}</c> returns the
/// PRs linked to a ticket. Both read the projected <c>github_pr_ticket_links</c>
/// edge table.
/// </summary>
public class PrTicketLinksControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly PrTicketLinksController _controller;

    public PrTicketLinksControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pr_ticket_ctrl_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _controller = new PrTicketLinksController(_db);

        using SqliteConnection conn = _db.OpenConnection();
        InsertEdge(conn, "HL7/fhir", 4213, "FHIR-1", "description");
        InsertEdge(conn, "HL7/fhir", 4213, "FHIR-2", "comment,description");
        InsertEdge(conn, "HL7/fhir", 9001, "FHIR-1", "commit");
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private static void InsertEdge(SqliteConnection conn, string repo, int pr, string jiraKey, string provenance)
    {
        GitHubPrTicketLinkRecord.Insert(conn, new GitHubPrTicketLinkRecord
        {
            Id = GitHubPrTicketLinkRecord.GetIndex(),
            RepoFullName = repo,
            PrNumber = pr,
            PrUniqueKey = $"{repo}#{pr}",
            JiraKey = jiraKey,
            Provenance = provenance,
        }, ignoreDuplicates: true);
    }

    private static JsonElement ToJson(IActionResult result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        string json = JsonSerializer.Serialize(ok.Value);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void GetTicketsForPr_ReturnsLinkedTickets_WithProvenance()
    {
        JsonElement root = ToJson(_controller.GetTicketsForPr("HL7/fhir#4213"));

        Assert.Equal("HL7/fhir#4213", root.GetProperty("pr").GetString());
        JsonElement tickets = root.GetProperty("tickets");
        Assert.Equal(2, tickets.GetArrayLength());
        Assert.Equal("FHIR-1", tickets[0].GetProperty("jiraKey").GetString());
        Assert.Equal("description", tickets[0].GetProperty("provenance").GetString());
        Assert.Equal("FHIR-2", tickets[1].GetProperty("jiraKey").GetString());
    }

    [Fact]
    public void GetPrsForTicket_ReturnsLinkedPrs_WithProvenance()
    {
        JsonElement root = ToJson(_controller.GetPrsForTicket("FHIR-1"));

        Assert.Equal("FHIR-1", root.GetProperty("ticket").GetString());
        JsonElement prs = root.GetProperty("prs");
        Assert.Equal(2, prs.GetArrayLength());
        Assert.Equal("HL7/fhir#4213", prs[0].GetProperty("pr").GetString());
        Assert.Equal("HL7/fhir#9001", prs[1].GetProperty("pr").GetString());
    }

    [Fact]
    public void GetTicketsForPr_InvalidKey_ReturnsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(_controller.GetTicketsForPr("not-a-key"));
    }

    [Fact]
    public void GetPrsForTicket_UnknownTicket_ReturnsEmpty()
    {
        JsonElement root = ToJson(_controller.GetPrsForTicket("FHIR-99999"));
        Assert.Equal(0, root.GetProperty("prs").GetArrayLength());
    }
}
