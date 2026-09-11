using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests.Api;

public class ItemContractTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void ItemPeopleResponse_PolicyVersionIsAdditiveAndNullable()
    {
        const string legacyJson = """
            {
              "reporter": "Reporter Name",
              "assignee": null,
              "inPersonRequesters": []
            }
            """;

        ItemPeopleResponse legacy = Assert.IsType<ItemPeopleResponse>(
            JsonSerializer.Deserialize<ItemPeopleResponse>(
                legacyJson,
                JsonOptions));
        Assert.Null(legacy.PublicDisplayNamePolicyVersion);

        ItemPeopleResponse current = new(
            "Reporter Name",
            "Assignee Name",
            ["Requester Name"])
        {
            PublicDisplayNamePolicyVersion =
                PublicDisplayNamePolicy.CurrentVersion,
        };
        string currentJson = JsonSerializer.Serialize(current, JsonOptions);
        ItemPeopleResponse roundTripped = Assert.IsType<ItemPeopleResponse>(
            JsonSerializer.Deserialize<ItemPeopleResponse>(
                currentJson,
                JsonOptions));

        Assert.Contains(
            "\"publicDisplayNamePolicyVersion\":1",
            currentJson,
            StringComparison.Ordinal);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            roundTripped.PublicDisplayNamePolicyVersion);
    }
}
