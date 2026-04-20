using System.Text;
using System.Xml;
using FhirAugury.Parsing.Xml;

namespace FhirAugury.Source.GitHub.Ingestion.Parsing;

/// <summary>
/// Extracts text nodes from XML files. For FHIR resources, extracts semantic fields
/// (name, title, description, definition, comment, narrative div text).
/// </summary>
public class XmlFileContentParser : IFileContentParser
{
    public string ParserType => "xml";

    private static readonly HashSet<string> FhirSemanticElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "title", "description", "definition", "comment",
        "purpose", "copyright", "requirements", "meaning",
    };

    public string? ExtractText(string filePath, Stream content, int maxOutputLength)
    {
        try
        {
            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1024 * 1024,
            };

            using XmlReader reader = XmlDowngradeReader.Create(content, settings);
            StringBuilder sb = new StringBuilder();
            bool isFhir = false;
            bool inSemanticElement = false;
            bool inNarrativeDiv = false;
            int divDepth = 0;

            while (reader.Read() && sb.Length < maxOutputLength)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        string localName = reader.LocalName;

                        // Detect FHIR resources by common root elements or namespace
                        if (reader.Depth == 0 && IsFhirRootElement(reader))
                            isFhir = true;

                        if (isFhir)
                        {
                            if (FhirSemanticElements.Contains(localName) && reader.GetAttribute("value") is string val)
                            {
                                AppendText(sb, val, maxOutputLength);
                            }

                            if (localName == "div" && reader.NamespaceURI == "http://www.w3.org/1999/xhtml")
                            {
                                inNarrativeDiv = true;
                                divDepth = reader.Depth;
                            }
                        }

                        if (FhirSemanticElements.Contains(localName))
                            inSemanticElement = true;

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        if (inNarrativeDiv || inSemanticElement || !isFhir)
                        {
                            AppendText(sb, reader.Value, maxOutputLength);
                        }
                        break;

                    case XmlNodeType.EndElement:
                        if (inNarrativeDiv && reader.Depth == divDepth)
                            inNarrativeDiv = false;

                        if (FhirSemanticElements.Contains(reader.LocalName))
                            inSemanticElement = false;

                        break;
                }
            }

            string result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (XmlException)
        {
            // Fall back to reading as plain text if XML is malformed
            content.Position = 0;
            return new PlainTextFileContentParser("xml").ExtractText(filePath, content, maxOutputLength);
        }
    }

    private static bool IsFhirRootElement(XmlReader reader)
    {
        string ns = reader.NamespaceURI;
        if (ns == "http://hl7.org/fhir")
            return true;

        // Common FHIR resource type names
        string name = reader.LocalName;
        return name is "Patient" or "Observation" or "Encounter" or "Condition" or
            "Procedure" or "MedicationRequest" or "DiagnosticReport" or "AllergyIntolerance" or
            "Immunization" or "CarePlan" or "Goal" or "ServiceRequest" or
            "StructureDefinition" or "ValueSet" or "CodeSystem" or "ConceptMap" or
            "CapabilityStatement" or "OperationDefinition" or "SearchParameter" or
            "ImplementationGuide" or "Bundle" or "Questionnaire" or "QuestionnaireResponse" or
            "Composition" or "DocumentReference" or "Binary" or "Parameters" or
            "OperationOutcome" or "Provenance" or "AuditEvent" or "Consent";
    }

    private static void AppendText(StringBuilder sb, string text, int maxLength)
    {
        if (sb.Length >= maxLength)
            return;

        string cleaned = text.Trim();
        if (string.IsNullOrEmpty(cleaned))
            return;

        if (sb.Length > 0)
            sb.Append(' ');

        int remaining = maxLength - sb.Length;
        if (cleaned.Length > remaining)
            sb.Append(cleaned.AsSpan(0, remaining));
        else
            sb.Append(cleaned);
    }
}
