using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.FhirSpecReview.Report;

/// <summary>
/// Reads a review SQLite DB and emits a per-workgroup static HTML site: an
/// <c>index.html</c> with provenance + roll-up tables, and one
/// <c>&lt;wg&gt;.html</c> per work group with detail rows. Read-only consumer.
/// </summary>
internal sealed class ReportEmitter
{
    private const string UnassignedKey = "__unassigned__";
    private const string UnassignedLabel = "Unassigned";

    private readonly string _reviewDbPath;

    public ReportEmitter(string reviewDbPath)
    {
        _reviewDbPath = reviewDbPath;
    }

    private sealed record PageRow(
        long Id, int? ArtifactId, string PageFileName, string? WgCode, string? WgName,
        string? MaturityLabel, string? StandardsStatus,
        long ConformantTotal, long NonConformantTotal, long RemovedCount,
        long UnknownCount, long TypoCount, long ImagesCount,
        long PriorVersionCount, long ZulipCount, long ConfluenceCount);

    private sealed record ArtifactRow(
        string FhirId, string Name, string? ArtifactType, string? WgCode, string? WgName,
        bool? SourceDirExists, bool? SourceDefExists, string? IntroPage, string? NotesPage,
        string? Status, string? StandardsStatus);

    private sealed record RunInfo(string Repo, string BuildVersion, string BaselineRelease, string RunAt);

    public void Emit(string outDir)
    {
        Directory.CreateDirectory(outDir);

        using SqliteConnection conn = new(new SqliteConnectionStringBuilder
        {
            DataSource = _reviewDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        conn.Open();

        RunInfo? run = ReadRun(conn);
        List<PageRow> pages = ReadPages(conn);
        List<ArtifactRow> artifacts = ReadArtifacts(conn);

        // group keys (code) → display name
        Dictionary<string, string> wgNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (PageRow p in pages) RegisterWg(wgNames, p.WgCode, p.WgName);
        foreach (ArtifactRow a in artifacts) RegisterWg(wgNames, a.WgCode, a.WgName);

        // Removed-baseline entities are recorded with no work group; surface them
        // under an Unassigned bucket so they are never dropped from the report.
        if (HasRemovedBaselineEntities(conn) || HasDuplicateArtifactKeys(conn)) wgNames.TryAdd(UnassignedKey, UnassignedLabel);

        if (wgNames.Count == 0) wgNames[UnassignedKey] = UnassignedLabel;

        ILookup<string, PageRow> pagesByWg = pages.ToLookup(p => WgKey(p.WgCode));
        ILookup<string, ArtifactRow> artifactsByWg = artifacts.ToLookup(a => WgKey(a.WgCode));

        WriteIndex(outDir, run, wgNames, pagesByWg, artifactsByWg);

        foreach ((string key, string name) in wgNames.OrderBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase))
        {
            WriteWorkGroupPage(conn, outDir, run, key, name, pagesByWg[key].ToList(), artifactsByWg[key].ToList());
        }
    }

    private static void RegisterWg(Dictionary<string, string> map, string? code, string? name)
    {
        string key = WgKey(code);
        if (!map.ContainsKey(key))
        {
            map[key] = key == UnassignedKey ? UnassignedLabel : (name ?? code ?? UnassignedLabel);
        }
    }

    private static string WgKey(string? code) => string.IsNullOrWhiteSpace(code) ? UnassignedKey : code;

    private static string WgFileName(string key) =>
        key == UnassignedKey ? "unassigned.html" : SanitizeFileName(key) + ".html";

    private static string SanitizeFileName(string value)
    {
        StringBuilder sb = new(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }
        return sb.ToString().ToLowerInvariant();
    }

    private void WriteIndex(
        string outDir, RunInfo? run, Dictionary<string, string> wgNames,
        ILookup<string, PageRow> pagesByWg, ILookup<string, ArtifactRow> artifactsByWg)
    {
        StringBuilder body = new();
        body.Append("<h1>FHIR Spec Review</h1>");
        body.Append(RenderProvenance(run));

        body.Append("<h2>Work groups</h2>");
        body.Append("<table><thead><tr>")
            .Append("<th>Work group</th><th>Pages</th><th>Artifacts</th>")
            .Append("<th>Conformant</th><th>Non-conformant</th><th>Removed refs</th>")
            .Append("<th>Unknown</th><th>Typos</th><th>Image issues</th>")
            .Append("</tr></thead><tbody>");

        foreach ((string key, string name) in wgNames.OrderBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase))
        {
            List<PageRow> wgPages = pagesByWg[key].ToList();
            List<ArtifactRow> wgArtifacts = artifactsByWg[key].ToList();
            body.Append("<tr>")
                .Append($"<td><a href=\"{Enc(WgFileName(key))}\">{Enc(name)}</a></td>")
                .Append($"<td>{wgPages.Count}</td>")
                .Append($"<td>{wgArtifacts.Count}</td>")
                .Append($"<td>{wgPages.Sum(p => p.ConformantTotal)}</td>")
                .Append($"<td>{wgPages.Sum(p => p.NonConformantTotal)}</td>")
                .Append($"<td>{wgPages.Sum(p => p.RemovedCount)}</td>")
                .Append($"<td>{wgPages.Sum(p => p.UnknownCount)}</td>")
                .Append($"<td>{wgPages.Sum(p => p.TypoCount)}</td>")
                .Append($"<td>{wgPages.Sum(p => p.ImagesCount)}</td>")
                .Append("</tr>");
        }
        body.Append("</tbody></table>");

        File.WriteAllText(Path.Combine(outDir, "index.html"), Page("FHIR Spec Review", body.ToString()));
    }

    private void WriteWorkGroupPage(
        SqliteConnection conn, string outDir, RunInfo? run, string key, string name,
        List<PageRow> pages, List<ArtifactRow> artifacts)
    {
        StringBuilder body = new();
        body.Append($"<p><a href=\"index.html\">&larr; All work groups</a></p>");
        body.Append($"<h1>{Enc(name)}</h1>");
        body.Append(RenderProvenance(run));

        // Artifacts
        body.Append($"<h2>Artifacts ({artifacts.Count})</h2>");
        if (artifacts.Count > 0)
        {
            body.Append("<table><thead><tr><th>FHIR id</th><th>Name</th><th>Type</th>")
                .Append("<th>Source dir</th><th>Definition</th><th>Intro</th><th>Notes</th>")
                .Append("<th>Status</th><th>Standards</th></tr></thead><tbody>");
            foreach (ArtifactRow a in artifacts.OrderBy(a => a.FhirId, StringComparer.OrdinalIgnoreCase))
            {
                body.Append("<tr>")
                    .Append($"<td>{Enc(a.FhirId)}</td><td>{Enc(a.Name)}</td><td>{Enc(a.ArtifactType)}</td>")
                    .Append($"<td>{Bool(a.SourceDirExists)}</td><td>{Bool(a.SourceDefExists)}</td>")
                    .Append($"<td>{Enc(a.IntroPage)}</td><td>{Enc(a.NotesPage)}</td>")
                    .Append($"<td>{Enc(a.Status)}</td><td>{Enc(a.StandardsStatus)}</td></tr>");
            }
            body.Append("</tbody></table>");
        }

        // Pages
        body.Append($"<h2>Pages ({pages.Count})</h2>");
        if (pages.Count > 0)
        {
            body.Append("<table><thead><tr><th>Page</th><th>Maturity</th><th>Standards</th>")
                .Append("<th>Conformant</th><th>Non-conf.</th><th>Removed</th><th>Unknown</th>")
                .Append("<th>Typos</th><th>Images</th><th>Prior ver.</th><th>Zulip</th><th>Confluence</th></tr></thead><tbody>");
            foreach (PageRow p in pages.OrderBy(p => p.PageFileName, StringComparer.OrdinalIgnoreCase))
            {
                body.Append("<tr>")
                    .Append($"<td>{Enc(p.PageFileName)}</td><td>{Enc(p.MaturityLabel)}</td><td>{Enc(p.StandardsStatus)}</td>")
                    .Append($"<td>{p.ConformantTotal}</td><td>{p.NonConformantTotal}</td><td>{p.RemovedCount}</td>")
                    .Append($"<td>{p.UnknownCount}</td><td>{p.TypoCount}</td><td>{p.ImagesCount}</td>")
                    .Append($"<td>{p.PriorVersionCount}</td><td>{p.ZulipCount}</td><td>{p.ConfluenceCount}</td></tr>");
            }
            body.Append("</tbody></table>");
        }

        HashSet<long> pageIds = pages.Select(p => p.Id).ToHashSet();

        body.Append(RenderDetailTable(conn, pageIds,
            "Removed FHIR artifact references", ["Page", "Word", "Class"],
            "SELECT p.PageFileName, r.Word, r.ArtifactClass FROM page_removed_fhir_artifacts r JOIN pages p ON p.Id = r.PageId",
            r => [r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)]));

        body.Append(RenderDetailTable(conn, pageIds,
            "Unknown words & typos", ["Page", "Word", "Typo", "Correction"],
            "SELECT p.PageFileName, u.Word, u.IsTypo, u.Correction FROM page_unknown_words u JOIN pages p ON p.Id = u.PageId",
            r => [r.GetString(0), r.GetString(1), r.GetInt64(2) != 0 ? "yes" : "no", r.IsDBNull(3) ? "" : r.GetString(3)]));

        body.Append(RenderDetailTable(conn, pageIds,
            "Image issues", ["Page", "Source", "Missing alt", "Not in figure"],
            "SELECT p.PageFileName, i.Source, i.MissingAlt, i.NotInFigure FROM page_images i JOIN pages p ON p.Id = i.PageId",
            r => [r.GetString(0), r.GetString(1), r.GetInt64(2) != 0 ? "yes" : "no", r.GetInt64(3) != 0 ? "yes" : "no"]));

        // Removed baseline entities for this work group (engine records null WG → Unassigned bucket)
        if (key == UnassignedKey)
        {
            body.Append(RenderRemovedBaseline(conn));
            body.Append(RenderDuplicateArtifactKeys(conn));
        }

        File.WriteAllText(Path.Combine(outDir, WgFileName(key)), Page(name, body.ToString()));
    }

    private string RenderDetailTable(
        SqliteConnection conn, HashSet<long> pageIds, string title, string[] headers,
        string sql, Func<SqliteDataReader, string[]> project)
    {
        List<string[]> rows = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // PageFileName is column 0; we filter by joining page id via a second lookup.
                rows.Add(project(reader));
            }
        }
        // Filter rows whose page belongs to this WG by re-querying page names in scope.
        HashSet<string> scopedPages = ScopedPageNames(conn, pageIds);
        List<string[]> scoped = rows.Where(r => scopedPages.Contains(r[0])).ToList();
        if (scoped.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.Append($"<h3>{Enc(title)} ({scoped.Count})</h3>");
        sb.Append("<table><thead><tr>");
        foreach (string h in headers) sb.Append($"<th>{Enc(h)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (string[] row in scoped)
        {
            sb.Append("<tr>");
            foreach (string cell in row) sb.Append($"<td>{Enc(cell)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static HashSet<string> ScopedPageNames(SqliteConnection conn, HashSet<long> pageIds)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        if (pageIds.Count == 0) return names;
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, PageFileName FROM pages";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (pageIds.Contains(reader.GetInt64(0))) names.Add(reader.GetString(1));
        }
        return names;
    }

    private string RenderRemovedBaseline(SqliteConnection conn)
    {
        List<string[]> rows = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT EntityKind, Name, BaselineRelease FROM removed_baseline_entities ORDER BY EntityKind, Name";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2)]);
            }
        }
        if (rows.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.Append($"<h3>Removed since baseline ({rows.Count})</h3>");
        sb.Append("<table><thead><tr><th>Kind</th><th>Name</th><th>Baseline</th></tr></thead><tbody>");
        foreach (string[] row in rows)
        {
            sb.Append($"<tr><td>{Enc(row[0])}</td><td>{Enc(row[1])}</td><td>{Enc(row[2])}</td></tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private string RenderDuplicateArtifactKeys(SqliteConnection conn)
    {
        List<string[]> rows = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT FhirId, KeptName, DuplicateName, KeptCanonicalUrl, DuplicateCanonicalUrl, ArtifactType FROM duplicate_artifact_keys ORDER BY FhirId";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add([
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                ]);
            }
        }
        if (rows.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.Append($"<h3>Duplicate artifact keys ({rows.Count})</h3>");
        sb.Append("<table><thead><tr><th>FHIR id</th><th>Kept</th><th>Skipped</th><th>Kept URL</th><th>Skipped URL</th><th>Type</th></tr></thead><tbody>");
        foreach (string[] row in rows)
        {
            sb.Append("<tr>");
            foreach (string cell in row) sb.Append($"<td>{Enc(cell)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static string RenderProvenance(RunInfo? run)
    {
        if (run is null) return "<p class=\"prov\">No review run recorded.</p>";
        return "<p class=\"prov\">"
            + $"Repository <strong>{Enc(run.Repo)}</strong> &middot; "
            + $"Build <strong>{Enc(run.BuildVersion)}</strong> &middot; "
            + $"Baseline <strong>{Enc(run.BaselineRelease)}</strong> &middot; "
            + $"Run at {Enc(run.RunAt)}</p>";
    }

    private static bool HasRemovedBaselineEntities(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM removed_baseline_entities LIMIT 1";
        return cmd.ExecuteScalar() is not null;
    }

    private static bool HasDuplicateArtifactKeys(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM duplicate_artifact_keys LIMIT 1";
        return cmd.ExecuteScalar() is not null;
    }

    private static RunInfo? ReadRun(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RepoFullName, BuildVersion, BaselineRelease, RunAt FROM review_runs ORDER BY Id DESC LIMIT 1";
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read()
            ? new RunInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static List<PageRow> ReadPages(SqliteConnection conn)
    {
        List<PageRow> pages = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ArtifactId, PageFileName, ResponsibleWorkGroupCode, ResponsibleWorkGroupName,
                   MaturityLabel, StandardsStatus,
                   COALESCE(ConformantTotalCount,0), COALESCE(NonConformantTotalCount,0),
                   COALESCE(RemovedFhirArtifactCount,0), COALESCE(UnknownWordCount,0),
                   COALESCE(TypoWordCount,0), COALESCE(ImagesWithIssuesCount,0),
                   COALESCE(PriorFhirVersionReferenceCount,0), COALESCE(ZulipLinkCount,0),
                   COALESCE(ConfluenceLinkCount,0)
            FROM pages
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            pages.Add(new PageRow(
                r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11),
                r.GetInt64(12), r.GetInt64(13), r.GetInt64(14), r.GetInt64(15)));
        }
        return pages;
    }

    private static List<ArtifactRow> ReadArtifacts(SqliteConnection conn)
    {
        List<ArtifactRow> artifacts = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FhirId, Name, ArtifactType, ResponsibleWorkGroupCode, ResponsibleWorkGroupName,
                   SourceDirectoryExists, SourceDefinitionExists, IntroPageFilename, NotesPageFilename,
                   Status, StandardsStatus
            FROM artifacts
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            artifacts.Add(new ArtifactRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt64(5) != 0, r.IsDBNull(6) ? null : r.GetInt64(6) != 0,
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return artifacts;
    }

    private static string Bool(bool? value) => value is null ? "" : value.Value ? "yes" : "no";

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{Enc(title)}}</title>
        <style>
        :root { color-scheme: light dark; }
        body { font-family: system-ui, sans-serif; margin: 1.5rem; line-height: 1.4; }
        h1 { font-size: 1.5rem; } h2 { font-size: 1.2rem; margin-top: 1.5rem; }
        h3 { font-size: 1.05rem; margin-top: 1.2rem; }
        .prov { color: gray; font-size: 0.9rem; }
        table { border-collapse: collapse; width: 100%; margin: 0.5rem 0 1rem; font-size: 0.9rem; }
        th, td { border: 1px solid #8884; padding: 0.3rem 0.5rem; text-align: left; }
        th { background: #8881; position: sticky; top: 0; }
        tr:nth-child(even) td { background: #8880; }
        a { color: #3b82f6; }
        @media (prefers-color-scheme: dark) { a { color: #60a5fa; } }
        </style>
        </head>
        <body>
        {{body}}
        </body>
        </html>
        """;
}
