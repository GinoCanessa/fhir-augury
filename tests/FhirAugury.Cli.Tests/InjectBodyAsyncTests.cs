using System.Text.Json;
using FhirAugury.Cli;

namespace FhirAugury.Cli.Tests;

public class InjectBodyAsyncTests
{
    [Fact]
    public async Task InjectBody_InlineJsonObject_MergesUnderBodyAsValue()
    {
        // The injected body must round-trip as a JSON value, not a quoted
        // string — that's the whole point of the flag.
        string envelope = """{"command":"call","source":"jira","operation":"jira.queryIssues"}""";
        string body = """{"workGroups":["Orders & Observations"],"limit":10}""";

        string merged = await Program.InjectBodyAsync(envelope, body);

        using JsonDocument doc = JsonDocument.Parse(merged);
        JsonElement root = doc.RootElement;
        Assert.Equal("call", root.GetProperty("command").GetString());
        Assert.Equal("jira", root.GetProperty("source").GetString());

        JsonElement bodyEl = root.GetProperty("body");
        Assert.Equal(JsonValueKind.Object, bodyEl.ValueKind);
        Assert.Equal("Orders & Observations",
            bodyEl.GetProperty("workGroups")[0].GetString());
        Assert.Equal(10, bodyEl.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task InjectBody_InlineJsonArray_PreservesArrayKind()
    {
        string envelope = """{"command":"call"}""";
        string body = """[1, 2, 3]""";

        string merged = await Program.InjectBodyAsync(envelope, body);

        using JsonDocument doc = JsonDocument.Parse(merged);
        JsonElement bodyEl = doc.RootElement.GetProperty("body");
        Assert.Equal(JsonValueKind.Array, bodyEl.ValueKind);
        Assert.Equal(3, bodyEl.GetArrayLength());
    }

    [Fact]
    public async Task InjectBody_FromFile_ReadsAndParses()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, """{"k":"v"}""");
            string envelope = """{"command":"call"}""";

            string merged = await Program.InjectBodyAsync(envelope, "@" + tempFile);

            using JsonDocument doc = JsonDocument.Parse(merged);
            Assert.Equal("v", doc.RootElement.GetProperty("body").GetProperty("k").GetString());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task InjectBody_MissingFile_Throws()
    {
        string envelope = """{"command":"call"}""";
        string missing = "@" + Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid() + ".json");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => Program.InjectBodyAsync(envelope, missing));
    }

    [Fact]
    public async Task InjectBody_EnvelopeAlreadyHasBody_Throws()
    {
        // Conflict with envelope-supplied body must be a usage error so
        // callers don't silently overwrite their own input.
        string envelope = """{"command":"call","body":{"existing":true}}""";
        string body = """{"new":true}""";

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.InjectBodyAsync(envelope, body));

        Assert.Contains("body", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InjectBody_EnvelopeNotObject_Throws()
    {
        string envelope = """[1, 2, 3]""";
        string body = """{"k":"v"}""";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.InjectBodyAsync(envelope, body));
    }

    [Fact]
    public async Task InjectBody_InvalidJsonBody_ThrowsJsonException()
    {
        string envelope = """{"command":"call"}""";
        string body = "not json";

        await Assert.ThrowsAnyAsync<JsonException>(
            () => Program.InjectBodyAsync(envelope, body));
    }
}
