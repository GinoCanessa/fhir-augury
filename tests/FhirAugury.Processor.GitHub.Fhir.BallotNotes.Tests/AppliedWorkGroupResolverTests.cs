using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="AppliedWorkGroupResolver"/>: a ticket's work group is
/// counted only when its attributed commit touched a unit source path (matched
/// via the <c>ChangedPaths ∪ resolution.Files</c> union the hydrator supplies),
/// codes derive from <see cref="Hl7WorkGroupNameCleaner.Clean"/>, and the coarse
/// fallback emits a warning when no commit-to-file signal is available.
/// </summary>
public sealed class AppliedWorkGroupResolverTests
{
    [Fact]
    public void Includes_wg_of_ticket_whose_commit_touched_unit_file_excludes_others()
    {
        List<WindowCommit> commits =
        [
            Commit("shaA", changed: ["source/observation/observation.xml"]),
            Commit("shaB", changed: ["source/other/unrelated.xml"]),
        ];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"], ["shaB"] = ["FHIR-2"] },
            tickets:
            [
                Ticket("FHIR-1", "Orders and Observations"),
                Ticket("FHIR-2", "Patient Administration"),
            ]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db: null, NameCache());

        Assert.Equal(string.Empty, result.WarningNote);
        WorkGroupRef one = Assert.Single(result.Refs);
        Assert.Equal(Hl7WorkGroupNameCleaner.Clean("Orders and Observations"), one.Code);
    }

    [Fact]
    public void Counts_ticket_whose_commit_touched_a_head_deleted_file_via_union()
    {
        // The deleted file is absent from resolution.Files at HEAD but present in
        // the union path set the hydrator supplies; the commit that touched it must
        // still count.
        List<WindowCommit> commits = [Commit("shaA", changed: ["source/observation/deleted.xml"])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"] },
            tickets: [Ticket("FHIR-1", "Orders and Observations")]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/deleted.xml"], db: null, NameCache());

        Assert.Equal(string.Empty, result.WarningNote);
        Assert.Equal(Hl7WorkGroupNameCleaner.Clean("Orders and Observations"), Assert.Single(result.Refs).Code);
    }

    [Fact]
    public void Applied_code_equals_cleaned_ticket_workgroup()
    {
        const string wg = "Clinical Quality Information";
        List<WindowCommit> commits = [Commit("shaA", changed: ["source/measure/measure.xml"])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-9"] },
            tickets: [Ticket("FHIR-9", wg)]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/measure/measure.xml"], db: null, NameCache());

        Assert.Equal(Hl7WorkGroupNameCleaner.Clean(wg), Assert.Single(result.Refs).Code);
    }

    [Fact]
    public void Fallback_lists_all_ticket_wgs_with_warning_when_no_changed_paths()
    {
        // Commits carry no per-file detail → no commit-to-file signal → fallback.
        List<WindowCommit> commits = [Commit("shaA", changed: []), Commit("shaB", changed: [])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"], ["shaB"] = ["FHIR-2"] },
            tickets:
            [
                Ticket("FHIR-1", "Orders and Observations"),
                Ticket("FHIR-2", "Patient Administration"),
            ]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db: null, NameCache());

        Assert.NotEqual(string.Empty, result.WarningNote);
        Assert.Equal(2, result.Refs.Count);
        Assert.Contains(result.Refs, r => r.Code == Hl7WorkGroupNameCleaner.Clean("Orders and Observations"));
        Assert.Contains(result.Refs, r => r.Code == Hl7WorkGroupNameCleaner.Clean("Patient Administration"));
    }

    [Fact]
    public void Skips_blank_work_groups_and_dedupes_by_code()
    {
        List<WindowCommit> commits =
        [
            Commit("shaA", changed: ["source/observation/observation.xml"]),
            Commit("shaB", changed: ["source/observation/observation.xml"]),
            Commit("shaC", changed: ["source/observation/observation.xml"]),
        ];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"], ["shaB"] = ["FHIR-2"], ["shaC"] = ["FHIR-3"] },
            tickets:
            [
                Ticket("FHIR-1", "Orders and Observations"),
                Ticket("FHIR-2", "Orders and Observations"), // duplicate WG
                Ticket("FHIR-3", ""),                          // blank WG → skipped
            ]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db: null, NameCache());

        Assert.Equal(Hl7WorkGroupNameCleaner.Clean("Orders and Observations"), Assert.Single(result.Refs).Code);
    }

    [Fact]
    public void Applied_code_uses_registry_short_code_when_db_present()
    {
        using SqliteConnection db = SeedHl7(("oo", "Orders and Observations", "OrdersandObservations"));
        List<WindowCommit> commits = [Commit("shaA", changed: ["source/observation/observation.xml"])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"] },
            tickets: [Ticket("FHIR-1", "Orders and Observations")]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db, NameCache());

        WorkGroupRef one = Assert.Single(result.Refs);
        Assert.Equal("oo", one.Code);
        Assert.Equal("Orders and Observations", one.DisplayName);
    }

    [Fact]
    public void Applied_code_resolves_short_code_for_parenthetical_suffix_value()
    {
        // Raw Jira work_group values can carry a trailing short-code suffix
        // (e.g. "Orders and Observations (OO)"); it must still resolve to "oo",
        // not the CamelCase cleaner fallback "OrdersandObservationsOO".
        using SqliteConnection db = SeedHl7(("oo", "Orders and Observations", "OrdersandObservations"));
        List<WindowCommit> commits = [Commit("shaA", changed: ["source/observation/observation.xml"])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"] },
            tickets: [Ticket("FHIR-1", "Orders and Observations (OO)")]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/observation/observation.xml"], db, NameCache());

        WorkGroupRef one = Assert.Single(result.Refs);
        Assert.Equal("oo", one.Code);
        Assert.Equal("Orders and Observations", one.DisplayName);
    }

    [Fact]
    public void Applied_code_falls_back_to_cleaner_when_db_unresolved()
    {
        // The registry has no row for this work group, so the cleaner basis is used.
        using SqliteConnection db = SeedHl7(("pa", "Patient Administration", "PatientAdministration"));
        const string wg = "Some Brand New Work Group";
        List<WindowCommit> commits = [Commit("shaA", changed: ["source/x/x.xml"])];
        UnitAttribution attribution = Attribution(
            commitKeys: new() { ["shaA"] = ["FHIR-1"] },
            tickets: [Ticket("FHIR-1", wg)]);

        AppliedWorkGroupResolution result = AppliedWorkGroupResolver.Resolve(
            commits, attribution, ["source/x/x.xml"], db, NameCache());

        Assert.Equal(Hl7WorkGroupNameCleaner.Clean(wg), Assert.Single(result.Refs).Code);
    }

    private static WindowCommit Commit(string sha, IReadOnlyList<string> changed)
        => new() { Sha = sha, ShortSha = sha, ChangedPaths = changed };

    private static AttributedTicket Ticket(string key, string workGroup)
        => new() { Key = key, WorkGroup = workGroup };

    private static UnitAttribution Attribution(
        Dictionary<string, IReadOnlyList<string>> commitKeys,
        IReadOnlyList<AttributedTicket> tickets)
        => new() { Tickets = tickets, CommitTicketKeys = commitKeys };

    private static Dictionary<string, string> NameCache()
        => new(StringComparer.OrdinalIgnoreCase);

    private static SqliteConnection SeedHl7(params (string Code, string Name, string NameClean)[] rows)
    {
        SqliteConnection conn = new("Data Source=:memory:");
        conn.Open();
        using (SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE hl7_workgroups (Id INTEGER PRIMARY KEY, Code TEXT, Name TEXT, NameClean TEXT)";
            create.ExecuteNonQuery();
        }

        foreach ((string code, string name, string nameClean) in rows)
        {
            using SqliteCommand insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO hl7_workgroups (Code, Name, NameClean) VALUES ($code, $name, $clean)";
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$clean", nameClean);
            insert.ExecuteNonQuery();
        }

        return conn;
    }
}
