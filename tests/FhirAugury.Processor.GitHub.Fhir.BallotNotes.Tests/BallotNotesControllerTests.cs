using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesControllerTests : IDisposable
{
    private const string EnvPrefix = "FHIR_AUGURY_BALLOTNOTES_BallotNotes__";

    private readonly string _tempDir;
    private readonly string _cloneRoot;
    private readonly WebApplicationFactory<Program> _factory;

    public BallotNotesControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ballotnotes-ctl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _cloneRoot = Path.Combine(_tempDir, "repos");

        SetEnv("DatabasePath", Path.Combine(_tempDir, "notes.db"));
        SetEnv("Hydration__CloneRoot", _cloneRoot);
        // Closed ports: attribution is best-effort and must fail fast (no upstream).
        // Use literal IPv4 127.0.0.1 to get an instant RST and avoid the dual-stack
        // ::1 connect detour, and a short connect timeout so the up-to-~8 sequential
        // closed-upstream lookups fail near-instantly and stay under the poll budget.
        SetEnv("Hydration__OrchestratorAddress", "http://127.0.0.1:1");
        SetEnv("Hydration__JiraSourceAddress", "http://127.0.0.1:1");
        SetEnv("Hydration__AttributionConnectTimeout", "00:00:00.250");

        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (string key in new[] { "DatabasePath", "Hydration__CloneRoot", "Hydration__OrchestratorAddress", "Hydration__JiraSourceAddress", "Hydration__AttributionConnectTimeout" })
        {
            Environment.SetEnvironmentVariable(EnvPrefix + key, null);
        }
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    private static void SetEnv(string key, string value) => Environment.SetEnvironmentVariable(EnvPrefix + key, value);

    [Fact]
    public async Task Hydrate_missing_clone_returns_503()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/ballot-notes/hydrate",
            new { repoOwner = "nope", repoName = "missing", sinceSha = "abc1234" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Hydrate_missing_fields_returns_400()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/ballot-notes/hydrate",
            new { repoOwner = "HL7" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_get_put_round_trip_marks_authored()
    {
        SeedNote("hl7-fhir-artifact-observation", "Observation");
        HttpClient client = _factory.CreateClient();

        // List
        using JsonDocument list = await GetJson(client, "/api/v1/ballot-notes");
        Assert.True(list.RootElement.GetProperty("total").GetInt32() >= 1);

        // Detail
        using JsonDocument detail = await GetJson(client, "/api/v1/ballot-notes/hl7-fhir-artifact-observation");
        Assert.Equal("Observation", detail.RootElement.GetProperty("name").GetString());
        Assert.Equal("awaiting-note", detail.RootElement.GetProperty("status").GetString());

        // Write prose back
        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/api/v1/ballot-notes/hl7-fhir-artifact-observation/note",
            new
            {
                needsNote = "yes",
                proposedBallotNoteHtml = "<blockquote class=\"ballot-note\">drafted</blockquote>",
                rollupSummaryMarkdown = "## Roll-up",
                notesForReviewerMarkdown = "note",
                sourceFilesNote = "",
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Re-read → authored
        using JsonDocument after = await GetJson(client, "/api/v1/ballot-notes/hl7-fhir-artifact-observation");
        Assert.Equal("authored", after.RootElement.GetProperty("status").GetString());
        Assert.Equal("yes", after.RootElement.GetProperty("needsNote").GetString());
    }

    [Fact]
    public async Task Put_unknown_slug_returns_404()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/api/v1/ballot-notes/never-hydrated/note",
            new { needsNote = "no" });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Hydrate_real_fixture_accepts_and_completes()
    {
        string since = await GitFixture.CreateAsync(_cloneRoot, "testowner", "testrepo");
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage accepted = await client.PostAsJsonAsync("/api/v1/ballot-notes/hydrate",
            new { repoOwner = "testowner", repoName = "testrepo", sinceSha = since });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using JsonDocument acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        string runKey = acceptedBody.RootElement.GetProperty("runKey").GetString()!;
        Assert.False(string.IsNullOrEmpty(runKey));

        string status = await PollUntilTerminalAsync(client, runKey);
        Assert.Equal("completed", status);

        using JsonDocument units = await GetJson(client, "/api/v1/ballot-notes?repo=testowner/testrepo");
        Assert.True(units.RootElement.GetProperty("total").GetInt32() >= 1);
        // The observation artifact unit must be present.
        bool hasObservation = units.RootElement.GetProperty("notes").EnumerateArray()
            .Any(n => n.GetProperty("type").GetString() == "Artifact"
                   && n.GetProperty("name").GetString() == "observation");
        Assert.True(hasObservation);
    }

    private async Task<string> PollUntilTerminalAsync(HttpClient client, string runKey)
    {
        for (int i = 0; i < 200; i++)
        {
            using JsonDocument doc = await GetJson(client, $"/api/v1/ballot-notes/hydrate/status?runKey={Uri.EscapeDataString(runKey)}");
            string status = doc.RootElement.GetProperty("status").GetString()!;
            if (status is "completed" or "failed") return status;
            await Task.Delay(50);
        }
        return "timeout";
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private void SeedNote(string noteId, string name)
    {
        BallotNotesDatabase db = _factory.Services.GetRequiredService<BallotNotesDatabase>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NoteRecord note = new()
        {
            NoteId = noteId,
            Type = "Artifact",
            Name = name,
            RepoOwner = "HL7",
            RepoName = "fhir",
            RepoCategory = "FhirCore",
            WorkGroupCode = "OO",
            CommitsInWindow = 1,
            TicketsAttributed = 0,
            GeneratedAt = now,
            SavedAt = now,
        };
        db.UpsertUnitEvidence(
            note,
            [new() { NoteId = noteId, Path = "source/observation/observation.xml", Role = "SD", TouchedInWindow = true }],
            [],
            []);
    }
}

/// <summary>Builds a throwaway git clone fixture for hydration tests.</summary>
internal static class GitFixture
{
    /// <summary>
    /// Creates <c>&lt;cloneRoot&gt;/&lt;owner&gt;_&lt;name&gt;/clone</c> with an artifact file and two
    /// commits, returning the SHA of the first commit (the since-commit; the window
    /// is that SHA..HEAD).
    /// </summary>
    public static async Task<string> CreateAsync(string cloneRoot, string owner, string name)
    {
        string clone = Path.Combine(cloneRoot, $"{owner}_{name}", "clone");
        Directory.CreateDirectory(Path.Combine(clone, "source", "observation"));
        string file = Path.Combine(clone, "source", "observation", "observation.xml");

        await Git(clone, "init", "-q");
        await Git(clone, "config", "user.email", "test@example.com");
        await Git(clone, "config", "user.name", "Test");
        await Git(clone, "config", "commit.gpgsign", "false");

        await File.WriteAllTextAsync(file, "<StructureDefinition><id value=\"Observation\"/></StructureDefinition>");
        await Git(clone, "add", "-A");
        await Git(clone, "commit", "-q", "-m", "FHIR-1 initial Observation");
        string since = (await Git(clone, "rev-parse", "HEAD")).Trim();

        await File.WriteAllTextAsync(file, "<StructureDefinition><id value=\"Observation\"/><status value=\"active\"/></StructureDefinition>");
        await Git(clone, "add", "-A");
        await Git(clone, "commit", "-q", "-m", "FHIR-2 add status to Observation");

        return since;
    }

    private static async Task<string> Git(string workingDir, params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
        return stdout;
    }
}
