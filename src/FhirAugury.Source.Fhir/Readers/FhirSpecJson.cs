using System.Text.Json;
using FhirAugury.Source.Fhir.Api;

namespace FhirAugury.Source.Fhir.Readers;

/// <summary>
/// Typed parsers for the high-value serialized TEXT columns in the spec database
/// (concept designations / properties and value-set compose). Long-tail metadata
/// is passed through raw by callers (FR Decision #6). All parsers are lenient:
/// malformed, empty, or absent JSON yields an empty result rather than throwing.
/// </summary>
internal static class FhirSpecJson
{
    private static readonly JsonDocumentOptions s_options = new() { AllowTrailingCommas = true };

    /// <summary>Parses a concept <c>Designations</c> JSON array.</summary>
    public static List<ConceptDesignation> ParseDesignations(string? json)
    {
        List<ConceptDesignation> result = [];
        if (!TryParseArray(json, out JsonElement array))
        {
            return result;
        }

        foreach (JsonElement el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? language = GetString(el, "language");
            string? use = null;
            if (el.TryGetProperty("use", out JsonElement useEl) && useEl.ValueKind == JsonValueKind.Object)
            {
                use = GetString(useEl, "code");
            }
            string value = GetString(el, "value") ?? "";
            result.Add(new ConceptDesignation(language, use, value));
        }
        return result;
    }

    /// <summary>Parses a concept <c>Properties</c> JSON array of <c>{code, value[x]}</c> entries.</summary>
    public static List<ConceptProperty> ParseConceptProperties(string? json)
    {
        List<ConceptProperty> result = [];
        if (!TryParseArray(json, out JsonElement array))
        {
            return result;
        }

        foreach (JsonElement el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? code = GetString(el, "code");
            if (code is null)
            {
                continue;
            }

            string type = "string";
            string value = "";
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                if (prop.Name.Length > 5 && prop.Name.StartsWith("value", StringComparison.Ordinal))
                {
                    type = char.ToLowerInvariant(prop.Name[5]) + prop.Name[6..];
                    value = RenderScalar(prop.Value);
                    break;
                }
            }
            result.Add(new ConceptProperty(code, type, value));
        }
        return result;
    }

    /// <summary>Parses a value-set <c>Compose</c> JSON object into include / exclude rules.</summary>
    public static List<ComposeRule> ParseCompose(string? json)
    {
        List<ComposeRule> result = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, s_options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (string mode in (string[])["include", "exclude"])
            {
                if (!doc.RootElement.TryGetProperty(mode, out JsonElement rules) ||
                    rules.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    if (rule.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    result.Add(ParseComposeRule(mode, rule));
                }
            }
        }
        catch (JsonException)
        {
            // lenient: ignore malformed compose
        }
        return result;
    }

    private static ComposeRule ParseComposeRule(string mode, JsonElement rule)
    {
        List<ComposeConcept> concepts = [];
        if (rule.TryGetProperty("concept", out JsonElement conceptArr) && conceptArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in conceptArr.EnumerateArray())
            {
                string? code = GetString(c, "code");
                if (code is not null)
                {
                    concepts.Add(new ComposeConcept(code, GetString(c, "display")));
                }
            }
        }

        List<ComposeFilter> filters = [];
        if (rule.TryGetProperty("filter", out JsonElement filterArr) && filterArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement f in filterArr.EnumerateArray())
            {
                filters.Add(new ComposeFilter(
                    GetString(f, "property") ?? "",
                    GetString(f, "op") ?? "",
                    GetString(f, "value") ?? ""));
            }
        }

        List<string> valueSets = [];
        if (rule.TryGetProperty("valueSet", out JsonElement vsEl))
        {
            if (vsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement v in vsEl.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        valueSets.Add(v.GetString()!);
                    }
                }
            }
            else if (vsEl.ValueKind == JsonValueKind.String)
            {
                valueSets.Add(vsEl.GetString()!);
            }
        }

        return new ComposeRule(mode, GetString(rule, "system"), GetString(rule, "version"),
            concepts, filters, valueSets);
    }

    private static bool TryParseArray(string? json, out JsonElement array)
    {
        array = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, s_options);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            array = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string RenderScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Object when value.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String
            => value.TryGetProperty("system", out JsonElement sys) && sys.ValueKind == JsonValueKind.String
                ? $"{sys.GetString()}#{code.GetString()}"
                : code.GetString() ?? "",
        _ => value.GetRawText(),
    };
}
