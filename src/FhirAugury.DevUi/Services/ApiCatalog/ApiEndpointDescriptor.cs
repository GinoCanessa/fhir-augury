using System.Collections.Generic;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog;

public enum ApiParameterKind
{
    Path,
    Query,
    Body,
    Header,
}

public enum ApiParameterValueType
{
    String,
    Int,
    Long,
    Bool,
    Double,
    Json,
}

public enum ApiEncoding
{
    /// <summary>
    /// <see cref="System.Uri.EscapeDataString(string)"/>.
    /// </summary>
    Default,

    /// <summary>
    /// GitHub-specific id encoding: replace <c>#</c> with <c>%23</c> and preserve all other
    /// characters (including <c>/</c>). Used for ids like <c>HL7/fhir#4006</c>.
    /// </summary>
    IdSlashPreserving,
}

public sealed record ApiParameter(
    string Name,
    ApiParameterKind Kind,
    bool Required,
    string? DefaultValue = null,
    string? Placeholder = null,
    string? HelpText = null,
    ApiParameterValueType ValueType = ApiParameterValueType.String,
    ApiEncoding Encoding = ApiEncoding.Default,
    bool IsCatchAll = false,
    bool Repeatable = false);

public sealed record ApiEndpointDescriptor(
    string Id,
    string DisplayName,
    string Group,
    HttpMethod Method,
    string PathTemplate,
    IReadOnlyList<ApiParameter> Parameters,
    bool Destructive = false,
    string? Description = null);
