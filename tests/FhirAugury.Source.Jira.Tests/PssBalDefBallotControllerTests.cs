using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Phase 9 lock-in for the new per-shape controllers
/// (<see cref="ProjectScopeStatementController"/>,
/// <see cref="BalDefController"/>, <see cref="BallotController"/>):
/// happy-path GET-by-key returns a payload containing the Key, missing
/// keys yield 404. Mirrors the direct-instantiation pattern used by
/// <c>ProjectsControllerTests</c>.
/// </summary>
public class PssBalDefBallotControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly IOptions<JiraServiceOptions> _options;

    public PssBalDefBallotControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_shape_ctrl_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _options = Options.Create(new JiraServiceOptions { BaseUrl = "https://jira.example.com" });
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static bool PayloadContainsKey(object? value, string expectedKey)
    {
        if (value is null) return false;
        System.Reflection.PropertyInfo? prop = value.GetType().GetProperty("Key");
        if (prop is null) return false;
        return string.Equals(prop.GetValue(value) as string, expectedKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Pss_GetByKey_ReturnsPayload_AndUnknownReturns404()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraProjectScopeStatementRecord.Insert(conn, new JiraProjectScopeStatementRecord
            {
                Id = JiraProjectScopeStatementRecord.GetIndex(),
                Key = "PSS-1",
                ProjectKey = "PSS",
                Title = "test pss",
                Description = null,
                Summary = "test pss",
                Type = "Project Scope Statement",
                Priority = "Major",
                Status = "Open",
                Assignee = null,
                Reporter = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ResolvedAt = null,
                SponsoringWorkGroup = "FHIR-I",
            });
        }

        ProjectScopeStatementController controller = new ProjectScopeStatementController(_db, _options);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetPss("PSS-1"));
        Assert.True(PayloadContainsKey(ok.Value, "PSS-1"));

        Assert.IsType<NotFoundObjectResult>(controller.GetPss("MISSING-1"));
    }

    [Fact]
    public void Baldef_GetByKey_ReturnsPayload_AndUnknownReturns404()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraBaldefRecord.Insert(conn, new JiraBaldefRecord
            {
                Id = JiraBaldefRecord.GetIndex(),
                Key = "BALDEF-1",
                ProjectKey = "BALDEF",
                Title = "pkg",
                Description = null,
                Summary = "pkg",
                Type = "Ballot",
                Priority = "Major",
                Status = "Open",
                Assignee = null,
                Reporter = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ResolvedAt = null,
                BallotCode = "2024-Sep | FHIR R5",
                BallotCycle = "2024-Sep",
                BallotPackageName = "FHIR R5",
                BallotCategory = "STU",
            });
        }

        BalDefController controller = new BalDefController(_db, _options);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetBalDef("BALDEF-1"));
        Assert.True(PayloadContainsKey(ok.Value, "BALDEF-1"));

        Assert.IsType<NotFoundObjectResult>(controller.GetBalDef("MISSING-1"));
    }

    [Fact]
    public void Ballot_GetByKey_ReturnsPayload_AndUnknownReturns404()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraBallotRecord.Insert(conn, new JiraBallotRecord
            {
                Id = JiraBallotRecord.GetIndex(),
                Key = "BALLOT-1",
                ProjectKey = "BALLOT",
                Title = "Affirmative - Jane Doe (Acme) : 2024-Sep | FHIR R5",
                Description = null,
                Summary = "Affirmative - Jane Doe (Acme) : 2024-Sep | FHIR R5",
                Type = "Vote",
                Priority = "Major",
                Status = "Open",
                Assignee = null,
                Reporter = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ResolvedAt = null,
                VoteBallot = "Affirmative",
                BallotPackageCode = "2024-Sep | FHIR R5",
                BallotCycle = "2024-Sep",
                Voter = "Jane Doe",
            });
        }

        BallotController controller = new BallotController(_db, _options);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetBallot("BALLOT-1"));
        Assert.True(PayloadContainsKey(ok.Value, "BALLOT-1"));

        Assert.IsType<NotFoundObjectResult>(controller.GetBallot("MISSING-1"));
    }
}
