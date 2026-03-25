using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Indexing;

/// <summary>BM25 keyword scoring pipeline scoped to the GitHub corpus.</summary>
public class GitHubIndexer(GitHubDatabase database, AuxiliaryDatabase auxiliaryDatabase, Bm25Options bm25Options, ILogger<GitHubIndexer> logger)
{
    private readonly double _k1 = bm25Options.K1;
    private readonly double _b = bm25Options.B;
    private readonly Lemmatizer _lemmatizer = bm25Options.UseLemmatization ? auxiliaryDatabase.Lemmatizer : Lemmatizer.Empty;

    /// <summary>Rebuilds the entire BM25 index from all GitHub issues, comments, and commits.</summary>
    public void RebuildFullIndex(CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        ClearIndex(connection);
        List<(string SourceType, string SourceId, string Text)> documents = CollectDocuments(connection, ct);

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

        using SqliteConnection connection = database.OpenConnection();

        foreach ((string? sourceType, string? sourceId, string _) in items)
        {
            ct.ThrowIfCancellationRequested();
            DeleteKeywordsForItem(connection, sourceType, sourceId);
        }

        List<GitHubKeywordRecord> allRecords = new List<GitHubKeywordRecord>();

        foreach ((string? sourceType, string? sourceId, string? text) in items)
        {
            ct.ThrowIfCancellationRequested();
            List<string> tokens = Tokenizer.Tokenize(text);
            Dictionary<string, (int Count, string KeywordType)> keywords = TokenCounter.CountAndClassifyTokens(
                tokens, _lemmatizer, auxiliaryDatabase.StopWords);

            foreach ((string? keyword, (int count, string? keywordType)) in keywords)
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

        foreach (GitHubKeywordRecord record in allRecords)
            GitHubKeywordRecord.Insert(connection, record);

        RecomputeCorpusStats(connection);
    }

    private static List<(string SourceType, string SourceId, string Text)> CollectDocuments(
        SqliteConnection connection, CancellationToken ct)
    {
        List<(string, string, string)> documents = new List<(string, string, string)>();

        List<GitHubIssueRecord> issues = GitHubIssueRecord.SelectList(connection);
        foreach (GitHubIssueRecord issue in issues)
        {
            ct.ThrowIfCancellationRequested();
            string text = string.Join(" ",
                new[] { issue.Title, issue.Body, issue.Labels }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
                documents.Add(("github-issue", issue.UniqueKey, text));
        }

        List<GitHubCommentRecord> comments = GitHubCommentRecord.SelectList(connection);
        foreach (GitHubCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(comment.Body))
                documents.Add(("github-comment", $"{comment.RepoFullName}#{comment.IssueNumber}:{comment.Id}", comment.Body));
        }

        List<GitHubCommitRecord> commits = GitHubCommitRecord.SelectList(connection);
        foreach (GitHubCommitRecord commit in commits)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(commit.Message))
                documents.Add(("github-commit", commit.Sha, commit.Message));
        }

        return documents;
    }

    private void BuildIndex(
        SqliteConnection connection,
        List<(string SourceType, string SourceId, string Text)> documents,
        CancellationToken ct)
    {
        foreach ((string? sourceType, string? sourceId, string? text) in documents)
        {
            ct.ThrowIfCancellationRequested();
            List<string> tokens = Tokenizer.Tokenize(text);
            Dictionary<string, (int Count, string KeywordType)> keywords = TokenCounter.CountAndClassifyTokens(
                tokens, _lemmatizer, auxiliaryDatabase.StopWords);

            foreach ((string? keyword, (int count, string? keywordType)) in keywords)
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

    private void RecomputeCorpusStats(SqliteConnection connection)
    {
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM index_corpus; DELETE FROM index_doc_stats;";
            cmd.ExecuteNonQuery();
        }

        // Compute doc stats per source type
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT SourceType, COUNT(DISTINCT SourceId) as DocCount,
                       CAST(SUM(Count) AS REAL) / COUNT(DISTINCT SourceId) as AvgLen
                FROM index_keywords
                GROUP BY SourceType
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
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
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(TotalDocuments), 0) FROM index_doc_stats";
            totalDocCount = Convert.ToInt32(cmd.ExecuteScalar());
        }

        if (totalDocCount == 0) return;

        // Compute and store IDF
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Keyword, KeywordType, COUNT(DISTINCT SourceType || ':' || SourceId) as DocFreq
                FROM index_keywords
                GROUP BY Keyword, KeywordType
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int df = reader.GetInt32(2);
                double idf = Math.Log(1.0 + (totalDocCount - df + 0.5) / (df + 0.5));

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
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CAST(SUM(DocLen) AS REAL) / COUNT(*) FROM (
                    SELECT SUM(Count) as DocLen FROM index_keywords GROUP BY SourceType, SourceId
                )
                """;
            object? scalar = cmd.ExecuteScalar();
            overallAvgDocLen = scalar is null or DBNull ? 1.0 : Convert.ToDouble(scalar);
        }

        using (SqliteCommand cmd = connection.CreateCommand())
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
            cmd.Parameters.AddWithValue("@k1", _k1);
            cmd.Parameters.AddWithValue("@b", _b);
            cmd.Parameters.AddWithValue("@avgDocLen", overallAvgDocLen > 0 ? overallAvgDocLen : 1.0);
            cmd.ExecuteNonQuery();
        }
    }

    private static void ClearIndex(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM index_keywords; DELETE FROM index_corpus; DELETE FROM index_doc_stats;";
        cmd.ExecuteNonQuery();
    }

    private static void DeleteKeywordsForItem(SqliteConnection connection, string sourceType, string sourceId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM index_keywords WHERE SourceType = @type AND SourceId = @id;";
        cmd.Parameters.AddWithValue("@type", sourceType);
        cmd.Parameters.AddWithValue("@id", sourceId);
        cmd.ExecuteNonQuery();
    }
}
