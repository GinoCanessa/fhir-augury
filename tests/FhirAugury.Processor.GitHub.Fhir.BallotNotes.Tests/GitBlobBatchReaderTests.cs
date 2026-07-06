using System.Diagnostics;
using System.Text;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Covers the pure <c>git cat-file --batch</c> stream parser (multi-object,
/// <c>missing</c>, empty blob, embedded newline) plus one end-to-end read against
/// a throwaway git repo to exercise the stdin-fed <see cref="GitRunner"/> path.
/// </summary>
public sealed class GitBlobBatchReaderTests : IDisposable
{
    private readonly string _clone;

    public GitBlobBatchReaderTests()
    {
        _clone = Path.Combine(Path.GetTempPath(), "blobbatch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_clone);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_clone);

    // ── pure stream-parser tests ─────────────────────────────────────

    [Fact]
    public void ParseBatchStream_parses_multiple_objects_with_shas_and_content()
    {
        byte[] stream =
        [
            .. Found("1111111111111111111111111111111111111111", "blob", Utf8("hello")),
            .. Found("2222222222222222222222222222222222222222", "blob", Utf8("world!")),
        ];

        IReadOnlyList<BlobResult> records = GitBlobBatchReader.ParseBatchStream(stream);

        Assert.Equal(2, records.Count);
        Assert.True(records[0].Found);
        Assert.Equal("1111111111111111111111111111111111111111", records[0].BlobSha);
        Assert.Equal("hello", records[0].Text);
        Assert.True(records[1].Found);
        Assert.Equal("world!", records[1].Text);
    }

    [Fact]
    public void ParseBatchStream_handles_missing_record()
    {
        byte[] stream =
        [
            .. Utf8("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef missing\n"),
            .. Found("3333333333333333333333333333333333333333", "blob", Utf8("after")),
        ];

        IReadOnlyList<BlobResult> records = GitBlobBatchReader.ParseBatchStream(stream);

        Assert.Equal(2, records.Count);
        Assert.False(records[0].Found);
        Assert.Null(records[0].BlobSha);
        Assert.Equal(string.Empty, records[0].Text);
        // Parser realigns on the next object after a missing line.
        Assert.True(records[1].Found);
        Assert.Equal("after", records[1].Text);
    }

    [Fact]
    public void ParseBatchStream_handles_empty_blob()
    {
        byte[] stream = Found("4444444444444444444444444444444444444444", "blob", []);

        IReadOnlyList<BlobResult> records = GitBlobBatchReader.ParseBatchStream(stream);

        BlobResult only = Assert.Single(records);
        Assert.True(only.Found);
        Assert.Empty(only.Content);
        Assert.Equal(string.Empty, only.Text);
    }

    [Fact]
    public void ParseBatchStream_preserves_embedded_newlines_by_length()
    {
        // Content itself contains newlines; the parser must use the declared size,
        // not scan for the next '\n'.
        string body = "line1\nline2\nline3";
        byte[] stream =
        [
            .. Found("5555555555555555555555555555555555555555", "blob", Utf8(body)),
            .. Found("6666666666666666666666666666666666666666", "blob", Utf8("next")),
        ];

        IReadOnlyList<BlobResult> records = GitBlobBatchReader.ParseBatchStream(stream);

        Assert.Equal(2, records.Count);
        Assert.Equal(body, records[0].Text);
        Assert.Equal("next", records[1].Text);
    }

    // ── integration read against a real throwaway repo ───────────────

    [Fact]
    public async Task ReadAsync_reads_multiple_blobs_and_reports_missing_in_one_call()
    {
        await GitInitAsync();
        await File.WriteAllTextAsync(Path.Combine(_clone, "a.txt"), "content A");
        await File.WriteAllTextAsync(Path.Combine(_clone, "b.txt"), "content B\nwith newline");
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "seed");

        string[] specs = ["HEAD:a.txt", "HEAD:b.txt", "HEAD:does-not-exist.txt"];
        IReadOnlyDictionary<string, BlobResult> map = await GitBlobBatchReader.ReadAsync(_clone, specs);

        Assert.Equal("content A", map["HEAD:a.txt"].Text);
        Assert.Equal("content B\nwith newline", map["HEAD:b.txt"].Text);
        Assert.True(map["HEAD:a.txt"].Found);
        Assert.NotNull(map["HEAD:a.txt"].BlobSha);
        Assert.False(map["HEAD:does-not-exist.txt"].Found);
    }

    [Fact]
    public async Task ReadAsync_returns_empty_for_no_specs()
    {
        IReadOnlyDictionary<string, BlobResult> map = await GitBlobBatchReader.ReadAsync(_clone, []);
        Assert.Empty(map);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Builds one found batch record: "&lt;sha&gt; &lt;type&gt; &lt;size&gt;\n" + content + "\n".</summary>
    private static byte[] Found(string sha, string type, byte[] content)
    {
        byte[] header = Utf8($"{sha} {type} {content.Length}\n");
        return [.. header, .. content, (byte)'\n'];
    }

    private async Task GitInitAsync()
    {
        await Git("init", "-q");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "user.name", "Test");
        await Git("config", "commit.gpgsign", "false");
    }

    private async Task<string> Git(params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = _clone,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
        return stdout;
    }
}
