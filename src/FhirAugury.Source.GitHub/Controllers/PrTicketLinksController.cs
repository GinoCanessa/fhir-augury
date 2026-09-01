using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

/// <summary>
/// Read surface over the first-class PR↔ticket edge table
/// (<c>github_pr_ticket_links</c>). Uses an <b>action-first</b> route layout
/// (<c>pr-tickets/{*key}</c>) so the catch-all PR key stays terminal and never
/// collides with <see cref="PullRequestsController"/>'s <c>pr/{*key}</c>. Jira
/// keys contain no slash, so <c>ticket-prs/{jiraKey}</c> is a plain segment.
/// </summary>
[ApiController]
[Route("api/v1/items")]
public class PrTicketLinksController(GitHubDatabase db) : ControllerBase
{
    /// <summary>Returns the distinct Jira tickets linked to a pull request, with provenance.</summary>
    [HttpGet("pr-tickets/{*key}")]
    public IActionResult GetTicketsForPr([FromRoute] string key)
    {
        string repo;
        int number;
        try
        {
            (repo, number) = GitHubUrlHelper.ParseIssueKey(key);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        string prUniqueKey = $"{repo}#{number}";

        using SqliteConnection connection = db.OpenConnection();
        List<GitHubPrTicketLinkRecord> links = GitHubPrTicketLinkRecord.SelectList(connection, PrUniqueKey: prUniqueKey);

        var tickets = links
            .OrderBy(l => l.JiraKey, StringComparer.Ordinal)
            .Select(l => new { jiraKey = l.JiraKey, provenance = l.Provenance })
            .ToList();

        return Ok(new
        {
            pr = prUniqueKey,
            url = GitHubUrlHelper.BuildIssueUrl(prUniqueKey),
            tickets,
        });
    }

    /// <summary>Returns the distinct pull requests linked to a Jira ticket, with provenance.</summary>
    [HttpGet("ticket-prs/{jiraKey}")]
    public IActionResult GetPrsForTicket([FromRoute] string jiraKey)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<GitHubPrTicketLinkRecord> links = GitHubPrTicketLinkRecord.SelectList(connection, JiraKey: jiraKey);

        var prs = links
            .OrderBy(l => l.RepoFullName, StringComparer.Ordinal)
            .ThenBy(l => l.PrNumber)
            .Select(l => new
            {
                pr = l.PrUniqueKey,
                url = GitHubUrlHelper.BuildIssueUrl(l.PrUniqueKey),
                provenance = l.Provenance,
            })
            .ToList();

        return Ok(new
        {
            ticket = jiraKey,
            prs,
        });
    }
}
