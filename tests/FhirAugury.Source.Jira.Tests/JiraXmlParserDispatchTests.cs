using System.Text;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Phase 5 lock-in: <see cref="JiraXmlParser.ParseExport(Stream, IReadOnlyDictionary{string, JiraProjectShape})"/>
/// dispatches each item to the correct concrete <see cref="JiraParsedItem"/>
/// subtype based on the per-project shape map.
/// </summary>
public class JiraXmlParserDispatchTests
{
    [Fact]
    public void ParseExport_DispatchesEachItemToCorrectShape()
    {
        string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss>
          <channel>
            <item>
              <title>[FHIR-1] FHIR change</title>
              <summary>FHIR change</summary>
              <project key="FHIR">FHIR</project>
              <key id="1">FHIR-1</key>
              <type>Bug</type>
              <priority>Major</priority>
              <status>Open</status>
              <created>Mon, 1 Jul 2024 10:00:00 +0000</created>
              <updated>Tue, 2 Jul 2024 10:00:00 +0000</updated>
            </item>
            <item>
              <title>[PSS-1] A scope</title>
              <summary>A scope</summary>
              <project key="PSS">PSS</project>
              <key id="2">PSS-1</key>
              <type>Project Scope Statement</type>
              <priority>Major</priority>
              <status>Open</status>
              <created>Mon, 1 Jul 2024 10:00:00 +0000</created>
              <updated>Tue, 2 Jul 2024 10:00:00 +0000</updated>
            </item>
            <item>
              <title>[BALDEF-1] A package</title>
              <summary>A package</summary>
              <project key="BALDEF">BALDEF</project>
              <key id="3">BALDEF-1</key>
              <type>Ballot</type>
              <priority>Major</priority>
              <status>Open</status>
              <created>Mon, 1 Jul 2024 10:00:00 +0000</created>
              <updated>Tue, 2 Jul 2024 10:00:00 +0000</updated>
            </item>
            <item>
              <title>Affirmative - Jane Doe (Acme) : 2024-Sep | FHIR R5</title>
              <summary>Affirmative - Jane Doe (Acme) : 2024-Sep | FHIR R5</summary>
              <project key="BALLOT">BALLOT</project>
              <key id="4">BALLOT-1</key>
              <type>Vote</type>
              <priority>Major</priority>
              <status>Open</status>
              <created>Mon, 1 Jul 2024 10:00:00 +0000</created>
              <updated>Tue, 2 Jul 2024 10:00:00 +0000</updated>
            </item>
          </channel>
        </rss>
        """;

        Dictionary<string, JiraProjectShape> shapeMap = new Dictionary<string, JiraProjectShape>(StringComparer.OrdinalIgnoreCase)
        {
            ["FHIR"] = JiraProjectShape.FhirChangeRequest,
            ["PSS"] = JiraProjectShape.ProjectScopeStatement,
            ["BALDEF"] = JiraProjectShape.BallotDefinition,
            ["BALLOT"] = JiraProjectShape.BallotVote,
        };

        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        List<JiraParsedItem> items = JiraXmlParser.ParseExport(stream, shapeMap).ToList();

        Assert.Equal(4, items.Count);
        Assert.IsType<JiraParsedFhirIssue>(items[0]);
        Assert.IsType<JiraParsedProjectScopeStatement>(items[1]);
        Assert.IsType<JiraParsedBaldef>(items[2]);
        Assert.IsType<JiraParsedBallot>(items[3]);

        Assert.Equal("FHIR-1", items[0].Key);
        Assert.Equal("PSS-1", items[1].Key);
        Assert.Equal("BALDEF-1", items[2].Key);
        Assert.Equal("BALLOT-1", items[3].Key);

        // Ballot row should have parsed Voter / BallotCycle from the summary.
        JiraParsedBallot ballot = (JiraParsedBallot)items[3];
        Assert.Equal("Jane Doe", ballot.Record.Voter);
        Assert.Equal("2024-Sep", ballot.Record.BallotCycle);
    }

    [Fact]
    public void ParseExport_UnknownProject_DefaultsToFhirShape()
    {
        string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss>
          <channel>
            <item>
              <title>[ZZZ-1] mystery</title>
              <summary>mystery</summary>
              <project key="ZZZ">ZZZ</project>
              <key id="1">ZZZ-1</key>
              <type>Bug</type>
              <priority>Major</priority>
              <status>Open</status>
              <created>Mon, 1 Jul 2024 10:00:00 +0000</created>
              <updated>Tue, 2 Jul 2024 10:00:00 +0000</updated>
            </item>
          </channel>
        </rss>
        """;

        Dictionary<string, JiraProjectShape> shapeMap = new Dictionary<string, JiraProjectShape>();
        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        List<JiraParsedItem> items = JiraXmlParser.ParseExport(stream, shapeMap).ToList();

        Assert.Single(items);
        Assert.IsType<JiraParsedFhirIssue>(items[0]);
    }
}
