using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Rebuilds lookup/index tables from the four Jira issue-shape tables
/// (<c>jira_issues</c>, <c>jira_pss</c>, <c>jira_baldef</c>,
/// <c>jira_ballot</c>).
/// </summary>
/// <remarks>
/// <para>
/// Phase 6 (plan §7) deviation: the simple roll-up records
/// (Type/Priority/Status/Resolution/User/Label/InPerson) carry a
/// <c>SourceTable</c> discriminator but the builder writes a single
/// <c>SourceTable = "all"</c> row per <c>Name</c>, summing across the
/// four shape tables. This keeps consumer queries — and the existing
/// 256 tests — unchanged. Composite (SourceTable, Name) uniqueness is
/// enforced in code by the DELETE + INSERT cycle.
/// </para>
/// <para>
/// Workgroup attribution for <c>jira_ballot</c> rows walks
/// <c>BallotPackageCode → jira_baldef.BallotCode → linked PSS via
/// jira_issue_links → jira_pss.SponsoringWorkGroup</c>. Rows that fail
/// to resolve are attributed to the synthetic <c>(Unattributed)</c>
/// workgroup so vote totals stay visible without polluting real
/// workgroup buckets.
/// </para>
/// </remarks>
public class JiraIndexBuilder(ILogger<JiraIndexBuilder> logger)
{
    private const string UnattributedWorkGroup = "(Unattributed)";

    private static readonly string[] AllShapeTables =
    [
        "jira_issues",
        "jira_pss",
        "jira_baldef",
        "jira_ballot",
    ];

    /// <summary>
    /// Rebuilds all of the per-source index/lookup tables from the
    /// current contents of the four Jira issue-shape tables. When
    /// invoked from the ingestion pipeline,
    /// <c>Hl7WorkGroupIndexer.Rebuild</c> (FR 02) must have already
    /// populated <c>hl7_workgroups</c> so that
    /// <see cref="RebuildWorkGroupsIndex"/> can resolve <c>WorkGroupId</c>.
    /// </summary>
    public void RebuildIndexTables(SqliteConnection conn)
    {
        logger.LogInformation("Rebuilding Jira index tables");

        RebuildWorkGroupsIndex(conn);
        RebuildSpecificationsIndex(conn);
        RebuildBallotTargetsIndex(conn);
        RebuildBallotCyclesIndex(conn);
        RebuildSimpleIndex(conn, "jira_index_types", "Type", AllShapeTables);
        RebuildSimpleIndex(conn, "jira_index_priorities", "Priority", AllShapeTables);
        RebuildSimpleIndex(conn, "jira_index_statuses", "Status", AllShapeTables);
        // Resolution exists only on jira_issues (FHIR-shape only).
        RebuildSimpleIndex(conn, "jira_index_resolutions", "Resolution", ["jira_issues"]);
        RebuildLabelsIndex(conn);
        RebuildUsersIndex(conn);
        RebuildInPersonsIndex(conn);

        logger.LogInformation("Index tables rebuilt");
    }

    // ------------------------------------------------------------------
    // Workgroups
    // ------------------------------------------------------------------

    private void RebuildWorkGroupsIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_workgroups";
            del.ExecuteNonQuery();
        }

        Dictionary<string, JiraIndexWorkGroupRecord> byName = new(StringComparer.Ordinal);

        // 1. FHIR-shape buckets: jira_issues.WorkGroup grouped by Status.
        AggregateFhirBuckets(conn, byName);

        // 2. PSS-shape buckets: jira_pss.SponsoringWorkGroup
        //    (with SponsoringWorkGroupsLegacy fallback) grouped by Status.
        AggregatePssBuckets(conn, byName);

        // 3. Ballot-disposition buckets: jira_ballot.VoteBallot, attributed
        //    via BallotPackageCode → jira_baldef → linked PSS workgroup,
        //    falling back to "(Unattributed)".
        AggregateBallotBuckets(conn, byName);

        // 4. Resolve WorkGroupId via hl7_workgroups: Name match first,
        //    then NameClean fallback. The synthetic "(Unattributed)"
        //    bucket is left unresolved.
        ResolveWorkGroupIds(conn, byName);

        List<JiraIndexWorkGroupRecord> rows = [.. byName.Values];
        rows.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        logger.LogInformation("Workgroups index rebuilt: {Count} distinct workgroups", byName.Count);
    }

    private static void AggregateFhirBuckets(SqliteConnection conn, Dictionary<string, JiraIndexWorkGroupRecord> byName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
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

            JiraIndexWorkGroupRecord rec = GetOrAddWorkGroup(byName, wg);
            rec.IssueCount += count;
            string col = Hl7JiraStatusBuckets.MapToBucketColumn(status);
            IncrementFhirBucket(rec, col, count);
        }
    }

    private void AggregatePssBuckets(SqliteConnection conn, Dictionary<string, JiraIndexWorkGroupRecord> byName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(NULLIF(SponsoringWorkGroup, ''), SponsoringWorkGroupsLegacy) AS WG,
                Status,
                COUNT(*) AS C
            FROM jira_pss
            WHERE COALESCE(NULLIF(SponsoringWorkGroup, ''), SponsoringWorkGroupsLegacy) IS NOT NULL
              AND COALESCE(NULLIF(SponsoringWorkGroup, ''), SponsoringWorkGroupsLegacy) <> ''
            GROUP BY WG, Status
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string wg = r.GetString(0);
            string? status = r.IsDBNull(1) ? null : r.GetString(1);
            int count = r.GetInt32(2);

            JiraIndexWorkGroupRecord rec = GetOrAddWorkGroup(byName, wg);
            rec.IssueCount += count;
            string col = MapPssStatusBucket(status);
            IncrementPssBucket(rec, col, count);
            if (col == nameof(JiraIndexWorkGroupRecord.IssueCountOtherPss) && !string.IsNullOrEmpty(status))
            {
                logger.LogDebug("Unrecognised PSS status {Status} (workgroup {WG}, count {Count})", status, wg, count);
            }
        }
    }

    private void AggregateBallotBuckets(SqliteConnection conn, Dictionary<string, JiraIndexWorkGroupRecord> byName)
    {
        // Pre-build resolution maps once: ballot package code -> workgroup.
        Dictionary<string, string?> packageCodeToWorkGroup = BuildBallotPackageCodeToWorkGroupMap(conn);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT BallotPackageCode, VoteBallot, COUNT(*) AS C
            FROM jira_ballot
            WHERE VoteBallot IS NOT NULL AND VoteBallot <> ''
            GROUP BY BallotPackageCode, VoteBallot
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? pkg = r.IsDBNull(0) ? null : r.GetString(0);
            string vote = r.GetString(1);
            int count = r.GetInt32(2);

            string? wg = null;
            if (!string.IsNullOrEmpty(pkg))
            {
                packageCodeToWorkGroup.TryGetValue(pkg, out wg);
            }
            if (string.IsNullOrEmpty(wg))
            {
                wg = UnattributedWorkGroup;
            }

            JiraIndexWorkGroupRecord rec = GetOrAddWorkGroup(byName, wg);
            rec.IssueCount += count;
            string? col = MapVoteBucket(vote);
            IncrementVoteBucket(rec, col, count);
            if (col is null)
            {
                logger.LogDebug("Unrecognised ballot vote {Vote} (package {Pkg}, count {Count})", vote, pkg, count);
            }
        }
    }

    private static Dictionary<string, string?> BuildBallotPackageCodeToWorkGroupMap(SqliteConnection conn)
    {
        // 1. baldef key -> ballot code(s) — we want code -> baldef key
        Dictionary<string, string> codeToBaldefKey = new(StringComparer.Ordinal);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Key, BallotCode FROM jira_baldef WHERE BallotCode IS NOT NULL AND BallotCode <> ''";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                string code = r.GetString(1);
                // Last-writer wins on duplicate codes — in practice ballot
                // codes are unique per package.
                codeToBaldefKey[code] = key;
            }
        }

        // 2. PSS key -> workgroup
        Dictionary<string, string> pssKeyToWg = new(StringComparer.Ordinal);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Key, COALESCE(NULLIF(SponsoringWorkGroup, ''), SponsoringWorkGroupsLegacy)
                FROM jira_pss
                WHERE COALESCE(NULLIF(SponsoringWorkGroup, ''), SponsoringWorkGroupsLegacy) IS NOT NULL
                  AND COALESCE(NULLIF(SponsoringWorkGroup, ''), SponsoringWorkGroupsLegacy) <> ''
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                pssKeyToWg[r.GetString(0)] = r.GetString(1);
            }
        }

        // 3. baldef key -> linked PSS keys (jira_issue_links is materialised
        //    in either direction for BALDEF<->PSS chains; consider both).
        Dictionary<string, List<string>> baldefToPssKeys = new(StringComparer.Ordinal);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT SourceKey, TargetKey FROM jira_issue_links
                WHERE (SourceKey LIKE 'BALDEF-%' AND TargetKey LIKE 'PSS-%')
                   OR (SourceKey LIKE 'PSS-%'    AND TargetKey LIKE 'BALDEF-%')
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string s = r.GetString(0);
                string t = r.GetString(1);
                string baldefKey = s.StartsWith("BALDEF-", StringComparison.Ordinal) ? s : t;
                string pssKey    = s.StartsWith("PSS-",    StringComparison.Ordinal) ? s : t;
                if (!baldefToPssKeys.TryGetValue(baldefKey, out List<string>? list))
                {
                    list = [];
                    baldefToPssKeys[baldefKey] = list;
                }
                list.Add(pssKey);
            }
        }

        // 4. Compose: ballot package code -> first resolved workgroup.
        Dictionary<string, string?> codeToWg = new(StringComparer.Ordinal);
        foreach ((string code, string baldefKey) in codeToBaldefKey)
        {
            if (!baldefToPssKeys.TryGetValue(baldefKey, out List<string>? pssKeys))
            {
                continue;
            }
            foreach (string pssKey in pssKeys)
            {
                if (pssKeyToWg.TryGetValue(pssKey, out string? wg))
                {
                    codeToWg[code] = wg;
                    break;
                }
            }
        }
        return codeToWg;
    }

    private void ResolveWorkGroupIds(SqliteConnection conn, Dictionary<string, JiraIndexWorkGroupRecord> byName)
    {
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
            if (rec.Name == UnattributedWorkGroup)
            {
                continue;
            }
            if (idByName.TryGetValue(rec.Name, out int id))
            {
                rec.WorkGroupId = id;
                continue;
            }
            string clean = FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(rec.Name);
            if (clean.Length > 0 && idByNameClean.TryGetValue(clean, out id))
            {
                rec.WorkGroupId = id;
                continue;
            }
            logger.LogDebug("workgroup {Name} did not match any hl7_workgroups row", rec.Name);
        }
    }

    private static JiraIndexWorkGroupRecord GetOrAddWorkGroup(Dictionary<string, JiraIndexWorkGroupRecord> byName, string wg)
    {
        if (byName.TryGetValue(wg, out JiraIndexWorkGroupRecord? rec))
        {
            return rec;
        }
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
        return rec;
    }

    private static void IncrementFhirBucket(JiraIndexWorkGroupRecord rec, string col, int count)
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

    /// <summary>
    /// Maps a PSS-row <c>Status</c> string to one of the four
    /// <see cref="JiraIndexWorkGroupRecord"/> PSS bucket columns.
    /// Inline mapping (no shared helper exists for PSS today) — review on
    /// next ingest if "Other" counts grow unexpectedly.
    /// </summary>
    private static string MapPssStatusBucket(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss);
        }
        return status switch
        {
            "Approved"                  => nameof(JiraIndexWorkGroupRecord.IssueCountApprovedPss),
            "Resolved with Approval"    => nameof(JiraIndexWorkGroupRecord.IssueCountApprovedPss),
            "Resolved - Approved"       => nameof(JiraIndexWorkGroupRecord.IssueCountApprovedPss),
            "Rejected"                  => nameof(JiraIndexWorkGroupRecord.IssueCountRejectedPss),
            "Withdrawn"                 => nameof(JiraIndexWorkGroupRecord.IssueCountRejectedPss),
            "Resolved - Rejected"       => nameof(JiraIndexWorkGroupRecord.IssueCountRejectedPss),
            "Resolved - Withdrawn"      => nameof(JiraIndexWorkGroupRecord.IssueCountRejectedPss),
            "Open"                      => nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss),
            "Submitted"                 => nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss),
            "Under Review"              => nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss),
            "In Progress"               => nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss),
            "Pending"                   => nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss),
            "Triaged"                   => nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss),
            _                           => nameof(JiraIndexWorkGroupRecord.IssueCountOtherPss),
        };
    }

    private static void IncrementPssBucket(JiraIndexWorkGroupRecord rec, string col, int count)
    {
        switch (col)
        {
            case nameof(JiraIndexWorkGroupRecord.IssueCountSubmittedPss): rec.IssueCountSubmittedPss += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountApprovedPss):  rec.IssueCountApprovedPss  += count; break;
            case nameof(JiraIndexWorkGroupRecord.IssueCountRejectedPss):  rec.IssueCountRejectedPss  += count; break;
            default:                                                       rec.IssueCountOtherPss     += count; break;
        }
    }

    private static string? MapVoteBucket(string? vote)
    {
        if (string.IsNullOrWhiteSpace(vote))
        {
            return null;
        }
        string v = vote.Trim();
        if (string.Equals(v, "Affirmative", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(JiraIndexWorkGroupRecord.AffirmativeVotes);
        }
        if (string.Equals(v, "Negative-with-Comment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "Negative with Comment", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(JiraIndexWorkGroupRecord.NegativeWithCommentVotes);
        }
        if (string.Equals(v, "Negative", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(JiraIndexWorkGroupRecord.NegativeVotes);
        }
        if (string.Equals(v, "Abstain", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(JiraIndexWorkGroupRecord.AbstainVotes);
        }
        return null;
    }

    private static void IncrementVoteBucket(JiraIndexWorkGroupRecord rec, string? col, int count)
    {
        switch (col)
        {
            case nameof(JiraIndexWorkGroupRecord.AffirmativeVotes):         rec.AffirmativeVotes         += count; break;
            case nameof(JiraIndexWorkGroupRecord.NegativeVotes):            rec.NegativeVotes            += count; break;
            case nameof(JiraIndexWorkGroupRecord.NegativeWithCommentVotes): rec.NegativeWithCommentVotes += count; break;
            case nameof(JiraIndexWorkGroupRecord.AbstainVotes):             rec.AbstainVotes             += count; break;
            // Unrecognised vote: contributes to IssueCount only (already
            // incremented), no per-bucket row.
        }
    }

    // ------------------------------------------------------------------
    // Specifications (per-shape breakdown)
    // ------------------------------------------------------------------

    private void RebuildSpecificationsIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_specifications";
            del.ExecuteNonQuery();
        }

        Dictionary<string, JiraIndexSpecificationRecord> bySpec = new(StringComparer.Ordinal);
        AccumulateSpec(conn, bySpec, "jira_issues", isFhir: true,    isBaldef: false, isBallot: false);
        AccumulateSpec(conn, bySpec, "jira_baldef", isFhir: false,   isBaldef: true,  isBallot: false);
        AccumulateSpec(conn, bySpec, "jira_ballot", isFhir: false,   isBaldef: false, isBallot: true);

        List<JiraIndexSpecificationRecord> rows = [.. bySpec.Values];
        rows.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        logger.LogInformation("Specifications index rebuilt: {Count} distinct specifications", rows.Count);
    }

    private static void AccumulateSpec(
        SqliteConnection conn,
        Dictionary<string, JiraIndexSpecificationRecord> bySpec,
        string table,
        bool isFhir,
        bool isBaldef,
        bool isBallot)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT Specification, COUNT(*) AS C
            FROM {table}
            WHERE Specification IS NOT NULL AND Specification <> ''
            GROUP BY Specification
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string name = r.GetString(0);
            int count = r.GetInt32(1);
            if (!bySpec.TryGetValue(name, out JiraIndexSpecificationRecord? rec))
            {
                rec = new JiraIndexSpecificationRecord
                {
                    Id = JiraIndexSpecificationRecord.GetIndex(),
                    Name = name,
                    IssueCount = 0,
                    IssueCountFhir = 0,
                    IssueCountBaldef = 0,
                    IssueCountBallot = 0,
                };
                bySpec[name] = rec;
            }
            rec.IssueCount += count;
            if (isFhir)   rec.IssueCountFhir   += count;
            if (isBaldef) rec.IssueCountBaldef += count;
            if (isBallot) rec.IssueCountBallot += count;
        }
    }

    // ------------------------------------------------------------------
    // Simple single-column rollups (Type/Priority/Status/Resolution)
    // ------------------------------------------------------------------

    /// <summary>
    /// Rebuilds a simple <c>(Name, IssueCount)</c> rollup table from a
    /// single column UNION'd across the supplied source tables. Writes
    /// one row per distinct value with <c>SourceTable = "all"</c>.
    /// </summary>
    private static void RebuildSimpleIndex(SqliteConnection conn, string indexTable, string columnName, string[] sourceTables)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = $"DELETE FROM {indexTable}";
            del.ExecuteNonQuery();
        }

        string union = string.Join(
            "\n            UNION ALL\n            ",
            sourceTables.Select(t => $"SELECT {columnName} FROM {t} WHERE {columnName} IS NOT NULL AND {columnName} <> ''"));

        using SqliteCommand insertCmd = conn.CreateCommand();
        insertCmd.CommandText = $"""
            INSERT INTO {indexTable} (Name, SourceTable, IssueCount)
            SELECT u.{columnName}, 'all', COUNT(*)
            FROM (
                {union}
            ) AS u
            GROUP BY u.{columnName}
            """;
        insertCmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // Labels (rollup + junction)
    // ------------------------------------------------------------------

    private void RebuildLabelsIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_labels";
            del.ExecuteNonQuery();
        }
        using (SqliteCommand del2 = conn.CreateCommand())
        {
            del2.CommandText = "DELETE FROM jira_issue_labels";
            del2.ExecuteNonQuery();
        }

        // Pass 1: aggregate label -> count across all four shape tables.
        Dictionary<string, int> labelCounts = new(StringComparer.Ordinal);
        foreach (string table in AllShapeTables)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Labels FROM {table} WHERE Labels IS NOT NULL AND Labels <> ''";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string labels = r.GetString(0);
                foreach (string label in labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    labelCounts[label] = labelCounts.GetValueOrDefault(label) + 1;
                }
            }
        }

        List<JiraIndexLabelRecord> indexRows = [];
        foreach ((string name, int count) in labelCounts)
        {
            indexRows.Add(new JiraIndexLabelRecord
            {
                Id = JiraIndexLabelRecord.GetIndex(),
                Name = name,
                IssueCount = count,
            });
        }
        indexRows.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);

        // Pass 2: junction (IssueKey, LabelId) across all four tables.
        Dictionary<string, int> labelIds = new(StringComparer.Ordinal);
        foreach (JiraIndexLabelRecord lr in JiraIndexLabelRecord.SelectList(conn))
        {
            labelIds[lr.Name] = lr.Id;
        }

        List<JiraIssueLabelRecord> junctionRows = [];
        foreach (string table in AllShapeTables)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Key, Labels FROM {table} WHERE Labels IS NOT NULL AND Labels <> ''";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string issueKey = r.GetString(0);
                string labels = r.GetString(1);
                foreach (string label in labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (labelIds.TryGetValue(label, out int labelId))
                    {
                        junctionRows.Add(new JiraIssueLabelRecord
                        {
                            Id = JiraIssueLabelRecord.GetIndex(),
                            IssueKey = issueKey,
                            LabelId = labelId,
                        });
                    }
                }
            }
        }
        junctionRows.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);

        logger.LogInformation("Labels index rebuilt: {Count} distinct labels", labelCounts.Count);
    }

    // ------------------------------------------------------------------
    // Users (multi-role across all four shape tables)
    // ------------------------------------------------------------------

    private void RebuildUsersIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_users";
            del.ExecuteNonQuery();
        }

        // VoteMover/VoteSeconder are FHIR-shape only; Assignee/Reporter
        // are on the shared base. Distinct (issue_key, user-name) pairs
        // ensure the same user doesn't double-count for multiple roles
        // on the same issue.
        using SqliteCommand insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO jira_index_users (Name, SourceTable, IssueCount)
            SELECT u.DisplayName, 'all', COUNT(DISTINCT roles.issue_key) AS IssueCount
            FROM jira_users u
            INNER JOIN (
                SELECT i.Key AS issue_key, i.Assignee     AS name FROM jira_issues i WHERE i.Assignee     IS NOT NULL AND i.Assignee     <> ''
                UNION ALL
                SELECT i.Key,              i.Reporter           FROM jira_issues i WHERE i.Reporter     IS NOT NULL AND i.Reporter     <> ''
                UNION ALL
                SELECT i.Key,              i.VoteMover          FROM jira_issues i WHERE i.VoteMover    IS NOT NULL AND i.VoteMover    <> ''
                UNION ALL
                SELECT i.Key,              i.VoteSeconder       FROM jira_issues i WHERE i.VoteSeconder IS NOT NULL AND i.VoteSeconder <> ''
                UNION ALL
                SELECT p.Key,              p.Assignee           FROM jira_pss    p WHERE p.Assignee     IS NOT NULL AND p.Assignee     <> ''
                UNION ALL
                SELECT p.Key,              p.Reporter           FROM jira_pss    p WHERE p.Reporter     IS NOT NULL AND p.Reporter     <> ''
                UNION ALL
                SELECT b.Key,              b.Assignee           FROM jira_baldef b WHERE b.Assignee     IS NOT NULL AND b.Assignee     <> ''
                UNION ALL
                SELECT b.Key,              b.Reporter           FROM jira_baldef b WHERE b.Reporter     IS NOT NULL AND b.Reporter     <> ''
                UNION ALL
                SELECT v.Key,              v.Assignee           FROM jira_ballot v WHERE v.Assignee     IS NOT NULL AND v.Assignee     <> ''
                UNION ALL
                SELECT v.Key,              v.Reporter           FROM jira_ballot v WHERE v.Reporter     IS NOT NULL AND v.Reporter     <> ''
                UNION ALL
                SELECT ip.IssueKey, u2.DisplayName
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

    // ------------------------------------------------------------------
    // In-persons (shared junction, keyed by IssueKey)
    // ------------------------------------------------------------------

    private void RebuildInPersonsIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_inpersons";
            del.ExecuteNonQuery();
        }

        using SqliteCommand insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO jira_index_inpersons (Name, SourceTable, IssueCount)
            SELECT u.DisplayName, 'all', COUNT(DISTINCT ip.IssueKey)
            FROM jira_issue_inpersons ip
            INNER JOIN jira_users u ON u.Id = ip.UserId
            WHERE u.DisplayName IS NOT NULL AND u.DisplayName <> ''
            GROUP BY u.DisplayName
            """;
        insertCmd.ExecuteNonQuery();

        using SqliteCommand countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM jira_index_inpersons";
        int count = Convert.ToInt32(countCmd.ExecuteScalar());
        logger.LogInformation("In-persons index rebuilt: {Count} distinct users", count);
    }

    // ------------------------------------------------------------------
    // Ballot targets (FHIR-side SelectedBallot only — plan §7.5)
    // ------------------------------------------------------------------

    private void RebuildBallotTargetsIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_ballot_targets";
            del.ExecuteNonQuery();
        }

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT SelectedBallot, COUNT(*)
                FROM jira_issues
                WHERE SelectedBallot IS NOT NULL AND SelectedBallot <> ''
                GROUP BY SelectedBallot
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                counts[r.GetString(0)] = r.GetInt32(1);
            }
        }

        List<JiraIndexBallotTargetRecord> rows = [];
        foreach ((string name, int count) in counts)
        {
            rows.Add(new JiraIndexBallotTargetRecord
            {
                Id = JiraIndexBallotTargetRecord.GetIndex(),
                Name = name,
                IssueCount = count,
            });
        }
        rows.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        logger.LogInformation("Ballot-targets index rebuilt: {Count} distinct targets", rows.Count);
    }

    // ------------------------------------------------------------------
    // Ballot cycles (vote-side rollup over jira_ballot — plan §7.4)
    // ------------------------------------------------------------------

    private void RebuildBallotCyclesIndex(SqliteConnection conn)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM jira_index_ballot_cycles";
            del.ExecuteNonQuery();
        }

        // Composite key: (BallotCycle ?? "", BallotCategory ?? null).
        // BallotCategory plays the role of "BallotLevel" per §7.4.
        Dictionary<(string Cycle, string? Level), JiraIndexBallotCycleRecord> byKey = new();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT BallotCycle, BallotCategory, VoteBallot, COUNT(*) AS C
            FROM jira_ballot
            GROUP BY BallotCycle, BallotCategory, VoteBallot
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string cycle = r.IsDBNull(0) ? string.Empty : r.GetString(0);
            string? level = r.IsDBNull(1) ? null : r.GetString(1);
            string? vote = r.IsDBNull(2) ? null : r.GetString(2);
            int count = r.GetInt32(3);

            (string, string?) key = (cycle, level);
            if (!byKey.TryGetValue(key, out JiraIndexBallotCycleRecord? rec))
            {
                rec = new JiraIndexBallotCycleRecord
                {
                    Id = JiraIndexBallotCycleRecord.GetIndex(),
                    BallotCycle = cycle,
                    BallotLevel = level,
                };
                byKey[key] = rec;
            }
            rec.IssueCount += count;
            string? bucket = MapVoteBucket(vote);
            switch (bucket)
            {
                case nameof(JiraIndexBallotCycleRecord.AffirmativeVotes):         rec.AffirmativeVotes         += count; break;
                case nameof(JiraIndexBallotCycleRecord.NegativeVotes):            rec.NegativeVotes            += count; break;
                case nameof(JiraIndexBallotCycleRecord.NegativeWithCommentVotes): rec.NegativeWithCommentVotes += count; break;
                case nameof(JiraIndexBallotCycleRecord.AbstainVotes):             rec.AbstainVotes             += count; break;
            }
        }

        List<JiraIndexBallotCycleRecord> rows = [.. byKey.Values];
        rows.Insert(conn, ignoreDuplicates: true, insertPrimaryKey: true);
        logger.LogInformation("Ballot-cycles index rebuilt: {Count} distinct (cycle, level) pairs", rows.Count);
    }
}
