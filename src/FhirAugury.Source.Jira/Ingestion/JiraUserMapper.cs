using FhirAugury.Common.Text;
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

        bool hasAccountUsername = username is not null;

        // Use username as primary key; fall back to displayName as synthetic username
        string effectiveUsername = username ?? displayName!;

        // Check in-memory cache first
        if (_usernameToId.TryGetValue(effectiveUsername, out int cachedId))
        {
            JiraUserRecord? cached = JiraUserRecord.SelectSingle(conn, Id: cachedId);
            if (cached is not null)
            {
                ApplyObservation(
                    conn,
                    cached,
                    hasAccountUsername,
                    displayName);
                return cachedId;
            }

            _usernameToId.Remove(effectiveUsername);
        }

        // Check database
        JiraUserRecord? existing = JiraUserRecord.SelectSingle(conn, Username: effectiveUsername);
        if (existing is not null)
        {
            ApplyObservation(
                conn,
                existing,
                hasAccountUsername,
                displayName);

            _usernameToId[effectiveUsername] = existing.Id;
            return existing.Id;
        }

        // Insert new user
        JiraUserRecord newUser = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = effectiveUsername,
            DisplayName = displayName ?? effectiveUsername,
            HasAccountUsername = hasAccountUsername,
            HasExplicitDisplayName = displayName is not null
                && PublicDisplayNamePolicy.Normalize(
                    displayName,
                    hasAccountUsername ? effectiveUsername : null) is not null,
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
        {
            JiraUserRecord? cached = JiraUserRecord.SelectSingle(conn, Id: cachedId);
            if (cached is not null)
            {
                ApplyObservation(
                    conn,
                    cached,
                    hasAccountUsername: false,
                    displayName);
                return cachedId;
            }

            _displayNameToId.Remove(displayName);
        }

        // Reuse an existing jira_users row with this DisplayName if present, to
        // avoid inserting a synthetic (Username=displayName) duplicate when a
        // real account with the same DisplayName already exists.
        List<JiraUserRecord> matches = JiraUserRecord.SelectList(conn, DisplayName: displayName);
        if (matches.Count > 0)
        {
            JiraUserRecord chosen = matches.OrderBy(u => u.Id).First();
            ApplyObservation(
                conn,
                chosen,
                hasAccountUsername: false,
                displayName);
            _displayNameToId[displayName] = chosen.Id;
            _usernameToId[chosen.Username] = chosen.Id;
            return chosen.Id;
        }

        return ResolveUser(conn, null, displayName);
    }

    /// <summary>Clears the in-memory cache. Call between full ingestion runs.</summary>
    public void ClearCache()
    {
        _usernameToId.Clear();
        _displayNameToId.Clear();
    }

    private void ApplyObservation(
        SqliteConnection conn,
        JiraUserRecord user,
        bool hasAccountUsername,
        string? displayName)
    {
        bool changed = false;
        bool accountOriginUpgraded =
            hasAccountUsername && !user.HasAccountUsername;
        if (accountOriginUpgraded)
        {
            user.HasAccountUsername = true;
            changed = true;
        }

        if (displayName is not null)
        {
            if (!string.Equals(
                    user.DisplayName,
                    displayName,
                    StringComparison.Ordinal))
            {
                if (_displayNameToId.TryGetValue(
                        user.DisplayName,
                        out int mappedId)
                    && mappedId == user.Id)
                {
                    _displayNameToId.Remove(user.DisplayName);
                }

                user.DisplayName = displayName;
                changed = true;
            }

            bool isEligible = PublicDisplayNamePolicy.Normalize(
                displayName,
                user.HasAccountUsername ? user.Username : null) is not null;
            if (user.HasExplicitDisplayName != isEligible)
            {
                user.HasExplicitDisplayName = isEligible;
                changed = true;
            }

            _displayNameToId[displayName] = user.Id;
        }
        else if (accountOriginUpgraded && user.HasExplicitDisplayName
                 && PublicDisplayNamePolicy.Normalize(
                     user.DisplayName,
                     user.Username) is null)
        {
            user.HasExplicitDisplayName = false;
            changed = true;
        }

        if (changed)
        {
            JiraUserRecord.Update(conn, user);
        }
    }
}
