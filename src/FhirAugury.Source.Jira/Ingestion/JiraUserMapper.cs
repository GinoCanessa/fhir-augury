using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Resolves Jira user references to JiraUserRecord IDs.
/// Maintains an in-memory cache during ingestion to minimize DB lookups.
/// </summary>
public class JiraUserMapper
{
    private readonly Dictionary<string, int> _usernameToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _displayNameToId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a user reference to a jira_users row ID.
    /// Inserts a new row if the username is not yet known; updates display name if changed.
    /// Returns null if both username and displayName are null/empty.
    /// </summary>
    public int? ResolveUser(SqliteConnection conn, string? username, string? displayName)
    {
        username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        if (username is null && displayName is null)
            return null;

        // Use username as primary key; fall back to displayName as synthetic username
        string effectiveUsername = username ?? displayName!;

        // Check in-memory cache first
        if (_usernameToId.TryGetValue(effectiveUsername, out int cachedId))
        {
            // Even on cache hit, update display name if we now have a better one
            if (displayName is not null && !_displayNameToId.ContainsKey(displayName))
            {
                _displayNameToId[displayName] = cachedId;
            }

            return cachedId;
        }

        // Check database
        JiraUserRecord? existing = JiraUserRecord.SelectSingle(conn, Username: effectiveUsername);
        if (existing is not null)
        {
            // Update display name if changed and we have a real one
            if (displayName is not null && existing.DisplayName != displayName)
            {
                existing.DisplayName = displayName;
                JiraUserRecord.Update(conn, existing);
            }

            _usernameToId[effectiveUsername] = existing.Id;
            if (displayName is not null)
                _displayNameToId[displayName] = existing.Id;
            return existing.Id;
        }

        // Insert new user
        JiraUserRecord newUser = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = effectiveUsername,
            DisplayName = displayName ?? effectiveUsername,
        };
        JiraUserRecord.Insert(conn, newUser, ignoreDuplicates: true);

        _usernameToId[effectiveUsername] = newUser.Id;
        if (displayName is not null)
            _displayNameToId[displayName] = newUser.Id;

        return newUser.Id;
    }

    /// <summary>
    /// Resolves a user by display name only (e.g., vote mover/seconder).
    /// Checks display name cache first, then falls through to full resolution.
    /// </summary>
    public int? ResolveByDisplayName(SqliteConnection conn, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        displayName = displayName.Trim();

        if (_displayNameToId.TryGetValue(displayName, out int cachedId))
            return cachedId;

        return ResolveUser(conn, null, displayName);
    }

    /// <summary>Clears the in-memory cache. Call between full ingestion runs.</summary>
    public void ClearCache()
    {
        _usernameToId.Clear();
        _displayNameToId.Clear();
    }
}
