using System.Text.Json;
using FhirAugury.Common.Api;

namespace FhirAugury.Common.Tests.Api;

public class ContentQueryContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── CrossReferenceQuery ──────────────────────────────────────────────

    [Fact]
    public void CrossReferenceQuery_RoundTrips()
    {
        CrossReferenceQuery query = new() { Value = "FHIR-123", SourceType = "jira" };

        string json = JsonSerializer.Serialize(query, JsonOptions);
        CrossReferenceQuery? deserialized = JsonSerializer.Deserialize<CrossReferenceQuery>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("FHIR-123", deserialized.Value);
        Assert.Equal("jira", deserialized.SourceType);
    }

    [Fact]
    public void CrossReferenceQuery_OptionalSourceType_IsNull()
    {
        CrossReferenceQuery query = new() { Value = "Patient.birthDate" };

        string json = JsonSerializer.Serialize(query, JsonOptions);
        CrossReferenceQuery? deserialized = JsonSerializer.Deserialize<CrossReferenceQuery>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("Patient.birthDate", deserialized.Value);
        Assert.Null(deserialized.SourceType);
    }

    // ── CrossReferenceHit ────────────────────────────────────────────────

    [Fact]
    public void CrossReferenceHit_RoundTrips()
    {
        CrossReferenceHit hit = new()
        {
            SourceType = "jira",
            ContentType = "issue",
            SourceId = "FHIR-100",
            SourceTitle = "Test Issue",
            SourceUrl = "https://jira.example.com/FHIR-100",
            TargetType = "zulip",
            TargetId = "179:topic",
            TargetTitle = "Topic Title",
            TargetUrl = "https://zulip.example.com/179",
            LinkType = "mentions",
            Context = "See FHIR-100 for details",
        };

        string json = JsonSerializer.Serialize(hit, JsonOptions);
        CrossReferenceHit? deserialized = JsonSerializer.Deserialize<CrossReferenceHit>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("jira", deserialized.SourceType);
        Assert.Equal("issue", deserialized.ContentType);
        Assert.Equal("FHIR-100", deserialized.SourceId);
        Assert.Equal("Test Issue", deserialized.SourceTitle);
        Assert.Equal("zulip", deserialized.TargetType);
        Assert.Equal("179:topic", deserialized.TargetId);
        Assert.Equal("mentions", deserialized.LinkType);
        Assert.Equal("See FHIR-100 for details", deserialized.Context);
    }

    [Fact]
    public void CrossReferenceHit_DefaultLinkType_IsMentions()
    {
        CrossReferenceHit hit = new()
        {
            SourceType = "github",
            SourceId = "owner/repo#1",
            TargetType = "jira",
            TargetId = "FHIR-1",
        };

        Assert.Equal("mentions", hit.LinkType);
    }

    // ── CrossReferenceQueryResponse ──────────────────────────────────────

    [Fact]
    public void CrossReferenceQueryResponse_RoundTrips()
    {
        CrossReferenceQueryResponse response = new()
        {
            Value = "FHIR-123",
            SourceType = "jira",
            Direction = "refers-to",
            Total = 1,
            Hits =
            [
                new CrossReferenceHit
                {
                    SourceType = "jira",
                    SourceId = "FHIR-123",
                    TargetType = "zulip",
                    TargetId = "179:topic",
                },
            ],
            Warnings = ["partial timeout"],
        };

        string json = JsonSerializer.Serialize(response, JsonOptions);
        CrossReferenceQueryResponse? deserialized = JsonSerializer.Deserialize<CrossReferenceQueryResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("FHIR-123", deserialized.Value);
        Assert.Equal("jira", deserialized.SourceType);
        Assert.Equal("refers-to", deserialized.Direction);
        Assert.Equal(1, deserialized.Total);
        Assert.Single(deserialized.Hits);
        Assert.NotNull(deserialized.Warnings);
        Assert.Single(deserialized.Warnings);
    }

    [Fact]
    public void CrossReferenceQueryResponse_WithEmptyHits_RoundTrips()
    {
        CrossReferenceQueryResponse response = new()
        {
            Value = "unknown",
            Direction = "referred-by",
            Total = 0,
            Hits = [],
        };

        string json = JsonSerializer.Serialize(response, JsonOptions);
        CrossReferenceQueryResponse? deserialized = JsonSerializer.Deserialize<CrossReferenceQueryResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Hits);
        Assert.Null(deserialized.Warnings);
    }

    // ── ContentSearchHit ─────────────────────────────────────────────────

    [Fact]
    public void ContentSearchHit_RoundTrips()
    {
        ContentSearchHit hit = new()
        {
            Source = "jira",
            ContentType = "issue",
            Id = "FHIR-456",
            Title = "Test Search Hit",
            Snippet = "...matched text...",
            Score = 0.95,
            Url = "https://jira.example.com/FHIR-456",
            UpdatedAt = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            Metadata = new Dictionary<string, string> { ["status"] = "Open" },
            MatchedValue = "test",
        };

        string json = JsonSerializer.Serialize(hit, JsonOptions);
        ContentSearchHit? deserialized = JsonSerializer.Deserialize<ContentSearchHit>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("jira", deserialized.Source);
        Assert.Equal("issue", deserialized.ContentType);
        Assert.Equal("FHIR-456", deserialized.Id);
        Assert.Equal("Test Search Hit", deserialized.Title);
        Assert.Equal(0.95, deserialized.Score);
        Assert.Equal("test", deserialized.MatchedValue);
        Assert.NotNull(deserialized.Metadata);
        Assert.Equal("Open", deserialized.Metadata["status"]);
    }

    // ── ContentSearchResponse ────────────────────────────────────────────

    [Fact]
    public void ContentSearchResponse_RoundTrips()
    {
        ContentSearchResponse response = new()
        {
            Values = ["test", "Patient"],
            Total = 2,
            Hits =
            [
                new ContentSearchHit { Source = "jira", Id = "FHIR-1", Title = "Hit 1", Score = 0.9 },
                new ContentSearchHit { Source = "zulip", Id = "179:topic", Title = "Hit 2", Score = 0.8, MatchedValue = "Patient" },
            ],
        };

        string json = JsonSerializer.Serialize(response, JsonOptions);
        ContentSearchResponse? deserialized = JsonSerializer.Deserialize<ContentSearchResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Values.Count);
        Assert.Equal(2, deserialized.Total);
        Assert.Equal(2, deserialized.Hits.Count);
        Assert.Null(deserialized.Warnings);
    }

    // ── ContentItemResponse ──────────────────────────────────────────────

    [Fact]
    public void ContentItemResponse_RoundTrips()
    {
        ContentItemResponse item = new()
        {
            Source = "jira",
            ContentType = "issue",
            Id = "FHIR-789",
            Title = "Full Item",
            Content = "Detailed description here.",
            Url = "https://jira.example.com/FHIR-789",
            CreatedAt = new DateTimeOffset(2025, 1, 10, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            Metadata = new Dictionary<string, string> { ["priority"] = "High" },
            Comments =
            [
                new CommentInfo("c1", "alice", "Looks good", new DateTimeOffset(2025, 1, 11, 9, 0, 0, TimeSpan.Zero), "https://jira.example.com/c1"),
            ],
            Snapshot = "# FHIR-789\n\nFull markdown snapshot.",
        };

        string json = JsonSerializer.Serialize(item, JsonOptions);
        ContentItemResponse? deserialized = JsonSerializer.Deserialize<ContentItemResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("FHIR-789", deserialized.Id);
        Assert.Equal("Full Item", deserialized.Title);
        Assert.Equal("Detailed description here.", deserialized.Content);
        Assert.NotNull(deserialized.Metadata);
        Assert.Equal("High", deserialized.Metadata["priority"]);
        Assert.NotNull(deserialized.Comments);
        Assert.Single(deserialized.Comments);
        Assert.Equal("alice", deserialized.Comments[0].Author);
        Assert.Equal("# FHIR-789\n\nFull markdown snapshot.", deserialized.Snapshot);
    }

    [Fact]
    public void ContentItemResponse_OptionalFields_AreNull()
    {
        ContentItemResponse item = new()
        {
            Source = "zulip",
            Id = "12345",
            Title = "Minimal Item",
        };

        string json = JsonSerializer.Serialize(item, JsonOptions);
        ContentItemResponse? deserialized = JsonSerializer.Deserialize<ContentItemResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("zulip", deserialized.Source);
        Assert.Null(deserialized.Content);
        Assert.Null(deserialized.Snapshot);
        Assert.Null(deserialized.Comments);
        Assert.Null(deserialized.Metadata);
    }

    // ── ContentSearchRequest ─────────────────────────────────────────────

    [Fact]
    public void ContentSearchRequest_RoundTrips()
    {
        ContentSearchRequest request = new()
        {
            Values = ["FHIR-123", "Patient.birthDate"],
            Sources = ["jira", "zulip"],
            Limit = 50,
        };

        string json = JsonSerializer.Serialize(request, JsonOptions);
        ContentSearchRequest? deserialized = JsonSerializer.Deserialize<ContentSearchRequest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Values.Count);
        Assert.NotNull(deserialized.Sources);
        Assert.Equal(2, deserialized.Sources.Count);
        Assert.Equal(50, deserialized.Limit);
    }
}

public class ValueFormatDetectorTests
{
    // ── DetectSourceType ─────────────────────────────────────────────────

    [Theory]
    [InlineData("FHIR-50783", "jira")]
    [InlineData("GF-1234", "jira")]
    [InlineData("ABC-1", "jira")]
    public void DetectSourceType_JiraKeys(string value, string expected)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("owner/repo#42", "github")]
    [InlineData("HL7/fhir-ig#100", "github")]
    public void DetectSourceType_GitHubIssues(string value, string expected)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("owner/repo:path/to/file", "github")]
    [InlineData("HL7/fhir:src/main.cs", "github")]
    public void DetectSourceType_GitHubFiles(string value, string expected)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Patient.birthDate", "fhir")]
    [InlineData("Observation.value.Quantity", "fhir")]
    [InlineData("Bundle.entry.resource", "fhir")]
    public void DetectSourceType_FhirElements(string value, string expected)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("179:topic name", "zulip")]
    [InlineData("1:test", "zulip")]
    public void DetectSourceType_ZulipStreamTopics(string value, string expected)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("msg:12345", "zulip")]
    [InlineData("MSG:99999", "zulip")]
    public void DetectSourceType_ZulipMessages(string value, string expected)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("page:12345", "confluence")]
    [InlineData("PAGE:67890", "confluence")]
    public void DetectSourceType_ConfluencePages(string value, string expected)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("hello")]
    [InlineData("some random text")]
    public void DetectSourceType_Ambiguous_ReturnsNull(string value)
    {
        string? result = ValueFormatDetector.DetectSourceType(value);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectSourceType_EmptyOrNull_ReturnsNull(string? value)
    {
        string? result = ValueFormatDetector.DetectSourceType(value!);
        Assert.Null(result);
    }

    // ── IsJiraKey ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FHIR-50783", true)]
    [InlineData("GF-1234", true)]
    [InlineData("A2-1", true)]
    [InlineData("fhir-123", false)]
    [InlineData("123", false)]
    [InlineData("FHIR", false)]
    public void IsJiraKey_ReturnsExpected(string value, bool expected)
    {
        bool result = ValueFormatDetector.IsJiraKey(value);
        Assert.Equal(expected, result);
    }

    // ── IsGitHubIssue ────────────────────────────────────────────────────

    [Theory]
    [InlineData("owner/repo#42", true)]
    [InlineData("HL7/fhir-ig#100", true)]
    [InlineData("owner/repo", false)]
    [InlineData("owner/repo#", false)]
    [InlineData("#42", false)]
    public void IsGitHubIssue_ReturnsExpected(string value, bool expected)
    {
        bool result = ValueFormatDetector.IsGitHubIssue(value);
        Assert.Equal(expected, result);
    }

    // ── IsFhirElement ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Patient.birthDate", true)]
    [InlineData("Observation.value.Quantity", true)]
    [InlineData("Patient", false)]
    [InlineData("patient.birthDate", false)]
    [InlineData("A.b", false)]
    [InlineData("Ab.cd", true)]
    public void IsFhirElement_ReturnsExpected(string value, bool expected)
    {
        bool result = ValueFormatDetector.IsFhirElement(value);
        Assert.Equal(expected, result);
    }

    // ── TryParseGitHubIssue ──────────────────────────────────────────────

    [Fact]
    public void TryParseGitHubIssue_ValidRef_Parses()
    {
        bool success = ValueFormatDetector.TryParseGitHubIssue("owner/repo#42", out string repoFullName, out int issueNumber);

        Assert.True(success);
        Assert.Equal("owner/repo", repoFullName);
        Assert.Equal(42, issueNumber);
    }

    [Fact]
    public void TryParseGitHubIssue_InvalidRef_ReturnsFalse()
    {
        bool success = ValueFormatDetector.TryParseGitHubIssue("not-a-ref", out string repoFullName, out int issueNumber);

        Assert.False(success);
        Assert.Equal("", repoFullName);
        Assert.Equal(0, issueNumber);
    }

    [Fact]
    public void TryParseGitHubIssue_ComplexRepoName_Parses()
    {
        bool success = ValueFormatDetector.TryParseGitHubIssue("HL7/fhir-ig.v2#999", out string repoFullName, out int issueNumber);

        Assert.True(success);
        Assert.Equal("HL7/fhir-ig.v2", repoFullName);
        Assert.Equal(999, issueNumber);
    }
}
