using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Rebuilds lookup/index tables from jira_issues data.</summary>
public class JiraIndexBuilder(ILogger<JiraIndexBuilder> logger)
{
    public void RebuildIndexTables(SqliteConnection conn)
    {
        logger.LogInformation("Rebuilding Jira index tables");

        RebuildSimpleIndex<JiraIndexWorkGroupRecord>(conn, "jira_index_workgroups", "WorkGroup");
        RebuildSimpleIndex<JiraIndexSpecificationRecord>(conn, "jira_index_specifications", "Specification");
        RebuildSimpleIndex<JiraIndexBallotRecord>(conn, "jira_index_ballots", "SelectedBallot");
        RebuildSimpleIndex<JiraIndexTypeRecord>(conn, "jira_index_types", "Type");
        RebuildSimpleIndex<JiraIndexPriorityRecord>(conn, "jira_index_priorities", "Priority");
        RebuildSimpleIndex<JiraIndexStatusRecord>(conn, "jira_index_statuses", "Status");
        RebuildSimpleIndex<JiraIndexResolutionRecord>(conn, "jira_index_resolutions", "Resolution");
        RebuildLabelsIndex(conn);

        logger.LogInformation("Index tables rebuilt");
    }

    private static void RebuildSimpleIndex<T>(SqliteConnection conn, string tableName, string columnName)
    {
        // Clear existing
        using SqliteCommand deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = $"DELETE FROM {tableName}";
        deleteCmd.ExecuteNonQuery();

        // Populate from jira_issues
        using SqliteCommand insertCmd = conn.CreateCommand();
        insertCmd.CommandText = $@"
            INSERT INTO {tableName} (Name, IssueCount)
            SELECT {columnName}, COUNT(*) 
            FROM jira_issues 
            WHERE {columnName} IS NOT NULL AND {columnName} != ''
            GROUP BY {columnName}";
        insertCmd.ExecuteNonQuery();
    }

    private void RebuildLabelsIndex(SqliteConnection conn)
    {
        // Clear existing
        using (SqliteCommand deleteCmd = conn.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM jira_index_labels";
            deleteCmd.ExecuteNonQuery();
        }
        using (SqliteCommand deleteCmd2 = conn.CreateCommand())
        {
            deleteCmd2.CommandText = "DELETE FROM jira_issue_labels";
            deleteCmd2.ExecuteNonQuery();
        }

        // Parse comma-delimited labels from all issues
        Dictionary<string, int> labelCounts = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Labels FROM jira_issues WHERE Labels IS NOT NULL AND Labels != ''";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string labels = reader.GetString(0);
                foreach (string label in labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    labelCounts[label] = labelCounts.GetValueOrDefault(label) + 1;
                }
            }
        }

        // Insert into index table
        foreach ((string name, int count) in labelCounts)
        {
            JiraIndexLabelRecord.Insert(conn, new JiraIndexLabelRecord
            {
                Id = 0,
                Name = name,
                IssueCount = count,
            }, ignoreDuplicates: true);
        }

        // Build junction table
        using SqliteCommand issueCmd = conn.CreateCommand();
        issueCmd.CommandText = "SELECT Id, Labels FROM jira_issues WHERE Labels IS NOT NULL AND Labels != ''";
        using SqliteDataReader issueReader = issueCmd.ExecuteReader();

        // Build label name → id map
        Dictionary<string, int> labelIds = [];
        List<JiraIndexLabelRecord> allLabels = JiraIndexLabelRecord.SelectList(conn);
        foreach (JiraIndexLabelRecord label in allLabels)
        {
            labelIds[label.Name] = label.Id;
        }

        while (issueReader.Read())
        {
            int issueId = issueReader.GetInt32(0);
            string labels = issueReader.GetString(1);
            foreach (string label in labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (labelIds.TryGetValue(label, out int labelId))
                {
                    JiraIssueLabelRecord.Insert(conn, new JiraIssueLabelRecord
                    {
                        Id = 0,
                        IssueId = issueId,
                        LabelId = labelId,
                    }, ignoreDuplicates: true);
                }
            }
        }

        logger.LogInformation("Labels index rebuilt: {Count} distinct labels", labelCounts.Count);
    }
}
