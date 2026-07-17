using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirAugury.DevUi.Services.ApiCatalog;

public sealed record ApiBuiltRequest(string Url, HttpMethod Method, string? JsonBody);

public static class ApiUrlBuilder
{
    private static readonly Regex TokenRegex = new(
        @"\{(?<star>\*{1,2})?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::[^}]*)?\}",
        RegexOptions.Compiled);

    public static ApiBuiltRequest Build(
        string httpBase,
        ApiEndpointDescriptor descriptor,
        IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(httpBase);
        ArgumentNullException.ThrowIfNull(descriptor);
        values ??= new Dictionary<string, string?>();

        // Validate required parameters: not present at all, or empty/whitespace.
        List<string> missing = [];
        foreach (ApiParameter p in descriptor.Parameters)
        {
            if (!p.Required) continue;
            if (!values.TryGetValue(p.Name, out string? v) || string.IsNullOrWhiteSpace(v))
                missing.Add(p.Name);
        }
        if (missing.Count > 0)
            throw new ApiInvocationValidationException(missing);

        Dictionary<string, ApiParameter> byName = descriptor.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

        // Path substitution.
        string path = TokenRegex.Replace(descriptor.PathTemplate, m =>
        {
            string name = m.Groups["name"].Value;
            bool starred = m.Groups["star"].Success;

            if (!byName.TryGetValue(name, out ApiParameter? param))
                throw new InvalidOperationException(
                    $"Path token '{{{name}}}' has no matching parameter on descriptor '{descriptor.Id}'.");

            string raw = (values.TryGetValue(name, out string? rv) ? rv : null) ?? param.DefaultValue ?? "";

            if (starred || param.IsCatchAll)
                return EncodeCatchAll(raw, param.Encoding);
            return EncodeSegment(raw, param.Encoding);
        });

        // Query string.
        StringBuilder query = new();
        foreach (ApiParameter p in descriptor.Parameters)
        {
            if (p.Kind != ApiParameterKind.Query) continue;
            string? v = values.TryGetValue(p.Name, out string? rv) ? rv : null;
            if (string.IsNullOrEmpty(v))
            {
                if (string.IsNullOrEmpty(p.DefaultValue)) continue;
                v = p.DefaultValue;
            }

            if (p.Repeatable)
            {
                string[] parts = v!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string part in parts)
                    AppendQuery(query, p.Name, part);
            }
            else
            {
                AppendQuery(query, p.Name, v!);
            }
        }

        string url = httpBase.TrimEnd('/') + "/" + path.TrimStart('/');
        if (query.Length > 0)
            url += (url.Contains('?') ? "&" : "?") + query.ToString();

        // Body — the single Body-kind parameter, if any.
        string? body = null;
        ApiParameter? bodyParam = descriptor.Parameters.FirstOrDefault(p => p.Kind == ApiParameterKind.Body);
        if (bodyParam is not null && values.TryGetValue(bodyParam.Name, out string? bodyVal) && !string.IsNullOrWhiteSpace(bodyVal))
        {
            try
            {
                using JsonDocument _ = JsonDocument.Parse(bodyVal);
                body = bodyVal;
            }
            catch
            {
                body = JsonSerializer.Serialize(bodyVal);
            }
        }

        return new ApiBuiltRequest(url, descriptor.Method, body);
    }

    private static void AppendQuery(StringBuilder sb, string name, string value)
    {
        if (sb.Length > 0) sb.Append('&');
        sb.Append(Uri.EscapeDataString(name));
        sb.Append('=');
        sb.Append(Uri.EscapeDataString(value));
    }

    private static string EncodeSegment(string value, ApiEncoding encoding) => encoding switch
    {
        ApiEncoding.IdSlashPreserving => value.Replace("#", "%23"),
        _ => Uri.EscapeDataString(value),
    };

    private static string EncodeCatchAll(string value, ApiEncoding encoding)
    {
        if (encoding == ApiEncoding.IdSlashPreserving)
            return value.Replace("#", "%23");

        string[] segments = value.Split('/');
        for (int i = 0; i < segments.Length; i++)
            segments[i] = Uri.EscapeDataString(segments[i]);
        return string.Join("/", segments);
    }
}
