using FhirAugury.Common.WorkGroups;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Common.Tests.WorkGroups;

/// <summary>
/// Tests for <see cref="Hl7WorkGroupIndexer"/> driven against an in-memory
/// <see cref="SqliteConnection"/> with a fake <see cref="IHl7WorkGroupStore"/>
/// that records every <c>ApplyChanges</c> call. Synthetic XML fixtures cover
/// insert / update / retire paths and verify that <c>retire</c> receives the
/// full prior DTO.
/// </summary>
public sealed class Hl7WorkGroupIndexerTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _workDir;

    public Hl7WorkGroupIndexerTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _workDir = Path.Combine(Path.GetTempPath(), $"common_hl7wg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private string WriteXml(string contents)
    {
        string path = Path.Combine(_workDir, $"cs_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, contents);
        return path;
    }

    private static Hl7WorkGroupIndexer NewIndexer(IHl7WorkGroupStore store)
        => new Hl7WorkGroupIndexer(store, NullLogger<Hl7WorkGroupIndexer>.Instance);

    [Fact]
    public void Rebuild_NullPath_ReturnsCurrentCount_NoStoreMutation()
    {
        FakeStore store = new();
        store.Seed(new Hl7WorkGroupDto("a", "A", null, false, "A"));

        int total = NewIndexer(store).Rebuild(null, _conn);

        Assert.Equal(1, total);
        Assert.Empty(store.AppliedUpserts);
        Assert.Empty(store.AppliedRetires);
    }

    [Fact]
    public void Rebuild_MissingFile_ReturnsCurrentCount_NoStoreMutation()
    {
        FakeStore store = new();
        int total = NewIndexer(store).Rebuild(Path.Combine(_workDir, "nope.xml"), _conn);
        Assert.Equal(0, total);
        Assert.Empty(store.AppliedUpserts);
    }

    [Fact]
    public void Rebuild_HappyPath_InsertsAllConcepts()
    {
        FakeStore store = new();
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CodeSystem xmlns="http://hl7.org/fhir">
              <concept>
                <code value="fhir"/>
                <display value="FHIR Infrastructure"/>
                <definition value="The FHIR Infrastructure Work Group."/>
                <concept>
                  <code value="fhir-i-sub"/>
                  <display value="FHIR Infrastructure Sub Group"/>
                </concept>
              </concept>
              <concept>
                <code value="oo"/>
                <display value="Orders &amp; Observations"/>
              </concept>
              <concept>
                <code value="nodisplay"/>
              </concept>
              <concept>
                <code value="oldwg"/>
                <display value="Retired Group"/>
                <property>
                  <code value="status"/>
                  <valueCode value="retired"/>
                </property>
              </concept>
            </CodeSystem>
            """;

        int total = NewIndexer(store).Rebuild(WriteXml(xml), _conn);

        Assert.Equal(5, total);
        Assert.Single(store.ApplyCalls);

        Hl7WorkGroupDto fhir = store.AppliedUpserts.Single(d => d.Code == "fhir");
        Assert.Equal("FHIRInfrastructure", fhir.NameClean);
        Assert.Equal("The FHIR Infrastructure Work Group.", fhir.Definition);

        Hl7WorkGroupDto sub = store.AppliedUpserts.Single(d => d.Code == "fhir-i-sub");
        Assert.Equal("FHIRInfrastructureSubGroup", sub.NameClean);

        Hl7WorkGroupDto oo = store.AppliedUpserts.Single(d => d.Code == "oo");
        Assert.Equal("OrdersAndObservations", oo.NameClean);
        Assert.False(oo.Retired);

        Hl7WorkGroupDto nd = store.AppliedUpserts.Single(d => d.Code == "nodisplay");
        Assert.Equal("nodisplay", nd.Name);
        Assert.Equal("Nodisplay", nd.NameClean);

        Hl7WorkGroupDto oldwg = store.AppliedUpserts.Single(d => d.Code == "oldwg");
        Assert.True(oldwg.Retired);

        Assert.Empty(store.AppliedRetires);
    }

    [Fact]
    public void Rebuild_RemovedConcept_AppearsInRetiresWithFullDto()
    {
        FakeStore store = new();
        // Pre-seed two concepts; XML will only re-list one and add a third.
        store.Seed(new Hl7WorkGroupDto("pc", "Patient Care", "Original definition.", false, "PatientCare"));
        store.Seed(new Hl7WorkGroupDto("fhir", "FHIR Infrastructure", null, false, "FHIRInfrastructure"));

        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CodeSystem xmlns="http://hl7.org/fhir">
              <concept>
                <code value="fhir"/>
                <display value="FHIR Infrastructure"/>
              </concept>
              <concept>
                <code value="newgrp"/>
                <display value="New Group"/>
              </concept>
            </CodeSystem>
            """;

        NewIndexer(store).Rebuild(WriteXml(xml), _conn);

        Hl7WorkGroupDto retiredDto = Assert.Single(store.AppliedRetires);
        Assert.Equal("pc", retiredDto.Code);
        Assert.Equal("Patient Care", retiredDto.Name);
        Assert.Equal("Original definition.", retiredDto.Definition);
        Assert.True(retiredDto.Retired);
        Assert.Equal("PatientCare", retiredDto.NameClean);

        Assert.Equal(2, store.AppliedUpserts.Count);
        Assert.Contains(store.AppliedUpserts, d => d.Code == "fhir");
        Assert.Contains(store.AppliedUpserts, d => d.Code == "newgrp");
    }

    [Fact]
    public void Rebuild_AlreadyRetiredAbsentFromXml_NotReRetired()
    {
        FakeStore store = new();
        store.Seed(new Hl7WorkGroupDto("oldwg", "Retired Group", null, true, "RetiredGroup"));

        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CodeSystem xmlns="http://hl7.org/fhir">
              <concept>
                <code value="fhir"/>
                <display value="FHIR Infrastructure"/>
              </concept>
            </CodeSystem>
            """;

        NewIndexer(store).Rebuild(WriteXml(xml), _conn);

        Assert.Empty(store.AppliedRetires);
    }

    /// <summary>
    /// Synthetic <see cref="IHl7WorkGroupStore"/> that keeps state in
    /// memory, matching the lookup semantics the real store provides
    /// (case-insensitive Code lookup; LoadAll returns the current snapshot).
    /// </summary>
    private sealed class FakeStore : IHl7WorkGroupStore
    {
        private readonly Dictionary<string, Hl7WorkGroupDto> _store
            = new(StringComparer.OrdinalIgnoreCase);

        public List<(IReadOnlyList<Hl7WorkGroupDto> Upserts, IReadOnlyList<Hl7WorkGroupDto> Retires)> ApplyCalls { get; } = [];
        public List<Hl7WorkGroupDto> AppliedUpserts => ApplyCalls.SelectMany(c => c.Upserts).ToList();
        public List<Hl7WorkGroupDto> AppliedRetires => ApplyCalls.SelectMany(c => c.Retires).ToList();

        public void Seed(Hl7WorkGroupDto dto) => _store[dto.Code] = dto;

        public IReadOnlyList<Hl7WorkGroupDto> LoadAll(SqliteConnection connection)
            => _store.Values.ToList();

        public void ApplyChanges(
            SqliteConnection connection,
            IReadOnlyList<Hl7WorkGroupDto> toUpsert,
            IReadOnlyList<Hl7WorkGroupDto> toRetire)
        {
            ApplyCalls.Add((toUpsert.ToList(), toRetire.ToList()));
            foreach (Hl7WorkGroupDto d in toUpsert)
                _store[d.Code] = d;
            foreach (Hl7WorkGroupDto d in toRetire)
                _store[d.Code] = d;
        }

        public int Count(SqliteConnection connection) => _store.Count;
    }
}
