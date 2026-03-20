namespace FhirAugury.Common.Text;

/// <summary>
/// Known FHIR resource names and operations for token classification.
/// </summary>
public static class FhirVocabulary
{
    private static readonly HashSet<string> ResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Foundation
        "CapabilityStatement", "StructureDefinition", "ImplementationGuide",
        "SearchParameter", "MessageDefinition", "OperationDefinition",
        "CompartmentDefinition", "StructureMap", "GraphDefinition",
        "ExampleScenario", "NamingSystem", "TerminologyCapabilities",
        "CodeSystem", "ValueSet", "ConceptMap",

        // Security
        "Provenance", "AuditEvent", "Consent",

        // Documents & Exchange
        "Composition", "DocumentManifest", "DocumentReference", "Bundle",
        "Subscription", "Endpoint", "SubscriptionTopic", "SubscriptionStatus",

        // Patient & Individual
        "Patient", "Practitioner", "PractitionerRole", "RelatedPerson",
        "Person", "Group", "Organization", "OrganizationAffiliation",
        "HealthcareService", "Location",

        // Clinical
        "Encounter", "EpisodeOfCare", "Condition", "Procedure",
        "AllergyIntolerance", "ClinicalImpression", "FamilyMemberHistory",
        "DetectedIssue", "RiskAssessment",

        // Diagnostics
        "Observation", "DiagnosticReport", "Specimen", "BodyStructure",
        "ImagingStudy", "Media", "MolecularSequence",

        // Medications
        "MedicationRequest", "MedicationAdministration", "MedicationDispense",
        "MedicationStatement", "Medication", "MedicationKnowledge",
        "Immunization", "ImmunizationEvaluation", "ImmunizationRecommendation",

        // Care Provision
        "CarePlan", "CareTeam", "Goal", "ServiceRequest", "NutritionOrder",
        "VisionPrescription", "RequestGroup", "ActivityDefinition",
        "PlanDefinition", "DeviceRequest", "DeviceUseStatement",
        "CommunicationRequest", "Communication", "SupplyRequest",
        "SupplyDelivery",

        // Financial
        "Coverage", "CoverageEligibilityRequest", "CoverageEligibilityResponse",
        "Claim", "ClaimResponse", "Invoice", "PaymentNotice",
        "PaymentReconciliation", "Account", "ChargeItem",
        "ChargeItemDefinition", "Contract", "ExplanationOfBenefit",
        "InsurancePlan", "EnrollmentRequest", "EnrollmentResponse",

        // Questionnaire & Measures
        "Questionnaire", "QuestionnaireResponse", "Measure", "MeasureReport",

        // Workflow
        "Task", "Appointment", "AppointmentResponse", "Schedule", "Slot",

        // Other
        "Binary", "Basic", "OperationOutcome", "Parameters", "List",
        "Library", "TestScript", "TestReport", "Evidence",
        "EvidenceVariable", "ResearchDefinition", "ResearchElementDefinition",
        "ResearchStudy", "ResearchSubject", "CatalogEntry",
        "Flag", "Linkage", "MessageHeader",
    };

    private static readonly HashSet<string> Operations = new(StringComparer.OrdinalIgnoreCase)
    {
        "$validate", "$expand", "$lookup", "$translate", "$subsumes",
        "$closure", "$everything", "$match", "$merge", "$process-message",
        "$apply", "$evaluate", "$evaluate-measure", "$submit-data",
        "$collect-data", "$data-requirements", "$conforms", "$snapshot",
        "$implements", "$questionnaire", "$populate", "$populatehtml",
        "$populatelink", "$document", "$find-matches", "$graph",
        "$graphql", "$meta", "$meta-add", "$meta-delete", "$convert",
        "$diff", "$transform",
    };

    /// <summary>Returns true if the token is a known FHIR resource name.</summary>
    public static bool IsResourceName(string token) => ResourceNames.Contains(token);

    /// <summary>Returns true if the token is a known FHIR operation.</summary>
    public static bool IsOperation(string token) => Operations.Contains(token);
}
