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

        // Tokenize and insert new keyword records
        var docLengths = new List<int>();
        var docKeywords = new List<(string SourceType, string SourceId, Dictionary<string, (int Count, string KeywordType)> Keywords)>();

        foreach (var (sourceType, sourceId, text) in items)
        {
            ct.ThrowIfCancellationRequested();

            var tokens = Tokenizer.Tokenize(text);
            var keywords = CountAndClassifyTokens(tokens);
            docLengths.Add(tokens.Count);
            docKeywords.Add((sourceType, sourceId, keywords));

            // Insert keyword records (BM25 score will be updated after IDF computation)
            foreach (var (keyword, (count, keywordType)) in keywords)
            {
                var record = new KeywordRecord
                {
                    Id = KeywordRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                };
                KeywordRecord.Insert(connection, record);
            }
        }

        // Recompute corpus-level stats and BM25 scores for all documents
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

        // Step 1-3: Tokenize, classify, count per-document term frequency
        var allDocKeywords = new List<(string SourceType, string SourceId, Dictionary<string, (int Count, string KeywordType)> Keywords, int DocLength)>();

        foreach (var (sourceType, sourceId, text) in documents)
        {
            ct.ThrowIfCancellationRequested();

            var tokens = Tokenizer.Tokenize(text);
            var keywords = CountAndClassifyTokens(tokens);
            allDocKeywords.Add((sourceType, sourceId, keywords, tokens.Count));
        }

        // Step 4: Store per-doc keyword records
        foreach (var (sourceType, sourceId, keywords, _) in allDocKeywords)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var (keyword, (count, keywordType)) in keywords)
            {
                var record = new KeywordRecord
                {
                    Id = KeywordRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    Keyword = keyword,
                    Count = count,
                    KeywordType = keywordType,
                    Bm25Score = 0,
                };
                KeywordRecord.Insert(connection, record);
            }
        }

        // Steps 5-10: Compute corpus stats and BM25 scores
        RecomputeCorpusStats(connection, k1, b);
    }

    /// <summary>
    /// Recomputes all corpus-level statistics and BM25 scores from keyword records.
    /// </summary>
    private static void RecomputeCorpusStats(SqliteConnection connection, double k1, double b)
    {
        // Clear existing corpus stats
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM index_corpus;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM index_doc_stats;";
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
                var sourceType = reader.GetString(0);
                var totalDocs = reader.GetInt32(1);
                var avgDocLen = reader.GetDouble(2);
                docStats[sourceType] = (totalDocs, avgDocLen);
            }
        }

        // Store doc stats
        foreach (var (sourceType, (totalDocs, avgDocLen)) in docStats)
        {
            var statsRecord = new DocStatsRecord
            {
                Id = DocStatsRecord.GetIndex(),
                SourceType = sourceType,
                TotalDocuments = totalDocs,
                AverageDocLength = avgDocLen,
            };
            DocStatsRecord.Insert(connection, statsRecord);
        }

        // Get total document count across all sources
        var totalDocCount = docStats.Values.Sum(ds => ds.TotalDocs);
        if (totalDocCount == 0) return;

        // Compute document frequency per keyword
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

                // IDF (BM25+ variant, always non-negative): log(1 + (N - df + 0.5) / (df + 0.5))
                var idf = Math.Log(1.0 + (totalDocCount - df + 0.5) / (df + 0.5));

                var corpusRecord = new CorpusKeywordRecord
                {
                    Id = CorpusKeywordRecord.GetIndex(),
                    Keyword = keyword,
                    KeywordType = keywordType,
                    DocumentFrequency = df,
                    Idf = idf,
                };
                CorpusKeywordRecord.Insert(connection, corpusRecord);
            }
        }

        // Compute per-document lengths (sum of keyword counts)
        var docLengths = new Dictionary<string, int>(); // "sourceType:sourceId" -> total tokens
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT SourceType, SourceId, SUM(Count) as DocLen
                FROM index_keywords
                GROUP BY SourceType, SourceId
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var compositeKey = reader.GetString(0) + ":" + reader.GetString(1);
                docLengths[compositeKey] = reader.GetInt32(2);
            }
        }

        // Compute overall average doc length
        var overallAvgDocLen = totalDocCount > 0 ? (double)docLengths.Values.Sum() / totalDocCount : 1.0;

        // Load IDF values into a lookup
        var idfLookup = new Dictionary<string, double>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Keyword, Idf FROM index_corpus";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                idfLookup[reader.GetString(0)] = reader.GetDouble(1);
            }
        }

        // Update BM25 scores for all keyword records
        var keywordRecords = KeywordRecord.SelectList(connection);
        foreach (var record in keywordRecords)
        {
            var compositeKey = record.SourceType + ":" + record.SourceId;
            var docLen = docLengths.GetValueOrDefault(compositeKey, 1);
            var idf = idfLookup.GetValueOrDefault(record.Keyword, 0.0);

            // BM25: idf × (tf × (k1 + 1)) / (tf + k1 × (1 - b + b × docLen / avgDocLen))
            var tf = (double)record.Count;
            var bm25 = idf * (tf * (k1 + 1.0)) / (tf + k1 * (1.0 - b + b * docLen / overallAvgDocLen));

            record.Bm25Score = bm25;
            KeywordRecord.Update(connection, record);
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
