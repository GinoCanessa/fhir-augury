using FshParser = Hl7.FhirShorthand.Serialization.FshParser;
using ParseResult = Hl7.FhirShorthand.Serialization.Models.ParseResult;
using FshDoc = Hl7.FhirShorthand.Serialization.Models.FshDoc;
using FshEntity = Hl7.FhirShorthand.Serialization.Models.FshEntity;
using FshProfile = Hl7.FhirShorthand.Serialization.Models.Profile;
using FshExtension = Hl7.FhirShorthand.Serialization.Models.Extension;
using FshResource = Hl7.FhirShorthand.Serialization.Models.Resource;
using FshLogical = Hl7.FhirShorthand.Serialization.Models.Logical;
using FshCodeSystem = Hl7.FhirShorthand.Serialization.Models.CodeSystem;
using FshValueSet = Hl7.FhirShorthand.Serialization.Models.ValueSet;
using FshInstance = Hl7.FhirShorthand.Serialization.Models.Instance;
using FshRule = Hl7.FhirShorthand.Serialization.Models.FshRule;
using FshCaretValueRule = Hl7.FhirShorthand.Serialization.Models.CaretValueRule;
using FshCsRule = Hl7.FhirShorthand.Serialization.Models.CsRule;
using FshCsCaretValueRule = Hl7.FhirShorthand.Serialization.Models.CsCaretValueRule;
using FshVsRule = Hl7.FhirShorthand.Serialization.Models.VsRule;
using FshVsCaretValueRule = Hl7.FhirShorthand.Serialization.Models.VsCaretValueRule;
using FshValue = Hl7.FhirShorthand.Serialization.Models.FshValue;
using FshStringValue = Hl7.FhirShorthand.Serialization.Models.StringValue;
using FshMetadata = Hl7.FhirShorthand.Serialization.Models.Metadata;
using FshCode = Hl7.FhirShorthand.Serialization.Models.Code;
using FshNameValue = Hl7.FhirShorthand.Serialization.Models.NameValue;

namespace FhirAugury.Parsing.Fsh;

/// <summary>
/// Parses FSH files using ANTLR4-based parsing via fsh-processor.
/// Extracts canonical artifact definitions and filters out non-canonical entities.
/// </summary>
public static class FshContentParser
{
    public static List<FshDefinitionInfo> ParseFile(string filePath)
    {
        string content = File.ReadAllText(filePath);
        return ParseContent(content);
    }

    public static List<FshDefinitionInfo> ParseContent(string content)
    {
        try
        {
            ParseResult result = FshParser.Parse(content);
            if (result is not ParseResult.Success success)
                return [];

            FshDoc doc = success.Document;
            List<FshDefinitionInfo> results = [];

            foreach (FshEntity entity in doc.Entities)
            {
                FshDefinitionInfo? info = MapEntity(entity);
                if (info is not null)
                    results.Add(info);
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    // ────────────────────────────────────────────────────────
    // Entity mapping
    // ────────────────────────────────────────────────────────

    private static FshDefinitionInfo? MapEntity(FshEntity entity)
    {
        return entity switch
        {
            FshProfile p => new FshDefinitionInfo(
                Kind: FshDefinitionKind.Profile,
                Name: p.Name,
                Id: p.Id?.Value,
                Parent: p.Parent?.Value,
                Title: p.Title?.Value,
                Description: p.Description?.Value,
                InstanceOf: null,
                Usage: null,
                ExplicitUrl: ExtractCaretRuleStringFromSdRules(p.Rules, "url"),
                ExplicitStatus: ExtractCaretRuleCodeFromSdRules(p.Rules, "status"),
                ExplicitVersion: ExtractCaretRuleStringFromSdRules(p.Rules, "version"),
                StartLine: p.Position?.StartLine ?? 0,
                EndLine: p.Position?.EndLine ?? 0),

            FshExtension e => new FshDefinitionInfo(
                Kind: FshDefinitionKind.Extension,
                Name: e.Name,
                Id: e.Id,
                Parent: e.Parent,
                Title: e.Title,
                Description: e.Description,
                InstanceOf: null,
                Usage: null,
                ExplicitUrl: ExtractCaretRuleStringFromSdRules(e.Rules, "url"),
                ExplicitStatus: ExtractCaretRuleCodeFromSdRules(e.Rules, "status"),
                ExplicitVersion: ExtractCaretRuleStringFromSdRules(e.Rules, "version"),
                StartLine: e.Position?.StartLine ?? 0,
                EndLine: e.Position?.EndLine ?? 0),

            FshResource r => new FshDefinitionInfo(
                Kind: FshDefinitionKind.Resource,
                Name: r.Name,
                Id: r.Id,
                Parent: r.Parent,
                Title: r.Title,
                Description: r.Description,
                InstanceOf: null,
                Usage: null,
                ExplicitUrl: ExtractCaretRuleStringFromSdRules(r.Rules, "url"),
                ExplicitStatus: ExtractCaretRuleCodeFromSdRules(r.Rules, "status"),
                ExplicitVersion: ExtractCaretRuleStringFromSdRules(r.Rules, "version"),
                StartLine: r.Position?.StartLine ?? 0,
                EndLine: r.Position?.EndLine ?? 0),

            FshLogical l => new FshDefinitionInfo(
                Kind: FshDefinitionKind.Logical,
                Name: l.Name,
                Id: l.Id,
                Parent: l.Parent,
                Title: l.Title,
                Description: l.Description,
                InstanceOf: null,
                Usage: null,
                ExplicitUrl: ExtractCaretRuleStringFromSdRules(l.Rules, "url"),
                ExplicitStatus: ExtractCaretRuleCodeFromSdRules(l.Rules, "status"),
                ExplicitVersion: ExtractCaretRuleStringFromSdRules(l.Rules, "version"),
                StartLine: l.Position?.StartLine ?? 0,
                EndLine: l.Position?.EndLine ?? 0),

            FshCodeSystem cs => new FshDefinitionInfo(
                Kind: FshDefinitionKind.CodeSystem,
                Name: cs.Name,
                Id: cs.Id,
                Parent: null,
                Title: cs.Title,
                Description: cs.Description,
                InstanceOf: null,
                Usage: null,
                ExplicitUrl: ExtractCaretRuleStringFromCsRules(cs.Rules, "url"),
                ExplicitStatus: ExtractCaretRuleCodeFromCsRules(cs.Rules, "status"),
                ExplicitVersion: ExtractCaretRuleStringFromCsRules(cs.Rules, "version"),
                StartLine: cs.Position?.StartLine ?? 0,
                EndLine: cs.Position?.EndLine ?? 0),

            FshValueSet vs => new FshDefinitionInfo(
                Kind: FshDefinitionKind.ValueSet,
                Name: vs.Name,
                Id: vs.Id,
                Parent: null,
                Title: vs.Title,
                Description: vs.Description,
                InstanceOf: null,
                Usage: null,
                ExplicitUrl: ExtractCaretRuleStringFromVsRules(vs.Rules, "url"),
                ExplicitStatus: ExtractCaretRuleCodeFromVsRules(vs.Rules, "status"),
                ExplicitVersion: ExtractCaretRuleStringFromVsRules(vs.Rules, "version"),
                StartLine: vs.Position?.StartLine ?? 0,
                EndLine: vs.Position?.EndLine ?? 0),

            FshInstance i when IsDefinitionalInstance(i) => new FshDefinitionInfo(
                Kind: FshDefinitionKind.DefinitionalInstance,
                Name: i.Name,
                Id: null,
                Parent: null,
                Title: i.Title,
                Description: i.Description,
                InstanceOf: i.InstanceOf,
                Usage: i.Usage,
                ExplicitUrl: null,
                ExplicitStatus: null,
                ExplicitVersion: null,
                StartLine: i.Position?.StartLine ?? 0,
                EndLine: i.Position?.EndLine ?? 0),

            // Skip: Alias, RuleSet, Mapping, Invariant, non-definitional Instance
            _ => null,
        };
    }

    // ────────────────────────────────────────────────────────
    // IsDefinitionalInstance
    // ────────────────────────────────────────────────────────

    private static bool IsDefinitionalInstance(FshInstance instance)
    {
        if (instance.Usage is not "#definition")
            return false;

        return instance.InstanceOf is
            "OperationDefinition" or "SearchParameter" or "ConceptMap" or
            "CapabilityStatement" or "NamingSystem" or "ImplementationGuide" or
            "MessageDefinition" or "StructureMap" or "GraphDefinition" or
            "CompartmentDefinition";
    }

    // ────────────────────────────────────────────────────────
    // Caret rule extraction from SD rules (Profile, Extension, Resource, Logical)
    // ────────────────────────────────────────────────────────

    private static string? ExtractCaretRuleStringFromSdRules(IEnumerable<FshRule> rules, string caretPath)
    {
        foreach (FshRule rule in rules)
        {
            if (rule is FshCaretValueRule caretRule
                && caretRule.CaretPath == caretPath)
            {
                return ExtractStringFromValue(caretRule.Value);
            }
        }
        return null;
    }

    private static string? ExtractCaretRuleCodeFromSdRules(IEnumerable<FshRule> rules, string caretPath)
    {
        foreach (FshRule rule in rules)
        {
            if (rule is FshCaretValueRule caretRule
                && caretRule.CaretPath == caretPath)
            {
                return ExtractCodeFromValue(caretRule.Value);
            }
        }
        return null;
    }

    // ────────────────────────────────────────────────────────
    // Caret rule extraction from CS rules (CodeSystem)
    // ────────────────────────────────────────────────────────

    private static string? ExtractCaretRuleStringFromCsRules(IEnumerable<FshCsRule> rules, string caretPath)
    {
        foreach (FshCsRule rule in rules)
        {
            if (rule is FshCsCaretValueRule caretRule
                && caretRule.CaretPath == caretPath)
            {
                return ExtractStringFromValue(caretRule.Value);
            }
        }
        return null;
    }

    private static string? ExtractCaretRuleCodeFromCsRules(IEnumerable<FshCsRule> rules, string caretPath)
    {
        foreach (FshCsRule rule in rules)
        {
            if (rule is FshCsCaretValueRule caretRule
                && caretRule.CaretPath == caretPath)
            {
                return ExtractCodeFromValue(caretRule.Value);
            }
        }
        return null;
    }

    // ────────────────────────────────────────────────────────
    // Caret rule extraction from VS rules (ValueSet)
    // ────────────────────────────────────────────────────────

    private static string? ExtractCaretRuleStringFromVsRules(IEnumerable<FshVsRule> rules, string caretPath)
    {
        foreach (FshVsRule rule in rules)
        {
            if (rule is FshVsCaretValueRule caretRule
                && caretRule.CaretPath == caretPath)
            {
                return ExtractStringFromValue(caretRule.Value);
            }
        }
        return null;
    }

    private static string? ExtractCaretRuleCodeFromVsRules(IEnumerable<FshVsRule> rules, string caretPath)
    {
        foreach (FshVsRule rule in rules)
        {
            if (rule is FshVsCaretValueRule caretRule
                && caretRule.CaretPath == caretPath)
            {
                return ExtractCodeFromValue(caretRule.Value);
            }
        }
        return null;
    }

    // ────────────────────────────────────────────────────────
    // Value extraction helpers
    // ────────────────────────────────────────────────────────

    private static string? ExtractStringFromValue(FshValue? value)
    {
        return value switch
        {
            FshStringValue sv => sv.Value,
            FshMetadata m => m.Value,
            FshCode c => c.Value,
            FshNameValue nv => nv.Value,
            _ => null,
        };
    }

    private static string? ExtractCodeFromValue(FshValue? value)
    {
        return value switch
        {
            FshCode c => c.Value.TrimStart('#'),
            FshStringValue sv => sv.Value,
            FshMetadata m => m.Value,
            FshNameValue nv => nv.Value,
            _ => null,
        };
    }

    // ────────────────────────────────────────────────────────
    // Canonical URL construction
    // ────────────────────────────────────────────────────────

    public static string? ConstructCanonicalUrl(FshDefinitionInfo definition, SushiConfig config)
    {
        if (definition.ExplicitUrl is not null)
            return definition.ExplicitUrl;

        if (config.Canonical is null)
            return null;

        string? id = definition.Id ?? definition.Name;
        if (id is null)
            return null;

        string resourceTypePath = definition.Kind switch
        {
            FshDefinitionKind.Profile => "StructureDefinition",
            FshDefinitionKind.Extension => "StructureDefinition",
            FshDefinitionKind.Resource => "StructureDefinition",
            FshDefinitionKind.Logical => "StructureDefinition",
            FshDefinitionKind.CodeSystem => "CodeSystem",
            FshDefinitionKind.ValueSet => "ValueSet",
            FshDefinitionKind.DefinitionalInstance => definition.InstanceOf ?? "Unknown",
            _ => "Unknown"
        };

        string canonical = config.Canonical.TrimEnd('/');
        return $"{canonical}/{resourceTypePath}/{id}";
    }
}
