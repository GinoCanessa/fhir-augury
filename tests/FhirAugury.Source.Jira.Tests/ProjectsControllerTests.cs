using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class ProjectsControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly ProjectsController _controller;

    public ProjectsControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_proj_ctrl_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _controller = new ProjectsController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void List_EmptyTable_ReturnsEmpty()
    {
        IActionResult result = _controller.ListProjects();
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void List_ReturnsAllProjects()
    {
        SeedProject("FHIR", baseline: 5);
        SeedProject("BALLOT", baseline: 1);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.ListProjects());
        Assert.NotNull(ok.Value);
        // Use reflection — anonymous types are internal, so `dynamic` won't bind across assemblies.
        int total = (int)ok.Value!.GetType().GetProperty("total")!.GetValue(ok.Value)!;
        Assert.Equal(2, total);
    }

    [Fact]
    public void Get_UnknownKey_Returns404()
    {
        IActionResult result = _controller.GetProject("NOPE");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void Put_ClampsBaselineValueAndPersists()
    {
        SeedProject("FHIR", baseline: 5);

        IActionResult highResult = _controller.UpdateProject(
            "FHIR",
            new JiraProjectUpdateRequest(Enabled: true, BaselineValue: 99));
        Assert.IsType<OkObjectResult>(highResult);

        using SqliteConnection conn = _db.OpenConnection();
        JiraProjectRecord? after = JiraProjectRecord.SelectSingle(conn, Key: "FHIR");
        Assert.NotNull(after);
        Assert.Equal(10, after.BaselineValue);

        IActionResult lowResult = _controller.UpdateProject(
            "FHIR",
            new JiraProjectUpdateRequest(Enabled: false, BaselineValue: -7));
        Assert.IsType<OkObjectResult>(lowResult);

        after = JiraProjectRecord.SelectSingle(conn, Key: "FHIR");
        Assert.NotNull(after);
        Assert.Equal(0, after.BaselineValue);
        Assert.False(after.Enabled);
    }

    [Fact]
    public void Put_OmittedBaselineLeavesValueUnchanged()
    {
        SeedProject("FHIR", baseline: 7);

        IActionResult result = _controller.UpdateProject(
            "FHIR",
            new JiraProjectUpdateRequest(Enabled: false, BaselineValue: null));
        Assert.IsType<OkObjectResult>(result);

        using SqliteConnection conn = _db.OpenConnection();
        JiraProjectRecord? after = JiraProjectRecord.SelectSingle(conn, Key: "FHIR");
        Assert.NotNull(after);
        Assert.Equal(7, after.BaselineValue);
        Assert.False(after.Enabled);
    }

    [Fact]
    public void Put_UnknownKey_Returns404()
    {
        IActionResult result = _controller.UpdateProject(
            "NOPE",
            new JiraProjectUpdateRequest(Enabled: true, BaselineValue: 3));
        Assert.IsType<NotFoundObjectResult>(result);
    }

    private void SeedProject(string key, int baseline)
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraProjectRecord.Insert(conn, new JiraProjectRecord
        {
            Id = JiraProjectRecord.GetIndex(),
            Key = key,
            Enabled = true,
            BaselineValue = baseline,
            IssueCount = 0,
            LastSyncAt = null,
        });
    }
}
