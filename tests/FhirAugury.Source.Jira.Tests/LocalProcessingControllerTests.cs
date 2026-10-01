using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Tests;

public class LocalProcessingControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly LocalProcessingController _controller;

    public LocalProcessingControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_lp_ctrl_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _controller = new LocalProcessingController(_db, Options.Create(new JiraServiceOptions()));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private JiraIssueRecord SeedIssue(
        SqliteConnection conn,
        string key,
        string projectKey = "FHIR",
        DateTimeOffset? processedAt = null,
        string? relatedArtifacts = null,
        string? status = "Open",
        string? reporter = null,
        string? labels = null,
        string? workGroup = null,
        string? specification = null)
    {
        JiraIssueRecord issue = new()
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = $"Issue {key}",
            Description = null,
            Summary = null,
            Type = "Bug",
            Priority = "Major",
            Status = status ?? "Open",
            Resolution = null,
            ResolutionDescription = null,
            Assignee = null,
            Reporter = reporter,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            WorkGroup = workGroup,
            Specification = specification,
            RaisedInVersion = null,
            SelectedBallot = null,
            RelatedArtifacts = relatedArtifacts,
            RelatedIssues = null,
            DuplicateOf = null,
            AppliedVersions = null,
            ChangeType = null,
            Impact = null,
            Vote = null,
            Labels = labels,
            CommentCount = 0,
            ChangeCategory = null,
            ChangeImpact = null,
            ProcessedLocallyAt = processedAt,
        };
        JiraIssueRecord.Insert(conn, issue);
        return issue;
    }

    private static JiraLocalProcessingListResponse UnwrapList(IActionResult result) =>
        Assert.IsType<JiraLocalProcessingListResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static JiraIssueSummaryEntry UnwrapSingle(IActionResult result) =>
        Assert.IsType<JiraIssueSummaryEntry>(Assert.IsType<OkObjectResult>(result).Value);

    private static JiraLocalProcessingSetResponse UnwrapSet(IActionResult result) =>
        Assert.IsType<JiraLocalProcessingSetResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static JiraLocalProcessingClearResponse UnwrapClear(IActionResult result) =>
        Assert.IsType<JiraLocalProcessingClearResponse>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public void GetTickets_EmptyFilter_ReturnsAllOrderedByKey()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-3");
            SeedIssue(conn, "FHIR-1");
            SeedIssue(conn, "FHIR-2");
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest()));

        Assert.Equal(3, response.Total);
        Assert.Equal(JiraLocalProcessingQueryBuilder.DefaultLimit, response.Limit);
        Assert.Equal(0, response.Offset);
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], response.Results.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void GetTickets_Paging_ReturnsRequestedPageAndUnpagedTotal()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            for (int i = 1; i <= 5; i++) SeedIssue(conn, $"FHIR-{i}");
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Limit = 2, Offset = 2 }));

        Assert.Equal(5, response.Total);
        Assert.Equal(2, response.Limit);
        Assert.Equal(2, response.Offset);
        Assert.Equal(["FHIR-3", "FHIR-4"], response.Results.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void GetTickets_EveryPageCarriesSameStableSourceProvenance()
    {
        DateTimeOffset refreshedAt = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        using (SqliteConnection connection = _db.OpenConnection())
        {
            SeedIssue(connection, "FHIR-1");
            SeedIssue(connection, "FHIR-2");
            SeedIssue(connection, "FHIR-3");
            InsertSyncState(connection, "FHIR", refreshedAt);
        }

        JiraLocalProcessingListResponse first = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest
            {
                Limit = 2,
                Offset = 0,
            }));
        JiraLocalProcessingListResponse second = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest
            {
                Limit = 2,
                Offset = 2,
            }));

        Assert.NotNull(first.Provenance);
        Assert.NotNull(second.Provenance);
        Assert.True(first.Provenance.IsStable);
        Assert.True(second.Provenance.IsStable);
        Assert.Equal(first.Provenance.ContentRevision, second.Provenance.ContentRevision);
        Assert.Equal(
            refreshedAt,
            first.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        Assert.Equal(
            refreshedAt,
            second.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        Assert.Equal(["FHIR-1", "FHIR-2"], first.Results.Select(result => result.Key));
        Assert.Equal(["FHIR-3"], second.Results.Select(result => result.Key));
        Assert.Equal(3, first.Total);
        Assert.Equal(3, second.Total);

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        string json = JsonSerializer.Serialize(first, jsonOptions);
        JiraLocalProcessingListResponse? roundTrip =
            JsonSerializer.Deserialize<JiraLocalProcessingListResponse>(
                json,
                jsonOptions);
        Assert.NotNull(roundTrip);
        Assert.NotNull(roundTrip.Provenance);
        Assert.Equal(
            refreshedAt,
            roundTrip.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
    }

    [Fact]
    public void GetTickets_DuringMutationMarksPageProvenanceUnstable()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            SeedIssue(connection, "FHIR-1");
        }

        JiraSourceStateRecord started = _db.BeginContentMutation();
        try
        {
            JiraLocalProcessingListResponse response = UnwrapList(
                _controller.GetTickets(new JiraLocalProcessingListRequest()));

            Assert.NotNull(response.Provenance);
            Assert.False(response.Provenance.IsStable);
            Assert.Equal(started.ContentRevision, response.Provenance.ContentRevision);
            Assert.Single(response.Results);
            Assert.Equal(1, response.Total);
        }
        finally
        {
            _db.CompleteContentMutation();
        }
    }

    [Fact]
    public void GetTickets_ProjectsFilter_OrJoinsWithinField()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", projectKey: "FHIR");
            SeedIssue(conn, "XYZ-1", projectKey: "XYZ");
            SeedIssue(conn, "OTHER-1", projectKey: "OTHER");
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Projects = ["FHIR", "XYZ"] }));

        Assert.Equal(2, response.Total);
        Assert.Equal(["FHIR-1", "XYZ-1"], response.Results.Select(r => r.Key).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void GetTickets_ProcessedLocallyFilter_RespectsTriState()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-2", processedAt: null);
            SeedIssue(conn, "FHIR-3", processedAt: DateTimeOffset.UtcNow);
        }

        JiraLocalProcessingListResponse onlyProcessed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }));
        Assert.Equal(2, onlyProcessed.Total);

        JiraLocalProcessingListResponse onlyUnprocessed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = false }));
        Assert.Equal(1, onlyUnprocessed.Total);
        Assert.Equal("FHIR-2", onlyUnprocessed.Results.Single().Key);

        JiraLocalProcessingListResponse all = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = null }));
        Assert.Equal(3, all.Total);
    }

    [Fact]
    public void GetTickets_RelatedArtifacts_CaseInsensitiveSubstring()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", relatedArtifacts: "US Core, IPS");
            SeedIssue(conn, "FHIR-2", relatedArtifacts: "SDC");
            SeedIssue(conn, "FHIR-3", relatedArtifacts: null);
        }

        JiraLocalProcessingListResponse coreOnly = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { RelatedArtifacts = ["core"] }));
        Assert.Equal(["FHIR-1"], coreOnly.Results.Select(r => r.Key).ToArray());

        JiraLocalProcessingListResponse both = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { RelatedArtifacts = ["core", "sdc"] }));
        Assert.Equal(["FHIR-1", "FHIR-2"], both.Results.Select(r => r.Key).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void GetTickets_Labels_OrSemantics()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraIssueRecord i1 = SeedIssue(conn, "FHIR-1");
            JiraIssueRecord i2 = SeedIssue(conn, "FHIR-2");
            JiraIssueRecord i3 = SeedIssue(conn, "FHIR-3");

            JiraIndexLabelRecord l1 = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "L1", IssueCount = 1 };
            JiraIndexLabelRecord l2 = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "L2", IssueCount = 1 };
            JiraIndexLabelRecord l3 = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "L3", IssueCount = 1 };
            JiraIndexLabelRecord.Insert(conn, l1);
            JiraIndexLabelRecord.Insert(conn, l2);
            JiraIndexLabelRecord.Insert(conn, l3);

            JiraIssueLabelRecord.Insert(conn, new JiraIssueLabelRecord { Id = JiraIssueLabelRecord.GetIndex(), IssueKey = i1.Key, LabelId = l1.Id });
            JiraIssueLabelRecord.Insert(conn, new JiraIssueLabelRecord { Id = JiraIssueLabelRecord.GetIndex(), IssueKey = i2.Key, LabelId = l2.Id });
            JiraIssueLabelRecord.Insert(conn, new JiraIssueLabelRecord { Id = JiraIssueLabelRecord.GetIndex(), IssueKey = i3.Key, LabelId = l3.Id });
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Labels = ["L1", "L2"] }));

        Assert.Equal(["FHIR-1", "FHIR-2"], response.Results.Select(r => r.Key).OrderBy(s => s).ToArray());
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData(new[] { "inc-01", "inc-02" }, null, "prefix-inc-02-suffix", true)]
    [InlineData(new[] { "inc-01", "inc-02" }, null, "other", false)]
    [InlineData(new[] { "inc-01" }, null, null, false)]
    [InlineData(null, new[] { "ex-01", "ex-02" }, "other", true)]
    [InlineData(null, new[] { "ex-01", "ex-02" }, "prefix-ex-02-suffix", false)]
    [InlineData(null, new[] { "ex-01", "ex-02" }, null, false)]
    [InlineData(null, new[] { "ex-01", "ex-02" }, "", true)]
    [InlineData(new[] { "inc-01", "inc-02" }, new[] { "ex-01", "ex-02" }, "inc-01,ex-02", false)]
    [InlineData(new[] { "inc-01", "inc-02" }, new[] { "ex-01", "ex-02" }, "prefix-inc-02-suffix", true)]
    public void GetSelectionTickets_LabelTruthTable(string[]? includes, string[]? excludes, string? labels, bool selected)
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            SeedIssue(connection, "FHIR-1", labels: labels);
            SeedIssue(connection, "FHIR-2", status: "Closed", labels: labels);
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetSelectionTickets(new JiraLocalProcessingSelectionRequest
            {
                Statuses = ["Open"],
                LabelText = new JiraLabelTextFilter
                {
                    Includes = includes?.ToList(),
                    Excludes = excludes?.ToList(),
                },
            }));

        string[] expectedKeys = selected ? ["FHIR-1"] : [];
        Assert.Equal(expectedKeys, response.Results.Select(result => result.Key));
        Assert.Equal(expectedKeys.Length, response.Total);
        Assert.Equal(500, response.Limit);
        Assert.Equal(0, response.Offset);
    }

    [Theory]
    [InlineData("null", null, true)]
    [InlineData("{}", null, true)]
    [InlineData("""{"includes":null,"excludes":null}""", null, true)]
    [InlineData("""{"includes":[],"excludes":[]}""", null, true)]
    [InlineData("""{"includes":[null,""," \t\r\n"],"excludes":[null,""," \t\r\n"]}""", null, true)]
    [InlineData("""{"includes":[null,""," \t\r\n"],"excludes":[null,""," \t\r\n"]}""", "", true)]
    [InlineData("""{"includes":[null,""," \t"],"excludes":["ex-01"]}""", "other", true)]
    [InlineData("""{"includes":[null,""," \t"],"excludes":["ex-01"]}""", null, false)]
    [InlineData("""{"includes":[null,""," \t","inc-01"],"excludes":[null,""," \t"]}""", "prefix-inc-01-suffix", true)]
    [InlineData("""{"includes":[null,""," \t","inc-01"],"excludes":[null,""," \t"]}""", "other", false)]
    [InlineData("""{"includes":[" inc-01 "]}""", "prefix inc-01 suffix", true)]
    [InlineData("""{"includes":[" inc-01 "]}""", "prefix-inc-01-suffix", false)]
    [InlineData("""{"excludes":[" ex-01 "]}""", "prefix ex-01 suffix", false)]
    [InlineData("""{"excludes":[" ex-01 "]}""", "prefix-ex-01-suffix", true)]
    [InlineData("""{"includes":["InC-01","InC-01"]}""", "prefix-inc-01-suffix", true)]
    [InlineData("""{"includes":["inc%02"]}""", "prefix-inc-anything-02-suffix", true)]
    [InlineData("""{"includes":["inc%02"]}""", "prefix-inc-only-suffix", false)]
    [InlineData("""{"includes":["inc_02"]}""", "prefix-incX02-suffix", true)]
    [InlineData("""{"includes":["inc_02"]}""", "prefix-inc02-suffix", false)]
    [InlineData("""{"includes":["inc_02"]}""", "prefix-incXX02-suffix", false)]
    [InlineData("""{"includes":["%"]}""", "", true)]
    [InlineData("""{"includes":["%"]}""", null, false)]
    [InlineData("""{"includes":["_"]}""", "x", true)]
    [InlineData("""{"includes":["_"]}""", "", false)]
    [InlineData("""{"excludes":["%"]}""", "", false)]
    [InlineData("""{"excludes":["%"]}""", "other", false)]
    [InlineData("""{"excludes":["%"]}""", null, false)]
    [InlineData("""{"excludes":["_"]}""", "", true)]
    [InlineData("""{"excludes":["_"]}""", "x", false)]
    [InlineData("""{"includes":["x' OR 1=1 --"]}""", "prefix-x' OR 1=1 --suffix", true)]
    [InlineData("""{"includes":["x' OR 1=1 --"]}""", "other", false)]
    [InlineData("""{"excludes":["x' OR 1=1 --"]}""", "prefix-x' OR 1=1 --suffix", false)]
    [InlineData("""{"excludes":["x' OR 1=1 --"]}""", "other", true)]
    [InlineData("""{"includes":["quoted\"label"]}""", "prefix-quoted\"label-suffix", true)]
    [InlineData("""{"includes":["quoted\"label"]}""", "prefix-quotedlabel-suffix", false)]
    [InlineData("""{"includes":["path\\label"]}""", @"prefix-path\label-suffix", true)]
    [InlineData("""{"includes":["path\\label"]}""", "prefix-pathlabel-suffix", false)]
    [InlineData("""{"excludes":["path\\label"]}""", @"prefix-path\label-suffix", false)]
    [InlineData("""{"includes":["path\\%end"]}""", @"prefix-path\anything-end-suffix", true)]
    [InlineData("""{"includes":["path\\%end"]}""", "prefix-path-anything-end-suffix", false)]
    [InlineData("""{"includes":["InC-AsCii"]}""", "prefix-iNc-aScII-suffix", true)]
    [InlineData("""{"excludes":["EX-AsCiI"]}""", "prefix-eX-aScII-suffix", false)]
    [InlineData("""{"includes":["æ"]}""", "prefix-Æ-suffix", false)]
    [InlineData("""{"includes":["æ"]}""", "prefix-æ-suffix", true)]
    [InlineData("""{"includes":["Æ"]}""", "prefix-æ-suffix", false)]
    [InlineData("""{"excludes":["æ"]}""", "prefix-Æ-suffix", true)]
    [InlineData("""{"excludes":["æ"]}""", "prefix-æ-suffix", false)]
    public void GetSelectionTickets_PreservesNativeLikeAndLiteralValues(string labelTextJson, string? labels, bool selected)
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            // Raw label text is deliberately independent of the exact-label index.
            SeedIssue(connection, "FHIR-1", labels: labels);
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetSelectionTickets(new JiraLocalProcessingSelectionRequest
            {
                LabelText = JsonSerializer.Deserialize<JiraLabelTextFilter>(
                    labelTextJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            }));

        string[] expectedKeys = selected ? ["FHIR-1"] : [];
        Assert.Equal(expectedKeys, response.Results.Select(result => result.Key));
        Assert.Equal(expectedKeys.Length, response.Total);
    }

    [Fact]
    public void GetSelectionTickets_FiltersBeforePagingAndReturnsMatchingTotal()
    {
        DateTimeOffset refreshedAt = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        using (SqliteConnection connection = _db.OpenConnection())
        {
            for (int i = 8; i >= 1; i--)
            {
                string? labels = i switch
                {
                    1 => "other",
                    3 => "inc-01,ex-02",
                    4 => "inc-02",
                    5 => null,
                    _ => "inc-01",
                };
                SeedIssue(connection, $"FHIR-{i}", status: i == 8 ? "Closed" : "Open", labels: labels);
            }
            InsertSyncState(connection, "FHIR", refreshedAt);
        }

        JiraLocalProcessingSelectionRequest request = new()
        {
            Statuses = ["Open"],
            LabelText = new JiraLabelTextFilter { Includes = ["inc-01", "inc-02"], Excludes = ["ex-01", "ex-02"] },
            Limit = 2,
            Offset = 0,
        };
        JiraLocalProcessingListResponse first = UnwrapList(_controller.GetSelectionTickets(request));
        JiraLocalProcessingListResponse second = UnwrapList(_controller.GetSelectionTickets(request with { Offset = 2 }));
        JiraLocalProcessingListResponse empty = UnwrapList(_controller.GetSelectionTickets(request with { Offset = 10 }));

        Assert.Equal(["FHIR-2", "FHIR-4"], first.Results.Select(result => result.Key));
        Assert.Equal(["FHIR-6", "FHIR-7"], second.Results.Select(result => result.Key));
        Assert.Empty(empty.Results);
        Assert.Equal(0, first.Offset);
        Assert.Equal(2, second.Offset);
        Assert.Equal(10, empty.Offset);
        foreach (JiraLocalProcessingListResponse page in new[] { first, second, empty })
        {
            Assert.Equal(4, page.Total);
            Assert.Equal(2, page.Limit);
            Assert.NotNull(page.Provenance);
            Assert.True(page.Provenance.IsStable);
            Assert.Equal(first.Provenance?.ContentRevision, page.Provenance.ContentRevision);
            Assert.Equal(refreshedAt, page.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        }

        JiraLocalProcessingListResponse oldPage = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Limit = 2, Offset = 0 }));
        Assert.Equal(8, oldPage.Total);
        Assert.Equal(["FHIR-1", "FHIR-2"], oldPage.Results.Select(result => result.Key));
    }

    [Fact]
    public void GetSelectionTickets_KeysScopeSelection()
    {
        const string quotedKey = "FHIR-' OR 1=1 --";
        using (SqliteConnection connection = _db.OpenConnection())
        {
            SeedIssue(connection, "FHIR-1", labels: "inc-01");
            SeedIssue(connection, "FHIR-2", processedAt: DateTimeOffset.UtcNow, labels: "inc-01");
            SeedIssue(connection, "FHIR-3", labels: "other");
            SeedIssue(connection, "FHIR-4", labels: "inc-01,ex-01");
            SeedIssue(connection, "FHIR-5", labels: null);
            SeedIssue(connection, quotedKey, labels: "inc-01");
        }

        JiraLocalProcessingSelectionRequest request = new()
        {
            Keys = ["FHIR-2", quotedKey, "MISSING", "FHIR-3", "FHIR-4", "FHIR-5", "FHIR-2"],
            LabelText = new JiraLabelTextFilter { Includes = ["inc-01"], Excludes = ["ex-01"] },
        };
        JiraLocalProcessingListResponse scoped = UnwrapList(_controller.GetSelectionTickets(request));
        Assert.Equal(2, scoped.Total);
        Assert.Equal([quotedKey, "FHIR-2"], scoped.Results.Select(result => result.Key));

        JiraLocalProcessingListResponse missing = UnwrapList(
            _controller.GetSelectionTickets(request with { Keys = ["MISSING"] }));
        Assert.Empty(missing.Results);
        Assert.Equal(0, missing.Total);

        foreach (List<string>? keys in new List<string>?[] { null, [] })
        {
            JiraLocalProcessingListResponse unrestrictedKeys = UnwrapList(
                _controller.GetSelectionTickets(request with { Keys = keys }));
            Assert.Equal(3, unrestrictedKeys.Total);
            Assert.Equal([quotedKey, "FHIR-1", "FHIR-2"], unrestrictedKeys.Results.Select(result => result.Key));
        }

        JiraLocalProcessingListResponse keysOnly = UnwrapList(
            _controller.GetSelectionTickets(new JiraLocalProcessingSelectionRequest { Keys = ["FHIR-3", "FHIR-5"] }));
        Assert.Equal(2, keysOnly.Total);
        Assert.Equal(["FHIR-3", "FHIR-5"], keysOnly.Results.Select(result => result.Key));

        JiraLocalProcessingListResponse processed = UnwrapList(
            _controller.GetSelectionTickets(request with { ProcessedLocally = true }));
        JiraLocalProcessingListResponse unprocessed = UnwrapList(
            _controller.GetSelectionTickets(request with { ProcessedLocally = false }));
        Assert.Equal("FHIR-2", Assert.Single(processed.Results).Key);
        Assert.Equal(1, processed.Total);
        Assert.Equal(quotedKey, Assert.Single(unprocessed.Results).Key);
        Assert.Equal(1, unprocessed.Total);
    }

    [Theory]
    [InlineData(null, "fhir")]
    [InlineData("", "fhir")]
    [InlineData(" \t", "fhir")]
    [InlineData("fhir", "fhir")]
    [InlineData("FHIR", "fhir")]
    [InlineData("pss", "pss")]
    [InlineData("PsS", "pss")]
    [InlineData("baldef", "baldef")]
    [InlineData("BALDEF", "baldef")]
    [InlineData("ballot", "ballot")]
    [InlineData("BALLOT", "ballot")]
    [InlineData("bogus", null)]
    [InlineData(" fhir ", null)]
    [InlineData("jira_issues; SELECT 1", null)]
    public void GetSelectionTickets_PreservesMappingValidationAndProvenance(string? type, string? expectedType)
    {
        HttpPostAttribute route = Assert.Single(
            typeof(LocalProcessingController).GetMethods()
                .Single(method => method.Name == nameof(LocalProcessingController.GetSelectionTickets))
                .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
                .Cast<HttpPostAttribute>());
        Assert.Equal("selection-tickets", route.Template);

        JiraLocalProcessingSelectionRequest request = new()
        {
            LabelText = new JiraLabelTextFilter { Includes = ["inc-01", "inc-02"], Excludes = ["ex-01", "ex-02"] },
            Limit = 0,
            Offset = -5,
        };
        if (expectedType is null)
        {
            BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(
                _controller.GetSelectionTickets(request, type));
            Assert.Equal(400, badRequest.StatusCode);
            string? error = JsonSerializer.SerializeToElement(badRequest.Value).GetProperty("error").GetString();
            Assert.Equal($"Unknown type '{type}'. Expected one of: fhir, pss, baldef, ballot.", error);
            return;
        }

        DateTimeOffset refreshedAt = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        Dictionary<string, JiraIssueBaseRecord> issues = [];
        long contentRevision;
        using (SqliteConnection connection = _db.OpenConnection())
        {
            issues["fhir"] = SeedIssue(
                connection, "FHIR-1", labels: "prefix-inc-02-suffix", workGroup: "FHIR-I", specification: "Core");
            SeedIssue(connection, "FHIR-0", labels: "other");

            JiraProjectScopeStatementRecord pss = NewPss("PSS-1", processedAt: null) with
            {
                Labels = "prefix-inc-02-suffix",
                SponsoringWorkGroupsLegacy = "Legacy work group",
            };
            JiraProjectScopeStatementRecord.Insert(connection, pss);
            JiraProjectScopeStatementRecord.Insert(connection, NewPss("PSS-0", processedAt: null) with { Labels = "inc-02,ex-02" });
            issues["pss"] = pss;

            JiraBaldefRecord baldef = NewBaldef("BALDEF-1", processedAt: null) with
            {
                Labels = "prefix-inc-02-suffix",
                Specification = "Core",
            };
            JiraBaldefRecord.Insert(connection, baldef);
            JiraBaldefRecord.Insert(connection, NewBaldef("BALDEF-0", processedAt: null));
            issues["baldef"] = baldef;

            JiraBallotRecord ballot = NewBallot("BALLOT-1", processedAt: null) with
            {
                Labels = "prefix-inc-02-suffix",
                Specification = "Core",
            };
            JiraBallotRecord.Insert(connection, ballot);
            JiraBallotRecord.Insert(connection, NewBallot("BALLOT-0", processedAt: null) with { Labels = "other" });
            issues["ballot"] = ballot;

            InsertSyncState(connection, "FHIR", refreshedAt);
            InsertSyncState(connection, "SYNC-ONLY", refreshedAt.AddHours(-1));
            contentRevision = JiraDatabase.ReadSourceState(connection).ContentRevision;
        }

        JiraIssueBaseRecord expected = issues[expectedType];
        JiraLocalProcessingListResponse stable = UnwrapList(_controller.GetSelectionTickets(request, type));
        JiraIssueSummaryEntry summary = Assert.Single(stable.Results);
        Assert.Equal(1, stable.Total);
        Assert.Equal(500, stable.Limit);
        Assert.Equal(0, stable.Offset);
        Assert.Equal(expected.Key, summary.Key);
        Assert.Equal(expected.ProjectKey, summary.ProjectKey);
        Assert.Equal(expected.Title, summary.Title);
        Assert.Equal(expected.Type, summary.Type);
        Assert.Equal(expected.Priority, summary.Priority);
        Assert.Equal(expected.Status, summary.Status);
        Assert.Equal(expected.UpdatedAt, summary.UpdatedAt);
        Assert.Equal($"{new JiraServiceOptions().BaseUrl}/browse/{expected.Key}", summary.Url);
        Assert.Equal(expectedType switch { "fhir" => "FHIR-I", "pss" => "Legacy work group", _ => "" }, summary.WorkGroup);
        Assert.Equal(expectedType == "pss" ? "" : "Core", summary.Specification);
        Assert.NotNull(stable.Provenance);
        Assert.Equal("jira", stable.Provenance.Source);
        Assert.True(stable.Provenance.IsStable);
        Assert.Equal(contentRevision, stable.Provenance.ContentRevision);
        Assert.Equal(refreshedAt, stable.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        Assert.Equal(refreshedAt.AddHours(-1), stable.Provenance.ProjectLastSuccessfulRefreshAt["SYNC-ONLY"]);
        Assert.True(stable.Provenance.ProjectLastSuccessfulRefreshAt.ContainsKey(expected.ProjectKey));
        if (expectedType != "fhir")
        {
            Assert.Null(stable.Provenance.ProjectLastSuccessfulRefreshAt[expected.ProjectKey]);
        }

        JiraLocalProcessingListResponse oldPage = UnwrapList(_controller.GetTickets(request, type));
        Assert.Equal(2, oldPage.Total);
        Assert.Equal(summary, oldPage.Results.Single(result => result.Key == expected.Key));
        Assert.NotNull(oldPage.Provenance);
        Assert.Equal(contentRevision, oldPage.Provenance.ContentRevision);
        Assert.Equal(
            oldPage.Provenance.ProjectLastSuccessfulRefreshAt.OrderBy(pair => pair.Key),
            stable.Provenance.ProjectLastSuccessfulRefreshAt.OrderBy(pair => pair.Key));

        JiraSourceStateRecord started = _db.BeginContentMutation();
        try
        {
            JiraLocalProcessingListResponse unstable = UnwrapList(_controller.GetSelectionTickets(request, type));
            Assert.Equal(expected.Key, Assert.Single(unstable.Results).Key);
            Assert.Equal(1, unstable.Total);
            Assert.NotNull(unstable.Provenance);
            Assert.False(unstable.Provenance.IsStable);
            Assert.Equal(started.ContentRevision, unstable.Provenance.ContentRevision);
            Assert.Equal(refreshedAt, unstable.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        }
        finally
        {
            _db.CompleteContentMutation();
        }

        JiraLocalProcessingListResponse empty = UnwrapList(
            _controller.GetSelectionTickets(request with { Offset = 3 }, type));
        Assert.Empty(empty.Results);
        Assert.Equal(1, empty.Total);
        Assert.Equal(500, empty.Limit);
        Assert.Equal(3, empty.Offset);
        Assert.NotNull(empty.Provenance);
        Assert.True(empty.Provenance.IsStable);
        Assert.Equal(contentRevision + 2, empty.Provenance.ContentRevision);
        Assert.Equal(refreshedAt, empty.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        Assert.Equal(refreshedAt.AddHours(-1), empty.Provenance.ProjectLastSuccessfulRefreshAt["SYNC-ONLY"]);

        using SqliteConnection check = _db.OpenConnection();
        Assert.Equal(contentRevision + 2, JiraDatabase.ReadSourceState(check).ContentRevision);
    }

    [Fact]
    public void GetSelectionTickets_CombinesInheritedExactLabelsWithoutRedefiningThem()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            SeedIssue(connection, "FHIR-1", labels: "inc-01");
            SeedIssue(connection, "FHIR-2", labels: "inc-02");
            SeedIssue(connection, "FHIR-3", labels: "inc-01,ex-02");
            SeedIssue(connection, "FHIR-4", labels: "other");
            SeedIssue(connection, "FHIR-5", labels: "inc-01");
            SeedIssue(connection, "FHIR-6", labels: "Exact-L1,inc-01");

            JiraIndexLabelRecord first = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "Exact-L1", IssueCount = 3 };
            JiraIndexLabelRecord second = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "Exact-L2", IssueCount = 1 };
            JiraIndexLabelRecord partial = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "Prefix-Exact-L1-Suffix", IssueCount = 1 };
            JiraIndexLabelRecord.Insert(connection, first);
            JiraIndexLabelRecord.Insert(connection, second);
            JiraIndexLabelRecord.Insert(connection, partial);
            foreach ((string key, int labelId) in new[]
                     {
                         ("FHIR-1", first.Id),
                         ("FHIR-2", second.Id),
                         ("FHIR-3", first.Id),
                         ("FHIR-4", first.Id),
                         ("FHIR-5", partial.Id),
                     })
            {
                JiraIssueLabelRecord.Insert(connection, new JiraIssueLabelRecord
                {
                    Id = JiraIssueLabelRecord.GetIndex(),
                    IssueKey = key,
                    LabelId = labelId,
                });
            }
        }

        JiraLocalProcessingSelectionRequest request = new()
        {
            Labels = ["Exact-L1", "Exact-L2"],
            LabelText = new JiraLabelTextFilter { Includes = ["inc-01", "inc-02"], Excludes = ["ex-01", "ex-02"] },
        };
        JiraLocalProcessingListResponse combined = UnwrapList(_controller.GetSelectionTickets(request));
        Assert.Equal(2, combined.Total);
        Assert.Equal(["FHIR-1", "FHIR-2"], combined.Results.Select(result => result.Key));

        JiraLocalProcessingListResponse exactOnly = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Labels = ["Exact-L1", "Exact-L2"] }));
        Assert.Equal(4, exactOnly.Total);
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3", "FHIR-4"], exactOnly.Results.Select(result => result.Key));

        JiraLocalProcessingListResponse inactiveText = UnwrapList(
            _controller.GetSelectionTickets(request with { LabelText = null }));
        Assert.Equal(exactOnly.Total, inactiveText.Total);
        Assert.Equal(exactOnly.Results.Select(result => result.Key), inactiveText.Results.Select(result => result.Key));

        JiraLocalProcessingListResponse textOnly = UnwrapList(
            _controller.GetSelectionTickets(request with { Labels = [] }));
        Assert.Equal(4, textOnly.Total);
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-5", "FHIR-6"], textOnly.Results.Select(result => result.Key));

        JiraLocalProcessingListResponse wildcardExact = UnwrapList(
            _controller.GetSelectionTickets(request with { Labels = ["Exact-L%"] }));
        JiraLocalProcessingListResponse oldWildcardExact = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Labels = ["Exact-L%"] }));
        Assert.Empty(wildcardExact.Results);
        Assert.Equal(0, wildcardExact.Total);
        Assert.Empty(oldWildcardExact.Results);
        Assert.Equal(0, oldWildcardExact.Total);

        JiraLocalProcessingListResponse fullExact = UnwrapList(
            _controller.GetSelectionTickets(request with { Labels = ["Prefix-Exact-L1-Suffix"] }));
        Assert.Equal("FHIR-5", Assert.Single(fullExact.Results).Key);
        Assert.Equal(1, fullExact.Total);
    }

    [Fact]
    public void GetRandomTicket_WithMatches_ReturnsOneOfThem()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1");
            SeedIssue(conn, "FHIR-2");
            SeedIssue(conn, "FHIR-3");
        }

        HashSet<string> seen = [];
        for (int i = 0; i < 20; i++)
        {
            JiraIssueSummaryEntry entry = UnwrapSingle(
                _controller.GetRandomTicket(new JiraLocalProcessingFilter()));
            Assert.Contains(entry.Key, new[] { "FHIR-1", "FHIR-2", "FHIR-3" });
            seen.Add(entry.Key);
        }

        Assert.True(seen.Count >= 2, "Expected at least two distinct random keys across 20 trials.");
    }

    [Fact]
    public void GetRandomTicket_NoMatches_Returns404()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: null);
        }

        IActionResult result = _controller.GetRandomTicket(
            new JiraLocalProcessingFilter { ProcessedLocally = true });
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void SetProcessed_True_StoresCurrentTimestamp()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: null);
        }

        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-5);
        JiraLocalProcessingSetResponse response = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "FHIR-1", ProcessedLocally = true }));
        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(5);

        Assert.Equal("FHIR-1", response.Key);
        Assert.False(response.PreviousValue);
        Assert.True(response.NewValue);

        using SqliteConnection conn2 = _db.OpenConnection();
        JiraIssueRecord? fetched = JiraIssueRecord.SelectList(conn2, Key: "FHIR-1").FirstOrDefault();
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.ProcessedLocallyAt);
        Assert.InRange(fetched.ProcessedLocallyAt.Value, before, after);
    }

    [Fact]
    public void SetProcessed_FalseAndNull_BothClearTheColumn()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-A", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-B", processedAt: DateTimeOffset.UtcNow);
        }

        JiraLocalProcessingSetResponse r1 = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "FHIR-A", ProcessedLocally = false }));
        Assert.True(r1.PreviousValue);
        Assert.False(r1.NewValue);

        JiraLocalProcessingSetResponse r2 = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "FHIR-B", ProcessedLocally = null }));
        Assert.True(r2.PreviousValue);
        Assert.False(r2.NewValue);

        using SqliteConnection conn2 = _db.OpenConnection();
        Assert.Null(JiraIssueRecord.SelectList(conn2, Key: "FHIR-A").Single().ProcessedLocallyAt);
        Assert.Null(JiraIssueRecord.SelectList(conn2, Key: "FHIR-B").Single().ProcessedLocallyAt);
    }

    [Fact]
    public void SetProcessed_UnknownKey_Returns404AndDoesNotInsert()
    {
        IActionResult result = _controller.SetProcessed(
            new JiraLocalProcessingSetRequest { Key = "NOPE-1", ProcessedLocally = true });
        Assert.IsType<NotFoundResult>(result);

        using SqliteConnection conn = _db.OpenConnection();
        Assert.Empty(JiraIssueRecord.SelectList(conn, Key: "NOPE-1"));
    }

    [Fact]
    public void GetTickets_InterleavedProcessedMutationsChangeMembershipAndRevision()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            SeedIssue(connection, "FHIR-1");
            SeedIssue(connection, "FHIR-2");
            SeedIssue(connection, "FHIR-3");
        }

        JiraLocalProcessingListRequest request = new()
        {
            ProcessedLocally = false,
            Limit = 1,
            Offset = 0,
        };
        JiraLocalProcessingListResponse before = UnwrapList(
            _controller.GetTickets(request));

        UnwrapSet(_controller.SetProcessed(
            new JiraLocalProcessingSetRequest
            {
                Key = "FHIR-1",
                ProcessedLocally = true,
            }));

        JiraLocalProcessingListResponse afterSet = UnwrapList(
            _controller.GetTickets(request));

        Assert.Equal(3, before.Total);
        Assert.Equal("FHIR-1", Assert.Single(before.Results).Key);
        Assert.Equal(2, afterSet.Total);
        Assert.Equal("FHIR-2", Assert.Single(afterSet.Results).Key);
        Assert.NotNull(before.Provenance);
        Assert.NotNull(afterSet.Provenance);
        Assert.Equal(
            before.Provenance.ContentRevision + 1,
            afterSet.Provenance.ContentRevision);

        JiraLocalProcessingClearResponse cleared = UnwrapClear(
            _controller.ClearAllProcessed(type: "fhir"));
        JiraLocalProcessingListResponse afterClear = UnwrapList(
            _controller.GetTickets(request));

        Assert.Equal(1, cleared.RowsAffected);
        Assert.Equal(3, afterClear.Total);
        Assert.Equal("FHIR-1", Assert.Single(afterClear.Results).Key);
        Assert.NotNull(afterClear.Provenance);
        Assert.Equal(
            afterSet.Provenance.ContentRevision + 1,
            afterClear.Provenance.ContentRevision);
    }

    [Fact]
    public void SetProcessed_WhenRevisionAdvanceFails_RollsBackMembershipChange()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            SeedIssue(connection, "FHIR-1");
            using SqliteCommand trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER reject_source_revision
                BEFORE UPDATE ON jira_source_state
                BEGIN
                    SELECT RAISE(ABORT, 'revision rejected');
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() =>
            _controller.SetProcessed(new JiraLocalProcessingSetRequest
            {
                Key = "FHIR-1",
                ProcessedLocally = true,
            }));

        using SqliteConnection check = _db.OpenConnection();
        JiraIssueRecord issue = Assert.Single(
            JiraIssueRecord.SelectList(check, Key: "FHIR-1"));
        Assert.Null(issue.ProcessedLocallyAt);
        Assert.Equal(0, JiraDatabase.ReadSourceState(check).ContentRevision);
    }

    // ── Phase 9 / 10: per-shape ?type= routing ─────────────────────────

    private static JiraProjectScopeStatementRecord NewPss(string key, DateTimeOffset? processedAt) => new JiraProjectScopeStatementRecord
    {
        Id = JiraProjectScopeStatementRecord.GetIndex(),
        Key = key,
        ProjectKey = "PSS",
        Title = $"PSS {key}",
        Description = null,
        Summary = null,
        Type = "Project Scope Statement",
        Priority = "Major",
        Status = "Open",
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        ProcessedLocallyAt = processedAt,
    };

    private static JiraBaldefRecord NewBaldef(string key, DateTimeOffset? processedAt) => new JiraBaldefRecord
    {
        Id = JiraBaldefRecord.GetIndex(),
        Key = key,
        ProjectKey = "BALDEF",
        Title = $"BALDEF {key}",
        Description = null,
        Summary = null,
        Type = "Ballot",
        Priority = "Major",
        Status = "Open",
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        ProcessedLocallyAt = processedAt,
    };

    private static JiraBallotRecord NewBallot(string key, DateTimeOffset? processedAt) => new JiraBallotRecord
    {
        Id = JiraBallotRecord.GetIndex(),
        Key = key,
        ProjectKey = "BALLOT",
        Title = $"BALLOT {key}",
        Description = null,
        Summary = null,
        Type = "Vote",
        Priority = "Major",
        Status = "Open",
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        ProcessedLocallyAt = processedAt,
    };

    private static void InsertSyncState(
        SqliteConnection connection,
        string project,
        DateTimeOffset refreshedAt)
    {
        JiraSyncStateRecord.Insert(connection, new JiraSyncStateRecord
        {
            Id = JiraSyncStateRecord.GetIndex(),
            SourceName = JiraSource.SourceName,
            SubSource = JiraSyncStateHelper.SyncKey(project, "full"),
            LastSyncAt = refreshedAt,
            LastSuccessfulSyncAt = refreshedAt,
            LastCursor = null,
            ItemsIngested = 1,
            SyncSchedule = null,
            NextScheduledAt = null,
            Status = "success",
            LastError = null,
        });
    }

    [Fact]
    public void SetProcessed_PssTable_MarksOnlyPssRow()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraProjectScopeStatementRecord.Insert(conn, NewPss("PSS-1", processedAt: null));
            SeedIssue(conn, "FHIR-1", processedAt: null);
        }

        JiraLocalProcessingSetResponse resp = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "PSS-1", ProcessedLocally = true }, type: "pss"));
        Assert.True(resp.NewValue);

        JiraLocalProcessingListResponse listed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "pss"));
        Assert.Equal(1, listed.Total);
        Assert.Equal("PSS-1", listed.Results[0].Key);

        // FHIR table should be untouched.
        JiraLocalProcessingListResponse fhir = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "fhir"));
        Assert.Equal(0, fhir.Total);
    }

    [Fact]
    public void SetProcessed_BaldefTable_MarksOnlyBaldefRow()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraBaldefRecord.Insert(conn, NewBaldef("BALDEF-1", processedAt: null));
        }

        JiraLocalProcessingSetResponse resp = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "BALDEF-1", ProcessedLocally = true }, type: "baldef"));
        Assert.True(resp.NewValue);

        JiraLocalProcessingListResponse listed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "baldef"));
        Assert.Equal(1, listed.Total);
        Assert.Equal("BALDEF-1", listed.Results[0].Key);
    }

    [Fact]
    public void SetProcessed_BallotTable_MarksOnlyBallotRow()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-1", processedAt: null));
        }

        JiraLocalProcessingSetResponse resp = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "BALLOT-1", ProcessedLocally = true }, type: "ballot"));
        Assert.True(resp.NewValue);

        JiraLocalProcessingListResponse listed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "ballot"));
        Assert.Equal(1, listed.Total);
        Assert.Equal("BALLOT-1", listed.Results[0].Key);
    }

    [Fact]
    public void ClearAllProcessed_NoType_ClearsAcrossAllFourTables()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: now);
            JiraProjectScopeStatementRecord.Insert(conn, NewPss("PSS-1", processedAt: now));
            JiraBaldefRecord.Insert(conn, NewBaldef("BALDEF-1", processedAt: now));
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-1", processedAt: now));
        }

        JiraLocalProcessingClearResponse resp = UnwrapClear(_controller.ClearAllProcessed());
        Assert.Equal(4, resp.RowsAffected);

        using SqliteConnection check = _db.OpenConnection();
        Assert.Null(JiraIssueRecord.SelectList(check, Key: "FHIR-1").Single().ProcessedLocallyAt);
        Assert.Null(JiraProjectScopeStatementRecord.SelectList(check, Key: "PSS-1").Single().ProcessedLocallyAt);
        Assert.Null(JiraBaldefRecord.SelectList(check, Key: "BALDEF-1").Single().ProcessedLocallyAt);
        Assert.Null(JiraBallotRecord.SelectList(check, Key: "BALLOT-1").Single().ProcessedLocallyAt);
    }

    [Fact]
    public void SetProcessed_UnknownType_Returns400()
    {
        IActionResult result = _controller.SetProcessed(
            new JiraLocalProcessingSetRequest { Key = "X-1", ProcessedLocally = true }, type: "bogus");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void ClearAllProcessed_ClearsEveryRowAndReportsCount()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-2", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-3", processedAt: null);
        }

        JiraLocalProcessingClearResponse response = UnwrapClear(_controller.ClearAllProcessed());
        Assert.Equal(2, response.RowsAffected);

        using SqliteConnection conn2 = _db.OpenConnection();
        foreach (JiraIssueRecord r in JiraIssueRecord.SelectList(conn2))
        {
            Assert.Null(r.ProcessedLocallyAt);
        }
    }
}
