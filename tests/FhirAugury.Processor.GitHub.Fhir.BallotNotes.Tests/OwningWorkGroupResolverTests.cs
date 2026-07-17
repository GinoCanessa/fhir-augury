using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="OwningWorkGroupResolver"/>: legacy ticket parity through
/// the <see cref="WorkGroupRef"/> seam (Phase 1), and the page chain that resolves
/// via the <c>[%wg%]</c> marker and never falls back to a ticket WG (Phase 2).
/// The registry DB path is pinned to a non-existent file so these tests are
/// independent of any cached <c>github.db</c>.
/// </summary>
public sealed class OwningWorkGroupResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _missingDbPath;

    public OwningWorkGroupResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "owningwg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _missingDbPath = Path.Combine(_tempDir, "does-not-exist.db");
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void Resolve_with_only_tickets_matches_legacy_behavior()
    {
        UnitAttribution attribution = new()
        {
            Tickets =
            [
                new() { Key = "FHIR-1", WorkGroup = "Old WG", AttributionDate = Date("2026-01-01") },
                new() { Key = "FHIR-2", WorkGroup = "Newest WG", AttributionDate = Date("2026-06-01") },
            ],
            CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(),
        };

        IReadOnlyList<WorkGroupRef> refs = Resolve(ArtifactUnit(), attribution, hint: null);

        (string legacyWg, string legacyCode) =
            TicketAttributor.SelectOwningWorkGroup(attribution.Tickets, hint: null);

        Assert.Single(refs);
        Assert.Equal(legacyWg, refs[0].DisplayName);
        Assert.Equal(legacyCode, refs[0].Code);
        Assert.Equal("Newest WG", refs[0].DisplayName);
        Assert.Equal("Newest WG", WorkGroupRef.JoinNames(refs));
        Assert.Equal(legacyCode, WorkGroupRef.JoinCodes(refs));
    }

    [Fact]
    public void Resolve_artifact_without_owner_yields_unknown()
    {
        IReadOnlyList<WorkGroupRef> refs = Resolve(ArtifactUnit(), EmptyAttribution(), hint: null);

        Assert.Single(refs);
        Assert.Equal("(unknown)", refs[0].DisplayName);
        Assert.Equal(string.Empty, refs[0].Code);
        Assert.Equal("(unknown)", WorkGroupRef.JoinNames(refs));
        Assert.Equal(string.Empty, WorkGroupRef.JoinCodes(refs));
    }

    [Fact]
    public void Page_resolves_via_marker_when_registry_empty()
    {
        WritePage("security", "<td id=\"wg\"><a href=\"[%wg sec%]\">[%wgt sec%]</a> Work Group</td>");
        HydrationUnit unit = new() { Type = "Page", Name = "security", ChangedPaths = ["source/security.html"] };

        IReadOnlyList<WorkGroupRef> refs = Resolve(unit, EmptyAttribution(), hint: null);

        Assert.Single(refs);
        Assert.Equal("sec", refs[0].Code);
        // No github.db → display name falls back to the raw code.
        Assert.Equal("sec", refs[0].DisplayName);
    }

    [Fact]
    public void Page_never_falls_back_to_ticket_wg()
    {
        // No page file and a strong ticket WG present: the page must stay (unknown).
        UnitAttribution attribution = new()
        {
            Tickets = [new() { Key = "FHIR-9", WorkGroup = "Some WG", AttributionDate = Date("2026-06-01") }],
            CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(),
        };
        HydrationUnit unit = new() { Type = "Page", Name = "missing-page", ChangedPaths = ["source/missing-page.html"] };

        IReadOnlyList<WorkGroupRef> refs = Resolve(unit, attribution, hint: "Hint WG");

        Assert.Single(refs);
        Assert.Equal("(unknown)", refs[0].DisplayName);
        Assert.Equal(string.Empty, refs[0].Code);
    }

    [Fact]
    public void Falls_through_registry_to_repo_read_to_specdb()
    {
        // Registry absent, the SD carries no own wg, but the spec-DB knows the
        // artifact's owner: registry(miss) → repo-read(miss) → spec-DB(hit).
        string rel = WriteSd("observation", "structuredefinition-Observation.xml", workGroup: null, baseDefinition: null);
        string specDb = SeedSpecDb("fhir-r6.db", ("Observation", "oo"));
        HydrationUnit unit = new() { Type = "Artifact", Name = "observation", ChangedPaths = [rel] };

        IReadOnlyList<WorkGroupRef> refs = ResolveArtifact(unit, [Sd(rel)], specDb);

        Assert.Single(refs);
        Assert.Equal("oo", refs[0].Code);
    }

    [Fact]
    public void Repo_read_wins_over_specdb_when_sd_declares_wg()
    {
        string rel = WriteSd("observation", "structuredefinition-Observation.xml", workGroup: "repo-wg", baseDefinition: null);
        string specDb = SeedSpecDb("fhir-r6.db", ("Observation", "oo"));
        HydrationUnit unit = new() { Type = "Artifact", Name = "observation", ChangedPaths = [rel] };

        IReadOnlyList<WorkGroupRef> refs = ResolveArtifact(unit, [Sd(rel)], specDb);

        Assert.Single(refs);
        Assert.Equal("repo-wg", refs[0].Code);
    }

    [Fact]
    public void Profile_inherits_base_resource_wg_when_no_own_wg()
    {
        // Profile SD has no own wg but a baseDefinition; the base resource's spec-DB
        // owner is inherited.
        string rel = WriteSd(
            "myobsprofile",
            "structuredefinition-MyObsProfile.xml",
            workGroup: null,
            baseDefinition: "http://hl7.org/fhir/StructureDefinition/Observation");
        string specDb = SeedSpecDb("fhir-r6.db", ("Observation", "oo"));
        HydrationUnit unit = new() { Type = "Artifact", Name = "myobsprofile", ChangedPaths = [rel] };

        IReadOnlyList<WorkGroupRef> refs = ResolveArtifact(unit, [Sd(rel)], specDb);

        Assert.Single(refs);
        Assert.Equal("oo", refs[0].Code);
    }

    [Fact]
    public void Datatype_unit_surfaces_distinct_set_of_owners()
    {
        string specDb = SeedSpecDb("fhir-r6.db", ("Quantity", "oo"), ("Money", "fm"));
        HydrationUnit unit = new()
        {
            Type = "DataType",
            Name = "datatypes",
            ChangedPaths = ["source/datatypes/Quantity.xml", "source/datatypes/Money.xml"],
        };

        IReadOnlyList<WorkGroupRef> refs = ResolveDataType(unit, specDb, headDatatypeNames: []);

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.Code == "oo");
        Assert.Contains(refs, r => r.Code == "fm");
        // Deterministic primary: neither is fhir → alphabetical by display name (fm < oo).
        Assert.Equal("fm", refs[0].Code);
        Assert.Equal("fm;oo", WorkGroupRef.JoinCodes(refs));
    }

    [Fact]
    public void Datatype_primary_prefers_fhir_infrastructure()
    {
        string specDb = SeedSpecDb("fhir-r6.db", ("Quantity", "oo"), ("Element", "fhir"));
        HydrationUnit unit = new()
        {
            Type = "DataType",
            Name = "datatypes",
            ChangedPaths = ["source/datatypes/Quantity.xml", "source/datatypes/Element.xml"],
        };

        IReadOnlyList<WorkGroupRef> refs = ResolveDataType(unit, specDb, headDatatypeNames: []);

        Assert.Equal("fhir", refs[0].Code);
    }

    [Fact]
    public void Datatype_aggregate_only_change_enumerates_from_head()
    {
        string specDb = SeedSpecDb("fhir-r6.db", ("Quantity", "oo"));
        HydrationUnit unit = new()
        {
            Type = "DataType",
            Name = "datatypes",
            ChangedPaths = ["source/datatypes.html"],
        };

        IReadOnlyList<WorkGroupRef> refs = ResolveDataType(unit, specDb, headDatatypeNames: ["Quantity"]);

        Assert.Single(refs);
        Assert.Equal("oo", refs[0].Code);
    }

    [Fact]
    public void Datatype_excludes_ticket_fallback()
    {
        // Spec-DB has no owner for these names and there is a hint WG, yet the
        // datatype unit must resolve to (unknown), never the ticket/hint WG.
        string specDb = SeedSpecDb("fhir-r6.db", ("Something", "oo"));
        HydrationUnit unit = new()
        {
            Type = "DataType",
            Name = "datatypes",
            ChangedPaths = ["source/datatypes/Unmapped.xml"],
        };

        IReadOnlyList<WorkGroupRef> refs = ResolveDataType(unit, specDb, headDatatypeNames: []);

        Assert.Single(refs);
        Assert.Equal("(unknown)", refs[0].DisplayName);
        Assert.Equal(string.Empty, refs[0].Code);
    }

    private string WriteSd(string folder, string fileName, string? workGroup, string? baseDefinition)
    {
        string dir = Path.Combine(_tempDir, "source", folder);
        Directory.CreateDirectory(dir);

        string name = Path.GetFileNameWithoutExtension(fileName).Replace("structuredefinition-", string.Empty);
        string wgXml = workGroup is null
            ? string.Empty
            : $"<extension url=\"http://hl7.org/fhir/StructureDefinition/structuredefinition-wg\"><valueCode value=\"{workGroup}\"/></extension>";
        string baseXml = baseDefinition is null ? string.Empty : $"<baseDefinition value=\"{baseDefinition}\"/>";

        string xml =
            "<StructureDefinition xmlns=\"http://hl7.org/fhir\">" +
            $"<url value=\"http://hl7.org/fhir/StructureDefinition/{name}\"/>" +
            $"<name value=\"{name}\"/>" +
            "<status value=\"active\"/>" +
            "<kind value=\"resource\"/>" +
            "<abstract value=\"false\"/>" +
            $"<type value=\"{name}\"/>" +
            wgXml +
            baseXml +
            "</StructureDefinition>";

        string fullPath = Path.Combine(dir, fileName);
        File.WriteAllText(fullPath, xml);
        return Path.GetRelativePath(_tempDir, fullPath).Replace('\\', '/');
    }

    private string SeedSpecDb(string fileName, params (string Name, string WorkGroup)[] rows)
    {
        string path = Path.Combine(_tempDir, fileName);
        using Microsoft.Data.Sqlite.SqliteConnection conn = new($"Data Source={path};Pooling=False");
        conn.Open();
        using (Microsoft.Data.Sqlite.SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE Structures (Id INTEGER PRIMARY KEY, Name TEXT, WorkGroup TEXT)";
            create.ExecuteNonQuery();
        }
        foreach ((string name, string wg) in rows)
        {
            using Microsoft.Data.Sqlite.SqliteCommand ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO Structures (Name, WorkGroup) VALUES ($n, $w)";
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$w", wg);
            ins.ExecuteNonQuery();
        }
        return path;
    }

    private static ResolvedSourceFile Sd(string path) => new() { Path = path, Role = "StructureDefinition" };

    private IReadOnlyList<WorkGroupRef> ResolveArtifact(
        HydrationUnit unit, IReadOnlyList<ResolvedSourceFile> files, string fhirR6DbPath)
        => OwningWorkGroupResolver.Resolve(
            unit,
            clonePath: _tempDir,
            owner: "HL7",
            name: "fhir",
            EmptyAttribution(),
            resolvedFiles: files,
            headDatatypeNames: [],
            workGroupHint: null,
            options: new BallotNotesHydrationOptions { GitHubDbPath = _missingDbPath, FhirR6DbPath = fhirR6DbPath, FhirSpecDbPath = string.Empty },
            logger: null);

    private IReadOnlyList<WorkGroupRef> ResolveDataType(
        HydrationUnit unit, string fhirR6DbPath, IReadOnlyList<string> headDatatypeNames)
        => OwningWorkGroupResolver.Resolve(
            unit,
            clonePath: _tempDir,
            owner: "HL7",
            name: "fhir",
            EmptyAttribution(),
            resolvedFiles: [],
            headDatatypeNames: headDatatypeNames,
            workGroupHint: "Hint WG",
            options: new BallotNotesHydrationOptions { GitHubDbPath = _missingDbPath, FhirR6DbPath = fhirR6DbPath, FhirSpecDbPath = string.Empty },
            logger: null);

    private void WritePage(string stem, string html)
    {
        string dir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{stem}.html"), html);
    }

    private IReadOnlyList<WorkGroupRef> Resolve(
        HydrationUnit unit, UnitAttribution attribution, string? hint)
        => OwningWorkGroupResolver.Resolve(
            unit,
            clonePath: _tempDir,
            owner: "HL7",
            name: "fhir",
            attribution,
            resolvedFiles: [],
            headDatatypeNames: [],
            workGroupHint: hint,
            options: new BallotNotesHydrationOptions { GitHubDbPath = _missingDbPath },
            logger: null);

    private static UnitAttribution EmptyAttribution() => new()
    {
        Tickets = [],
        CommitTicketKeys = new Dictionary<string, IReadOnlyList<string>>(),
    };

    private static HydrationUnit ArtifactUnit() => new()
    {
        Type = "Artifact",
        Name = "Observation",
        ChangedPaths = ["source/observation/observation.xml"],
    };

    private static DateTimeOffset Date(string iso) => DateTimeOffset.Parse(iso);
}
