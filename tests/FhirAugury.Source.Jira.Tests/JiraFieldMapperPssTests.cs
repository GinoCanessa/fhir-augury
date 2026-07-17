using System.Text.Json;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Phase 5 lock-in for <see cref="JiraFieldMapper.MapProjectScopeStatement"/>:
/// PSS custom fields populate the right columns; FHIR-only customfields
/// (e.g. <c>customfield_11302</c> Specification on FHIR rows) are NOT read.
/// </summary>
public class JiraFieldMapperPssTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void MapProjectScopeStatement_PopulatesCoreAndCustomFields()
    {
        string json = """
        {
            "key": "PSS-77",
            "fields": {
                "summary": "Adopt FHIR R5",
                "description": "<p>desc</p>",
                "created": "2024-01-15T10:00:00.000+0000",
                "updated": "2024-02-20T11:30:00.000+0000",
                "issuetype": { "name": "Project Scope Statement" },
                "priority": { "name": "Major" },
                "status": { "name": "Approved" },
                "project": { "key": "PSS" },
                "customfield_14500": "FHIR Infrastructure",
                "customfield_12802": "<p>Build a thing</p>",
                "customfield_12316": "2024-03-01T00:00:00.000+0000",
                "customfield_13704": "Universal"
            }
        }
        """;

        JiraProjectScopeStatementRecord rec = JiraFieldMapper.MapProjectScopeStatement(Parse(json));

        Assert.Equal("PSS-77", rec.Key);
        Assert.Equal("PSS", rec.ProjectKey);
        Assert.Equal("Adopt FHIR R5", rec.Title);
        Assert.Equal("Approved", rec.Status);
        Assert.Equal("Major", rec.Priority);
        Assert.Equal("FHIR Infrastructure", rec.SponsoringWorkGroup);
        Assert.Equal("<p>Build a thing</p>", rec.ProjectDescription);
        Assert.Equal("Build a thing", rec.ProjectDescriptionPlain);
        Assert.NotNull(rec.ApprovalDate);
        Assert.Equal(2024, rec.ApprovalDate!.Value.Year);
        Assert.Equal("Universal", rec.Realm);
    }

    [Fact]
    public void MapProjectScopeStatement_IgnoresFhirOnlyCustomFields()
    {
        // customfield_11302 = Specification on FHIR rows; PSS mapper must NOT
        // pick it up (PSS has no Specification column).
        // customfield_11400 = WorkGroup on FHIR rows; PSS has SponsoringWorkGroup
        // (customfield_14500) instead.
        string json = """
        {
            "key": "PSS-200",
            "fields": {
                "summary": "Some PSS",
                "created": "2024-01-15T10:00:00.000+0000",
                "updated": "2024-02-20T11:30:00.000+0000",
                "issuetype": { "name": "Project Scope Statement" },
                "priority": { "name": "Major" },
                "status": { "name": "Open" },
                "project": { "key": "PSS" },
                "customfield_11302": "FHIR Core",
                "customfield_11400": "FHIR-I",
                "customfield_14500": "Patient Care"
            }
        }
        """;

        JiraProjectScopeStatementRecord rec = JiraFieldMapper.MapProjectScopeStatement(Parse(json));

        // SponsoringWorkGroup pulled from 14500.
        Assert.Equal("Patient Care", rec.SponsoringWorkGroup);
        // PSS has no Specification or WorkGroup column at all — verifies via record shape.
        string[] propertyNames = typeof(JiraProjectScopeStatementRecord)
            .GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Specification", propertyNames);
        Assert.DoesNotContain("WorkGroup", propertyNames);
    }

    [Fact]
    public void MapProjectScopeStatement_HtmlTableForCoSponsoringWorkGroups_StripsToCsv()
    {
        string json = """
        {
            "key": "PSS-300",
            "fields": {
                "summary": "Co-sponsor test",
                "created": "2024-01-15T10:00:00.000+0000",
                "updated": "2024-02-20T11:30:00.000+0000",
                "issuetype": { "name": "PSS" },
                "priority": { "name": "Minor" },
                "status": { "name": "Open" },
                "project": { "key": "PSS" },
                "customfield_14501": "<table><tr><td>Patient Care</td></tr><tr><td>Orders</td></tr></table>"
            }
        }
        """;

        JiraProjectScopeStatementRecord rec = JiraFieldMapper.MapProjectScopeStatement(Parse(json));

        Assert.NotNull(rec.CoSponsoringWorkGroups);
        Assert.Contains("Patient Care", rec.CoSponsoringWorkGroups);
        Assert.Contains("Orders", rec.CoSponsoringWorkGroups);
    }
}
