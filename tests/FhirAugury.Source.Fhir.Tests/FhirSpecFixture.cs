using FhirAugury.Source.Fhir.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Fhir.Tests;

/// <summary>
/// Builds a tiny synthetic spec database that mirrors the real
/// <c>cache/fhir-spec.db</c> schema (the columns the readers query), so the test
/// suite never depends on the 196&#160;MB production database. Three releases are
/// present (R4, R5, R6-ballot) plus a focused set of structures, terminology,
/// operations, and search parameters.
/// </summary>
public sealed class FhirSpecFixture : IDisposable
{
    public string DatabasePath { get; }

    public FhirSpecFixture()
    {
        DatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"fhir-spec-fixture-{Guid.NewGuid():N}.db");

        using SqliteConnection conn = new($"Data Source={DatabasePath};Pooling=False");
        conn.Open();
        Exec(conn, Schema);
        Exec(conn, SeedData);
    }

    /// <summary>Creates a read-only <see cref="FhirSpecDatabase"/> over the fixture file.</summary>
    public FhirSpecDatabase CreateDatabase()
        => new(DatabasePath, NullLogger<FhirSpecDatabase>.Instance);

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => TestFileCleanup.SafeDeleteFile(DatabasePath);

    // ── Schema (subset of real columns the readers query) ────────────
    private const string Schema = """
        CREATE TABLE Packages (
            Key INTEGER PRIMARY KEY, Name TEXT NOT NULL, PackageId TEXT NOT NULL,
            PackageVersion TEXT NOT NULL, FhirVersionShort TEXT NOT NULL,
            CanonicalUrl TEXT NOT NULL, WebUrl TEXT, ShortName TEXT NOT NULL,
            Title TEXT, Description TEXT, DefinitionFhirSequence TEXT NOT NULL,
            ProcessDate TEXT);

        CREATE TABLE Structures (
            Id TEXT NOT NULL, VersionedUrl TEXT NOT NULL, UnversionedUrl TEXT NOT NULL,
            Name TEXT NOT NULL, Version TEXT NOT NULL, Status TEXT, Title TEXT,
            Description TEXT, Narrative TEXT, StandardStatus TEXT, WorkGroup TEXT,
            FhirMaturity INTEGER, PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY,
            Comment TEXT, ArtifactClass TEXT NOT NULL, SnapshotCount INTEGER NOT NULL,
            DifferentialCount INTEGER NOT NULL, Implements TEXT, Kind TEXT,
            IsAbstract INTEGER, FhirType TEXT, BaseDefinition TEXT,
            BaseDefinitionShort TEXT, Derivation TEXT);

        CREATE TABLE Elements (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, StructureKey INTEGER NOT NULL,
            ParentElementKey INTEGER, ResourceFieldOrder INTEGER NOT NULL,
            ComponentFieldOrder INTEGER NOT NULL, Id TEXT NOT NULL, Path TEXT NOT NULL,
            ChildElementCount INTEGER NOT NULL, Name TEXT NOT NULL, Short TEXT, Definition TEXT,
            MinCardinality INTEGER NOT NULL, MaxCardinality INTEGER NOT NULL,
            MaxCardinalityString TEXT NOT NULL, SliceName TEXT,
            FullCollatedTypeLiteral TEXT NOT NULL, ValueSetBindingStrength TEXT,
            BindingValueSet TEXT, BindingValueSetKey INTEGER, AdditionalBindingCount INTEGER NOT NULL,
            BindingDescription TEXT, IsInherited INTEGER NOT NULL, IsSimpleType INTEGER NOT NULL,
            IsModifier INTEGER NOT NULL, IsModifierReason TEXT, StandardStatus TEXT,
            FixedValue TEXT, PatternValue TEXT, MeaningWhenMissing TEXT);

        CREATE TABLE ElementTypes (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, StructureKey INTEGER NOT NULL,
            ElementKey INTEGER NOT NULL, CollatedTypeKey INTEGER NOT NULL, TypeName TEXT,
            TypeProfile TEXT, TargetProfile TEXT, TypeStructureKey INTEGER);

        CREATE TABLE ElementAdditionalBindings (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, StructureKey INTEGER NOT NULL,
            ElementKey INTEGER NOT NULL, FhirKey TEXT, Purpose TEXT, BindingValueSet TEXT,
            BindingValueSetKey INTEGER, Documentation TEXT, ShortDocumentation TEXT,
            CollatedUsageContexts TEXT, SatisfiedBySingleRepetition INTEGER);

        CREATE TABLE CodeSystems (
            Id TEXT NOT NULL, VersionedUrl TEXT NOT NULL, UnversionedUrl TEXT NOT NULL,
            Name TEXT NOT NULL, Version TEXT NOT NULL, Status TEXT, Title TEXT, Description TEXT,
            Narrative TEXT, StandardStatus TEXT, WorkGroup TEXT, FhirMaturity INTEGER,
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, IsCaseSensitive INTEGER,
            HierarchyMeaning TEXT, IsCompositional INTEGER, Content TEXT, Count INTEGER);

        CREATE TABLE CodeSystemConcepts (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, CodeSystemKey INTEGER NOT NULL,
            FlatOrder INTEGER NOT NULL, RelativeOrder INTEGER NOT NULL, Code TEXT NOT NULL,
            Display TEXT, Definition TEXT, Designations TEXT NOT NULL, Properties TEXT NOT NULL,
            ParentConceptKey INTEGER, ChildConceptCount INTEGER NOT NULL);

        CREATE TABLE CodeSystemConceptProperties (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, CodeSystemConceptKey INTEGER NOT NULL,
            CodeSystemPropertyDefinitionKey INTEGER NOT NULL, Code TEXT NOT NULL, Type TEXT NOT NULL,
            Value TEXT NOT NULL);

        CREATE TABLE CodeSystemPropertyDefinitions (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, CodeSystemKey INTEGER NOT NULL,
            Code TEXT NOT NULL, Uri TEXT, Description TEXT, Type TEXT NOT NULL);

        CREATE TABLE ValueSets (
            Id TEXT NOT NULL, VersionedUrl TEXT NOT NULL, UnversionedUrl TEXT NOT NULL,
            Name TEXT NOT NULL, Version TEXT NOT NULL, Status TEXT, Title TEXT, Description TEXT,
            Narrative TEXT, StandardStatus TEXT, WorkGroup TEXT, FhirMaturity INTEGER,
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, CanExpand INTEGER NOT NULL,
            IsExcluded INTEGER NOT NULL, ConceptCount INTEGER NOT NULL,
            ActiveConcreteConceptCount INTEGER NOT NULL, ReferencedSystems TEXT,
            BindingCountCore INTEGER NOT NULL, StrongestBindingCore TEXT,
            BindingCountExtended INTEGER NOT NULL, Compose TEXT);

        CREATE TABLE ValueSetConcepts (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, ValueSetKey INTEGER NOT NULL,
            System TEXT NOT NULL, SystemVersion TEXT NOT NULL, Code TEXT NOT NULL, Display TEXT,
            Inactive INTEGER NOT NULL, Abstract INTEGER NOT NULL, Properties TEXT);

        CREATE TABLE ValueSetSystems (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, ValueSetKey INTEGER NOT NULL,
            System TEXT NOT NULL, Version TEXT, CodeSystemKey INTEGER);

        CREATE TABLE Operations (
            Id TEXT NOT NULL, VersionedUrl TEXT NOT NULL, UnversionedUrl TEXT NOT NULL,
            Name TEXT NOT NULL, Version TEXT NOT NULL, Status TEXT, Title TEXT, Description TEXT,
            Narrative TEXT, StandardStatus TEXT, WorkGroup TEXT, FhirMaturity INTEGER,
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, Kind TEXT NOT NULL,
            AffectsState INTEGER, Code TEXT, Comment TEXT, BaseCanonical TEXT, ResourceTypes TEXT,
            AdditionalResourceTypes TEXT, InvokeOnSystem INTEGER NOT NULL, InvokeOnType INTEGER NOT NULL,
            InvokeOnInstance INTEGER NOT NULL, ParameterCount INTEGER NOT NULL);

        CREATE TABLE OperationParameters (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, OperationKey INTEGER NOT NULL,
            Name TEXT NOT NULL, Use TEXT NOT NULL, Scopes TEXT, Min INTEGER NOT NULL, Max TEXT NOT NULL,
            Documentation TEXT, Type TEXT, AllowedTypes TEXT, TargetProfileCanonicals TEXT,
            SearchType TEXT, BindingStrength TEXT, BindingValueSetCanonical TEXT,
            ParentParameterKey INTEGER, ChildParameterCount INTEGER NOT NULL,
            OperationParameterOrder INTEGER NOT NULL, ParameterPartOrder INTEGER NOT NULL);

        CREATE TABLE SearchParameters (
            Id TEXT NOT NULL, VersionedUrl TEXT NOT NULL, UnversionedUrl TEXT NOT NULL,
            Name TEXT NOT NULL, Version TEXT NOT NULL, Status TEXT, Title TEXT, Description TEXT,
            Narrative TEXT, StandardStatus TEXT, WorkGroup TEXT, FhirMaturity INTEGER,
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, Code TEXT NOT NULL,
            AliasCodes TEXT, BaseResources TEXT NOT NULL, AdditionalBaseResources TEXT,
            SearchType TEXT, Expression TEXT, ProcessingMode TEXT, ReferenceTargets TEXT,
            MultipleOr INTEGER, MultipleAnd INTEGER, Comparators TEXT, Modifiers TEXT,
            ChainableSearchParameters TEXT, ComponentCount INTEGER NOT NULL);

        CREATE TABLE SearchParameterComponents (
            PackageKey INTEGER NOT NULL, Key INTEGER PRIMARY KEY, SearchParameterKey INTEGER NOT NULL,
            DefinitionCanonical TEXT NOT NULL, Expression TEXT NOT NULL);
        """;

    // ── Seed data ────────────────────────────────────────────────────
    private const string SeedData = """
        INSERT INTO Packages (Key, Name, PackageId, PackageVersion, FhirVersionShort, CanonicalUrl, ShortName, Title, DefinitionFhirSequence) VALUES
            (1, 'hl7.fhir.r2.core', 'hl7.fhir.r2.core', '1.0.2', '1.0', 'http://hl7.org/fhir', 'DSTU2', 'FHIR R2 package : Core', 'DSTU2'),
            (4, 'hl7.fhir.r4.core', 'hl7.fhir.r4.core', '4.0.1', '4.0', 'http://hl7.org/fhir', 'R4', 'FHIR R4 package : Core', 'R4'),
            (5, 'hl7.fhir.r5.core', 'hl7.fhir.r5.core', '5.0.0', '5.0', 'http://hl7.org/fhir', 'R5', 'FHIR R5 package : Core', 'R5'),
            (6, 'hl7.fhir.r6.core', 'hl7.fhir.r6.core', '6.0.0-ballot4', '6.0', 'http://hl7.org/fhir', 'R6', 'FHIR R6 package : Core', 'R6');

        -- R5 structures
        INSERT INTO Structures (Id, VersionedUrl, UnversionedUrl, Name, Version, Status, Title, Description, StandardStatus, WorkGroup, FhirMaturity, PackageKey, Key, ArtifactClass, SnapshotCount, DifferentialCount, Kind, IsAbstract, FhirType, BaseDefinition) VALUES
            ('Observation', 'http://hl7.org/fhir/StructureDefinition/Observation|5.0.0', 'http://hl7.org/fhir/StructureDefinition/Observation', 'Observation', '5.0.0', 'active', 'Observation', 'Measurements and simple assertions.', 'normative', 'oo', 5, 5, 100, 'Resource', 4, 0, 'resource', 0, 'Observation', 'http://hl7.org/fhir/StructureDefinition/DomainResource'),
            ('Patient', 'http://hl7.org/fhir/StructureDefinition/Patient|5.0.0', 'http://hl7.org/fhir/StructureDefinition/Patient', 'Patient', '5.0.0', 'active', 'Patient', 'Demographics about a person.', 'trial-use', 'pa', 5, 5, 101, 'Resource', 3, 0, 'resource', 0, 'Patient', 'http://hl7.org/fhir/StructureDefinition/DomainResource'),
            ('HumanName', 'http://hl7.org/fhir/StructureDefinition/HumanName|5.0.0', 'http://hl7.org/fhir/StructureDefinition/HumanName', 'HumanName', '5.0.0', 'active', 'HumanName', 'A name of a human.', 'normative', 'fhir', 5, 5, 102, 'ComplexType', 1, 0, 'complex-type', 0, 'HumanName', 'http://hl7.org/fhir/StructureDefinition/DataType'),
            ('string', 'http://hl7.org/fhir/StructureDefinition/string|5.0.0', 'http://hl7.org/fhir/StructureDefinition/string', 'string', '5.0.0', 'active', 'string', 'A sequence of Unicode characters.', 'normative', 'fhir', 5, 5, 103, 'PrimitiveType', 1, 0, 'primitive-type', 0, 'string', 'http://hl7.org/fhir/StructureDefinition/PrimitiveType');

        -- R6 structures (profile + interface partitioning)
        INSERT INTO Structures (Id, VersionedUrl, UnversionedUrl, Name, Version, Status, Title, PackageKey, Key, ArtifactClass, SnapshotCount, DifferentialCount, Kind, FhirType, BaseDefinition) VALUES
            ('Observation', 'http://hl7.org/fhir/StructureDefinition/Observation|6.0.0-ballot4', 'http://hl7.org/fhir/StructureDefinition/Observation', 'Observation', '6.0.0-ballot4', 'active', 'Observation', 6, 120, 'Resource', 4, 0, 'resource', 'Observation', 'http://hl7.org/fhir/StructureDefinition/DomainResource'),
            ('vitalsigns', 'http://hl7.org/fhir/StructureDefinition/vitalsigns|6.0.0-ballot4', 'http://hl7.org/fhir/StructureDefinition/vitalsigns', 'observation-vitalsigns', '6.0.0-ballot4', 'active', 'Vital Signs Profile', 6, 121, 'Profile', 2, 1, 'resource', 'Observation', 'http://hl7.org/fhir/StructureDefinition/Observation'),
            ('CanonicalResource', 'http://hl7.org/fhir/StructureDefinition/CanonicalResource|6.0.0-ballot4', 'http://hl7.org/fhir/StructureDefinition/CanonicalResource', 'CanonicalResource', '6.0.0-ballot4', 'active', 'CanonicalResource', 6, 122, 'Interface', 1, 0, 'resource', 'CanonicalResource', 'http://hl7.org/fhir/StructureDefinition/Base');

        -- Observation elements (R5)
        INSERT INTO Elements (PackageKey, Key, StructureKey, ParentElementKey, ResourceFieldOrder, ComponentFieldOrder, Id, Path, ChildElementCount, Name, Short, Definition, MinCardinality, MaxCardinality, MaxCardinalityString, FullCollatedTypeLiteral, ValueSetBindingStrength, BindingValueSet, BindingValueSetKey, AdditionalBindingCount, IsInherited, IsSimpleType, IsModifier) VALUES
            (5, 1000, 100, NULL, 0, 0, 'Observation', 'Observation', 3, 'Observation', 'Measurements', 'Measurements and simple assertions.', 0, 2147483647, '*', '', NULL, NULL, NULL, 0, 0, 0, 0),
            (5, 1001, 100, 1000, 1, 0, 'Observation.status', 'Observation.status', 0, 'status', 'registered | preliminary | final', 'The status of the result value.', 1, 1, '1', 'code', 'Required', 'http://hl7.org/fhir/ValueSet/observation-status', 200, 0, 0, 1, 1),
            (5, 1002, 100, 1000, 2, 0, 'Observation.code', 'Observation.code', 0, 'code', 'Type of observation', 'Describes what was observed.', 1, 1, '1', 'CodeableConcept', NULL, NULL, NULL, 0, 0, 0, 0),
            (5, 1003, 100, 1000, 3, 0, 'Observation.subject', 'Observation.subject', 0, 'subject', 'Who/what the observation is about', 'The patient.', 0, 1, '1', 'Reference', NULL, NULL, NULL, 0, 0, 0, 0);

        -- Patient elements (R5) for element-by-path
        INSERT INTO Elements (PackageKey, Key, StructureKey, ParentElementKey, ResourceFieldOrder, ComponentFieldOrder, Id, Path, ChildElementCount, Name, Short, MinCardinality, MaxCardinality, MaxCardinalityString, FullCollatedTypeLiteral, AdditionalBindingCount, IsInherited, IsSimpleType, IsModifier) VALUES
            (5, 1100, 101, NULL, 0, 0, 'Patient', 'Patient', 1, 'Patient', 'Patient', 0, 2147483647, '*', '', 0, 0, 0, 0),
            (5, 1101, 101, 1100, 1, 0, 'Patient.contact', 'Patient.contact', 1, 'contact', 'A contact party', 0, 2147483647, '*', 'BackboneElement', 0, 0, 0, 0),
            (5, 1102, 101, 1101, 2, 0, 'Patient.contact.name', 'Patient.contact.name', 0, 'name', 'A name', 0, 1, '1', 'HumanName', 0, 0, 0, 0);

        INSERT INTO ElementTypes (PackageKey, Key, StructureKey, ElementKey, CollatedTypeKey, TypeName, TypeProfile, TargetProfile) VALUES
            (5, 5000, 100, 1001, 0, 'code', NULL, NULL),
            (5, 5001, 100, 1002, 0, 'CodeableConcept', NULL, NULL),
            (5, 5002, 100, 1003, 0, 'Reference', NULL, 'http://hl7.org/fhir/StructureDefinition/Patient'),
            (5, 5003, 100, 1003, 0, 'Reference', NULL, 'http://hl7.org/fhir/StructureDefinition/Group'),
            (5, 5004, 101, 1102, 0, 'HumanName', NULL, NULL);

        INSERT INTO ElementAdditionalBindings (PackageKey, Key, StructureKey, ElementKey, Purpose, BindingValueSet, BindingValueSetKey, ShortDocumentation) VALUES
            (5, 6000, 100, 1002, 'preferred', 'http://hl7.org/fhir/ValueSet/observation-codes', NULL, 'LOINC codes');

        -- CodeSystem observation-status (R5)
        INSERT INTO CodeSystems (Id, VersionedUrl, UnversionedUrl, Name, Version, Status, Title, Description, StandardStatus, WorkGroup, FhirMaturity, PackageKey, Key, IsCaseSensitive, HierarchyMeaning, Content, Count) VALUES
            ('observation-status', 'http://hl7.org/fhir/observation-status|5.0.0', 'http://hl7.org/fhir/observation-status', 'ObservationStatus', '5.0.0', 'active', 'ObservationStatus', 'Codes providing the status of an observation.', 'normative', 'oo', 5, 5, 300, 1, 'is-a', 'complete', 3);

        INSERT INTO CodeSystemConcepts (PackageKey, Key, CodeSystemKey, FlatOrder, RelativeOrder, Code, Display, Definition, Designations, Properties, ParentConceptKey, ChildConceptCount) VALUES
            (5, 400, 300, 0, 0, 'final', 'Final', 'The observation is complete.', '[{"use":{"system":"http://acme.com/x","code":"label"},"value":"Final result"}]', '[{"code":"status","valueCode":"active"}]', NULL, 1),
            (5, 401, 300, 1, 0, 'amended', 'Amended', 'Subsequently amended.', '[]', '[]', 400, 0),
            (5, 402, 300, 2, 0, 'registered', 'Registered', 'Exists but no value yet.', '[]', '[]', NULL, 0);

        INSERT INTO CodeSystemPropertyDefinitions (PackageKey, Key, CodeSystemKey, Code, Uri, Description, Type) VALUES
            (5, 500, 300, 'status', 'http://hl7.org/fhir/concept-properties#status', 'Status of the concept', 'code');

        INSERT INTO CodeSystemConceptProperties (PackageKey, Key, CodeSystemConceptKey, CodeSystemPropertyDefinitionKey, Code, Type, Value) VALUES
            (5, 700, 400, 500, 'status', 'code', 'active');

        -- ValueSet observation-status (R5), bound by Observation.status
        INSERT INTO ValueSets (Id, VersionedUrl, UnversionedUrl, Name, Version, Status, Title, Description, StandardStatus, WorkGroup, FhirMaturity, PackageKey, Key, CanExpand, IsExcluded, ConceptCount, ActiveConcreteConceptCount, BindingCountCore, StrongestBindingCore, BindingCountExtended, Compose) VALUES
            ('observation-status', 'http://hl7.org/fhir/ValueSet/observation-status|5.0.0', 'http://hl7.org/fhir/ValueSet/observation-status', 'ObservationStatus', '5.0.0', 'active', 'ObservationStatus', 'Codes for the status.', 'normative', 'oo', 5, 5, 200, 1, 0, 3, 3, 1, 'Required', 0, '{"include":[{"system":"http://hl7.org/fhir/observation-status"}]}');

        INSERT INTO ValueSetConcepts (PackageKey, Key, ValueSetKey, System, SystemVersion, Code, Display, Inactive, Abstract) VALUES
            (5, 800, 200, 'http://hl7.org/fhir/observation-status', '5.0.0', 'final', 'Final', 0, 0),
            (5, 801, 200, 'http://hl7.org/fhir/observation-status', '5.0.0', 'amended', 'Amended', 0, 0),
            (5, 802, 200, 'http://hl7.org/fhir/observation-status', '5.0.0', 'registered', 'Registered', 0, 0);

        INSERT INTO ValueSetSystems (PackageKey, Key, ValueSetKey, System, Version, CodeSystemKey) VALUES
            (5, 900, 200, 'http://hl7.org/fhir/observation-status', '5.0.0', 300);

        -- Operation ValueSet-expand (R5)
        INSERT INTO Operations (Id, VersionedUrl, UnversionedUrl, Name, Version, Status, Title, Description, StandardStatus, WorkGroup, FhirMaturity, PackageKey, Key, Kind, AffectsState, Code, ResourceTypes, InvokeOnSystem, InvokeOnType, InvokeOnInstance, ParameterCount) VALUES
            ('ValueSet-expand', 'http://hl7.org/fhir/OperationDefinition/ValueSet-expand|5.0.0', 'http://hl7.org/fhir/OperationDefinition/ValueSet-expand', 'Expand', '5.0.0', 'active', 'Value Set Expansion', 'Expand a value set.', 'trial-use', 'vocab', 3, 5, 250, 'Operation', 0, 'expand', 'ValueSet', 1, 1, 1, 2);

        INSERT INTO OperationParameters (PackageKey, Key, OperationKey, Name, Use, Min, Max, Documentation, Type, ChildParameterCount, OperationParameterOrder, ParameterPartOrder) VALUES
            (5, 1500, 250, 'url', 'in', 0, '1', 'A canonical reference to a value set.', 'uri', 0, 0, 0),
            (5, 1501, 250, 'return', 'out', 1, '1', 'The expansion.', 'ValueSet', 0, 1, 0);

        -- SearchParameters (R5)
        INSERT INTO SearchParameters (Id, VersionedUrl, UnversionedUrl, Name, Version, Status, Description, StandardStatus, WorkGroup, FhirMaturity, PackageKey, Key, Code, BaseResources, SearchType, Expression, ReferenceTargets, ComponentCount) VALUES
            ('Observation-code', 'http://hl7.org/fhir/SearchParameter/Observation-code|5.0.0', 'http://hl7.org/fhir/SearchParameter/Observation-code', 'Observation-code', '5.0.0', 'active', 'The code of the observation type.', 'trial-use', 'oo', 3, 5, 350, 'code', 'Observation', 'token', 'Observation.code', '', 0),
            ('Observation-subject', 'http://hl7.org/fhir/SearchParameter/Observation-subject|5.0.0', 'http://hl7.org/fhir/SearchParameter/Observation-subject', 'Observation-subject', '5.0.0', 'active', 'The subject of the observation.', 'trial-use', 'oo', 3, 5, 351, 'subject', 'Observation', 'reference', 'Observation.subject', 'Patient,Group', 0),
            ('clinical-patient', 'http://hl7.org/fhir/SearchParameter/clinical-patient|5.0.0', 'http://hl7.org/fhir/SearchParameter/clinical-patient', 'clinical-patient', '5.0.0', 'active', 'Patient across clinical resources.', 'trial-use', 'oo', 3, 5, 352, 'patient', 'Observation,Condition', 'reference', 'Observation.subject', 'Patient', 0),
            ('Observation-combo', 'http://hl7.org/fhir/SearchParameter/Observation-combo|5.0.0', 'http://hl7.org/fhir/SearchParameter/Observation-combo', 'Observation-combo', '5.0.0', 'active', 'Code and value composite.', 'trial-use', 'oo', 3, 5, 353, 'combo-code-value-quantity', 'Observation', 'composite', NULL, '', 2);

        INSERT INTO SearchParameterComponents (PackageKey, Key, SearchParameterKey, DefinitionCanonical, Expression) VALUES
            (5, 1600, 353, 'http://hl7.org/fhir/SearchParameter/Observation-combo-code', 'code'),
            (5, 1601, 353, 'http://hl7.org/fhir/SearchParameter/Observation-combo-value-quantity', 'value.ofType(Quantity)');
        """;
}
