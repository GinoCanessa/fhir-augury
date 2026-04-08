using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Common.Indexing;
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
        List<IndexContent> documents = CollectDocuments(connection, ct);

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
    public void UpdateIndex(IReadOnlyList<IndexContent> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        using SqliteConnection connection = database.OpenConnection();

        DeleteKeywordsForItems(connection, items);

        List<GitHubKeywordRecord> allRecords = new List<GitHubKeywordRecord>();

        foreach (IndexContent content in items)
        {
            ct.ThrowIfCancellationRequested();
            List<string> tokens = Tokenizer.Tokenize(content.Text);
            Dictionary<string, (int Count, string KeywordType)> keywords = TokenCounter.CountAndClassifyTokens(
                tokens, _lemmatizer, auxiliaryDatabase.StopWords);

            foreach ((string? keyword, (int count, string? keywordType)) in keywords)
            {
                allRecords.Add(new GitHubKeywordRecord
                {
                    Id = GitHubKeywordRecord.GetIndex(),
                    ContentType = content.ContentType,
                    SourceId = content.SourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                });
            }
        }

        allRecords.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);

        RecomputeCorpusStats(connection);
    }

    private static List<IndexContent> CollectDocuments(
        SqliteConnection connection, CancellationToken ct)
    {
        List<GitHubIssueRecord> issues = GitHubIssueRecord.SelectList(connection);
        List<GitHubCommentRecord> comments = GitHubCommentRecord.SelectList(connection);
        List<GitHubCommitRecord> commits = GitHubCommitRecord.SelectList(connection);
        List<GitHubFileContentRecord> fileContents = GitHubFileContentRecord.SelectList(connection);
        List<GitHubCanonicalArtifactRecord> canonicalArtifacts = GitHubCanonicalArtifactRecord.SelectList(connection);

        List<IndexContent> documents = new(issues.Count + comments.Count + commits.Count + fileContents.Count + canonicalArtifacts.Count);

        foreach (GitHubIssueRecord issue in issues)
        {
            ct.ThrowIfCancellationRequested();
            string text = string.Join(" ",
                new[] { issue.Title, issue.Body, issue.Labels }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                documents.Add(new()
                {
                    ContentType = ContentTypes.Issue,
                    SourceId = issue.UniqueKey,
                    Text = text,
                });
            }
        }

        foreach (GitHubCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(comment.Body))
            {
                documents.Add(new()
                {
                    ContentType = ContentTypes.Comment,
                    SourceId = $"{comment.RepoFullName}#{comment.IssueNumber}:{comment.Id}", 
                    Text = comment.Body,
                });
            }
        }

        foreach (GitHubCommitRecord commit in commits)
        {
            ct.ThrowIfCancellationRequested();
            string text = string.Join(" ",
                new[] { commit.Message, commit.Body }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                documents.Add(new()
                { 
                    ContentType = ContentTypes.Commit, 
                    SourceId = commit.Sha, 
                    Text = text 
                });
            }
        }

        foreach (GitHubFileContentRecord file in fileContents)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(file.ContentText))
            {
                documents.Add(new()
                {
                    ContentType = ContentTypes.File,
                    SourceId = $"{file.RepoFullName}:{file.FilePath}",
                    Text = file.ContentText,
                });
            }
        }

        foreach (GitHubCanonicalArtifactRecord artifact in canonicalArtifacts)
        {
            ct.ThrowIfCancellationRequested();
            string text = string.Join(" ",
                new[] { artifact.ResourceType, artifact.Name, artifact.Title, artifact.Description }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                documents.Add(new()
                {
                    ContentType = ContentTypes.CanonicalArtifact,
                    SourceId = $"{artifact.RepoFullName}:{artifact.FilePath}",
                    Text = text,
                });
            }
        }

        List<GitHubStructureDefinitionRecord> sds = GitHubStructureDefinitionRecord.SelectList(connection);
        foreach (GitHubStructureDefinitionRecord sd in sds)
        {
            ct.ThrowIfCancellationRequested();
            string text = string.Join(" ",
                new[] { sd.Name, sd.Title, sd.Description }
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                documents.Add(new()
                {
                    ContentType = ContentTypes.StructureDefinition,
                    SourceId = $"{sd.RepoFullName}:{sd.FilePath}",
                    Text = text,
                });
            }
        }

        return documents;
    }

    private void BuildIndex(
        SqliteConnection connection,
        List<IndexContent> documents,
        CancellationToken ct)
    {
        List<GitHubKeywordRecord> toInsert = new(documents.Count);

        foreach (IndexContent content in documents)
        {
            ct.ThrowIfCancellationRequested();
            List<string> tokens = Tokenizer.Tokenize(content.Text);
            Dictionary<string, (int Count, string KeywordType)> keywords = TokenCounter.CountAndClassifyTokens(
                tokens, _lemmatizer, auxiliaryDatabase.StopWords);

            foreach ((string? keyword, (int count, string? keywordType)) in keywords)
            {
                toInsert.Add(new GitHubKeywordRecord()
                {
                    Id = GitHubKeywordRecord.GetIndex(),
                    ContentType = content.ContentType,
                    SourceId = content.SourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                });
            }
        }

        toInsert.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
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
                SELECT ContentType, COUNT(DISTINCT SourceId) as DocCount,
                       CAST(SUM(Count) AS REAL) / COUNT(DISTINCT SourceId) as AvgLen
                FROM index_keywords
                GROUP BY ContentType
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();

            List<GitHubDocStatsRecord> statsToInsert = [];

            while (reader.Read())
            {
                statsToInsert.Add(new GitHubDocStatsRecord()
                {
                    Id = GitHubDocStatsRecord.GetIndex(),
                    ContentType = reader.GetString(0),
                    TotalDocuments = reader.GetInt32(1),
                    AverageDocLength = reader.GetDouble(2),
                });
            }

            statsToInsert.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
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
                SELECT Keyword, KeywordType, COUNT(DISTINCT ContentType || ':' || SourceId) as DocFreq
                FROM index_keywords
                GROUP BY Keyword, KeywordType
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();

            List<GitHubCorpusKeywordRecord> keywordsToInsert = [];

            while (reader.Read())
            {
                int df = reader.GetInt32(2);
                double idf = Math.Log(1.0 + (totalDocCount - df + 0.5) / (df + 0.5));

                keywordsToInsert.Add(new GitHubCorpusKeywordRecord()
                {
                    Id = GitHubCorpusKeywordRecord.GetIndex(),
                    Keyword = reader.GetString(0),
                    KeywordType = reader.GetString(1),
                    DocumentFrequency = df,
                    Idf = idf,
                });
            }

            keywordsToInsert.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        // Compute BM25 scores via bulk SQL UPDATE
        double overallAvgDocLen;
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CAST(SUM(DocLen) AS REAL) / COUNT(*) FROM (
                    SELECT SUM(Count) as DocLen FROM index_keywords GROUP BY ContentType, SourceId
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
                        SELECT ContentType, SourceId, SUM(Count) as DocLen
                        FROM index_keywords
                        GROUP BY ContentType, SourceId
                    ) dl ON dl.ContentType = index_keywords.ContentType AND dl.SourceId = index_keywords.SourceId
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

    private static void DeleteKeywordsForItems(SqliteConnection connection, IReadOnlyList<IndexContent> contents)
    {
        if (contents.Count == 0)
        {
            return;
        }

        const int batchSize = 500;

        foreach (IGrouping<string, IndexContent> contentGroup in contents.GroupBy(c => c.ContentType))
        {
            string sourceType = contentGroup.Key;

            int index = 0;
            string[] paramNames = new string[500];

            using SqliteCommand cmd = connection.CreateCommand();

            foreach (IndexContent content in contentGroup)
            {
                if (index >= batchSize)
                {
                    cmd.CommandText = $"DELETE FROM index_keywords WHERE ContentType = @type AND SourceId IN ({string.Join(", ", paramNames)});";
                    cmd.Parameters.AddWithValue("@type", sourceType);
                    cmd.ExecuteNonQuery();

                    index = 0;
                    cmd.Parameters.Clear();
                }

                paramNames[index] = $"@id{index}";
                cmd.Parameters.AddWithValue(paramNames[index], content.SourceId);
                index++;
            }

            // execute last batch
            if (index == 0)
            {
                continue;
            }

            cmd.CommandText = $"DELETE FROM index_keywords WHERE ContentType = @type AND SourceId IN ({string.Join(", ", paramNames.AsSpan(0, index))});";
            cmd.Parameters.AddWithValue("@type", sourceType);
            cmd.ExecuteNonQuery();
        }
    }
}
