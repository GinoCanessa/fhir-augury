namespace FhirAugury.Parsing.Fsh.Tests;

public class FshContentParserTests
{
    // ────────────────────────────────────────────────────────
    // Profile parsing
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_Profile_ReturnsProfileDefinition()
    {
        string fsh = """
            Profile: USCorePatient
            Parent: Patient
            Id: us-core-patient
            Title: "US Core Patient Profile"
            Description: "A profile on Patient for US Core."
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        FshDefinitionInfo profile = results[0];
        Assert.Equal(FshDefinitionKind.Profile, profile.Kind);
        Assert.Equal("USCorePatient", profile.Name);
        Assert.Equal("us-core-patient", profile.Id);
        Assert.Equal("Patient", profile.Parent);
        Assert.Equal("US Core Patient Profile", profile.Title);
        Assert.Equal("A profile on Patient for US Core.", profile.Description);
    }

    // ────────────────────────────────────────────────────────
    // Extension parsing
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_Extension_ReturnsExtensionDefinition()
    {
        string fsh = """
            Extension: MyExtension
            Id: my-extension
            Title: "My Extension"
            Description: "An example extension."
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        FshDefinitionInfo ext = results[0];
        Assert.Equal(FshDefinitionKind.Extension, ext.Kind);
        Assert.Equal("MyExtension", ext.Name);
        Assert.Equal("my-extension", ext.Id);
    }

    // ────────────────────────────────────────────────────────
    // CodeSystem parsing
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_CodeSystem_ReturnsCodeSystemDefinition()
    {
        string fsh = """
            CodeSystem: MyCodeSystem
            Id: my-code-system
            Title: "My Code System"
            Description: "A test code system."
            * #code1 "Code One" "Definition one"
            * #code2 "Code Two" "Definition two"
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        FshDefinitionInfo cs = results[0];
        Assert.Equal(FshDefinitionKind.CodeSystem, cs.Kind);
        Assert.Equal("MyCodeSystem", cs.Name);
        Assert.Equal("my-code-system", cs.Id);
        Assert.Null(cs.Parent);
    }

    // ────────────────────────────────────────────────────────
    // ValueSet parsing
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_ValueSet_ReturnsValueSetDefinition()
    {
        string fsh = """
            ValueSet: MyValueSet
            Id: my-value-set
            Title: "My Value Set"
            Description: "A test value set."
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        FshDefinitionInfo vs = results[0];
        Assert.Equal(FshDefinitionKind.ValueSet, vs.Kind);
        Assert.Equal("MyValueSet", vs.Name);
        Assert.Equal("my-value-set", vs.Id);
    }

    // ────────────────────────────────────────────────────────
    // Resource parsing
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_Resource_ReturnsResourceDefinition()
    {
        string fsh = """
            Resource: MyResource
            Parent: DomainResource
            Id: my-resource
            Title: "My Resource"
            Description: "A custom resource."
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        FshDefinitionInfo res = results[0];
        Assert.Equal(FshDefinitionKind.Resource, res.Kind);
        Assert.Equal("MyResource", res.Name);
        Assert.Equal("my-resource", res.Id);
        Assert.Equal("DomainResource", res.Parent);
    }

    // ────────────────────────────────────────────────────────
    // Logical model parsing
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_Logical_ReturnsLogicalDefinition()
    {
        string fsh = """
            Logical: MyLogical
            Parent: Element
            Id: my-logical
            Title: "My Logical Model"
            Description: "A logical model."
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        FshDefinitionInfo logical = results[0];
        Assert.Equal(FshDefinitionKind.Logical, logical.Kind);
        Assert.Equal("MyLogical", logical.Name);
    }

    // ────────────────────────────────────────────────────────
    // Definitional Instance
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_DefinitionalInstance_ReturnsDefinitionalInstance()
    {
        string fsh = """
            Instance: FeatureQuery
            InstanceOf: OperationDefinition
            Usage: #definition
            Title: "Feature Query"
            Description: "Queries for features."
            * name = "FeatureQuery"
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        FshDefinitionInfo inst = results[0];
        Assert.Equal(FshDefinitionKind.DefinitionalInstance, inst.Kind);
        Assert.Equal("FeatureQuery", inst.Name);
        Assert.Equal("OperationDefinition", inst.InstanceOf);
        Assert.Equal("#definition", inst.Usage);
    }

    // ────────────────────────────────────────────────────────
    // Filtered entities
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_ExampleInstance_FilteredOut()
    {
        string fsh = """
            Instance: ExamplePatient
            InstanceOf: Patient
            Usage: #example
            * name.given = "Jane"
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseContent_Alias_FilteredOut()
    {
        string fsh = """
            Alias: $SCT = http://snomed.info/sct
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseContent_RuleSet_FilteredOut()
    {
        string fsh = """
            RuleSet: CommonRules
            * status = #active
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseContent_Invariant_FilteredOut()
    {
        string fsh = """
            Invariant: inv-1
            Description: "A test invariant"
            Expression: "name.exists()"
            Severity: #error
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseContent_Mapping_FilteredOut()
    {
        string fsh = """
            Mapping: MyMapping
            Source: MyProfile
            Target: "http://hl7.org/v2"
            Id: my-mapping
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Empty(results);
    }

    // ────────────────────────────────────────────────────────
    // Multiple definitions
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_MultipleDefinitions_ReturnsAllInOrder()
    {
        string fsh = """
            Profile: MyProfile
            Parent: Patient
            Id: my-profile
            Title: "My Profile"

            Extension: MyExtension
            Id: my-extension
            Title: "My Extension"

            CodeSystem: MyCodeSystem
            Id: my-cs
            Title: "My CS"
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Equal(3, results.Count);
        Assert.Equal(FshDefinitionKind.Profile, results[0].Kind);
        Assert.Equal(FshDefinitionKind.Extension, results[1].Kind);
        Assert.Equal(FshDefinitionKind.CodeSystem, results[2].Kind);
    }

    // ────────────────────────────────────────────────────────
    // Source positions
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_SourcePositions_Populated()
    {
        string fsh = """
            Profile: MyProfile
            Parent: Patient
            Id: my-profile
            """;

        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);

        Assert.Single(results);
        Assert.True(results[0].StartLine > 0, "StartLine should be populated");
    }

    // ────────────────────────────────────────────────────────
    // Error handling
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ParseContent_InvalidFsh_ReturnsEmptyList()
    {
        string fsh = "This is not valid FSH content @#$%^&*()";
        List<FshDefinitionInfo> results = FshContentParser.ParseContent(fsh);
        Assert.Empty(results);
    }

    [Fact]
    public void ParseContent_EmptyContent_ReturnsEmptyList()
    {
        List<FshDefinitionInfo> results = FshContentParser.ParseContent("");
        Assert.Empty(results);
    }

    // ────────────────────────────────────────────────────────
    // ConstructCanonicalUrl
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ConstructCanonicalUrl_Profile_ReturnsStructureDefinitionUrl()
    {
        FshDefinitionInfo def = new(
            Kind: FshDefinitionKind.Profile,
            Name: "USCorePatient",
            Id: "us-core-patient",
            Parent: "Patient",
            Title: null,
            Description: null,
            InstanceOf: null,
            Usage: null,
            ExplicitUrl: null,
            ExplicitStatus: null,
            ExplicitVersion: null,
            StartLine: 1,
            EndLine: 5);
        SushiConfig config = new("test.ig", "http://example.org/fhir", "TestIG", null, null, null, [], []);

        string? url = FshContentParser.ConstructCanonicalUrl(def, config);

        Assert.Equal("http://example.org/fhir/StructureDefinition/us-core-patient", url);
    }

    [Fact]
    public void ConstructCanonicalUrl_CodeSystem_ReturnsCodeSystemUrl()
    {
        FshDefinitionInfo def = new(
            Kind: FshDefinitionKind.CodeSystem,
            Name: "MyCS",
            Id: "my-cs",
            Parent: null,
            Title: null,
            Description: null,
            InstanceOf: null,
            Usage: null,
            ExplicitUrl: null,
            ExplicitStatus: null,
            ExplicitVersion: null,
            StartLine: 1,
            EndLine: 5);
        SushiConfig config = new("test.ig", "http://example.org/fhir", "TestIG", null, null, null, [], []);

        string? url = FshContentParser.ConstructCanonicalUrl(def, config);

        Assert.Equal("http://example.org/fhir/CodeSystem/my-cs", url);
    }

    [Fact]
    public void ConstructCanonicalUrl_ValueSet_ReturnsValueSetUrl()
    {
        FshDefinitionInfo def = new(
            Kind: FshDefinitionKind.ValueSet,
            Name: "MyVS",
            Id: "my-vs",
            Parent: null,
            Title: null,
            Description: null,
            InstanceOf: null,
            Usage: null,
            ExplicitUrl: null,
            ExplicitStatus: null,
            ExplicitVersion: null,
            StartLine: 1,
            EndLine: 5);
        SushiConfig config = new("test.ig", "http://example.org/fhir", "TestIG", null, null, null, [], []);

        string? url = FshContentParser.ConstructCanonicalUrl(def, config);

        Assert.Equal("http://example.org/fhir/ValueSet/my-vs", url);
    }

    [Fact]
    public void ConstructCanonicalUrl_WithExplicitUrl_ReturnsExplicitUrl()
    {
        FshDefinitionInfo def = new(
            Kind: FshDefinitionKind.Profile,
            Name: "MyProfile",
            Id: "my-profile",
            Parent: "Patient",
            Title: null,
            Description: null,
            InstanceOf: null,
            Usage: null,
            ExplicitUrl: "http://custom.org/fhir/StructureDefinition/custom-url",
            ExplicitStatus: null,
            ExplicitVersion: null,
            StartLine: 1,
            EndLine: 5);
        SushiConfig config = new("test.ig", "http://example.org/fhir", "TestIG", null, null, null, [], []);

        string? url = FshContentParser.ConstructCanonicalUrl(def, config);

        Assert.Equal("http://custom.org/fhir/StructureDefinition/custom-url", url);
    }

    [Fact]
    public void ConstructCanonicalUrl_NoCanonical_ReturnsNull()
    {
        FshDefinitionInfo def = new(
            Kind: FshDefinitionKind.Profile,
            Name: "MyProfile",
            Id: "my-profile",
            Parent: "Patient",
            Title: null,
            Description: null,
            InstanceOf: null,
            Usage: null,
            ExplicitUrl: null,
            ExplicitStatus: null,
            ExplicitVersion: null,
            StartLine: 1,
            EndLine: 5);
        SushiConfig config = new("test.ig", null, "TestIG", null, null, null, [], []);

        string? url = FshContentParser.ConstructCanonicalUrl(def, config);

        Assert.Null(url);
    }

    [Fact]
    public void ConstructCanonicalUrl_UsesNameWhenNoId()
    {
        FshDefinitionInfo def = new(
            Kind: FshDefinitionKind.Profile,
            Name: "MyProfileName",
            Id: null,
            Parent: "Patient",
            Title: null,
            Description: null,
            InstanceOf: null,
            Usage: null,
            ExplicitUrl: null,
            ExplicitStatus: null,
            ExplicitVersion: null,
            StartLine: 1,
            EndLine: 5);
        SushiConfig config = new("test.ig", "http://example.org/fhir", "TestIG", null, null, null, [], []);

        string? url = FshContentParser.ConstructCanonicalUrl(def, config);

        Assert.Equal("http://example.org/fhir/StructureDefinition/MyProfileName", url);
    }
}
