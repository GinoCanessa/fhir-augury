using Microsoft.Data.Sqlite;

namespace FhirAugury.Database;

/// <summary>Creates and manages FTS5 virtual tables and content-sync triggers.</summary>
public static class FtsSetup
{
    /// <summary>Creates FTS5 tables and triggers for Jira issues and comments.</summary>
    public static void CreateJiraFts(SqliteConnection connection)
    {
        CreateJiraIssuesFts(connection);
        CreateJiraCommentsFts(connection);
    }

    private static void CreateJiraIssuesFts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();

        // FTS5 virtual table backed by jira_issues content table
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS jira_issues_fts USING fts5(
                Key,
                Title,
                Description,
                Summary,
                ResolutionDescription,
                Labels,
                Specification,
                WorkGroup,
                RelatedArtifacts,
                content='jira_issues',
                content_rowid='Id'
            );
            """;
        cmd.ExecuteNonQuery();

        // INSERT trigger
        cmd.CommandText = """
            CREATE TRIGGER IF NOT EXISTS jira_issues_ai AFTER INSERT ON jira_issues BEGIN
                INSERT INTO jira_issues_fts(rowid, Key, Title, Description, Summary, ResolutionDescription, Labels, Specification, WorkGroup, RelatedArtifacts)
                VALUES (new.Id, new.Key, new.Title, new.Description, new.Summary, new.ResolutionDescription, new.Labels, new.Specification, new.WorkGroup, new.RelatedArtifacts);
            END;
            """;
        cmd.ExecuteNonQuery();

        // DELETE trigger
        cmd.CommandText = """
            CREATE TRIGGER IF NOT EXISTS jira_issues_ad AFTER DELETE ON jira_issues BEGIN
                INSERT INTO jira_issues_fts(jira_issues_fts, rowid, Key, Title, Description, Summary, ResolutionDescription, Labels, Specification, WorkGroup, RelatedArtifacts)
                VALUES ('delete', old.Id, old.Key, old.Title, old.Description, old.Summary, old.ResolutionDescription, old.Labels, old.Specification, old.WorkGroup, old.RelatedArtifacts);
            END;
            """;
        cmd.ExecuteNonQuery();

        // UPDATE trigger
        cmd.CommandText = """
            CREATE TRIGGER IF NOT EXISTS jira_issues_au AFTER UPDATE ON jira_issues BEGIN
                INSERT INTO jira_issues_fts(jira_issues_fts, rowid, Key, Title, Description, Summary, ResolutionDescription, Labels, Specification, WorkGroup, RelatedArtifacts)
                VALUES ('delete', old.Id, old.Key, old.Title, old.Description, old.Summary, old.ResolutionDescription, old.Labels, old.Specification, old.WorkGroup, old.RelatedArtifacts);
                INSERT INTO jira_issues_fts(rowid, Key, Title, Description, Summary, ResolutionDescription, Labels, Specification, WorkGroup, RelatedArtifacts)
                VALUES (new.Id, new.Key, new.Title, new.Description, new.Summary, new.ResolutionDescription, new.Labels, new.Specification, new.WorkGroup, new.RelatedArtifacts);
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CreateJiraCommentsFts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();

        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS jira_comments_fts USING fts5(
                IssueKey,
                Author,
                Body,
                content='jira_comments',
                content_rowid='Id'
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TRIGGER IF NOT EXISTS jira_comments_ai AFTER INSERT ON jira_comments BEGIN
                INSERT INTO jira_comments_fts(rowid, IssueKey, Author, Body)
                VALUES (new.Id, new.IssueKey, new.Author, new.Body);
            END;
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TRIGGER IF NOT EXISTS jira_comments_ad AFTER DELETE ON jira_comments BEGIN
                INSERT INTO jira_comments_fts(jira_comments_fts, rowid, IssueKey, Author, Body)
                VALUES ('delete', old.Id, old.IssueKey, old.Author, old.Body);
            END;
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TRIGGER IF NOT EXISTS jira_comments_au AFTER UPDATE ON jira_comments BEGIN
                INSERT INTO jira_comments_fts(jira_comments_fts, rowid, IssueKey, Author, Body)
                VALUES ('delete', old.Id, old.IssueKey, old.Author, old.Body);
                INSERT INTO jira_comments_fts(rowid, IssueKey, Author, Body)
                VALUES (new.Id, new.IssueKey, new.Author, new.Body);
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rebuilds all Jira FTS5 tables from content tables.</summary>
    public static void RebuildJiraFts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();

        cmd.CommandText = "INSERT INTO jira_issues_fts(jira_issues_fts) VALUES ('rebuild');";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO jira_comments_fts(jira_comments_fts) VALUES ('rebuild');";
        cmd.ExecuteNonQuery();
    }
}
