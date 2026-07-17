using FhirAugury.Tools.FhirXverElementDiff.Attribution;
using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Readers;
using FhirAugury.Tools.FhirXverElementDiff.Report;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

/// <summary>
/// Unit tests for the structure-window attributor: the ticket-extraction rules (allowlist
/// filtering, the custom bare-<c>#N</c> pass, hash-alias mapping, ordering) and the writer's
/// ticket/commit link rendering. A trailing live smoke test attributes a real R4→R4B report
/// when the cache is present, otherwise skips.
/// </summary>
public sealed class AttributorTests
{
    private static FhirKeyAllowlist Allow(params int[] numbers) => new([.. numbers]);

    private static IReadOnlyList<CommitInfo> Commits(params string[] messages) =>
        [.. messages.Select((m, i) => new CommitInfo("sha" + i, "sha" + i, m, string.Empty))];

    [Fact]
    public void Allowlist_Drops_Bogus_Fhir_Number()
    {
        // FHIR-999999 is out of the real key range; only the allowlisted FHIR-27849 survives.
        List<int> tickets = Attributor.ExtractAuthoringTickets(
            Commits("Fix FHIR-27849 and a bogus FHIR-999999 token"), Allow(27849));

        Assert.Equal([27849], tickets);
    }

    [Fact]
    public void Bare_Hash_Ref_Resolves_Via_Custom_Pass()
    {
        // The extractor's own bare-number pass excludes "#N"; our custom pass catches it.
        List<int> tickets = Attributor.ExtractAuthoringTickets(
            Commits("#31847 Appointment patientInstruction cardinality"), Allow(31847));

        Assert.Equal([31847], tickets);
    }

    [Fact]
    public void Jira_Hash_Alias_Maps_To_Fhir()
    {
        // J#N is a FHIR hash alias; the extractor maps it and the allowlist validates it.
        List<int> tickets = Attributor.ExtractAuthoringTickets(
            Commits("J#46599 AdverseEvent to use CodeableReference"), Allow(46599));

        Assert.Equal([46599], tickets);
    }

    [Fact]
    public void Tickets_Are_Deduplicated_And_Sorted()
    {
        List<int> tickets = Attributor.ExtractAuthoringTickets(
            Commits("FHIR-27849 first pass", "#12345 second", "FHIR-27849 follow-up"),
            Allow(27849, 12345));

        Assert.Equal([12345, 27849], tickets);
    }

    [Fact]
    public void Non_Allowlisted_Hash_Number_Is_Ignored()
    {
        // A bare "#4567" that is not a real FHIR ticket (e.g. a PR number) must not resolve.
        List<int> tickets = Attributor.ExtractAuthoringTickets(
            Commits("Merge branch context mentioning #4567 in passing"), Allow(27849));

        Assert.Empty(tickets);
    }

    [Fact]
    public void Writer_Renders_Ticket_And_Commit_Links()
    {
        string md = MarkdownReportWriter.Render(LinkModel());

        Assert.Contains("[FHIR-27849](https://jira.hl7.org/browse/FHIR-27849)", md);
        Assert.Contains("[`abc1234`](https://github.com/HL7/fhir/commit/abc1234)", md);
    }

    [Fact]
    public async Task R4_To_R4B_Attribution_Populates_Records()
    {
        if (!LiveDb.TryAttributionPaths(out string specDb, out string jiraDb, out string clone))
        {
            return; // full cache unavailable — skip
        }

        ReleaseReader reader = new(NullLogger.Instance);
        ReleaseModel r4 = reader.LoadRelease(reader.ResolveRelease(ReleaseId.R4, specDb));
        ReleaseModel r4b = reader.LoadRelease(reader.ResolveRelease(ReleaseId.R4B, specDb));

        ReportModel model = ReportBuilder.Build(Increments.R4ToR4B, r4, r4b, MinimalHeader());

        FhirKeyAllowlist allowlist = JiraAllowlistReader.Load(jiraDb);
        GitLog git = new(clone);
        ReportModel attributed = await Attributor.AttributeAsync(
            model, git, Increments.R4ToR4B.DefaultSince, Increments.R4ToR4B.DefaultUntil, allowlist);

        bool anyTicket = attributed.Mapped
            .SelectMany(m => m.Rows)
            .Any(row => row.ChangeRecord is { TicketKeys.Count: > 0 });
        Assert.True(anyTicket, "expected at least one mapped-structure row attributed to a FHIR ticket");
    }

    private static ReportModel LinkModel()
    {
        StructurePair patient = new(
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.gender")),
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.gender")),
            RenameKind.None);

        List<ElementRow> rows =
        [
            new ElementRow("Patient.gender", "Patient.gender",
                new ElementFlags(false, false, RenameKind.None, true, false, false), "0..1 → 1..1")
            {
                ChangeRecord = new ElementChangeRecord(["FHIR-27849"], []),
            },
            new ElementRow("Patient.name", "Patient.name",
                new ElementFlags(false, false, RenameKind.None, false, true, false), "HumanName → HumanName")
            {
                ChangeRecord = new ElementChangeRecord([], ["abc1234"]),
            },
        ];

        return new ReportModel(
            Increments.R4BToR5,
            MinimalHeader(),
            [new MappedStructureReport(patient, rows)],
            [],
            []);
    }

    private static ReportHeader MinimalHeader() => new(
        GeneratedUtc: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        EarlierLabel: "R4",
        LaterLabel: "R4B",
        EarlierVersion: "4.0.1",
        LaterVersion: "4.3.0",
        EarlierBuilt: "2025-10-31",
        LaterBuilt: "2025-10-31",
        SinceSha: "b6357157",
        UntilSha: "d685d85",
        CloneHead: null,
        AttributionEnabled: true,
        HeaderNote: null);
}
