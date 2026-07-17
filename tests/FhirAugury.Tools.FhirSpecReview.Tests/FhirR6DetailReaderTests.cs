using FhirAugury.Tools.FhirSpecReview.Readers;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// Exercises <see cref="FhirR6DetailReader"/> against a seeded mini
/// <c>fhir-r6.db</c> subset (Packages / Structures / Elements / Operations /
/// SearchParameters). Raw connections use <c>;Pooling=False</c> per repo
/// convention; the temp dir is removed via <see cref="TestFileCleanup"/>.
/// </summary>
public sealed class FhirR6DetailReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public FhirR6DetailReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "r6reader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "fhir-r6.db");
        SeedDb(_dbPath);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void ResolvePackageKey_Resolves_R6()
    {
        FhirR6DetailReader reader = new(_dbPath);
        int? key = reader.ResolvePackageKey(out string? error);
        Assert.Null(error);
        Assert.Equal(1, key);
    }

    [Fact]
    public void ResolveStructureKey_Matches_By_Id_First()
    {
        FhirR6DetailReader reader = new(_dbPath);
        // Patient: Id == Name.
        Assert.Equal(10, reader.ResolveStructureKey(1, "Patient"));
        // bp profile: Id ('bp') != Name ('Observationbp') — must resolve by Id.
        Assert.Equal(11, reader.ResolveStructureKey(1, "bp"));
        // Non-resolvable artifact returns null.
        Assert.Null(reader.ResolveStructureKey(1, "DoesNotExist"));
    }

    [Fact]
    public void LoadElements_Maps_Fields()
    {
        FhirR6DetailReader reader = new(_dbPath);
        List<ArtifactElementDetail> elements = reader.LoadElements(1, 10);
        Assert.Equal(2, elements.Count);

        ArtifactElementDetail identifier = elements[0];
        Assert.Equal("Patient.identifier", identifier.Path);
        Assert.False(identifier.IsRequired);
        Assert.Equal("*", identifier.MaxCardinality);
        Assert.False(identifier.RequiredBinding);

        ArtifactElementDetail gender = elements[1];
        Assert.Equal("Patient.gender", gender.Path);
        Assert.True(gender.IsRequired);
        Assert.True(gender.RequiredBinding);
        Assert.Equal("http://hl7.org/fhir/ValueSet/administrative-gender", gender.RequiredBindingValueSet);
        Assert.False(gender.ExternalRequiredBinding);
        Assert.True(gender.IsModifier);
        Assert.True(gender.HasPattern);
    }

    [Fact]
    public void LoadElements_External_Required_Binding_Detected()
    {
        FhirR6DetailReader reader = new(_dbPath);
        List<ArtifactElementDetail> elements = reader.LoadElements(1, 11);
        ArtifactElementDetail ext = Assert.Single(elements);
        Assert.True(ext.RequiredBinding);
        Assert.True(ext.ExternalRequiredBinding);
    }

    [Fact]
    public void LoadOperations_Token_Membership()
    {
        FhirR6DetailReader reader = new(_dbPath);
        List<ArtifactOperationDetail> patientOps = reader.LoadOperations(1, "Patient");
        Assert.Single(patientOps, o => o.OperationId == "Patient-match");

        // 'Patient' must not match 'PatientXyz' substring nor an unrelated resource.
        List<ArtifactOperationDetail> personOps = reader.LoadOperations(1, "Person");
        Assert.Empty(personOps);
    }

    [Fact]
    public void LoadSearchParameters_MultiResource_Membership()
    {
        FhirR6DetailReader reader = new(_dbPath);
        // individual-address lists Patient,Person,Practitioner,RelatedPerson.
        Assert.Single(reader.LoadSearchParameters(1, "Patient"), s => s.SearchParamId == "individual-address");
        Assert.Single(reader.LoadSearchParameters(1, "Practitioner"), s => s.SearchParamId == "individual-address");
        // A non-member resource gets nothing.
        Assert.Empty(reader.LoadSearchParameters(1, "Observation"));
    }

    private static void SeedDb(string dbPath)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        Exec(conn, """
            CREATE TABLE Packages (Key INTEGER PRIMARY KEY, Name TEXT, PackageId TEXT, FhirVersionShort TEXT, ShortName TEXT);
            CREATE TABLE Structures (Key INTEGER PRIMARY KEY, PackageKey INTEGER, Id TEXT, Name TEXT);
            CREATE TABLE Elements (
                PackageKey INTEGER, Key INTEGER PRIMARY KEY, StructureKey INTEGER, ResourceFieldOrder INTEGER,
                Path TEXT, MinCardinality INTEGER, MaxCardinalityString TEXT, StandardStatus TEXT,
                FixedValue TEXT, PatternValue TEXT, ValueSetBindingStrength TEXT, BindingValueSet TEXT,
                MeaningWhenMissing TEXT, IsModifier INTEGER);
            CREATE TABLE Operations (
                Key INTEGER PRIMARY KEY, PackageKey INTEGER, Id TEXT, Code TEXT, Name TEXT, Kind TEXT,
                Status TEXT, StandardStatus TEXT, FhirMaturity INTEGER, IsExperimental INTEGER,
                WorkGroup TEXT, Description TEXT, ResourceTypes TEXT, AdditionalResourceTypes TEXT);
            CREATE TABLE SearchParameters (
                Key INTEGER PRIMARY KEY, PackageKey INTEGER, Id TEXT, Name TEXT, Status TEXT,
                FhirMaturity INTEGER, StandardStatus TEXT, IsExperimental INTEGER, WorkGroup TEXT,
                SearchType TEXT, Description TEXT, BaseResources TEXT, AdditionalBaseResources TEXT);

            INSERT INTO Packages VALUES (1, 'hl7.fhir.r6.core', 'hl7.fhir.r6.core', '6.0', 'R6');

            INSERT INTO Structures VALUES (10, 1, 'Patient', 'Patient');
            INSERT INTO Structures VALUES (11, 1, 'bp', 'Observationbp');

            INSERT INTO Elements VALUES
                (1, 100, 10, 0, 'Patient.identifier', 0, '*', '', NULL, NULL, NULL, NULL, NULL, 0),
                (1, 101, 10, 1, 'Patient.gender', 1, '1', '', NULL, 'male', 'Required', 'http://hl7.org/fhir/ValueSet/administrative-gender', NULL, 1),
                (1, 102, 11, 0, 'Observation.code', 1, '1', '', NULL, NULL, 'Required', 'http://loinc.org/vs/observation-codes', NULL, 0);

            INSERT INTO Operations VALUES
                (200, 1, 'Patient-match', 'match', 'Patient Match', 'operation', 'active', 'trial-use', 2, 0, 'pa', 'Match a patient', 'Patient', NULL),
                (201, 1, 'PatientXyz-foo', 'foo', 'Decoy', 'operation', 'active', NULL, NULL, 0, 'pa', NULL, 'PatientXyz', NULL);

            INSERT INTO SearchParameters VALUES
                (300, 1, 'individual-address', 'address', 'active', 3, 'normative', 0, 'pa', 'string', 'An address', 'Patient,Person,Practitioner,RelatedPerson', NULL),
                (301, 1, 'Patient-active', 'active', 'active', 3, 'normative', 0, 'pa', 'token', 'Active flag', 'Patient', NULL);
            """);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
