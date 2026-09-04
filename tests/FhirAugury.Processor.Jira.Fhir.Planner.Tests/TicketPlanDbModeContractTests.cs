namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class TicketPlanDbModeContractTests
{
    [Fact]
    public void SkillRequiresRunBackedSubmissionAndCanonicalReposFlag()
    {
        string skill = File.ReadAllText(FindSkillPath());

        Assert.Contains("--repos <json-array>", skill, StringComparison.Ordinal);
        Assert.Contains("PlannedTicketPayload", skill, StringComparison.Ordinal);
        Assert.Contains("observedSourceRevision", skill, StringComparison.Ordinal);
        Assert.Contains("Exact receipt gate", skill, StringComparison.Ordinal);
        Assert.Contains("Outer-control mode", skill, StringComparison.Ordinal);
        Assert.Contains("Worker mode", skill, StringComparison.Ordinal);
        Assert.DoesNotContain("--db", skill, StringComparison.Ordinal);
        Assert.DoesNotContain("planned_tickets", skill, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "direct SQLite",
            skill,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StagedWorkerCommandOmitsDatabaseAndRetainsRepoFilters()
    {
        string command =
            FhirAugury.Processor.Jira.Fhir.Planner.Configuration.PlannerJiraProcessingDefaults
                .AuthoringAgentCliCommand;

        Assert.Contains("{ticketKey}", command, StringComparison.Ordinal);
        Assert.Contains("{repoFilters}", command, StringComparison.Ordinal);
        Assert.DoesNotContain("{dbPath}", command, StringComparison.Ordinal);
        Assert.DoesNotContain("{operationToken}", command, StringComparison.Ordinal);
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
