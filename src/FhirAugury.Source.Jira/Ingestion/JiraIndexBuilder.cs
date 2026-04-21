using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Rebuilds lookup/index tables from jira_issues data.</summary>
public class JiraIndexBuilder(ILogger<JiraIndexBuilder> logger)
{
    /// <summary>
    /// Rebuilds all of the per-source index/lookup tables from the current
    /// contents of <c>jira_issues</c>. Note: when invoked from the
    /// ingestion pipeline, <c>Hl7WorkGroupIndexer.Rebuild</c> (FR 02) must
    /// have already populated <c>hl7_workgroups</c> so that
    /// <see cref="RebuildWorkGroupsIndex"/> can resolve <c>WorkGroupId</c>.
    /// </summary>
    public void RebuildIndexTables(SqliteConnection conn)
    {
        logger.LogInformation("Rebuilding Jira index tables");

        RebuildWorkGroupsIndex(conn);
        RebuildSimpleIndex<JiraIndexSpecificationRecord>(conn, "jira_index_specifications", "Specification");
        RebuildSimpleIndex<JiraIndexBallotRecord>(conn, "jira_index_ballots", "SelectedBallot");
        RebuildSimpleIndex<JiraIndexTypeRecord>(conn, "jira_index_types", "Type");
        RebuildSimpleIndex<JiraIndexPriorityRecord>(conn, "jira_index_priorities", "Priority");
        RebuildSimpleIndex<JiraIndexStatusRecord>(conn, "jira_index_statuses", "Status");
        RebuildSimpleIndex<JiraIndexResolutionRecord>(conn, "jira_index_resolutions", "Resolution");
        RebuildLabelsIndex(conn);
        RebuildUsersIndex(conn);
        RebuildInPersonsIndex(conn);

        logger.LogInformation("Index tables rebuilt");
    }

    private void RebuildWorkGroupsIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_workgroups";
            del.ExecuteNonQuery();
        }

        // Aggregate (WorkGroup, Status) -> count, then pivot to per-status
        // bucket columns in C# via Hl7JiraStatusBuckets.
        Dictionary<string, JiraIndexWorkGroupRecord> byName = new(StringComparer.Ordinal);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT WorkGroup, Status, COUNT(*) AS C
                FROM jira_issues
                WHERE WorkGroup IS NOT NULL AND WorkGroup <> ''
                GROUP BY WorkGroup, Status
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string wg = r.GetString(0);
                string? status = r.IsDBNull(1) ? null : r.GetString(1);
                int count = r.GetInt32(2);

                if (!byName.TryGetValue(wg, out JiraIndexWorkGroupRecord? rec))
                {
                    rec = new JiraIndexWorkGroupRecord
                    {
                        Id = JiraIndexWorkGroupRecord.GetIndex(),
                        Name = wg,
                        WorkGroupId = null,
                        IssueCount = 0,
                        IssueCountSubmitted = 0,
                        IssueCountTriaged = 0,
                        IssueCountWaitingForInput = 0,
                        IssueCountNoChange = 0,
                        IssueCountChangeRequired = 0,
                        IssueCountPublished = 0,
                        IssueCountApplied = 0,
                        IssueCountDuplicate = 0,
                        IssueCountClosed = 0,
                        IssueCountBalloted = 0,
                        IssueCountWithdrawn = 0,
                        IssueCountDeferred = 0,
                        IssueCountOther = 0,
                    };
                    byName[wg] = rec;
                }

                rec.IssueCount += count;
                string col = Hl7JiraStatusBuckets.MapToBucketColumn(status);
                IncrementBucket(rec, col, count);
            }
        }

        // Resolve WorkGroupId via hl7_workgroups: Name match first, then
        // NameClean fallback (cleaning the issue's free-text WorkGroup with
        // the same algorithm so "FHIR Infrastructure" matches "FHIRInfrastructure").
        Dictionary<string, int> idByName = new(StringComparer.Ordinal);
        Dictionary<string, int> idByNameClean = new(StringComparer.Ordinal);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, NameClean FROM hl7_workgroups";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                idByName[r.GetString(1)] = id;
                idByNameClean[r.GetString(2)] = id;
            }
        }

        foreach (JiraIndexWorkGroupRecord rec in byName.Values)
        {
            if (idByName.TryGetValue(rec.Name, out int id))
            {
                rec.WorkGroupId = id;
                continue;
            }
            string clean = Hl7WorkGroupNameCleaner.Clean(rec.Name);
            if (clean.Length > 0 && idByNameClean.TryGetValue(clean, out id))
            {
                rec.WorkGroupId = id;
                continue;
            }
            logger.LogDebug("workgroup {Name} did not match any hl7_workgroups row", rec.Name);
        }

        List<JiraIndexWorkGroupRecord> rows = [.. byName.Values];
        rows.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        logger.LogInformation("Workgroups index rebuilt: {Count} distinct workgroups", byName.Count);
    }

    private static void IncrementBucket(JiraIndexWorkGroupRecord rec, string col, int count)
    {
        switch (col)
        {
            case nameof(JiraIndexWorkGroupRecord.IssueCountSubmitted):       rec.IssueCountSubmitted       += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountTriaged):         rec.IssueCountTriaged         += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountWaitingForInput): rec.IssueCountWaitingForInput += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountNoChange):        rec.IssueCountNoChange        += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountChangeRequired):  rec.IssueCountChangeRequired  += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountPublished):       rec.IssueCountPublished       += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountApplied):         rec.IssueCountApplied         += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountDuplicate):       rec.IssueCountDuplicate       += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountClosed):          rec.IssueCountClosed          += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountBalloted):        rec.IssueCountBalloted        += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountWithdrawn):       rec.IssueCountWithdrawn       += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountDeferred):        rec.IssueCountDeferred        += count; break;
            default:                                                          rec.IssueCountOther          += count; break;
        }
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

        List<JiraIndexLabelRecord> indexLabelsToInsert = [];

        // Insert into index table
        foreach ((string name, int count) in labelCounts)
        {
            indexLabelsToInsert.Add(new()
            {
                Id = JiraIndexLabelRecord.GetIndex(),
                Name = name,
                IssueCount = count,
            });
        }

        indexLabelsToInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);

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

        List<JiraIssueLabelRecord> issueLabelsToInsert = [];

        while (issueReader.Read())
        {
            int issueId = issueReader.GetInt32(0);
            string labels = issueReader.GetString(1);
            foreach (string label in labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (labelIds.TryGetValue(label, out int labelId))
                {
                    issueLabelsToInsert.Add(new()
                    {
                        Id = JiraIssueLabelRecord.GetIndex(),
                        IssueId = issueId,
                        LabelId = labelId,
                    });
                }
            }
        }

        issueLabelsToInsert.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);

        logger.LogInformation("Labels index rebuilt: {Count} distinct labels", labelCounts.Count);
    }

    private void RebuildUsersIndex(SqliteConnection conn)
    {
        using (SqliteCommand deleteCmd = conn.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM jira_index_users";
            deleteCmd.ExecuteNonQuery();
        }

        using SqliteCommand insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO jira_index_users (Name, IssueCount)
            SELECT u.DisplayName, COUNT(DISTINCT roles.issue_id) AS IssueCount
            FROM jira_users u
            INNER JOIN (
                SELECT i.Id AS issue_id, i.Assignee AS name FROM jira_issues i WHERE i.Assignee IS NOT NULL AND i.Assignee <> ''
                UNION ALL
                SELECT i.Id, i.Reporter FROM jira_issues i WHERE i.Reporter IS NOT NULL AND i.Reporter <> ''
                UNION ALL
                SELECT i.Id, i.VoteMover FROM jira_issues i WHERE i.VoteMover IS NOT NULL AND i.VoteMover <> ''
                UNION ALL
                SELECT i.Id, i.VoteSeconder FROM jira_issues i WHERE i.VoteSeconder IS NOT NULL AND i.VoteSeconder <> ''
                UNION ALL
                SELECT ip.IssueId, u2.DisplayName
                    FROM jira_issue_inpersons ip
                    INNER JOIN jira_users u2 ON u2.Id = ip.UserId
                    WHERE u2.DisplayName IS NOT NULL AND u2.DisplayName <> ''
            ) roles
              ON roles.name = u.DisplayName OR roles.name = u.Username
            WHERE u.DisplayName IS NOT NULL AND u.DisplayName <> ''
            GROUP BY u.DisplayName
            """;
        insertCmd.ExecuteNonQuery();

        using SqliteCommand countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM jira_index_users";
        int count = Convert.ToInt32(countCmd.ExecuteScalar());
        logger.LogInformation("Users index rebuilt: {Count} distinct users", count);
    }

    private void RebuildInPersonsIndex(SqliteConnection conn)
    {
        using (SqliteCommand deleteCmd = conn.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM jira_index_inpersons";
            deleteCmd.ExecuteNonQuery();
        }

        using SqliteCommand insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO jira_index_inpersons (Name, IssueCount)
            SELECT u.DisplayName, COUNT(DISTINCT ip.IssueId)
            FROM jira_issue_inpersons ip
            INNER JOIN jira_users u ON u.Id = ip.UserId
            WHERE u.DisplayName IS NOT NULL AND u.DisplayName != ''
            GROUP BY u.DisplayName
            """;
        insertCmd.ExecuteNonQuery();

        using SqliteCommand countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM jira_index_inpersons";
        int count = Convert.ToInt32(countCmd.ExecuteScalar());
        logger.LogInformation("In-persons index rebuilt: {Count} distinct users", count);
    }
}
