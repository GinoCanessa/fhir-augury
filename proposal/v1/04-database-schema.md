# FHIR Augury — Database Schema

## Schema Design Principles

1. **Source-generated** — all table definitions are `partial record class` types
   decorated with `cslightdbgen.sqlitegen` attributes. CRUD code is generated
   at compile time.
2. **Single database file** — all sources share `fhir-augury.db`. Cross-source
   queries use standard JOINs.
3. **FTS5 for full-text search** — each source has a companion FTS5 virtual
   table for fast text search.
4. **BM25 keyword scoring** — pre-computed keyword/IDF/BM25 scores enable
   relevance-ranked search without runtime computation.
5. **Cross-reference tables** — explicit linking between items from different
   sources.

---

## Core Tables

### Ingestion Metadata

```csharp
/// Tracks sync state and schedule per source (or sub-source like a stream/repo).
/// The scheduler reads this to decide when to trigger incremental syncs.
/// On-demand API calls also read LastSyncAt to know what "since" value to use.
[LdgSQLiteTable("sync_state")]
public partial record class SyncStateRecord
{
    [LdgSQLiteKey]
    public required string SourceName { get; set; }     // "zulip", "jira", etc.

    public required string? SubSource { get; set; }     // stream ID, repo name, space key, etc.
    public required DateTimeOffset LastSyncAt { get; set; }
    public required string? LastCursor { get; set; }    // pagination cursor / last ID
    public required int ItemsIngested { get; set; }
    public required string? SyncSchedule { get; set; }  // TimeSpan string, e.g. "01:00:00"
    public required DateTimeOffset? NextScheduledAt { get; set; }
    public required string? Status { get; set; }        // "completed", "in_progress", "failed"
    public required string? LastError { get; set; }
}

/// Log of every ingestion run.
[LdgSQLiteTable("ingestion_log")]
[LdgSQLiteIndex(nameof(SourceName), nameof(StartedAt))]
public partial record class IngestionLogRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceName { get; set; }
    public required string RunType { get; set; }        // "full", "incremental", "on_demand"
    public required DateTimeOffset StartedAt { get; set; }
    public required DateTimeOffset? CompletedAt { get; set; }
    public required int ItemsProcessed { get; set; }
    public required int ItemsNew { get; set; }
    public required int ItemsUpdated { get; set; }
    public required string? ErrorMessage { get; set; }
}
```

---

## Zulip Tables

```csharp
[LdgSQLiteTable("zulip_streams")]
public partial record class ZulipStreamRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Name { get; set; }
    public required string Description { get; set; }
    public required bool IsWebPublic { get; set; }
    public required int MessageCount { get; set; }
    public required DateTimeOffset? LastFetchedAt { get; set; }
}

[LdgSQLiteTable("zulip_messages")]
[LdgSQLiteIndex(nameof(StreamId))]
[LdgSQLiteIndex(nameof(StreamId), nameof(Topic))]
[LdgSQLiteIndex(nameof(SenderId))]
[LdgSQLiteIndex(nameof(Timestamp))]
public partial record class ZulipMessageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(ZulipStreamRecord.Id))]
    public required int StreamId { get; set; }

    public required string StreamName { get; set; }
    public required string Topic { get; set; }
    public required int SenderId { get; set; }
    public required string SenderName { get; set; }
    public required string SenderEmail { get; set; }
    public required string ContentHtml { get; set; }
    public required string ContentPlain { get; set; }
    public required long Timestamp { get; set; }
    public required string CreatedAt { get; set; }
    public required string? Reactions { get; set; }     // JSON array
}
```

### Zulip FTS5

Indexed fields: `StreamName`, `Topic`, `SenderName`, `ContentPlain`

---

## Jira Tables

```csharp
[LdgSQLiteTable("jira_issues")]
[LdgSQLiteIndex(nameof(Key))]
[LdgSQLiteIndex(nameof(ProjectKey), nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(WorkGroup))]
[LdgSQLiteIndex(nameof(Specification))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
public partial record class JiraIssueRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Key { get; set; }            // "FHIR-12345"

    public required string ProjectKey { get; set; }     // "FHIR"
    public required string Title { get; set; }
    public required string? Description { get; set; }
    public required string? Summary { get; set; }
    public required string Type { get; set; }           // Bug, Enhancement, etc.
    public required string Priority { get; set; }
    public required string Status { get; set; }
    public required string? Resolution { get; set; }
    public required string? ResolutionDescription { get; set; }
    public required string? Assignee { get; set; }
    public required string? Reporter { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required DateTimeOffset? ResolvedAt { get; set; }

    // Custom fields (mapped from Jira custom field IDs)
    public required string? WorkGroup { get; set; }
    public required string? Specification { get; set; }
    public required string? RaisedInVersion { get; set; }
    public required string? SelectedBallot { get; set; }
    public required string? RelatedArtifacts { get; set; }
    public required string? RelatedIssues { get; set; }
    public required string? DuplicateOf { get; set; }
    public required string? AppliedVersions { get; set; }
    public required string? ChangeType { get; set; }
    public required string? Impact { get; set; }
    public required string? Vote { get; set; }
    public required string? Labels { get; set; }        // Comma-separated
    public required int CommentCount { get; set; }
}

[LdgSQLiteTable("jira_comments")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(CreatedAt))]
public partial record class JiraCommentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(JiraIssueRecord.Id))]
    public required int IssueId { get; set; }

    public required string IssueKey { get; set; }
    public required string Author { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string Body { get; set; }
}
```

### Jira FTS5

Indexed fields: `Key`, `Title`, `Description`, `Summary`, `ResolutionDescription`,
`Labels`, `Specification`, `WorkGroup`, `RelatedArtifacts`

---

## Confluence Tables

```csharp
[LdgSQLiteTable("confluence_spaces")]
public partial record class ConfluenceSpaceRecord
{
    [LdgSQLiteKey]
    public required string Key { get; set; }

    public required string Name { get; set; }
    public required string? Description { get; set; }
    public required string Url { get; set; }
    public required DateTimeOffset? LastFetchedAt { get; set; }
}

[LdgSQLiteTable("confluence_pages")]
[LdgSQLiteIndex(nameof(SpaceKey))]
[LdgSQLiteIndex(nameof(ParentId))]
[LdgSQLiteIndex(nameof(LastModifiedAt))]
public partial record class ConfluencePageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SpaceKey { get; set; }
    public required string Title { get; set; }
    public required int? ParentId { get; set; }
    public required string? BodyStorage { get; set; }   // Confluence storage format
    public required string? BodyPlain { get; set; }     // Stripped for indexing
    public required string? Labels { get; set; }        // Comma-separated
    public required int VersionNumber { get; set; }
    public required string? LastModifiedBy { get; set; }
    public required DateTimeOffset LastModifiedAt { get; set; }
    public required string Url { get; set; }
}

[LdgSQLiteTable("confluence_comments")]
[LdgSQLiteIndex(nameof(PageId))]
public partial record class ConfluenceCommentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(ConfluencePageRecord.Id))]
    public required int PageId { get; set; }

    public required string Author { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string Body { get; set; }
}
```

### Confluence FTS5

Indexed fields: `Title`, `BodyPlain`, `Labels`

---

## GitHub Tables

```csharp
[LdgSQLiteTable("github_repos")]
public partial record class GitHubRepoRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Owner { get; set; }
    public required string Name { get; set; }
    public required string FullName { get; set; }       // "HL7/fhir"
    public required string? Description { get; set; }
    public required DateTimeOffset? LastFetchedAt { get; set; }
}

[LdgSQLiteTable("github_issues")]
[LdgSQLiteIndex(nameof(RepoFullName))]
[LdgSQLiteIndex(nameof(State))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
public partial record class GitHubIssueRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }   // "HL7/fhir"
    public required int Number { get; set; }
    public required bool IsPullRequest { get; set; }
    public required string Title { get; set; }
    public required string? Body { get; set; }
    public required string State { get; set; }          // "open", "closed"
    public required string Author { get; set; }
    public required string? Labels { get; set; }        // Comma-separated
    public required string? Assignees { get; set; }     // Comma-separated
    public required string? Milestone { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required DateTimeOffset? ClosedAt { get; set; }

    // PR-specific fields (null for issues)
    public required string? MergeState { get; set; }
    public required string? HeadBranch { get; set; }
    public required string? BaseBranch { get; set; }
}

[LdgSQLiteTable("github_comments")]
[LdgSQLiteIndex(nameof(IssueId))]
[LdgSQLiteIndex(nameof(RepoFullName))]
public partial record class GitHubCommentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(GitHubIssueRecord.Id))]
    public required int IssueId { get; set; }

    public required string RepoFullName { get; set; }
    public required int IssueNumber { get; set; }
    public required string Author { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string Body { get; set; }
    public required bool IsReviewComment { get; set; }
}
```

### GitHub FTS5

Indexed fields: `Title`, `Body`, `Labels`

---

## Cross-Reference Tables

```csharp
/// Links items across sources. Populated by the cross-reference linker.
[LdgSQLiteTable("xref_links")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(TargetType), nameof(TargetId))]
public partial record class CrossRefLinkRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceType { get; set; }     // "zulip", "jira", "confluence", "github"
    public required string SourceId { get; set; }       // message ID, issue key, page ID, etc.
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string LinkType { get; set; }       // "mentions", "references", "duplicate_of"
    public required string? Context { get; set; }       // surrounding text snippet
}
```

---

## Indexing Tables

```csharp
/// Per-item keyword frequencies for BM25 scoring.
[LdgSQLiteTable("index_keywords")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(Keyword))]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class KeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string Keyword { get; set; }
    public required int Count { get; set; }
    public required string KeywordType { get; set; }    // "word", "stop_word", "fhir_path", "fhir_operation"
    public required double? Bm25Score { get; set; }
}

/// Corpus-level keyword statistics for IDF calculation.
[LdgSQLiteTable("index_corpus")]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class CorpusKeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Keyword { get; set; }
    public required string KeywordType { get; set; }
    public required int DocumentFrequency { get; set; }
    public required double? Idf { get; set; }
}

/// Document-level statistics for BM25 normalization.
[LdgSQLiteTable("index_doc_stats")]
public partial record class DocStatsRecord
{
    [LdgSQLiteKey]
    public required string SourceType { get; set; }

    public required int TotalDocuments { get; set; }
    public required double AverageDocLength { get; set; }
}
```
