namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Evaluates gitignore-style glob patterns against file paths to determine whether
/// a file should be excluded from indexing. Supports the same syntax as .gitignore:
/// *, **, ?, negation with !, directory patterns with trailing /, comments with #.
/// Patterns are evaluated in order — last matching pattern wins.
/// </summary>
public class IgnorePatternMatcher
{
    private readonly List<(bool IsNegation, string Pattern, bool IsDirectoryOnly)> _rules = [];

    /// <summary>
    /// Creates a matcher from configuration patterns and an optional repo-level ignore file.
    /// </summary>
    public IgnorePatternMatcher(IReadOnlyList<string>? configPatterns, string? repoIgnoreFilePath = null)
    {
        if (configPatterns is not null)
        {
            foreach (string pattern in configPatterns)
                AddPattern(pattern);
        }

        if (repoIgnoreFilePath is not null && File.Exists(repoIgnoreFilePath))
        {
            foreach (string line in File.ReadAllLines(repoIgnoreFilePath))
                AddPattern(line);
        }
    }

    /// <summary>Returns true if the given relative file path should be excluded from indexing.</summary>
    public bool IsExcluded(string relativePath)
    {
        if (_rules.Count == 0)
            return false;

        // Normalize to forward slashes
        string normalized = relativePath.Replace('\\', '/');

        // Evaluate all rules; last match wins
        bool excluded = false;
        bool matched = false;

        foreach ((bool isNegation, string pattern, bool isDirectoryOnly) in _rules)
        {
            // Directory-only patterns only match directories, but since we're checking
            // file paths, we treat them as prefix matches (the file is inside that directory).
            if (isDirectoryOnly)
            {
                // Pattern like "docs/internal/" matches anything under docs/internal/
                if (MatchesDirectoryPattern(normalized, pattern))
                {
                    excluded = !isNegation;
                    matched = true;
                }
            }
            else if (MatchesGlob(normalized, pattern))
            {
                excluded = !isNegation;
                matched = true;
            }
        }

        return matched && excluded;
    }

    private void AddPattern(string rawLine)
    {
        // Strip inline comments and whitespace
        string line = rawLine.Trim();
        if (string.IsNullOrEmpty(line) || line[0] == '#')
            return;

        bool isNegation = false;
        if (line[0] == '!')
        {
            isNegation = true;
            line = line[1..].TrimStart();
        }

        bool isDirectoryOnly = line.EndsWith('/');
        if (isDirectoryOnly)
            line = line.TrimEnd('/');

        // Normalize
        line = line.Replace('\\', '/');

        _rules.Add((isNegation, line, isDirectoryOnly));
    }

    private static bool MatchesDirectoryPattern(string filePath, string dirPattern)
    {
        // A directory pattern matches if the file path starts with the pattern as a directory prefix
        // or if the pattern matches a directory component at any depth (when using **)
        if (dirPattern.Contains("**"))
            return MatchesGlob(filePath, dirPattern + "/**") || MatchesGlob(filePath, dirPattern);

        // "docs/internal" matches "docs/internal/foo.txt" and "docs/internal/sub/bar.txt"
        if (filePath.StartsWith(dirPattern + "/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Also match if the dirPattern has no slash (matches at any depth)
        if (!dirPattern.Contains('/'))
        {
            // Match "vendor" against "src/vendor/foo.txt" — any path segment
            string prefix = dirPattern + "/";
            if (filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

            string infix = "/" + dirPattern + "/";
            if (filePath.Contains(infix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Matches a file path against a gitignore-style glob pattern.
    /// Supports *, **, and ? wildcards.
    /// </summary>
    internal static bool MatchesGlob(string path, string pattern)
    {
        // If pattern has no slash and no **, it matches against filename only
        if (!pattern.Contains('/') && !pattern.Contains("**"))
        {
            string fileName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            return SimpleGlobMatch(fileName, pattern);
        }

        // If pattern starts with **/, try matching the rest at every directory level
        if (pattern.StartsWith("**/"))
        {
            string subPattern = pattern[3..];
            // Try at root level
            if (MatchesGlob(path, subPattern))
                return true;
            // Try at every subdirectory level
            int slashIdx = path.IndexOf('/');
            while (slashIdx >= 0)
            {
                if (MatchesGlob(path[(slashIdx + 1)..], subPattern))
                    return true;
                slashIdx = path.IndexOf('/', slashIdx + 1);
            }
            return false;
        }

        // If pattern ends with /**, match the prefix against any start of path
        if (pattern.EndsWith("/**"))
        {
            string prefix = pattern[..^3];
            // Path must start with prefix (exact or as directory)
            if (SimpleGlobMatch(path, prefix) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
            // Also handle if prefix itself contains globs
            int slashIdx = path.IndexOf('/');
            while (slashIdx >= 0)
            {
                if (SimpleGlobMatch(path[..slashIdx], prefix))
                    return true;
                slashIdx = path.IndexOf('/', slashIdx + 1);
            }
            return false;
        }

        // Handle ** in the middle: split on /**/
        int doubleStarIdx = pattern.IndexOf("/**/");
        if (doubleStarIdx >= 0)
        {
            string prefix = pattern[..doubleStarIdx];
            string suffix = pattern[(doubleStarIdx + 4)..];

            // Find all positions where prefix matches path up to a /
            // Then check if suffix matches the remainder at any depth
            for (int i = 0; i <= path.Length; i++)
            {
                if (i > 0 && i < path.Length && path[i - 1] != '/')
                    continue;
                if (i == path.Length)
                    continue;

                string before = i == 0 ? "" : path[..(i - 1)];
                string after = path[i..];

                if (i == 0 || SimpleGlobMatch(before, prefix))
                {
                    if (i == 0 && !string.IsNullOrEmpty(prefix))
                        continue;
                    if (MatchesGlob(after, suffix))
                        return true;
                    // Try suffix at every remaining depth
                    int subIdx = after.IndexOf('/');
                    while (subIdx >= 0)
                    {
                        if (MatchesGlob(after[(subIdx + 1)..], suffix))
                            return true;
                        subIdx = after.IndexOf('/', subIdx + 1);
                    }
                }
            }
            return false;
        }

        // No ** — simple path matching
        return SimpleGlobMatch(path, pattern);
    }

    /// <summary>
    /// Simple glob matching supporting * (any chars except /) and ? (single char except /).
    /// </summary>
    private static bool SimpleGlobMatch(string text, string pattern)
    {
        return SimpleGlobMatchRecursive(text, 0, pattern, 0);
    }

    private static bool SimpleGlobMatchRecursive(string text, int ti, string pattern, int pi)
    {
        while (pi < pattern.Length)
        {
            if (pattern[pi] == '*')
            {
                // Skip consecutive stars
                while (pi < pattern.Length && pattern[pi] == '*')
                    pi++;

                if (pi == pattern.Length)
                    return !text[ti..].Contains('/'); // * doesn't cross /

                // Try matching rest of pattern at every position
                for (int i = ti; i <= text.Length; i++)
                {
                    if (i > ti && i - 1 < text.Length && text[i - 1] == '/')
                        break; // * doesn't cross directory boundaries

                    if (SimpleGlobMatchRecursive(text, i, pattern, pi))
                        return true;
                }

                return false;
            }

            if (ti >= text.Length)
                return false;

            if (pattern[pi] == '?')
            {
                if (text[ti] == '/')
                    return false;
                ti++;
                pi++;
            }
            else
            {
                if (!char.Equals(char.ToLowerInvariant(text[ti]), char.ToLowerInvariant(pattern[pi])))
                    return false;
                ti++;
                pi++;
            }
        }

        return ti == text.Length;
    }
}
