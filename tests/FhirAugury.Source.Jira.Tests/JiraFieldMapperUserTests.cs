using System.Text.Json;
using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

public class JiraFieldMapperUserTests
{
    [Fact]
    public void ExtractUserRef_ValidUser_ReturnsUsernameAndDisplayName()
    {
        string json = """
        {
            "assignee": {
                "name": "alice.smith",
                "displayName": "Alice Smith"
            }
        }
        """;
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        (string? username, string? displayName) = JiraFieldMapper.ExtractUserRef(fields, "assignee");

        Assert.Equal("alice.smith", username);
        Assert.Equal("Alice Smith", displayName);
    }

    [Fact]
    public void ExtractUserRef_NullField_ReturnsBothNull()
    {
        string json = """{ "assignee": null }""";
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        (string? username, string? displayName) = JiraFieldMapper.ExtractUserRef(fields, "assignee");

        Assert.Null(username);
        Assert.Null(displayName);
    }

    [Fact]
    public void ExtractUserRef_MissingField_ReturnsBothNull()
    {
        string json = """{ }""";
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        (string? username, string? displayName) = JiraFieldMapper.ExtractUserRef(fields, "assignee");

        Assert.Null(username);
        Assert.Null(displayName);
    }

    [Fact]
    public void ExtractUserRef_KeyFallback_ReturnsKey()
    {
        string json = """
        {
            "reporter": {
                "key": "bob.key",
                "displayName": "Bob"
            }
        }
        """;
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        (string? username, string? displayName) = JiraFieldMapper.ExtractUserRef(fields, "reporter");

        Assert.Equal("bob.key", username);
        Assert.Equal("Bob", displayName);
    }

    [Fact]
    public void ExtractInPersonRequesters_ValidArray_ReturnsUsers()
    {
        string json = """
        {
            "customfield_11000": [
                { "name": "alice", "displayName": "Alice A" },
                { "name": "bob", "displayName": "Bob B" }
            ]
        }
        """;
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        List<JiraInPersonRef> result = JiraFieldMapper.ExtractInPersonRequesters(fields);

        Assert.Equal(2, result.Count);
        Assert.Equal("alice", result[0].Username);
        Assert.Equal("Alice A", result[0].DisplayName);
        Assert.Equal("bob", result[1].Username);
        Assert.Equal("Bob B", result[1].DisplayName);
    }

    [Fact]
    public void ExtractInPersonRequesters_NullField_ReturnsEmpty()
    {
        string json = """{ }""";
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        List<JiraInPersonRef> result = JiraFieldMapper.ExtractInPersonRequesters(fields);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractInPersonRequesters_EmptyArray_ReturnsEmpty()
    {
        string json = """{ "customfield_11000": [] }""";
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        List<JiraInPersonRef> result = JiraFieldMapper.ExtractInPersonRequesters(fields);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractInPersonRequesters_MixedContent_ExtractsBoth()
    {
        string json = """
        {
            "customfield_11000": [
                { "name": "alice" },
                { "displayName": "Bob B" },
                { "name": "charlie", "displayName": "Charlie C" }
            ]
        }
        """;
        JsonElement fields = JsonDocument.Parse(json).RootElement;

        List<JiraInPersonRef> result = JiraFieldMapper.ExtractInPersonRequesters(fields);

        Assert.Equal(3, result.Count);
        Assert.Equal("alice", result[0].Username);
        Assert.Null(result[0].DisplayName);
        Assert.Null(result[1].Username);
        Assert.Equal("Bob B", result[1].DisplayName);
        Assert.Equal("charlie", result[2].Username);
        Assert.Equal("Charlie C", result[2].DisplayName);
    }
}
