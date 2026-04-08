namespace FhirAugury.Parsing.Fhir.Tests;

public class FhirContentParserTests
{
    // ────────────────────────────────────────────────────────
    // TryParseStructureDefinition — XML
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseStructureDefinition_PatientXml_ReturnsCorrectMetadata()
    {
        string xml = """
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
                  <definition value="Demographics and other administrative information."/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition(xml, "xml");

        Assert.NotNull(result);
        Assert.Equal("http://hl7.org/fhir/StructureDefinition/Patient", result.Url);
        Assert.Equal("Patient", result.Name);
        Assert.Equal("Patient", result.Title);
        Assert.Equal("active", result.Status);
        Assert.Equal("resource", result.Kind);
        Assert.Equal(false, result.IsAbstract);
        Assert.Equal("Patient", result.FhirType);
        Assert.Equal("http://hl7.org/fhir/StructureDefinition/DomainResource", result.BaseDefinition);
        Assert.Equal("specialization", result.Derivation);
        Assert.Single(result.DifferentialElements);
    }

    // ────────────────────────────────────────────────────────
    // TryParseStructureDefinition — JSON
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseStructureDefinition_PatientJson_ReturnsCorrectMetadata()
    {
        string json = """
            {
              "resourceType": "StructureDefinition",
              "url": "http://hl7.org/fhir/StructureDefinition/Patient",
              "name": "Patient",
              "title": "Patient",
              "status": "active",
              "kind": "resource",
              "abstract": false,
              "type": "Patient",
              "baseDefinition": "http://hl7.org/fhir/StructureDefinition/DomainResource",
              "derivation": "specialization",
              "differential": {
                "element": [{
                  "id": "Patient",
                  "path": "Patient",
                  "short": "Information about an individual"
                }]
              }
            }
            """;

        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition(json, "json");

        Assert.NotNull(result);
        Assert.Equal("http://hl7.org/fhir/StructureDefinition/Patient", result.Url);
        Assert.Equal("Patient", result.Name);
        Assert.Equal("active", result.Status);
        Assert.Equal("resource", result.Kind);
        Assert.Single(result.DifferentialElements);
    }

    // ────────────────────────────────────────────────────────
    // TryParseStructureDefinition — Extension with contexts
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseStructureDefinition_ExtensionSd_ReturnsContexts()
    {
        string xml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://example.org/fhir/StructureDefinition/my-extension"/>
              <name value="MyExtension"/>
              <status value="draft"/>
              <kind value="complex-type"/>
              <abstract value="false"/>
              <type value="Extension"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/Extension"/>
              <derivation value="constraint"/>
              <context>
                <type value="element"/>
                <expression value="Patient"/>
              </context>
              <context>
                <type value="element"/>
                <expression value="Observation.component"/>
              </context>
              <differential>
                <element>
                  <id value="Extension"/>
                  <path value="Extension"/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition(xml, "xml");

        Assert.NotNull(result);
        Assert.Equal("Extension", result.FhirType);
        Assert.Equal("constraint", result.Derivation);
        Assert.NotNull(result.Contexts);
        Assert.Equal(2, result.Contexts.Count);
        Assert.Equal("element", result.Contexts[0].Type);
        Assert.Equal("Patient", result.Contexts[0].Expression);
        Assert.Equal("Observation.component", result.Contexts[1].Expression);
    }

    // ────────────────────────────────────────────────────────
    // TryParseStructureDefinition — FHIR extensions (WG, FMM, status)
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseStructureDefinition_WithExtensions_ReturnsWgFmmStatus()
    {
        string xml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-wg">
                <valueCode value="pa"/>
              </extension>
              <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm">
                <valueInteger value="5"/>
              </extension>
              <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status">
                <valueCode value="normative"/>
              </extension>
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
                </element>
              </differential>
            </StructureDefinition>
            """;

        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition(xml, "xml");

        Assert.NotNull(result);
        Assert.Equal("pa", result.WorkGroup);
        Assert.Equal(5, result.FhirMaturity);
        Assert.Equal("normative", result.StandardsStatus);
    }

    // ────────────────────────────────────────────────────────
    // TryParseStructureDefinition — Differential element details
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseStructureDefinition_DifferentialElements_ReturnsAllFields()
    {
        string xml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://example.org/fhir/StructureDefinition/TestProfile"/>
              <name value="TestProfile"/>
              <status value="draft"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <derivation value="constraint"/>
              <differential>
                <element>
                  <id value="Patient"/>
                  <path value="Patient"/>
                </element>
                <element>
                  <id value="Patient.identifier"/>
                  <path value="Patient.identifier"/>
                  <short value="An identifier for this patient"/>
                  <min value="1"/>
                  <max value="*"/>
                  <type>
                    <code value="Identifier"/>
                  </type>
                </element>
                <element>
                  <id value="Patient.generalPractitioner"/>
                  <path value="Patient.generalPractitioner"/>
                  <type>
                    <code value="Reference"/>
                    <targetProfile value="http://hl7.org/fhir/StructureDefinition/Practitioner"/>
                    <targetProfile value="http://hl7.org/fhir/StructureDefinition/Organization"/>
                  </type>
                </element>
              </differential>
            </StructureDefinition>
            """;

        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition(xml, "xml");

        Assert.NotNull(result);
        Assert.Equal(3, result.DifferentialElements.Count);

        ElementInfo root = result.DifferentialElements[0];
        Assert.Equal("Patient", root.ElementId);
        Assert.Equal("Patient", root.Path);
        Assert.Equal("Patient", root.Name);
        Assert.Equal(0, root.FieldOrder);

        ElementInfo identifier = result.DifferentialElements[1];
        Assert.Equal("Patient.identifier", identifier.Path);
        Assert.Equal("identifier", identifier.Name);
        Assert.Equal("An identifier for this patient", identifier.Short);
        Assert.Equal(1, identifier.MinCardinality);
        Assert.Equal("*", identifier.MaxCardinality);
        Assert.Single(identifier.Types);
        Assert.Equal("Identifier", identifier.Types[0].Code);
        Assert.Equal(1, identifier.FieldOrder);

        ElementInfo gp = result.DifferentialElements[2];
        Assert.Equal("Reference", gp.Types[0].Code);
        Assert.NotNull(gp.Types[0].TargetProfiles);
        Assert.Equal(2, gp.Types[0].TargetProfiles.Count);
    }

    // ────────────────────────────────────────────────────────
    // Error handling
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseStructureDefinition_MalformedXml_ReturnsNull()
    {
        string xml = "<this is not valid xml>";
        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition(xml, "xml");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseStructureDefinition_NonFhirXml_ReturnsNull()
    {
        string xml = """<root xmlns="http://example.org"><child value="test"/></root>""";
        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition(xml, "xml");
        Assert.Null(result);
    }

    [Fact]
    public void TryParseStructureDefinition_EmptyContent_ReturnsNull()
    {
        StructureDefinitionInfo? result = FhirContentParser.TryParseStructureDefinition("", "xml");
        Assert.Null(result);
    }

    // ────────────────────────────────────────────────────────
    // TryParseCanonicalArtifact — CodeSystem
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseCanonicalArtifact_CodeSystem_ReturnsTypeSpecificData()
    {
        string xml = """
            <CodeSystem xmlns="http://hl7.org/fhir">
              <url value="http://example.org/fhir/CodeSystem/test-cs"/>
              <name value="TestCodeSystem"/>
              <title value="Test Code System"/>
              <status value="active"/>
              <content value="complete"/>
              <caseSensitive value="true"/>
              <concept>
                <code value="code1"/>
                <display value="Code One"/>
              </concept>
              <concept>
                <code value="code2"/>
                <display value="Code Two"/>
              </concept>
            </CodeSystem>
            """;

        CanonicalArtifactInfo? result = FhirContentParser.TryParseCanonicalArtifact(xml, "xml");

        Assert.NotNull(result);
        Assert.Equal("CodeSystem", result.ResourceType);
        Assert.Equal("http://example.org/fhir/CodeSystem/test-cs", result.Url);
        Assert.Equal("TestCodeSystem", result.Name);
        Assert.Equal("Test Code System", result.Title);
        Assert.Equal("active", result.Status);
        Assert.Equal("complete", result.TypeSpecificData["content"]);
        Assert.Equal(true, result.TypeSpecificData["caseSensitive"]);
        Assert.Equal(2, result.TypeSpecificData["conceptCount"]);
    }

    // ────────────────────────────────────────────────────────
    // TryParseCanonicalArtifact — ValueSet
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseCanonicalArtifact_ValueSet_ReturnsReferencedSystems()
    {
        string xml = """
            <ValueSet xmlns="http://hl7.org/fhir">
              <url value="http://example.org/fhir/ValueSet/test-vs"/>
              <name value="TestValueSet"/>
              <status value="active"/>
              <compose>
                <include>
                  <system value="http://example.org/fhir/CodeSystem/cs-1"/>
                </include>
                <include>
                  <system value="http://example.org/fhir/CodeSystem/cs-2"/>
                </include>
              </compose>
            </ValueSet>
            """;

        CanonicalArtifactInfo? result = FhirContentParser.TryParseCanonicalArtifact(xml, "xml");

        Assert.NotNull(result);
        Assert.Equal("ValueSet", result.ResourceType);
        List<string> systems = Assert.IsType<List<string>>(result.TypeSpecificData["referencedSystems"]);
        Assert.Equal(2, systems.Count);
        Assert.Contains("http://example.org/fhir/CodeSystem/cs-1", systems);
        Assert.Contains("http://example.org/fhir/CodeSystem/cs-2", systems);
    }

    // ────────────────────────────────────────────────────────
    // TryParseCanonicalArtifact — SearchParameter
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseCanonicalArtifact_SearchParameter_ReturnsCodeAndType()
    {
        string json = """
            {
              "resourceType": "SearchParameter",
              "url": "http://example.org/fhir/SearchParameter/test-sp",
              "name": "TestSearchParam",
              "status": "active",
              "code": "test-code",
              "type": "token",
              "expression": "Patient.identifier",
              "base": ["Patient"]
            }
            """;

        CanonicalArtifactInfo? result = FhirContentParser.TryParseCanonicalArtifact(json, "json");

        Assert.NotNull(result);
        Assert.Equal("SearchParameter", result.ResourceType);
        Assert.Equal("test-code", result.TypeSpecificData["code"]);
        Assert.Equal("token", result.TypeSpecificData["type"]);
        Assert.Equal("Patient.identifier", result.TypeSpecificData["expression"]);
    }

    // ────────────────────────────────────────────────────────
    // TryParseCanonicalArtifact — OperationDefinition
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseCanonicalArtifact_OperationDefinition_ReturnsKind()
    {
        string json = """
            {
              "resourceType": "OperationDefinition",
              "url": "http://example.org/fhir/OperationDefinition/test-op",
              "name": "TestOperation",
              "status": "active",
              "kind": "operation",
              "code": "test-op",
              "system": false,
              "type": true,
              "instance": false
            }
            """;

        CanonicalArtifactInfo? result = FhirContentParser.TryParseCanonicalArtifact(json, "json");

        Assert.NotNull(result);
        Assert.Equal("OperationDefinition", result.ResourceType);
        Assert.Equal("test-op", result.TypeSpecificData["code"]);
        Assert.Equal("operation", result.TypeSpecificData["kind"]);
        Assert.Equal(false, result.TypeSpecificData["system"]);
        Assert.Equal(true, result.TypeSpecificData["type"]);
    }

    // ────────────────────────────────────────────────────────
    // TryParseCanonicalArtifact — ConceptMap
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseCanonicalArtifact_ConceptMap_ReturnsScopeData()
    {
        string json = """
            {
              "resourceType": "ConceptMap",
              "url": "http://example.org/fhir/ConceptMap/test-cm",
              "name": "TestConceptMap",
              "status": "active",
              "sourceScopeCanonical": "http://example.org/fhir/ValueSet/source",
              "targetScopeCanonical": "http://example.org/fhir/ValueSet/target",
              "group": [
                {
                  "source": "http://example.org/cs1",
                  "target": "http://example.org/cs2"
                }
              ]
            }
            """;

        CanonicalArtifactInfo? result = FhirContentParser.TryParseCanonicalArtifact(json, "json");

        Assert.NotNull(result);
        Assert.Equal("ConceptMap", result.ResourceType);
        Assert.Equal(1, result.TypeSpecificData["groupCount"]);
    }

    // ────────────────────────────────────────────────────────
    // TryParseCanonicalArtifact — NamingSystem
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseCanonicalArtifact_NamingSystem_ReturnsKind()
    {
        string xml = """
            <NamingSystem xmlns="http://hl7.org/fhir">
              <name value="TestNamingSystem"/>
              <status value="active"/>
              <kind value="identifier"/>
              <date value="2024-01-01"/>
              <uniqueId>
                <type value="uri"/>
                <value value="http://example.org/ns/test"/>
              </uniqueId>
            </NamingSystem>
            """;

        CanonicalArtifactInfo? result = FhirContentParser.TryParseCanonicalArtifact(xml, "xml");

        Assert.NotNull(result);
        Assert.Equal("NamingSystem", result.ResourceType);
        Assert.Equal("identifier", result.TypeSpecificData["kind"]);
        Assert.Equal(1, result.TypeSpecificData["uniqueIdCount"]);
    }

    // ────────────────────────────────────────────────────────
    // TryParseBundle
    // ────────────────────────────────────────────────────────

    [Fact]
    public void TryParseBundle_WithSearchParameters_ReturnsEntries()
    {
        string json = """
            {
              "resourceType": "Bundle",
              "type": "collection",
              "entry": [
                {
                  "resource": {
                    "resourceType": "SearchParameter",
                    "url": "http://example.org/fhir/SearchParameter/sp1",
                    "name": "SP1",
                    "status": "active",
                    "code": "sp1",
                    "type": "token",
                    "base": ["Patient"]
                  }
                },
                {
                  "resource": {
                    "resourceType": "SearchParameter",
                    "url": "http://example.org/fhir/SearchParameter/sp2",
                    "name": "SP2",
                    "status": "active",
                    "code": "sp2",
                    "type": "string",
                    "base": ["Observation"]
                  }
                }
              ]
            }
            """;

        List<CanonicalArtifactInfo> results = FhirContentParser.TryParseBundle(json, "json");

        Assert.Equal(2, results.Count);
        Assert.Equal("SP1", results[0].Name);
        Assert.Equal("SP2", results[1].Name);
    }

    [Fact]
    public void TryParseBundle_EmptyBundle_ReturnsEmptyList()
    {
        string json = """
            {
              "resourceType": "Bundle",
              "type": "collection",
              "entry": []
            }
            """;

        List<CanonicalArtifactInfo> results = FhirContentParser.TryParseBundle(json, "json");

        Assert.Empty(results);
    }
}
