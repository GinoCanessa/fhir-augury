using FhirAugury.Tools.FhirSpecReview.Database;
using FhirAugury.Tools.FhirSpecReview.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// Seeds a review DB with two work groups and verifies the report emitter
/// produces an index with roll-up tables + provenance and per-workgroup detail
/// pages, plus the overwrite guard. Raw connections use <c>;Pooling=False</c>.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ReportEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public ReportEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "report-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private string SeedReviewDb()
    {
        string dbPath = Path.Combine(_tempDir, "review.db");
        using ReviewDatabase db = new(dbPath, NullLogger<ReviewDatabase>.Instance);
        db.Initialize();
        using SqliteConnection conn = db.OpenConnection();

        conn.Insert(new ReviewRunRecord
        {
            Id = ReviewRunRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            BuildVersion = "6.0.0-test",
            BaselineRelease = "R5",
            RunAt = "2026-06-12T00:00:00Z",
        }, insertPrimaryKey: true);

        SpecPageRecord patientPage = new()
        {
            Id = SpecPageRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            ArtifactId = null,
            FhirArtifactId = null,
            PageFileName = "patient.html",
            ExistsInPublishIni = true,
            ExistsInSource = true,
            ResponsibleWorkGroupCode = "pa",
            ResponsibleWorkGroupName = "Patient Administration",
            ConformantShallCount = 2,
            ConformantTotalCount = 2,
            NonConformantTotalCount = 1,
            UnknownWordCount = 1,
        };
        conn.Insert(patientPage, insertPrimaryKey: true);

        conn.Insert(new SpecPageUnknownWordRecord
        {
            Id = SpecPageUnknownWordRecord.GetIndex(),
            PageId = patientPage.Id,
            Word = "Zorblax",
            IsTypo = false,
            Correction = null,
        }, insertPrimaryKey: true);

        conn.Insert(new SpecPageRecord
        {
            Id = SpecPageRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            ArtifactId = null,
            FhirArtifactId = null,
            PageFileName = "observation.html",
            ExistsInPublishIni = true,
            ExistsInSource = true,
            ResponsibleWorkGroupCode = "oo",
            ResponsibleWorkGroupName = "Orders and Observations",
            ConformantTotalCount = 5,
        }, insertPrimaryKey: true);

        conn.Insert(new ArtifactRecord
        {
            Id = ArtifactRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FhirId = "Patient",
            Name = "Patient",
            ArtifactType = "resource",
            ResponsibleWorkGroupCode = "pa",
            ResponsibleWorkGroupName = "Patient Administration",
        }, insertPrimaryKey: true);

        conn.Insert(new RemovedBaselineEntityRecord
        {
            Id = RemovedBaselineEntityRecord.GetIndex(),
            EntityKind = "page",
            Name = "removedpage.html",
            BaselineRelease = "R5",
            WorkGroupCode = null,
        }, insertPrimaryKey: true);

        conn.Insert(new DuplicateArtifactKeyRecord
        {
            Id = DuplicateArtifactKeyRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FhirId = "operationoutcome-issue-source",
            KeptName = "OOIssueCol",
            DuplicateName = "OOSourceFile",
            KeptCanonicalUrl = "http://hl7.org/fhir/StructureDefinition/operationoutcome-issue-source",
            DuplicateCanonicalUrl = "http://hl7.org/fhir/StructureDefinition/operationoutcome-issue-source",
            ArtifactType = "extension",
            WorkGroupCode = "fhir",
        }, insertPrimaryKey: true);

        return dbPath;
    }

    [Fact]
    public void Emit_Writes_Index_And_PerWorkgroup_Pages()
    {
        string dbPath = SeedReviewDb();
        string outDir = Path.Combine(_tempDir, "site");

        new Report.ReportEmitter(dbPath).Emit(outDir);

        string index = File.ReadAllText(Path.Combine(outDir, "index.html"));
        Assert.Contains("Patient Administration", index);
        Assert.Contains("Orders and Observations", index);
        Assert.Contains("6.0.0-test", index);
        Assert.Contains("R5", index);

        Assert.True(File.Exists(Path.Combine(outDir, "pa.html")));
        Assert.True(File.Exists(Path.Combine(outDir, "oo.html")));

        string pa = File.ReadAllText(Path.Combine(outDir, "pa.html"));
        Assert.Contains("patient.html", pa);
        Assert.Contains("Zorblax", pa);
        Assert.Contains("Patient", pa);

        // removed baseline entities (null WG) land in the Unassigned page
        Assert.True(File.Exists(Path.Combine(outDir, "unassigned.html")));
        string unassigned = File.ReadAllText(Path.Combine(outDir, "unassigned.html"));
        Assert.Contains("removedpage.html", unassigned);

        // duplicate-artifact-key findings (null WG) also land in the Unassigned page
        Assert.Contains("Duplicate artifact keys", unassigned);
        Assert.Contains("operationoutcome-issue-source", unassigned);
    }

    [Fact]
    public async Task ReportRunner_Overwrite_Guard()
    {
        string dbPath = SeedReviewDb();
        string outDir = Path.Combine(_tempDir, "guarded");

        ReportOptions first = new(dbPath, outDir, Force: false);
        Assert.Equal(0, await RunRedirectedAsync(first));

        ReportOptions second = new(dbPath, outDir, Force: false);
        Assert.Equal(1, await RunRedirectedAsync(second));

        ReportOptions forced = new(dbPath, outDir, Force: true);
        Assert.Equal(0, await RunRedirectedAsync(forced));
    }

    private static async Task<int> RunRedirectedAsync(ReportOptions options)
    {
        TextWriter origOut = Console.Out;
        TextWriter origErr = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return await ReportRunner.RunAsync(options).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }
}
