using System.Diagnostics;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Structural;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="StructuralDiffService"/> end-to-end against a throwaway
/// git clone: each structural delta kind, added/deleted/renamed SD files, an
/// extension-stored round (#10), and a narrative-only negative.
/// </summary>
public sealed class StructuralDiffServiceTests : IDisposable
{
    private readonly string _clone;

    public StructuralDiffServiceTests()
    {
        _clone = Path.Combine(Path.GetTempPath(), "structdiff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_clone);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_clone);

    [Fact]
    public async Task DiffAsync_detects_each_delta_kind_and_ignores_narrative()
    {
        // Since side.
        await GitInitAsync();
        WriteSd("structuredefinition-observation.xml", "Observation",
            Elem("Observation"),
            Elem("Observation.status", min: 1, max: "1"),
            Elem("Observation.code", typeCode: "CodeableConcept"),
            Elem("Observation.value", isModifier: false),
            Elem("Observation.note", isSummary: false),
            Elem("Observation.method"),
            Elem("Observation.category", shortText: "old short"),
            Elem("Observation.obsolete"));
        WriteSd("structuredefinition-legacy.xml", "Device", Elem("Device"), Elem("Device.udi"));
        WriteSd("structuredefinition-rena.xml", "Group", Elem("Group"), Elem("Group.member", min: 0, max: "*"));
        WriteExtensionSd("structuredefinition-myext.xml", min: 0, max: "1");
        string since = await CommitAsync("since");

        // Head side: mutate one aspect per element, add/remove/rename/extension.
        WriteSd("structuredefinition-observation.xml", "Observation",
            Elem("Observation"),
            Elem("Observation.status", min: 0, max: "1"),                 // Cardinality
            Elem("Observation.code", typeCode: "CodeableReference"),       // Type
            Elem("Observation.value", isModifier: true),                   // Modifier
            Elem("Observation.note", isSummary: true),                     // Summary
            Elem("Observation.method", mustSupport: true),                 // MustSupport
            Elem("Observation.category", shortText: "new short"),          // narrative-only (ignored)
            Elem("Observation.newElement"));                               // Added (+ Removed obsolete)
        File.Delete(Path.Combine(_clone, "structuredefinition-legacy.xml")); // deleted file → Removed
        WriteSd("structuredefinition-device.xml", "Encounter", Elem("Encounter"), Elem("Encounter.class")); // added file → Added
        File.Delete(Path.Combine(_clone, "structuredefinition-rena.xml"));
        WriteSd("structuredefinition-renb.xml", "Group", Elem("Group"), Elem("Group.member", min: 1, max: "*")); // rename + cardinality
        WriteExtensionSd("structuredefinition-myext.xml", min: 1, max: "1"); // extension cardinality
        string head = await CommitAsync("head");

        IReadOnlyList<StructuralChange> changes = await StructuralDiffService.DiffAsync(_clone, since, head);

        // In-place delta kinds on the observation file.
        AssertChange(changes, "Observation.status", "Cardinality");
        AssertChange(changes, "Observation.code", "Type");
        AssertChange(changes, "Observation.value", "Modifier");
        AssertChange(changes, "Observation.note", "Summary");
        AssertChange(changes, "Observation.method", "MustSupport");
        AssertChange(changes, "Observation.newElement", "Added");
        AssertChange(changes, "Observation.obsolete", "Removed");
        // Narrative-only edit must NOT be flagged.
        Assert.DoesNotContain(changes, c => c.ElementPath == "Observation.category");

        // Added file → its elements are Added.
        AssertChange(changes, "Encounter.class", "Added");
        // Deleted file → its elements are Removed.
        AssertChange(changes, "Device.udi", "Removed");
        // Renamed file with a cardinality change is detected.
        AssertChange(changes, "Group.member", "Cardinality");
        // Extension-stored SD (#10) cardinality delta is detected.
        Assert.Contains(changes, c => c.ElementPath == "Extension.value[x]" && c.ChangeKind == "Cardinality");
    }

    [Fact]
    public async Task DiffAsync_returns_empty_when_no_sd_files_change()
    {
        await GitInitAsync();
        await File.WriteAllTextAsync(Path.Combine(_clone, "readme.md"), "hello");
        string since = await CommitAsync("since");
        await File.WriteAllTextAsync(Path.Combine(_clone, "readme.md"), "hello world");
        string head = await CommitAsync("head");

        Assert.Empty(await StructuralDiffService.DiffAsync(_clone, since, head));
    }

    private static void AssertChange(IReadOnlyList<StructuralChange> changes, string elementPath, string kind)
        => Assert.Contains(changes, c => c.ElementPath == elementPath && c.ChangeKind == kind);

    // ── fixture helpers ──────────────────────────────────────────────

    private void WriteSd(string fileName, string type, params string[] elements)
    {
        string xml =
            $"""
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://example.org/fhir/StructureDefinition/{type}"/>
              <name value="{type}"/>
              <status value="draft"/>
              <kind value="resource"/>
              <type value="{type}"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/{type}"/>
              <derivation value="constraint"/>
              <differential>
                {string.Join("\n    ", elements)}
              </differential>
            </StructureDefinition>
            """;
        File.WriteAllText(Path.Combine(_clone, fileName), xml);
    }

    private void WriteExtensionSd(string fileName, int min, string max)
    {
        string xml =
            $"""
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://example.org/fhir/StructureDefinition/myext"/>
              <name value="myext"/>
              <status value="draft"/>
              <kind value="complex-type"/>
              <type value="Extension"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/Extension"/>
              <derivation value="constraint"/>
              <differential>
                <element><id value="Extension"/><path value="Extension"/></element>
                <element>
                  <id value="Extension.value[x]"/>
                  <path value="Extension.value[x]"/>
                  <min value="{min}"/>
                  <max value="{max}"/>
                </element>
              </differential>
            </StructureDefinition>
            """;
        File.WriteAllText(Path.Combine(_clone, fileName), xml);
    }

    private static string Elem(
        string path,
        int? min = null,
        string? max = null,
        string? typeCode = null,
        bool? isModifier = null,
        bool? isSummary = null,
        bool? mustSupport = null,
        string? shortText = null)
    {
        string id = path;
        System.Text.StringBuilder sb = new();
        sb.Append($"<element><id value=\"{id}\"/><path value=\"{path}\"/>");
        if (shortText is not null) sb.Append($"<short value=\"{shortText}\"/>");
        if (min is not null) sb.Append($"<min value=\"{min}\"/>");
        if (max is not null) sb.Append($"<max value=\"{max}\"/>");
        if (typeCode is not null) sb.Append($"<type><code value=\"{typeCode}\"/></type>");
        if (isModifier is not null) sb.Append($"<isModifier value=\"{(isModifier.Value ? "true" : "false")}\"/>");
        if (isSummary is not null) sb.Append($"<isSummary value=\"{(isSummary.Value ? "true" : "false")}\"/>");
        if (mustSupport is not null) sb.Append($"<mustSupport value=\"{(mustSupport.Value ? "true" : "false")}\"/>");
        sb.Append("</element>");
        return sb.ToString();
    }

    private async Task GitInitAsync()
    {
        await Git("init", "-q");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "user.name", "Test");
        await Git("config", "commit.gpgsign", "false");
    }

    private async Task<string> CommitAsync(string message)
    {
        await Git("add", "-A");
        await Git("commit", "-q", "-m", message);
        return (await Git("rev-parse", "HEAD")).Trim();
    }

    private async Task<string> Git(params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = _clone,
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
