using Microsoft.Extensions.Logging;

namespace FhirAugury.Parsing.Fhir;

/// <summary>
/// Parses FHIR resources (StructureDefinitions, canonical artifacts, Bundles) from XML or JSON content
/// using the Firely .NET SDK.
/// </summary>
public static class FhirContentParser
{
    // ────────────────────────────────────────────────────────
    // Format detection
    // ────────────────────────────────────────────────────────

    private static string DetectFormat(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".xml" => "xml",
            ".json" => "json",
            _ => throw new ArgumentException($"Cannot detect FHIR format from extension: {extension}")
        };
    }

    private static string DetectFormatFromContent(string content)
    {
        string trimmed = content.TrimStart();
        if (trimmed.StartsWith('<')) return "xml";
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return "json";
        throw new ArgumentException("Cannot detect FHIR format from content");
    }

    // ────────────────────────────────────────────────────────
    // SDK deserialization
    // ────────────────────────────────────────────────────────

    private static Hl7.Fhir.Model.Resource DeserializeResource(string content, string format)
    {
        return format switch
        {
            "xml" => Hl7.Fhir.Serialization.PocoDeserializationExtensions.DeserializeResource(
                Hl7.Fhir.Serialization.FhirXmlDeserializer.RECOVERABLE, content),
            "json" => Hl7.Fhir.Serialization.PocoDeserializationExtensions.DeserializeResource(
                Hl7.Fhir.Serialization.FhirJsonDeserializer.RECOVERABLE, content),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };
    }

    // ────────────────────────────────────────────────────────
    // TryParseStructureDefinition
    // ────────────────────────────────────────────────────────

    public static StructureDefinitionInfo? TryParseStructureDefinition(string filePath, ILogger? logger = null)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            string format = DetectFormat(filePath);
            return TryParseStructureDefinition(content, format, logger, filePath);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read/parse StructureDefinition at {Path}", filePath);
            return null;
        }
    }

    public static StructureDefinitionInfo? TryParseStructureDefinition(string content, string format, ILogger? logger = null)
    {
        return TryParseStructureDefinition(content, format, logger, sourcePath: null);
    }

    private static StructureDefinitionInfo? TryParseStructureDefinition(string content, string format, ILogger? logger, string? sourcePath)
    {
        try
        {
            Hl7.Fhir.Model.Resource resource = DeserializeResource(content, format);
            if (resource is not Hl7.Fhir.Model.StructureDefinition sd)
            {
                return null;
            }

            string? url = sd.Url ?? sd.Id;
            string? name = sd.Name ?? sd.Id;
            if (url is null || name is null)
            {
                logger?.LogWarning(
                    "StructureDefinition missing url/name/id at {Path} (ResourceType={ResourceType})",
                    sourcePath ?? "<inline>", sd.TypeName);
                return null;
            }

            return new StructureDefinitionInfo(
                Url: url,
                Name: name,
                Title: sd.Title,
                Status: sd.Status?.ToString()?.ToLowerInvariant(),
                Kind: sd.Kind?.ToString()?.ToLowerInvariant() ?? "",
                IsAbstract: sd.Abstract,
                FhirType: sd.Type,
                BaseDefinition: sd.BaseDefinition,
                Derivation: sd.Derivation?.ToString()?.ToLowerInvariant(),
                FhirVersion: sd.FhirVersion?.ToString(),
                Description: sd.Description,
                Publisher: sd.Publisher,
                WorkGroup: ExtractExtensionCode(sd, "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg"),
                FhirMaturity: ExtractExtensionInteger(sd, "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm"),
                StandardsStatus: ExtractExtensionCode(sd, "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status"),
                Category: ExtractExtensionString(sd, "http://hl7.org/fhir/StructureDefinition/structuredefinition-category"),
                Contexts: ExtractContexts(sd),
                DifferentialElements: ExtractDifferentialElements(sd));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse StructureDefinition ({Format}) at {Path}", format, sourcePath ?? "<inline>");
            return null;
        }
    }

    // ────────────────────────────────────────────────────────
    // TryParseCanonicalArtifact
    // ────────────────────────────────────────────────────────

    public static CanonicalArtifactInfo? TryParseCanonicalArtifact(string filePath, ILogger? logger = null)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            string format = DetectFormat(filePath);
            return TryParseCanonicalArtifact(content, format, logger, filePath);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read/parse canonical artifact at {Path}", filePath);
            return null;
        }
    }

    public static CanonicalArtifactInfo? TryParseCanonicalArtifact(string content, string format, ILogger? logger = null)
    {
        return TryParseCanonicalArtifact(content, format, logger, sourcePath: null);
    }

    private static CanonicalArtifactInfo? TryParseCanonicalArtifact(string content, string format, ILogger? logger, string? sourcePath)
    {
        try
        {
            Hl7.Fhir.Model.Resource resource = DeserializeResource(content, format);
            CanonicalArtifactInfo? info = ExtractCanonicalArtifact(resource);
            if (info is null)
            {
                logger?.LogWarning(
                    "Unsupported canonical resource type {ResourceType} at {Path}",
                    resource.TypeName, sourcePath ?? "<inline>");
            }
            return info;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse canonical artifact ({Format}) at {Path}", format, sourcePath ?? "<inline>");
            return null;
        }
    }

    // ────────────────────────────────────────────────────────
    // TryParseBundle
    // ────────────────────────────────────────────────────────

    public static List<CanonicalArtifactInfo> TryParseBundle(string filePath, ILogger? logger = null)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            string format = DetectFormat(filePath);
            return TryParseBundle(content, format, logger, filePath);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read/parse Bundle at {Path}", filePath);
            return [];
        }
    }

    public static List<CanonicalArtifactInfo> TryParseBundle(string content, string format, ILogger? logger = null)
    {
        return TryParseBundle(content, format, logger, sourcePath: null);
    }

    private static List<CanonicalArtifactInfo> TryParseBundle(string content, string format, ILogger? logger, string? sourcePath)
    {
        try
        {
            Hl7.Fhir.Model.Resource resource = DeserializeResource(content, format);
            if (resource is not Hl7.Fhir.Model.Bundle bundle)
            {
                logger?.LogWarning(
                    "Expected Bundle but got {ResourceType} at {Path}",
                    resource.TypeName, sourcePath ?? "<inline>");
                return [];
            }

            List<CanonicalArtifactInfo> results = [];
            foreach (Hl7.Fhir.Model.Bundle.EntryComponent entry in bundle.Entry)
            {
                if (entry.Resource is null)
                    continue;

                CanonicalArtifactInfo? info = ExtractCanonicalArtifact(entry.Resource);
                if (info is not null)
                {
                    results.Add(info);
                }
                else
                {
                    logger?.LogWarning(
                        "Skipping unsupported bundle entry resource type {ResourceType} at {Path}",
                        entry.Resource.TypeName, sourcePath ?? "<inline>");
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse Bundle ({Format}) at {Path}", format, sourcePath ?? "<inline>");
            return [];
        }
    }

    // ────────────────────────────────────────────────────────
    // Extension extraction helpers
    // ────────────────────────────────────────────────────────

    private static Hl7.Fhir.Model.Extension? FindExtension(Hl7.Fhir.Model.DomainResource resource, string url)
    {
        return resource.Extension?.FirstOrDefault(e => e.Url == url);
    }

    private static string? ExtractExtensionCode(Hl7.Fhir.Model.DomainResource resource, string url)
    {
        Hl7.Fhir.Model.Extension? ext = FindExtension(resource, url);
        return ext?.Value switch
        {
            Hl7.Fhir.Model.Code code => code.Value,
            Hl7.Fhir.Model.FhirString str => str.Value,
            _ => null
        };
    }

    private static int? ExtractExtensionInteger(Hl7.Fhir.Model.DomainResource resource, string url)
    {
        Hl7.Fhir.Model.Extension? ext = FindExtension(resource, url);
        return ext?.Value switch
        {
            Hl7.Fhir.Model.Integer integer => integer.Value,
            _ => null
        };
    }

    private static string? ExtractExtensionString(Hl7.Fhir.Model.DomainResource resource, string url)
    {
        Hl7.Fhir.Model.Extension? ext = FindExtension(resource, url);
        return ext?.Value switch
        {
            Hl7.Fhir.Model.FhirString str => str.Value,
            _ => null
        };
    }

    // ────────────────────────────────────────────────────────
    // Context extraction
    // ────────────────────────────────────────────────────────

    private static List<ExtensionContext>? ExtractContexts(Hl7.Fhir.Model.StructureDefinition sd)
    {
        if (sd.Context is null || sd.Context.Count == 0)
            return null;

        List<ExtensionContext> contexts = [];
        foreach (Hl7.Fhir.Model.StructureDefinition.ContextComponent ctx in sd.Context)
        {
            contexts.Add(new ExtensionContext(
                Type: ctx.Type?.ToString()?.ToLowerInvariant() ?? "",
                Expression: ctx.Expression ?? ""));
        }
        return contexts;
    }

    // ────────────────────────────────────────────────────────
    // Differential element extraction
    // ────────────────────────────────────────────────────────

    private static List<ElementInfo> ExtractDifferentialElements(Hl7.Fhir.Model.StructureDefinition sd)
    {
        List<ElementInfo> elements = [];

        if (sd.Differential?.Element is null)
            return elements;

        int fieldOrder = 0;
        foreach (Hl7.Fhir.Model.ElementDefinition element in sd.Differential.Element)
        {
            string path = element.Path ?? "";
            string name = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;

            List<ElementTypeInfo> types = [];
            if (element.Type is not null)
            {
                foreach (Hl7.Fhir.Model.ElementDefinition.TypeRefComponent typeRef in element.Type)
                {
                    types.Add(new ElementTypeInfo(
                        Code: typeRef.Code ?? "",
                        Profiles: typeRef.Profile?.Where(v => v is not null).Cast<string>().ToList(),
                        TargetProfiles: typeRef.TargetProfile?.Where(v => v is not null).Cast<string>().ToList()));
                }
            }

            elements.Add(new ElementInfo(
                ElementId: element.ElementId ?? "",
                Path: path,
                Name: name,
                Short: element.Short,
                Definition: element.Definition,
                Comment: element.Comment,
                MinCardinality: element.Min,
                MaxCardinality: element.Max,
                Types: types,
                BindingStrength: element.Binding?.Strength?.ToString()?.ToLowerInvariant(),
                BindingValueSet: element.Binding?.ValueSet,
                SliceName: element.SliceName,
                IsModifier: element.IsModifier,
                IsSummary: element.IsSummary,
                FixedValue: element.Fixed?.ToString(),
                PatternValue: element.Pattern?.ToString(),
                FieldOrder: fieldOrder++));
        }

        return elements;
    }

    // ────────────────────────────────────────────────────────
    // Canonical artifact extraction
    // ────────────────────────────────────────────────────────

    internal static CanonicalArtifactInfo? ExtractCanonicalArtifact(Hl7.Fhir.Model.Resource resource)
    {
        return resource switch
        {
            Hl7.Fhir.Model.CodeSystem cs => BuildCanonicalArtifactInfo(cs, ExtractCodeSystemData(cs)),
            Hl7.Fhir.Model.ValueSet vs => BuildCanonicalArtifactInfo(vs, ExtractValueSetData(vs)),
            Hl7.Fhir.Model.ConceptMap cm => BuildCanonicalArtifactInfo(cm, ExtractConceptMapData(cm)),
            Hl7.Fhir.Model.SearchParameter sp => BuildCanonicalArtifactInfo(sp, ExtractSearchParameterData(sp)),
            Hl7.Fhir.Model.OperationDefinition od => BuildCanonicalArtifactInfo(od, ExtractOperationDefinitionData(od)),
            Hl7.Fhir.Model.NamingSystem ns => BuildNamingSystemInfo(ns),
            Hl7.Fhir.Model.CapabilityStatement cs2 => BuildCanonicalArtifactInfo(cs2, []),
            _ => null
        };
    }

    // ────────────────────────────────────────────────────────
    // Common builder for canonical resource types
    // ────────────────────────────────────────────────────────

    private static CanonicalArtifactInfo BuildCanonicalArtifactInfo(
        Hl7.Fhir.Model.DomainResource resource,
        Dictionary<string, object?> typeSpecificData)
    {
        string resourceType = resource.TypeName;
        string url = "";
        string name = "";
        string? title = null;
        string? version = null;
        string? status = null;
        string? description = null;
        string? publisher = null;

        switch (resource)
        {
            case Hl7.Fhir.Model.CodeSystem cs:
                url = cs.Url ?? "";
                name = cs.Name ?? "";
                title = cs.Title;
                version = cs.Version;
                status = cs.Status?.ToString()?.ToLowerInvariant();
                description = cs.Description;
                publisher = cs.Publisher;
                break;
            case Hl7.Fhir.Model.ValueSet vs:
                url = vs.Url ?? "";
                name = vs.Name ?? "";
                title = vs.Title;
                version = vs.Version;
                status = vs.Status?.ToString()?.ToLowerInvariant();
                description = vs.Description;
                publisher = vs.Publisher;
                break;
            case Hl7.Fhir.Model.ConceptMap cm:
                url = cm.Url ?? "";
                name = cm.Name ?? "";
                title = cm.Title;
                version = cm.Version;
                status = cm.Status?.ToString()?.ToLowerInvariant();
                description = cm.Description;
                publisher = cm.Publisher;
                break;
            case Hl7.Fhir.Model.SearchParameter sp:
                url = sp.Url ?? "";
                name = sp.Name ?? "";
                title = sp.Title;
                version = sp.Version;
                status = sp.Status?.ToString()?.ToLowerInvariant();
                description = sp.Description;
                publisher = sp.Publisher;
                break;
            case Hl7.Fhir.Model.OperationDefinition od:
                url = od.Url ?? "";
                name = od.Name ?? "";
                title = od.Title;
                version = od.Version;
                status = od.Status?.ToString()?.ToLowerInvariant();
                description = od.Description;
                publisher = od.Publisher;
                break;
            case Hl7.Fhir.Model.CapabilityStatement cs2:
                url = cs2.Url ?? "";
                name = cs2.Name ?? "";
                title = cs2.Title;
                version = cs2.Version;
                status = cs2.Status?.ToString()?.ToLowerInvariant();
                description = cs2.Description;
                publisher = cs2.Publisher;
                break;
        }

        string? workGroup = ExtractExtensionCode(resource, "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg");
        int? fhirMaturity = ExtractExtensionInteger(resource, "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm");
        string? standardsStatus = ExtractExtensionCode(resource, "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status");

        return new CanonicalArtifactInfo(
            ResourceType: resourceType,
            Url: url,
            Name: name,
            Title: title,
            Version: version,
            Status: status,
            Description: description,
            Publisher: publisher,
            WorkGroup: workGroup,
            FhirMaturity: fhirMaturity,
            StandardsStatus: standardsStatus,
            TypeSpecificData: typeSpecificData);
    }

    private static CanonicalArtifactInfo BuildNamingSystemInfo(Hl7.Fhir.Model.NamingSystem ns)
    {
        Dictionary<string, object?> data = new()
        {
            ["kind"] = ns.Kind?.ToString()?.ToLowerInvariant(),
            ["uniqueIdCount"] = ns.UniqueId?.Count ?? 0,
        };

        string? workGroup = ExtractExtensionCode(ns, "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg");
        int? fhirMaturity = ExtractExtensionInteger(ns, "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm");
        string? standardsStatus = ExtractExtensionCode(ns, "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status");

        string? namingSystemUrl = ns.UniqueId?
            .FirstOrDefault(u => u.Type == Hl7.Fhir.Model.NamingSystem.NamingSystemIdentifierType.Uri)
            ?.Value;

        return new CanonicalArtifactInfo(
            ResourceType: "NamingSystem",
            Url: namingSystemUrl ?? "",
            Name: ns.Name ?? "",
            Title: ns.Title,
            Version: ns.Version,
            Status: ns.Status?.ToString()?.ToLowerInvariant(),
            Description: ns.Description,
            Publisher: ns.Publisher,
            WorkGroup: workGroup,
            FhirMaturity: fhirMaturity,
            StandardsStatus: standardsStatus,
            TypeSpecificData: data);
    }

    // ────────────────────────────────────────────────────────
    // Type-specific data extractors
    // ────────────────────────────────────────────────────────

    private static Dictionary<string, object?> ExtractCodeSystemData(Hl7.Fhir.Model.CodeSystem cs)
    {
        return new Dictionary<string, object?>
        {
            ["content"] = cs.Content?.ToString()?.ToLowerInvariant(),
            ["caseSensitive"] = cs.CaseSensitive,
            ["valueSet"] = cs.ValueSet,
            ["hierarchyMeaning"] = cs.HierarchyMeaning?.ToString()?.ToLowerInvariant(),
            ["conceptCount"] = CountConcepts(cs.Concept),
        };
    }

    private static int CountConcepts(List<Hl7.Fhir.Model.CodeSystem.ConceptDefinitionComponent>? concepts)
    {
        if (concepts is null)
            return 0;

        int count = 0;
        foreach (Hl7.Fhir.Model.CodeSystem.ConceptDefinitionComponent concept in concepts)
        {
            count++;
            count += CountConcepts(concept.Concept);
        }
        return count;
    }

    private static Dictionary<string, object?> ExtractValueSetData(Hl7.Fhir.Model.ValueSet vs)
    {
        List<string> referencedSystems = [];
        if (vs.Compose?.Include is not null)
        {
            foreach (Hl7.Fhir.Model.ValueSet.ConceptSetComponent include in vs.Compose.Include)
            {
                if (include.System is not null && !referencedSystems.Contains(include.System))
                    referencedSystems.Add(include.System);
            }
        }

        return new Dictionary<string, object?>
        {
            ["referencedSystems"] = referencedSystems,
        };
    }

    private static Dictionary<string, object?> ExtractConceptMapData(Hl7.Fhir.Model.ConceptMap cm)
    {
        return new Dictionary<string, object?>
        {
            ["sourceScope"] = (cm.SourceScope as Hl7.Fhir.Model.FhirUri)?.Value
                           ?? (cm.SourceScope as Hl7.Fhir.Model.Canonical)?.Value,
            ["targetScope"] = (cm.TargetScope as Hl7.Fhir.Model.FhirUri)?.Value
                           ?? (cm.TargetScope as Hl7.Fhir.Model.Canonical)?.Value,
            ["groupCount"] = cm.Group?.Count ?? 0,
        };
    }

    private static Dictionary<string, object?> ExtractSearchParameterData(Hl7.Fhir.Model.SearchParameter sp)
    {
        return new Dictionary<string, object?>
        {
            ["code"] = sp.Code,
            ["type"] = sp.Type?.ToString()?.ToLowerInvariant(),
            ["expression"] = sp.Expression,
            ["baseResources"] = sp.Base?.Select(b => b?.ToString() ?? "").ToList() ?? [],
        };
    }

    private static Dictionary<string, object?> ExtractOperationDefinitionData(Hl7.Fhir.Model.OperationDefinition od)
    {
        return new Dictionary<string, object?>
        {
            ["code"] = od.Code,
            ["kind"] = od.Kind?.ToString()?.ToLowerInvariant(),
            ["system"] = od.System,
            ["type"] = od.Type,
            ["instance"] = od.Instance,
            ["inputProfile"] = od.InputProfile,
            ["outputProfile"] = od.OutputProfile,
        };
    }
}
