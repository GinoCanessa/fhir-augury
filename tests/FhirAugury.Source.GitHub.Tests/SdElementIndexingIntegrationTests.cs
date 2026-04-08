using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class SdElementIndexingIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly StructureDefinitionIndexer _indexer;
    private readonly string _tempDir;

    public SdElementIndexingIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sd_element_integ_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _indexer = new StructureDefinitionIndexer(NullLogger<StructureDefinitionIndexer>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_element_clone_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void IndexStructureDefinitions_InsertsElementsWithSdId()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <title value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential>
                <element>
                  <id value="Patient"/>
                  <path value="Patient"/>
                  <short value="Information about an individual"/>
                  <definition value="Demographics and administrative information."/>
                </element>
                <element>
                  <id value="Patient.name"/>
                  <path value="Patient.name"/>
                  <short value="A name associated with the patient"/>
                  <min value="0"/>
                  <max value="*"/>
                  <type>
                    <code value="HumanName"/>
                  </type>
                </element>
                <element>
                  <id value="Patient.gender"/>
                  <path value="Patient.gender"/>
                  <short value="male | female | other | unknown"/>
                  <min value="0"/>
                  <max value="1"/>
                  <type>
                    <code value="code"/>
                  </type>
                  <binding>
                    <strength value="required"/>
                    <valueSet value="http://hl7.org/fhir/ValueSet/administrative-gender"/>
                  </binding>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        // Verify SD record
        List<GitHubStructureDefinitionRecord> sds = GitHubStructureDefinitionRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        Assert.Single(sds);

        // Verify element records
        List<GitHubSdElementRecord> elements = GitHubSdElementRecord.SelectList(connection, StructureDefinitionId: sds[0].Id);
        Assert.Equal(3, elements.Count);

        // Verify FK relationship
        Assert.All(elements, e => Assert.Equal(sds[0].Id, e.StructureDefinitionId));

        // Verify FieldOrder is sequential
        List<int> orders = elements.OrderBy(e => e.FieldOrder).Select(e => e.FieldOrder).ToList();
        Assert.Equal([0, 1, 2], orders);

        // Verify specific element data
        GitHubSdElementRecord genderElement = elements.First(e => e.Path == "Patient.gender");
        Assert.Equal("required", genderElement.BindingStrength);
        Assert.Equal("http://hl7.org/fhir/ValueSet/administrative-gender", genderElement.BindingValueSet);
        Assert.Equal("code", genderElement.Types);
    }

    [Fact]
    public void IndexStructureDefinitions_ReIndex_ClearsExistingElements()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential>
                <element>
                  <id value="Patient"/>
                  <path value="Patient"/>
                  <short value="Information about an individual"/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();

        // First run
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);
        int firstRunCount = GitHubSdElementRecord.SelectList(connection, RepoFullName: "HL7/fhir").Count;
        Assert.Equal(1, firstRunCount);

        // Second run — should clear and re-insert, not double
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);
        int secondRunCount = GitHubSdElementRecord.SelectList(connection, RepoFullName: "HL7/fhir").Count;
        Assert.Equal(1, secondRunCount);
    }

    [Fact]
    public void IndexStructureDefinitions_EmptyDifferential_NoElements()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential/>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubSdElementRecord> elements = GitHubSdElementRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        Assert.Empty(elements);
    }

    [Fact]
    public void IndexStructureDefinitions_MultipleRepos_ElementsIsolated()
    {
        string sdXml1 = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential>
                <element>
                  <id value="Patient"/>
                  <path value="Patient"/>
                  <short value="A patient"/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string sdXml2 = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://example.org/StructureDefinition/MyPatient"/>
              <name value="MyPatient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <derivation value="constraint"/>
              <differential>
                <element>
                  <id value="Patient"/>
                  <path value="Patient"/>
                  <short value="Custom patient"/>
                </element>
                <element>
                  <id value="Patient.name"/>
                  <path value="Patient.name"/>
                  <short value="Required name"/>
                  <min value="1"/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string filePath1 = CreateFile("source/patient/structuredefinition-patient.xml", sdXml1);
        string filePath2 = CreateFile("profiles/structuredefinition-mypatient.xml", sdXml2);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath1], _tempDir, connection, CancellationToken.None);
        _indexer.IndexStructureDefinitions("example/ig", [filePath2], _tempDir, connection, CancellationToken.None);

        List<GitHubSdElementRecord> repo1Elements = GitHubSdElementRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        List<GitHubSdElementRecord> repo2Elements = GitHubSdElementRecord.SelectList(connection, RepoFullName: "example/ig");
        Assert.Single(repo1Elements);
        Assert.Equal(2, repo2Elements.Count);

        // Re-indexing repo1 should not affect repo2
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath1], _tempDir, connection, CancellationToken.None);
        Assert.Equal(2, GitHubSdElementRecord.SelectList(connection, RepoFullName: "example/ig").Count);
    }

    [Fact]
    public void IndexStructureDefinitions_TypeMapping_SemicolonJoined()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Observation"/>
              <name value="Observation"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Observation"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential>
                <element>
                  <id value="Observation.value[x]"/>
                  <path value="Observation.value[x]"/>
                  <short value="Actual result"/>
                  <min value="0"/>
                  <max value="1"/>
                  <type>
                    <code value="Quantity"/>
                  </type>
                  <type>
                    <code value="CodeableConcept"/>
                  </type>
                  <type>
                    <code value="string"/>
                  </type>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/observation/structuredefinition-observation.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubSdElementRecord> elements = GitHubSdElementRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        Assert.Single(elements);

        GitHubSdElementRecord valueElement = elements[0];
        Assert.Equal("Quantity;CodeableConcept;string", valueElement.Types);
    }

    [Fact]
    public void IndexStructureDefinitions_IsModifier_BoolToInt()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential>
                <element>
                  <id value="Patient.active"/>
                  <path value="Patient.active"/>
                  <short value="Whether this patient record is active"/>
                  <isModifier value="true"/>
                  <isSummary value="true"/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubSdElementRecord> elements = GitHubSdElementRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        Assert.Single(elements);
        Assert.Equal(1, elements[0].IsModifier);
        Assert.Equal(1, elements[0].IsSummary);
    }

    [Fact]
    public void ArtifactFileMapper_ElementPath_UsesGitHubSdElements()
    {
        using SqliteConnection connection = _db.OpenConnection();

        GitHubStructureDefinitionRecord sd = new()
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FilePath = "source/patient/structuredefinition-Patient.xml",
            Url = "http://hl7.org/fhir/StructureDefinition/Patient",
            Name = "Patient",
            ArtifactClass = "Resource",
            Kind = "resource",
        };
        GitHubStructureDefinitionRecord.Insert(connection, sd);

        GitHubSdElementRecord element = new()
        {
            Id = GitHubSdElementRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            StructureDefinitionId = sd.Id,
            ElementId = "Patient.contact.relationship",
            Path = "Patient.contact.relationship",
            Name = "relationship",
            FieldOrder = 0,
        };
        GitHubSdElementRecord.Insert(connection, element);

        Indexing.ArtifactFileMapper mapper = new(_db, NullLogger<Indexing.ArtifactFileMapper>.Instance);
        List<string> paths = mapper.ResolveFilePaths(connection, "HL7/fhir", elementPath: "Patient.contact.relationship");

        Assert.Single(paths);
        Assert.Equal("source/patient/structuredefinition-Patient.xml", paths[0]);
    }

    private string CreateFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }
}
