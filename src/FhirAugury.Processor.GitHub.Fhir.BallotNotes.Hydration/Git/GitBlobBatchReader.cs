using System.Globalization;
using System.Text;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

/// <summary>
/// The result of resolving one <c>git cat-file --batch</c> spec: the resolved
/// object SHA, its raw content bytes, and whether it was found. Content is kept
/// as bytes because blobs are length-delimited and may be binary; callers that
/// want text use <see cref="Text"/>.
/// </summary>
public readonly record struct BlobResult(string? BlobSha, byte[] Content, bool Found)
{
    /// <summary>
    /// Content decoded as UTF-8 (empty string when not found). A single leading
    /// UTF-8 byte-order mark is stripped so this mirrors <c>GitRunner</c>'s
    /// <see cref="StreamReader"/>-based <c>git show</c> decode byte-for-byte (the
    /// reader strips a detected BOM preamble; <see cref="Encoding.UTF8"/>'s
    /// <c>GetString</c> would otherwise keep it as <c>U+FEFF</c>).
    /// </summary>
    public string Text
    {
        get
        {
            if (!Found) return string.Empty;
            ReadOnlySpan<byte> bytes = Content;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..];
            }
            return Encoding.UTF8.GetString(bytes);
        }
    }
}

/// <summary>
/// Reads many git objects in a single <c>git cat-file --batch</c> invocation,
/// replacing the per-file <c>git show {sha}:{path}</c> spawns. Each spec is a
/// 40-hex blob SHA <b>or</b> a <c>rev:path</c> string (both accepted by
/// <c>cat-file --batch</c>). Results are keyed by the exact input spec so callers
/// can also dedupe/memoize by the resolved <see cref="BlobResult.BlobSha"/>.
/// </summary>
public static class GitBlobBatchReader
{
    /// <summary>
    /// Reads every spec in <paramref name="specs"/> in one batch. Duplicate and
    /// empty specs are collapsed. Missing objects come back as
    /// <c>BlobResult(Found: false)</c> rather than throwing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, BlobResult>> ReadAsync(
        string clonePath,
        IReadOnlyCollection<string> specs,
        CancellationToken ct = default)
    {
        // Preserve first-seen order while de-duplicating; batch output is
        // positional, so the request order is the response order.
        List<string> ordered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string spec in specs)
        {
            if (!string.IsNullOrEmpty(spec) && seen.Add(spec))
            {
                ordered.Add(spec);
            }
        }

        Dictionary<string, BlobResult> map = new(StringComparer.Ordinal);
        if (ordered.Count == 0) return map;

        byte[] stdout = await GitRunner.RunWithInputAsync(
            clonePath, ["cat-file", "--batch"], ordered, ct).ConfigureAwait(false);

        IReadOnlyList<BlobResult> records = ParseBatchStream(stdout);
        for (int i = 0; i < ordered.Count; i++)
        {
            map[ordered[i]] = i < records.Count ? records[i] : new BlobResult(null, [], false);
        }
        return map;
    }

    /// <summary>
    /// Parses a <c>git cat-file --batch</c> output stream into positional records.
    /// For each requested object git emits either
    /// <c>&lt;sha&gt; &lt;type&gt; &lt;size&gt;\n&lt;content bytes&gt;\n</c> when found, or
    /// <c>&lt;spec&gt; missing\n</c> when not. The content is read by its declared byte
    /// length, so embedded newlines and binary bytes are preserved.
    /// </summary>
    internal static IReadOnlyList<BlobResult> ParseBatchStream(byte[] stream)
    {
        List<BlobResult> records = [];
        int pos = 0;

        while (pos < stream.Length)
        {
            int newline = Array.IndexOf(stream, (byte)'\n', pos);
            if (newline < 0) break;

            string header = Encoding.UTF8.GetString(stream, pos, newline - pos);
            pos = newline + 1;
            if (header.Length == 0) continue;

            // "<spec...> missing" — the echoed spec may itself contain spaces.
            if (header.EndsWith(" missing", StringComparison.Ordinal))
            {
                records.Add(new BlobResult(null, [], false));
                continue;
            }

            // Found header: "<sha> <type> <size>". SHA/type/size never contain
            // spaces, so a plain split is safe here.
            string[] fields = header.Split(' ');
            if (fields.Length < 3
                || !long.TryParse(fields[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)
                || size < 0
                || pos + size > stream.Length)
            {
                // Malformed or truncated stream: stop rather than misalign.
                break;
            }

            byte[] content = new byte[size];
            Array.Copy(stream, pos, content, 0, (int)size);
            pos += (int)size;

            // git writes a trailing '\n' after each object's content.
            if (pos < stream.Length && stream[pos] == (byte)'\n') pos++;

            records.Add(new BlobResult(fields[0], content, true));
        }

        return records;
    }
}
