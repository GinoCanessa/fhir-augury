using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Pins the response-shape additions introduced by the preparer-hydration
/// feature (slot 0517-02, Phase 2): the additional Jira-issue metadata
/// keys land in <c>ItemResponse.Metadata</c> when the underlying record
/// carries them, and are absent (not empty-string) when null.
/// </summary>
public class ItemsControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly ItemsController _controller;

    public ItemsControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_items_ctrl_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        IOptions<JiraServiceOptions> options = Options.Create(new JiraServiceOptions { BaseUrl = "https://jira.example.com" });
        _controller = new ItemsController(_db, options);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void GetItem_SurfacesAllHydrationMetadataKeysWhenPresent()
    {
        InsertIssue(NewIssue("FHIR-100", configure: i =>
        {
            i.CommentCount = 7;
            i.Resolution = "Persuasive";
            i.ResolutionDescriptionPlain = "Done because reasons.";
            i.RaisedInVersion = "5.0.0-ballot1";
            i.SelectedBallot = "2026-Jan";
            i.ChangeCategory = "Refinement";
            i.Impact = "Compatible, substantive";
            i.DuplicateOf = "FHIR-99";
            i.RelatedIssues = "FHIR-101, FHIR-102";
            i.RelatedArtifacts = "FHIR-103, R4/observation";
            i.RelatedPages = "patient.html, observation.html";
            i.DescriptionPlain = "plaintext description body";
        }));

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetItem("FHIR-100", includeContent: true, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);
        Dictionary<string, string> metadata = response.Metadata!;

        Assert.Equal("7", metadata["comment_count"]);
        Assert.Equal("Persuasive", metadata["resolution"]);
        Assert.Equal("Done because reasons.", metadata["resolution_description_plain"]);
        Assert.Equal("5.0.0-ballot1", metadata["raised_in_version"]);
        Assert.Equal("2026-Jan", metadata["selected_ballot"]);
        Assert.Equal("Refinement", metadata["change_category"]);
        Assert.Equal("Compatible, substantive", metadata["impact"]);
        Assert.Equal("FHIR-99", metadata["duplicate_of"]);
        Assert.Equal("FHIR-101, FHIR-102", metadata["related_issues"]);
        Assert.Equal("FHIR-103, R4/observation", metadata["related_artifacts"]);
        Assert.Equal("patient.html, observation.html", metadata["related_pages"]);
        Assert.Equal("plaintext description body", metadata["description_plain"]);
    }

    [Fact]
    public void GetItem_OmitsNullMetadataKeys()
    {
        InsertIssue(NewIssue("FHIR-200"));

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetItem("FHIR-200", includeContent: true, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);
        Dictionary<string, string> metadata = response.Metadata!;

        Assert.False(metadata.ContainsKey("raised_in_version"));
        Assert.False(metadata.ContainsKey("selected_ballot"));
        Assert.False(metadata.ContainsKey("change_category"));
        Assert.False(metadata.ContainsKey("impact"));
        Assert.False(metadata.ContainsKey("duplicate_of"));
        Assert.False(metadata.ContainsKey("related_issues"));
        Assert.False(metadata.ContainsKey("related_artifacts"));
        Assert.False(metadata.ContainsKey("description_plain"));
        Assert.False(metadata.ContainsKey("resolution_description_plain"));
        Assert.Equal("0", metadata["comment_count"]);
    }

    [Fact]
    public void GetItem_OmitsDescriptionPlainWhenIncludeContentFalse()
    {
        InsertIssue(NewIssue("FHIR-300", configure: i => i.DescriptionPlain = "should not surface"));

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetItem("FHIR-300", includeContent: false, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);
        Assert.False(response.Metadata!.ContainsKey("description_plain"));
    }

    [Fact]
    public void GetItem_ReturnsDisplayOnlyPeopleAndProvenance()
    {
        DateTimeOffset refreshedAt = new(2026, 9, 8, 15, 30, 0, TimeSpan.Zero);
        JiraIssueRecord issue = NewIssue("FHIR-400", issue =>
        {
            issue.Reporter = "Reporter Name";
            issue.Assignee = "Assignee Name";
        });

        using (SqliteConnection connection = _db.OpenConnection())
        {
            JiraUserRecord reporter = InsertUser(
                connection,
                "reporter-private-id",
                "Reporter Name",
                isExplicit: true);
            JiraUserRecord assignee = InsertUser(
                connection,
                "assignee-private-id",
                "Assignee Name",
                isExplicit: true);
            JiraUserRecord amy = InsertUser(connection, "amy-private-id", "Amy", isExplicit: true);
            JiraUserRecord amyDuplicate = InsertUser(connection, "amy-other-private-id", "Amy", isExplicit: true);
            JiraUserRecord zoe = InsertUser(connection, "zoe-private-id", "Zoe", isExplicit: true);
            JiraUserRecord unsafeRequester = InsertUser(
                connection,
                "private-requester@example.com",
                "private-requester@example.com",
                isExplicit: false);

            issue.ReporterUserId = reporter.Id;
            issue.AssigneeUserId = assignee.Id;
            JiraIssueRecord.Insert(connection, issue);
            InsertRequester(connection, "FHIR-400", zoe.Id);
            InsertRequester(connection, "FHIR-400", amy.Id);
            InsertRequester(connection, "FHIR-400", unsafeRequester.Id);
            InsertRequester(connection, "FHIR-400", amyDuplicate.Id);

            JiraSyncStateRecord.Insert(connection, new JiraSyncStateRecord
            {
                Id = JiraSyncStateRecord.GetIndex(),
                SourceName = JiraSource.SourceName,
                SubSource = JiraSyncStateHelper.SyncKey("FHIR", "full"),
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

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetItem("FHIR-400", includeContent: false, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);

        Assert.NotNull(response.People);
        Assert.Equal("Reporter Name", response.People.Reporter);
        Assert.Equal("Assignee Name", response.People.Assignee);
        Assert.Equal(["Amy", "Zoe"], response.People.InPersonRequesters);

        Assert.NotNull(response.Provenance);
        Assert.Equal("jira", response.Provenance.Source);
        Assert.True(response.Provenance.IsStable);
        Assert.Equal(
            refreshedAt,
            response.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);

        string peopleJson = JsonSerializer.Serialize(response.People);
        Assert.DoesNotContain("private-id", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userid", peopleJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetItem_UnsafeLegacyPeopleAreSuppressedFromStructuredResponse()
    {
        InsertIssue(NewIssue("FHIR-500", issue =>
        {
            issue.Reporter = "legacy-reporter";
            issue.Assignee = "legacy-assignee";
        }));

        using (SqliteConnection connection = _db.OpenConnection())
        {
            JiraUserRecord requester = InsertUser(
                connection,
                "legacy-requester",
                "legacy-requester",
                isExplicit: false);
            InsertUser(connection, "other-reporter", "legacy-reporter", isExplicit: true);
            InsertUser(connection, "other-assignee", "legacy-assignee", isExplicit: true);
            InsertRequester(connection, "FHIR-500", requester.Id);
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetItem("FHIR-500", includeContent: false, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);

        Assert.NotNull(response.People);
        Assert.Null(response.People.Reporter);
        Assert.Null(response.People.Assignee);
        Assert.Empty(response.People.InPersonRequesters);

        // The established metadata keys remain available for old clients;
        // structured hydration does not use them.
        Assert.Equal("legacy-reporter", response.Metadata!["reporter"]);
        Assert.Equal("legacy-assignee", response.Metadata["assignee"]);
    }

    [Fact]
    public void GetItem_DisplayNameCollisionCannotOverrideBoundUserIdentity()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            JiraUserRecord boundReporter = InsertUser(
                connection,
                "bound-reporter",
                "Shared Name",
                isExplicit: false);
            InsertUser(
                connection,
                "different-reporter",
                "Shared Name",
                isExplicit: true);
            JiraUserRecord boundAssignee = InsertUser(
                connection,
                "bound-assignee",
                "Current Assignee",
                isExplicit: true);
            InsertUser(
                connection,
                "different-assignee",
                "Legacy Assignee",
                isExplicit: true);

            JiraIssueRecord issue = NewIssue("FHIR-550", record =>
            {
                record.Reporter = "Shared Name";
                record.ReporterUserId = boundReporter.Id;
                record.Assignee = "Legacy Assignee";
                record.AssigneeUserId = boundAssignee.Id;
            });
            JiraIssueRecord.Insert(connection, issue);
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            _controller.GetItem("FHIR-550", includeContent: false, includeComments: false));
        ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);

        Assert.NotNull(response.People);
        Assert.Null(response.People.Reporter);
        Assert.Equal("Current Assignee", response.People.Assignee);
        Assert.Equal("Shared Name", response.Metadata!["reporter"]);
        Assert.Equal("Legacy Assignee", response.Metadata["assignee"]);
    }

    [Fact]
    public async Task CachedReingestion_PersistsReporterAndAssigneeUserIds()
    {
        InsertIssue(NewIssue("FHIR-575", issue =>
        {
            issue.Reporter = "Actual Reporter";
            issue.Assignee = "Actual Assignee";
        }));

        string cachePath = Path.Combine(
            Path.GetTempPath(),
            $"jira_people_cache_{Guid.NewGuid():N}");
        try
        {
            FileSystemResponseCache cache = new(cachePath);
            const string xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss>
                  <channel>
                    <item>
                      <title>[FHIR-575] Bound people</title>
                      <summary>Bound people</summary>
                      <project key="FHIR">FHIR</project>
                      <key id="575">FHIR-575</key>
                      <type>Bug</type>
                      <priority>Major</priority>
                      <status>Open</status>
                      <assignee username="actual-assignee">Actual Assignee</assignee>
                      <reporter username="actual-reporter">Actual Reporter</reporter>
                      <created>Mon, 1 Jul 2024 10:00:00 +0000</created>
                      <updated>Tue, 2 Jul 2024 10:00:00 +0000</updated>
                    </item>
                  </channel>
                </rss>
                """;
            using (MemoryStream stream = new(Encoding.UTF8.GetBytes(xml)))
            {
                await cache.PutAsync(
                    JiraCacheLayout.SourceName,
                    JiraCacheLayout.ProjectXmlKey(
                        "FHIR",
                        "20260909-20260909-000.xml"),
                    stream,
                    CancellationToken.None);
            }

            JiraSource source = new(
                Options.Create(new JiraServiceOptions()),
                httpClientFactory: null!,
                _db,
                cache,
                new JiraUserMapper(),
                NullLogger<JiraSource>.Instance);

            IngestionResult result = await source.LoadFromCacheAsync("FHIR");

            Assert.Equal(1, result.ItemsProcessed);
            Assert.Equal(1, result.ItemsUpdated);
            using SqliteConnection connection = _db.OpenConnection();
            JiraIssueRecord issue = Assert.Single(
                JiraIssueRecord.SelectList(connection, Key: "FHIR-575"));
            JiraUserRecord reporter = Assert.Single(
                JiraUserRecord.SelectList(connection, Username: "actual-reporter"));
            JiraUserRecord assignee = Assert.Single(
                JiraUserRecord.SelectList(connection, Username: "actual-assignee"));
            Assert.Equal(reporter.Id, issue.ReporterUserId);
            Assert.Equal(assignee.Id, issue.AssigneeUserId);

            OkObjectResult ok = Assert.IsType<OkObjectResult>(
                _controller.GetItem("FHIR-575", includeContent: false, includeComments: false));
            ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);
            Assert.NotNull(response.People);
            Assert.Equal("Actual Reporter", response.People.Reporter);
            Assert.Equal("Actual Assignee", response.People.Assignee);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(cachePath);
        }
    }

    [Fact]
    public void GetItem_DuringMutationMarksCapturedProvenanceUnstable()
    {
        InsertIssue(NewIssue("FHIR-600"));
        JiraSourceStateRecord started = _db.BeginContentMutation();
        try
        {
            OkObjectResult ok = Assert.IsType<OkObjectResult>(
                _controller.GetItem("FHIR-600", includeContent: false, includeComments: false));
            ItemResponse response = Assert.IsType<ItemResponse>(ok.Value);

            Assert.NotNull(response.Provenance);
            Assert.False(response.Provenance.IsStable);
            Assert.Equal(started.ContentRevision, response.Provenance.ContentRevision);
            Assert.True(response.Provenance.ProjectLastSuccessfulRefreshAt.ContainsKey("FHIR"));
            Assert.Null(response.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        }
        finally
        {
            _db.CompleteContentMutation();
        }
    }

    private void InsertIssue(JiraIssueRecord issue)
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, issue);
    }

    private static JiraUserRecord InsertUser(
        SqliteConnection connection,
        string username,
        string displayName,
        bool isExplicit)
    {
        JiraUserRecord user = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = username,
            DisplayName = displayName,
            HasExplicitDisplayName = isExplicit,
        };
        JiraUserRecord.Insert(connection, user);
        return user;
    }

    private static void InsertRequester(
        SqliteConnection connection,
        string issueKey,
        int userId)
    {
        JiraIssueInPersonRecord.Insert(connection, new JiraIssueInPersonRecord
        {
            Id = JiraIssueInPersonRecord.GetIndex(),
            IssueKey = issueKey,
            UserId = userId,
        });
    }

    private static JiraIssueRecord NewIssue(string key, Action<JiraIssueRecord>? configure = null)
    {
        JiraIssueRecord issue = new JiraIssueRecord
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = "FHIR",
            Title = $"Issue {key}",
            Description = null,
            DescriptionPlain = null,
            Summary = null,
            Type = "Bug",
            Priority = "Major",
            Status = "Triaged",
            Resolution = null,
            ResolutionDescription = null,
            ResolutionDescriptionPlain = null,
            Assignee = null,
            Reporter = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            WorkGroup = null,
            Specification = null,
            RaisedInVersion = null,
            SelectedBallot = null,
            RelatedArtifacts = null,
            RelatedIssues = null,
            DuplicateOf = null,
            AppliedVersions = null,
            ChangeType = null,
            Impact = null,
            Vote = null,
            Labels = null,
            CommentCount = 0,
            ChangeCategory = null,
            ChangeImpact = null,
        };
        configure?.Invoke(issue);
        return issue;
    }
}
