using System.Text.Json;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Phase 5 lock-in for <see cref="JiraFieldMapper.MapBaldef"/>: BALDEF
/// custom fields populate the right columns and <c>BallotCode</c> is split
/// into <c>(BallotCycle, BallotPackageName)</c>.
/// </summary>
public class JiraFieldMapperBaldefTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void MapBaldef_PopulatesBallotCodeAndSplitsCycle()
    {
        string json = """
        {
            "key": "BALDEF-15",
            "fields": {
                "summary": "FHIR R5 Sep 2024",
                "created": "2024-06-01T10:00:00.000+0000",
                "updated": "2024-06-15T11:30:00.000+0000",
                "issuetype": { "name": "Ballot" },
                "priority": { "name": "Major" },
                "status": { "name": "Open" },
                "project": { "key": "BALDEF" },
                "customfield_11704": "2024-Sep | FHIR R5",
                "customfield_11604": "STU",
                "customfield_11302": "FHIR Core",
                "customfield_11606": "150",
                "customfield_11607": "120",
                "customfield_11608": "20",
                "customfield_11609": "10"
            }
        }
        """;

        JiraBaldefRecord rec = JiraFieldMapper.MapBaldef(Parse(json));

        Assert.Equal("BALDEF-15", rec.Key);
        Assert.Equal("BALDEF", rec.ProjectKey);
        Assert.Equal("2024-Sep | FHIR R5", rec.BallotCode);
        Assert.Equal("2024-Sep", rec.BallotCycle);
        Assert.Equal("FHIR R5", rec.BallotPackageName);
        Assert.Equal("STU", rec.BallotCategory);
        Assert.Equal("FHIR Core", rec.Specification);
        Assert.Equal(150, rec.VotersTotalEligible);
        Assert.Equal(120, rec.VotersAffirmative);
        Assert.Equal(20, rec.VotersNegative);
        Assert.Equal(10, rec.VotersAbstain);
    }

    [Fact]
    public void MapBaldef_BallotCodeWithoutPipe_LeavesCycleNull()
    {
        string json = """
        {
            "key": "BALDEF-16",
            "fields": {
                "summary": "no pipe",
                "created": "2024-06-01T10:00:00.000+0000",
                "updated": "2024-06-15T11:30:00.000+0000",
                "issuetype": { "name": "Ballot" },
                "priority": { "name": "Major" },
                "status": { "name": "Open" },
                "project": { "key": "BALDEF" },
                "customfield_11704": "FHIR R5"
            }
        }
        """;

        JiraBaldefRecord rec = JiraFieldMapper.MapBaldef(Parse(json));

        Assert.Equal("FHIR R5", rec.BallotCode);
        Assert.Null(rec.BallotCycle);
        Assert.Equal("FHIR R5", rec.BallotPackageName);
    }
}
