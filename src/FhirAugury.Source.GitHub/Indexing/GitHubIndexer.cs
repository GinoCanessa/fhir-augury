using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Indexing;

/// <summary>BM25 keyword scoring pipeline scoped to the GitHub corpus.</summary>
public class GitHubIndexer(GitHubDatabase database, ILogger<GitHubIndexer> logger)
{
    public const double DefaultK1 = 1.2;
    public const double DefaultB = 0.75;

    /// <summary>Rebuilds the entire BM25 index from all GitHub issues, comments, and commits.</summary>
    public void RebuildFullIndex(CancellationToken ct = default)
    {
        using var connection = database.OpenConnection();

        ClearIndex(connection);
        var documents = CollectDocuments(connection, ct);

        if (documents.Count == 0)
        {
            logger.LogInformation("No documents to index");
            return;
        }

        BuildIndex(connection, documents, ct);
        RecomputeCorpusStats(connection);

        logger.LogInformation("BM25 index rebuilt: {Count} documents", documents.Count);
    }

    /// <summary>Incrementally updates BM25 scores for specific items.</summary>
    public void UpdateIndex(IReadOnlyList<(string SourceType, string SourceId, string Text)> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        using var connection = database.OpenConnection();

        foreach (var (sourceType, sourceId, _) in items)
        {
            ct.ThrowIfCancellationRequested();
            DeleteKeywordsForItem(connection, sourceType, sourceId);
        }

        var allRecords = new List<GitHubKeywordRecord>();

        foreach (var (sourceType, sourceId, text) in items)
        {
            ct.ThrowIfCancellationRequested();
            var tokens = Tokenizer.Tokenize(text);
            var keywords = CountAndClassifyTokens(tokens);

            foreach (var (keyword, (count, keywordType)) in keywords)
            {
                allRecords.Add(new GitHubKeywordRecord
                {
                    Id = GitHubKeywordRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                });
            }
        }

        foreach (var record in allRecords)
            GitHubKeywordRecord.Insert(connection, record);

        RecomputeCorpusStats(connection);
    }

    private static List<(string SourceType, string SourceId, string Text)> CollectDocuments(
        SqliteConnection connection, CancellationToken ct)
    {
        var documents = new List<(string, string, string)>();

        var issues = GitHubIssueRecord.SelectList(connection);
        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Join(" ",
                new[] { issue.Title, issue.Body, issue.Labels }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
                documents.Add(("github-issue", issue.UniqueKey, text));
        }

        var comments = GitHubCommentRecord.SelectList(connection);
        foreach (var comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(comment.Body))
                documents.Add(("github-comment", $"{comment.RepoFullName}#{comment.IssueNumber}:{comment.Id}", comment.Body));
        }

        var commits = GitHubCommitRecord.SelectList(connection);
        foreach (var commit in commits)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(commit.Message))
                documents.Add(("github-commit", commit.Sha, commit.Message));
        }

        return documents;
    }

    private static void BuildIndex(
        SqliteConnection connection,
        List<(string SourceType, string SourceId, string Text)> documents,
        CancellationToken ct)
    {
        foreach (var (sourceType, sourceId, text) in documents)
        {
            ct.ThrowIfCancellationRequested();
            var tokens = Tokenizer.Tokenize(text);
            var keywords = CountAndClassifyTokens(tokens);

            foreach (var (keyword, (count, keywordType)) in keywords)
            {
                GitHubKeywordRecord.Insert(connection, new GitHubKeywordRecord
                {
                    Id = GitHubKeywordRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                });
            }
        }
    }

    private static void RecomputeCorpusStats(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM index_corpus; DELETE FROM index_doc_stats;";
            cmd.ExecuteNonQuery();
        }

        // Compute doc stats per source type
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
                GitHubDocStatsRecord.Insert(connection, new GitHubDocStatsRecord
                {
                    Id = GitHubDocStatsRecord.GetIndex(),
                    SourceType = reader.GetString(0),
                    TotalDocuments = reader.GetInt32(1),
                    AverageDocLength = reader.GetDouble(2),
                });
            }
        }

        // Total doc count for IDF
        int totalDocCount;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(TotalDocuments), 0) FROM index_doc_stats";
            totalDocCount = Convert.ToInt32(cmd.ExecuteScalar());
        }

        if (totalDocCount == 0) return;

        // Compute and store IDF
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
                var df = reader.GetInt32(2);
                var idf = Math.Log(1.0 + (totalDocCount - df + 0.5) / (df + 0.5));

                GitHubCorpusKeywordRecord.Insert(connection, new GitHubCorpusKeywordRecord
                {
                    Id = GitHubCorpusKeywordRecord.GetIndex(),
                    Keyword = reader.GetString(0),
                    KeywordType = reader.GetString(1),
                    DocumentFrequency = df,
                    Idf = idf,
                });
            }
        }

        // Compute BM25 scores via bulk SQL UPDATE
        double overallAvgDocLen;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CAST(SUM(DocLen) AS REAL) / COUNT(*) FROM (
                    SELECT SUM(Count) as DocLen FROM index_keywords GROUP BY SourceType, SourceId
                )
                """;
            var scalar = cmd.ExecuteScalar();
            overallAvgDocLen = scalar is null or DBNull ? 1.0 : Convert.ToDouble(scalar);
        }

        using (var cmd = connection.CreateCommand())
        {
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
            cmd.Parameters.AddWithValue("@k1", DefaultK1);
            cmd.Parameters.AddWithValue("@b", DefaultB);
            cmd.Parameters.AddWithValue("@avgDocLen", overallAvgDocLen > 0 ? overallAvgDocLen : 1.0);
            cmd.ExecuteNonQuery();
        }
    }

    internal static Dictionary<string, (int Count, string KeywordType)> CountAndClassifyTokens(List<string> tokens)
    {
        var result = new Dictionary<string, (int Count, string KeywordType)>();

        foreach (var token in tokens)
        {
            var classification = KeywordClassifier.Classify(token);
            if (classification == KeywordClassifier.TypeStopWord)
                continue;

            if (result.TryGetValue(token, out var existing))
                result[token] = (existing.Count + 1, existing.KeywordType);
            else
                result[token] = (1, classification);
        }

        return result;
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
