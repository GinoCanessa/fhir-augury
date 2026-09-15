using System.Text.Json;
using System.Text.Json.Serialization;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Common.Authoring;

/// <summary>
/// Durable maintenance selection metadata. Only a database null selects a
/// legacy recipe; an invalid or unsupported stored envelope is never legacy.
/// Processor-specific input remains private to the owning processor.
/// </summary>
public sealed record AuthoringMaintenanceRunRequest(
    int ContractVersion,
    string RecipeName,
    int RecipeVersion,
    AuthoringRunCorpusComparison? CorpusComparison,
    string RecipeInputJson)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerOptions.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    public static AuthoringMaintenanceRunRequest? Parse(string? requestJson)
    {
        if (requestJson is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(requestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Maintenance request metadata must be an object.");
        }
        EnsureUniqueProperties(document.RootElement);
        AuthoringMaintenanceRunRequest request =
            JsonSerializer.Deserialize<AuthoringMaintenanceRunRequest>(
                requestJson,
                SerializerOptions)
            ?? throw new JsonException("Maintenance request metadata is empty.");
        request.Validate();
        return request;
    }

    public void EnsureRecipe(string recipeName, int recipeVersion)
    {
        Validate();
        if (!string.Equals(RecipeName, recipeName, StringComparison.Ordinal) ||
            RecipeVersion != recipeVersion)
        {
            throw new NotSupportedException(
                $"Unsupported maintenance recipe '{RecipeName}' version {RecipeVersion}.");
        }
    }

    /// <summary>
    /// Reads the safe comparison without selecting a recipe. Invalid or
    /// unsupported envelopes throw rather than being treated as legacy.
    /// </summary>
    public static AuthoringRunCorpusComparison? ReadCorpusComparison(
        string? requestJson)
        => Parse(requestJson)?.CorpusComparison;

    private void Validate()
    {
        if (ContractVersion != CurrentVersion)
        {
            throw new NotSupportedException(
                $"Unsupported maintenance request contract version {ContractVersion}.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(RecipeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(RecipeVersion, 1);
        CorpusComparison?.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(RecipeInputJson);
        using JsonDocument input = JsonDocument.Parse(RecipeInputJson);
        if (input.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Maintenance recipe input must be an object.");
        }
        EnsureUniqueProperties(input.RootElement);
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Maintenance request contains duplicate properties.");
                }
                EnsureUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureUniqueProperties(item);
            }
        }
    }
}
