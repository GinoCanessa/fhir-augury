using System.Text.Json;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

public class JiraPublicPeopleObservationReaderTests
{
    private const string Revision = "2026-09-15T17:00:00+00:00";
    private const string Origin = "source-acquisition-fingerprint";

    [Fact]
    public void Read_AbsentRoleDiffersFromMissingField()
    {
        JiraPeopleObservation json = Read(Json("""
            "assignee": null, "customfield_11000": []
            """));
        Assert.Equal(JiraPeopleFieldState.Missing, json.Reporter.State);
        Assert.Equal(JiraPeopleFieldState.Absent, json.Assignee.State);
        Assert.Equal(JiraPeopleFieldState.Absent, json.Requesters.State);

        JiraPeopleObservation xml = Read("""
            <rss><channel><item><key>FHIR-1</key>
            <updated>Tue, 15 Sep 2026 12:00:00 -0500</updated>
            <assignee username="-1">Unassigned</assignee>
            </item></channel></rss>
            """, JiraPeopleFormat.Xml);
        Assert.Equal(JiraPeopleFieldState.Missing, xml.Reporter.State);
        Assert.Equal(JiraPeopleFieldState.Absent, xml.Assignee.State);
        Assert.Equal(JiraPeopleFieldState.Missing, xml.Requesters.State);
    }

    [Fact]
    public void Read_XmlRequesterUsernameDoesNotSupplyExplicitName()
    {
        JiraPeopleObservation observation = Read("""
            <rss><channel><item><key>FHIR-1</key>
            <updated>Tue, 15 Sep 2026 17:00:00 +0000</updated>
            <customfields><customfield id="customfield_11000"><customfieldvalues>
            <customfieldvalue>private-requester</customfieldvalue>
            </customfieldvalues></customfield></customfields>
            </item></channel></rss>
            """, JiraPeopleFormat.Xml);
        JiraPersonObservation requester = Assert.Single(observation.Requesters.People);
        Assert.Equal("private-requester", requester.Identity);
        Assert.Null(requester.ExplicitName);
    }

    [Fact]
    public void Read_ConflictingSameRevisionObservationsRefuses()
    {
        JiraPeopleEvidence evidence = Evidence(
            new(Json(Reporter("private-account", "First Name")), JiraPeopleFormat.Json, Origin),
            new(Json(Reporter("private-account", "Other Name"),
                "2026-09-15T12:00:00-05:00"), JiraPeopleFormat.Json, Origin));
        JiraPeopleRevisionObservation result = JiraPublicPeopleObservationReader.ReadForRevision(evidence, "FHIR-1", Revision);
        Assert.Equal(JiraPeopleFieldState.Conflict, Assert.IsType<JiraPeopleObservation>(result.Observation).Reporter.State);
        Assert.Equal(JiraPublicPeopleCodes.ConflictingObservations,
            JiraPublicPeopleObservationReader.FieldReason(result.Observation.Reporter.State));
    }

    [Fact]
    public void Read_UpstreamDifferentRevisionRefuses()
    {
        JiraPeopleEvidence evidence = JiraPublicPeopleObservationReader.Read(
            [new(Json("\"reporter\":null", "2026-09-16T17:00:00+00:00"), JiraPeopleFormat.Json, Origin)],
            ["FHIR-1"], Origin, upstream: true);
        Assert.Equal(JiraPublicPeopleCodes.RevisionMismatch,
            JiraPublicPeopleObservationReader.ReadForRevision(evidence, "FHIR-1", Revision).Failure);
    }

    [Theory]
    [InlineData("2026-09-15T17:00:00Z")]
    [InlineData("2026-09-15T12:00:00-05:00")]
    [InlineData("2026-09-15T18:00:00+0100")]
    [InlineData("Tue, 15 Sep 2026 17:00:00 +0000")]
    [InlineData("2026-09-15 17:00:00+00:00")]
    public void Read_RevisionEquivalenceUsesExplicitInstants(string revision)
    {
        JiraPeopleEvidence evidence = Evidence(new JiraRawPeopleObservation(Json("\"assignee\":null", revision), JiraPeopleFormat.Json, Origin));
        Assert.NotNull(JiraPublicPeopleObservationReader.ReadForRevision(evidence, "FHIR-1", Revision).Observation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("2026-09-15")]
    [InlineData("2026-09-15T17:00:00")]
    [InlineData("0001-01-01T00:00:00+00:00")]
    public void Read_MissingMalformedAndDefaultDatesAreNotRevisionEvidence(string? revision)
    {
        JiraPeopleEvidence evidence = Evidence(new JiraRawPeopleObservation(Json("\"assignee\":null", revision), JiraPeopleFormat.Json, Origin));
        Assert.Equal(JiraPublicPeopleCodes.MissingRevision,
            JiraPublicPeopleObservationReader.ReadForRevision(evidence, "FHIR-1", Revision).Failure);
    }

    [Theory]
    [InlineData("""{"name":"first","key":"second","displayName":"Safe Name"}""", JiraPeopleFieldState.IdentityConflict)]
    [InlineData("""{"name":"first","key":"FIRST","displayName":"Safe Name"}""", JiraPeopleFieldState.IdentityConflict)]
    [InlineData("""{"name":"first","accountId":"second","displayName":"Safe Name"}""", JiraPeopleFieldState.IdentityConflict)]
    [InlineData("""{"name":3,"displayName":"Safe Name"}""", JiraPeopleFieldState.Malformed)]
    [InlineData("""{"name":" first ","displayName":"Safe Name"}""", JiraPeopleFieldState.Malformed)]
    [InlineData("""{"name":"first","displayName":3}""", JiraPeopleFieldState.Malformed)]
    [InlineData("\"legacy-name\"", JiraPeopleFieldState.Malformed)]
    public void Read_JsonIdentityAmbiguityAndMalformedValuesNeverGuess(string person, object expected)
    {
        Assert.Equal((JiraPeopleFieldState)expected, Read(Json("\"reporter\":" + person)).Reporter.State);
    }

    [Fact]
    public void Read_JsonRequestersPreserveExactUserObjects()
    {
        JiraPeopleObservation observation = Read(Json("""
            "customfield_11000":[
              {"name":"exact-one","key":"exact-one","displayName":"Public One"},
              {"accountId":"exact-two","displayName":"Public Two"},
              {"name":"username-only"},
              {"displayName":"Name Only"}
            ]
            """));
        Assert.Equal(JiraPeopleFieldState.Present, observation.Requesters.State);
        Assert.Equal(4, observation.Requesters.People.Count);
        Assert.Equal("exact-one", observation.Requesters.People[0].Identity);
        Assert.Equal("Public One", observation.Requesters.People[0].ExplicitName);
        Assert.Equal("exact-two", observation.Requesters.People[1].Identity);
        Assert.Null(observation.Requesters.People[2].ExplicitName);
        Assert.Null(observation.Requesters.People[3].Identity);
    }

    [Theory]
    [InlineData("", JiraPeopleFieldState.Missing)]
    [InlineData("\"customfield_11000\":null", JiraPeopleFieldState.Absent)]
    [InlineData("\"customfield_11000\":[]", JiraPeopleFieldState.Absent)]
    [InlineData("\"customfield_11000\":\"legacy-requester\"", JiraPeopleFieldState.Malformed)]
    [InlineData("\"customfield_11000\":{}", JiraPeopleFieldState.Malformed)]
    public void Read_RequesterSetPresenceIsExplicit(string fields, object expected)
    {
        Assert.Equal((JiraPeopleFieldState)expected, Read(Json(fields)).Requesters.State);
    }

    [Fact]
    public void Read_MissingFieldsCanBeCompletedButExplicitAbsenceCannot()
    {
        JiraRawPeopleObservation missing = new(Json("\"assignee\":null"), JiraPeopleFormat.Json, Origin);
        JiraRawPeopleObservation present = new(Json(Reporter("exact", "Public Name")),
            JiraPeopleFormat.Json, Origin);
        JiraPeopleRevisionObservation merged = JiraPublicPeopleObservationReader.ReadForRevision(Evidence(missing, present), "FHIR-1", Revision);
        Assert.Equal("exact", merged.Observation!.Reporter.Identity);
        JiraRawPeopleObservation absent = new(Json("\"reporter\":null"), JiraPeopleFormat.Json, Origin);
        merged = JiraPublicPeopleObservationReader.ReadForRevision(Evidence(absent, present), "FHIR-1", Revision);
        Assert.Equal(JiraPeopleFieldState.Conflict, merged.Observation!.Reporter.State);
    }

    [Fact]
    public void Read_OnlySameRevisionCacheEvidenceQualifiesAndEveryRawDigestIsBound()
    {
        JiraRawPeopleObservation current = new(Json(Reporter("exact", "Public Name")),
            JiraPeopleFormat.Json, Origin);
        JiraRawPeopleObservation equivalent = current with { Body = Json(Reporter("exact", "Public Name"), "2026-09-15T12:00:00-05:00") };
        JiraRawPeopleObservation older = current with { Body = Json(Reporter("exact", "Public Name"), "2020-01-01T00:00:00Z") };
        JiraPeopleObservation one = JiraPublicPeopleObservationReader.ReadForRevision(Evidence(current, older), "FHIR-1", Revision).Observation!;
        JiraPeopleObservation two = JiraPublicPeopleObservationReader.ReadForRevision(Evidence(current, equivalent), "FHIR-1", Revision).Observation!;
        JiraPeopleObservation reversed = JiraPublicPeopleObservationReader.ReadForRevision(Evidence(equivalent, current), "FHIR-1", Revision).Observation!;
        Assert.Equal(one.Reporter, two.Reporter);
        Assert.NotEqual(one.Digest, two.Digest);
        Assert.Equal(two.Digest, reversed.Digest);
    }

    [Fact]
    public void Read_UnknownOriginAndWrongKeyDoNotQualify()
    {
        string body = Json(Reporter("exact", "Public Name"));
        Assert.Equal(JiraPublicPeopleCodes.UnknownCacheOrigin,
            JiraPublicPeopleObservationReader.ReadForRevision(
                Evidence(new JiraRawPeopleObservation(body, JiraPeopleFormat.Json, null)), "FHIR-1", Revision).Failure);
        Assert.Equal(JiraPublicPeopleCodes.MissingObservation,
            JiraPublicPeopleObservationReader.ReadForRevision(
                Evidence(new JiraRawPeopleObservation(body.Replace("FHIR-1", "FHIR-2"), JiraPeopleFormat.Json, Origin)), "FHIR-1", Revision).Failure);
    }

    [Theory]
    [InlineData("""{"key":"FHIR-1","fields":{"updated":"2026-09-15T17:00:00Z","reporter":null,"reporter":{}}}""", JiraPeopleFormat.Json)]
    [InlineData("""<!DOCTYPE rss [<!ENTITY x SYSTEM "file:///not-read">]><rss><channel>&x;</channel></rss>""", JiraPeopleFormat.Xml)]
    [InlineData("<html>private upstream body</html>", JiraPeopleFormat.Xml)]
    public void Read_MalformedEnvelopesRefuseWithoutParserDetails(string body, object format)
    {
        JiraPeopleEvidence evidence = Evidence(new JiraRawPeopleObservation(body, (JiraPeopleFormat)format, Origin));
        Assert.Equal(JiraPublicPeopleCodes.MalformedValue,
            JiraPublicPeopleObservationReader.ReadForRevision(evidence, "FHIR-1", Revision).Failure);
    }

    [Fact]
    public void Read_DoesNotChangeNormalIngestionDefaults()
    {
        using JsonDocument document = JsonDocument.Parse("""{"key":"FHIR-1","fields":{"summary":"Test"}}""");
        Assert.Equal(DateTimeOffset.MinValue, JiraFieldMapper.MapIssue(document.RootElement).UpdatedAt);
        Assert.Null(JiraFieldMapper.ExtractUserRef(document.RootElement.GetProperty("fields"), "reporter").Username);
        Assert.Equal(JiraPeopleFieldState.Missing,
            JiraFieldMapper.ReadPublicPeopleObservations(document.RootElement.GetRawText())[0].Reporter.State);
    }

    private static string Json(string fields, string? revision = Revision) =>
        "{\"key\":\"FHIR-1\",\"fields\":{\"updated\":" + JsonSerializer.Serialize(revision)
        + (fields.Length == 0 ? "" : "," + fields) + "}}";

    private static string Reporter(string identity, string name) =>
        "\"reporter\":" + JsonSerializer.Serialize(new { name = identity, displayName = name });

    private static JiraPeopleEvidence Evidence(params JiraRawPeopleObservation[] values) =>
        JiraPublicPeopleObservationReader.Read(values, ["FHIR-1"], Origin, upstream: false);

    private static JiraPeopleObservation Read(string body, JiraPeopleFormat format = JiraPeopleFormat.Json) =>
        Assert.IsType<JiraPeopleObservation>(
            JiraPublicPeopleObservationReader.ReadForRevision(Evidence(new JiraRawPeopleObservation(body, format, Origin)), "FHIR-1", Revision).Observation);
}
