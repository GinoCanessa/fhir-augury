using FhirAugury.Database;
using FhirAugury.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Indexing.Bm25;

/// <summary>
/// BM25 keyword scoring pipeline for document similarity.
/// </summary>
public static class Bm25Calculator
{
    /// <summary>BM25 term frequency saturation parameter.</summary>
    public const double DefaultK1 = 1.2;

    /// <summary>BM25 document length normalization parameter.</summary>
    public const double DefaultB = 0.75;

    /// <summary>
    /// Builds the full BM25 keyword index from all source tables.
    /// </summary>
    public static void BuildFullIndex(
        SqliteConnection connection,
        double k1 = DefaultK1,
        double b = DefaultB,
        CancellationToken ct = default)
    {
        // Clear existing index data
        ClearIndex(connection);

        // Collect all documents
        var documents = CollectAllDocuments(connection, ct);

        // Build the index from collected documents
        BuildIndex(connection, documents, k1, b, ct);
    }

    /// <summary>
    /// Incrementally updates the index for newly ingested items.
    /// </summary>
    public static void UpdateIndex(
        SqliteConnection connection,
        IReadOnlyList<(string SourceType, string SourceId, string Text)> items,
        double k1 = DefaultK1,
        double b = DefaultB,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        // Remove existing keyword records for these items
        foreach (var (sourceType, sourceId, _) in items)
        {
            ct.ThrowIfCancellationRequested();
            DeleteKeywordsForItem(connection, sourceType, sourceId);
        }

        // Tokenize and prepare all keyword records
        var allRecords = new List<KeywordRecord>();

        foreach (var (sourceType, sourceId, text) in items)
        {
            ct.ThrowIfCancellationRequested();
            var tokens = Tokenizer.Tokenize(text);
            var keywords = CountAndClassifyTokens(tokens);

            foreach (var (keyword, (count, keywordType)) in keywords)
            {
                allRecords.Add(new KeywordRecord
                {
                    Id = KeywordRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                });
            }
        }

        // Insert all keyword records
        foreach (var record in allRecords)
        {
            KeywordRecord.Insert(connection, record);
        }

        // Recompute corpus-level stats and BM25 scores
        RecomputeCorpusStats(connection, k1, b);
    }

    /// <summary>
    /// Builds index from a list of (SourceType, SourceId, Text) documents.
    /// </summary>
    private static void BuildIndex(
        SqliteConnection connection,
        List<(string SourceType, string SourceId, string Text)> documents,
        double k1, double b,
        CancellationToken ct)
    {
        if (documents.Count == 0) return;

        // Tokenize, classify, count per-document term frequency
        var allRecords = new List<KeywordRecord>();

        foreach (var (sourceType, sourceId, text) in documents)
        {
            ct.ThrowIfCancellationRequested();

            var tokens = Tokenizer.Tokenize(text);
            var keywords = CountAndClassifyTokens(tokens);

            foreach (var (keyword, (count, keywordType)) in keywords)
            {
                allRecords.Add(new KeywordRecord
                {
                    Id = KeywordRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                });
            }
        }

        // Batch insert all keyword records
        foreach (var record in allRecords)
        {
            KeywordRecord.Insert(connection, record);
        }

        // Compute corpus stats and BM25 scores
        RecomputeCorpusStats(connection, k1, b);
    }

    /// <summary>
    /// Recomputes all corpus-level statistics and BM25 scores from keyword records.
    /// Uses SQL-based bulk UPDATE for performance instead of loading all records into memory.
    /// </summary>
    private static void RecomputeCorpusStats(SqliteConnection connection, double k1, double b)
    {
        // Clear existing corpus stats
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM index_corpus; DELETE FROM index_doc_stats;";
            cmd.ExecuteNonQuery();
        }

        // Compute total documents and average doc length per source type
        var docStats = new Dictionary<string, (int TotalDocs, double AvgDocLen)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT SourceType, COUNT(DISTINCT SourceId) as DocCount,
                       CAST(SUM(Count) AS REAL) / COUNT(DISTINCT SourceId) as AvgLen
                FROM index_keywords
                GROUP BY SourceType
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                docStats[reader.GetString(0)] = (reader.GetInt32(1), reader.GetDouble(2));
            }
        }

        // Store doc stats
        foreach (var (sourceType, (totalDocs, avgDocLen)) in docStats)
        {
            DocStatsRecord.Insert(connection, new DocStatsRecord
            {
                Id = DocStatsRecord.GetIndex(),
                SourceType = sourceType,
                TotalDocuments = totalDocs,
                AverageDocLength = avgDocLen,
            });
        }

        var totalDocCount = docStats.Values.Sum(ds => ds.TotalDocs);
        if (totalDocCount == 0) return;

        // Compute and store corpus keyword records (IDF)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Keyword, KeywordType, COUNT(DISTINCT SourceType || ':' || SourceId) as DocFreq
                FROM index_keywords
                GROUP BY Keyword, KeywordType
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var keyword = reader.GetString(0);
                var keywordType = reader.GetString(1);
                var df = reader.GetInt32(2);
                var idf = Math.Log(1.0 + (totalDocCount - df + 0.5) / (df + 0.5));

                CorpusKeywordRecord.Insert(connection, new CorpusKeywordRecord
                {
                    Id = CorpusKeywordRecord.GetIndex(),
                    Keyword = keyword,
                    KeywordType = keywordType,
                    DocumentFrequency = df,
                    Idf = idf,
                });
            }
        }

        // Compute BM25 scores in bulk using SQL UPDATE with subqueries
        using (var cmd = connection.CreateCommand())
        {
            // Compute overall average doc length
            double overallAvgDocLen;
            using (var avgCmd = connection.CreateCommand())
            {
                avgCmd.CommandText = """
                    SELECT CAST(SUM(DocLen) AS REAL) / COUNT(*) FROM (
                        SELECT SUM(Count) as DocLen FROM index_keywords GROUP BY SourceType, SourceId
                    )
                    """;
                var scalar = avgCmd.ExecuteScalar();
                overallAvgDocLen = scalar is null or DBNull ? 1.0 : Convert.ToDouble(scalar);
            }

            cmd.CommandText = """
                UPDATE index_keywords
                SET Bm25Score = (
                    SELECT
                        c.Idf * (CAST(index_keywords.Count AS REAL) * (@k1 + 1.0))
                        / (CAST(index_keywords.Count AS REAL) + @k1 * (1.0 - @b + @b * dl.DocLen / @avgDocLen))
                    FROM index_corpus c
                    JOIN (
                        SELECT SourceType, SourceId, SUM(Count) as DocLen
                        FROM index_keywords
                        GROUP BY SourceType, SourceId
                    ) dl ON dl.SourceType = index_keywords.SourceType AND dl.SourceId = index_keywords.SourceId
                    WHERE c.Keyword = index_keywords.Keyword
                    LIMIT 1
                )
                """;
            cmd.Parameters.AddWithValue("@k1", k1);
            cmd.Parameters.AddWithValue("@b", b);
            cmd.Parameters.AddWithValue("@avgDocLen", overallAvgDocLen > 0 ? overallAvgDocLen : 1.0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Counts token frequencies and classifies each token, filtering stop words.
    /// </summary>
    public static Dictionary<string, (int Count, string KeywordType)> CountAndClassifyTokens(List<string> tokens)
    {
        var result = new Dictionary<string, (int Count, string KeywordType)>();

        foreach (var token in tokens)
        {
            var classification = KeywordClassifier.Classify(token);
            if (classification == KeywordClassifier.TypeStopWord)
            {
                continue;
            }

            if (result.TryGetValue(token, out var existing))
            {
                result[token] = (existing.Count + 1, existing.KeywordType);
            }
            else
            {
                result[token] = (1, classification);
            }
        }

        return result;
    }

    /// <summary>
    /// Collects all documents from Jira issues, comments, and Zulip messages.
    /// </summary>
    private static List<(string SourceType, string SourceId, string Text)> CollectAllDocuments(
        SqliteConnection connection, CancellationToken ct)
    {
        var documents = new List<(string, string, string)>();

        // Jira issues
        var issues = JiraIssueRecord.SelectList(connection);
        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Join(" ",
                new[] { issue.Title, issue.Description, issue.Summary, issue.ResolutionDescription, issue.Labels, issue.RelatedArtifacts }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                documents.Add(("jira", issue.Key, text));
            }
        }

        // Jira comments
        var comments = JiraCommentRecord.SelectList(connection);
        foreach (var comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(comment.Body))
            {
                documents.Add(("jira-comment", $"{comment.IssueKey}:{comment.Id}", comment.Body));
            }
        }

        // Zulip messages
        var messages = ZulipMessageRecord.SelectList(connection);
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(msg.ContentPlain))
            {
                documents.Add(("zulip", $"{msg.StreamName}:{msg.Topic}", msg.ContentPlain));
            }
        }

        // Confluence pages
        var pages = ConfluencePageRecord.SelectList(connection);
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Join(" ",
                new[] { page.Title, page.BodyPlain, page.Labels }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                documents.Add(("confluence", page.ConfluenceId, text));
            }
        }

        // GitHub issues
        var ghIssues = GitHubIssueRecord.SelectList(connection);
        foreach (var ghIssue in ghIssues)
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Join(" ",
                new[] { ghIssue.Title, ghIssue.Body, ghIssue.Labels }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                documents.Add(("github", ghIssue.UniqueKey, text));
            }
        }

        // GitHub comments
        var ghComments = GitHubCommentRecord.SelectList(connection);
        foreach (var ghComment in ghComments)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(ghComment.Body))
            {
                documents.Add(("github-comment", $"{ghComment.RepoFullName}#{ghComment.IssueNumber}:{ghComment.Id}", ghComment.Body));
            }
        }

        return documents;
    }

    private static void ClearIndex(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM index_keywords; DELETE FROM index_corpus; DELETE FROM index_doc_stats;";
        cmd.ExecuteNonQuery();
    }

    private static void DeleteKeywordsForItem(SqliteConnection connection, string sourceType, string sourceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM index_keywords WHERE SourceType = @type AND SourceId = @id;";
        cmd.Parameters.AddWithValue("@type", sourceType);
        cmd.Parameters.AddWithValue("@id", sourceId);
        cmd.ExecuteNonQuery();
    }
}
