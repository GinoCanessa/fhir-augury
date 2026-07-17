namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class TicketPlanDbModeContractTests
{
    [Fact]
    public void SkillDocumentsDbModeAndCanonicalReposFlag()
    {
        string skill = File.ReadAllText(FindSkillPath());

        Assert.Contains("--db <path>", skill, StringComparison.Ordinal);
        Assert.Contains("--repos <json-array>", skill, StringComparison.Ordinal);
        Assert.Contains("planned_tickets", skill, StringComparison.Ordinal);
        Assert.Contains("planned_ticket_repos", skill, StringComparison.Ordinal);
        Assert.Contains("planned_ticket_repo_changes", skill, StringComparison.Ordinal);
        Assert.Contains("planned_ticket_repo_impacts", skill, StringComparison.Ordinal);
        Assert.Contains("planned_ticket_change_validations", skill, StringComparison.Ordinal);
        Assert.Contains("planned_ticket_testing_considerations", skill, StringComparison.Ordinal);
        Assert.Contains("planned_ticket_open_questions", skill, StringComparison.Ordinal);
        Assert.Contains("Resolution Summary", skill, StringComparison.Ordinal);
        Assert.Contains("ReplacementLines", skill, StringComparison.Ordinal);
        Assert.Contains("RepoRevision", skill, StringComparison.Ordinal);
    }

    private static string FindSkillPath()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".github", "skills", "ticket-plan", "SKILL.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find .github/skills/ticket-plan/SKILL.md from the test working directory.");
    }
}
