using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;

public sealed record BallotNotesAuthoringCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string OperationToken);

public sealed record BallotNotesAuthoringCommandResult(
    int ExitCode,
    string StandardOutputTail,
    string StandardErrorTail,
    bool Canceled);

public interface IBallotNotesAuthoringCommandRunner
{
    Task<BallotNotesAuthoringCommandResult> RunAsync(
        BallotNotesAuthoringCommand command,
        CancellationToken ct);
}

public sealed class BallotNotesAuthoringHandler(
    AuthoringRunStore authoringStore,
    IBallotNotesAuthoringCommandRunner runner,
    IOptions<BallotNotesServiceOptions> optionsAccessor)
    : IAuthoringWorkItemHandler<BallotNotesAuthoringWorkItem>
{
    private readonly BallotNotesServiceOptions _options =
        optionsAccessor.Value;

    public async Task<AuthoringWorkResult> ProcessAsync(
        BallotNotesAuthoringWorkItem item,
        AuthoringQueueClaim claim,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.RunItem.AcceptedReceiptId))
        {
            AuthoringRunItemRecord current =
                (await authoringStore.GetRunItemsAsync(
                    item.RunItem.RunId,
                    ct)).Single(value => value.Id == item.RunItem.Id);
            if (!string.Equals(
                current.PostPersistenceLeaseId,
                claim.OperationId,
                StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Post-receipt lease '{claim.OperationId}' no longer owns item '{item.RunItem.Id}'.");
            }
            return AuthoringWorkResult.Complete(
                item.RunItem.AcceptedReceiptId);
        }

        string callbackBase = string.IsNullOrWhiteSpace(
            _options.AuthoringCallbackAddress)
            ? $"http://localhost:{_options.Ports.Http}"
            : _options.AuthoringCallbackAddress.TrimEnd('/');
        string callbackUrl =
            $"{callbackBase}/api/v1/ballot-notes/authoring/runs/" +
            $"{Uri.EscapeDataString(item.RunItem.RunId)}/items/" +
            $"{Uri.EscapeDataString(item.RunItem.Id)}/" +
            $"{Uri.EscapeDataString(item.RunItem.ItemKind)}/" +
            $"{Uri.EscapeDataString(item.RunItem.BusinessKey)}/result";
        string template = GetCommandTemplate(item.RunItem.ItemKind);
        BallotNotesAuthoringCommand command = RenderCommand(
            template,
            item,
            claim,
            callbackUrl);
        BallotNotesAuthoringCommandResult result =
            await runner.RunAsync(command, ct);

        AuthoringRunItemRecord persisted =
            (await authoringStore.GetRunItemsAsync(
                item.RunItem.RunId,
                ct)).Single(value => value.Id == item.RunItem.Id);
        if (!string.IsNullOrWhiteSpace(persisted.AcceptedReceiptId))
        {
            if (!string.Equals(
                persisted.CurrentOperationId,
                claim.OperationId,
                StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{claim.OperationId}' no longer owns item '{item.RunItem.Id}'.");
            }
            return AuthoringWorkResult.Persisted(
                persisted.AcceptedReceiptId);
        }
        if (result.Canceled)
        {
            return AuthoringWorkResult.Retry(
                "Ballot-note authoring worker was canceled.");
        }
        if (result.ExitCode != 0)
        {
            return AuthoringWorkResult.Retry(
                string.IsNullOrWhiteSpace(result.StandardErrorTail)
                    ? $"Ballot-note authoring worker exited with code {result.ExitCode}."
                    : result.StandardErrorTail);
        }
        return AuthoringWorkResult.Retry(
            "Ballot-note authoring worker exited successfully without an accepted persistence receipt.");
    }

    private string GetCommandTemplate(string itemKind)
        => itemKind.ToLowerInvariant() switch
        {
            "artifact" => _options.ArtifactAuthoringCommand,
            "page" => _options.PageAuthoringCommand,
            "datatype" => _options.DataTypeAuthoringCommand,
            _ => throw new InvalidOperationException(
                $"Unsupported ballot-note type '{itemKind}'."),
        };

    private BallotNotesAuthoringCommand RenderCommand(
        string template,
        BallotNotesAuthoringWorkItem item,
        AuthoringQueueClaim claim,
        string callbackUrl)
    {
        Dictionary<string, string> tokens = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["noteId"] = item.RunItem.BusinessKey,
            ["type"] = item.RunItem.ItemKind,
            ["executionId"] = item.HydrationItem.ExecutionId,
        };
        string rendered = Regex.Replace(
            template,
            "\\{([A-Za-z0-9_]+)\\}",
            match => tokens.TryGetValue(match.Groups[1].Value, out string? value)
                ? value
                : throw new InvalidOperationException(
                    $"BallotNotes authoring command contains unresolved token '{match.Value}'."));
        if (rendered.Contains(claim.OperationToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BallotNotes authoring command must not place the operation token on argv.");
        }
        List<string> parts = SplitCommandLine(rendered);
        if (parts.Count == 0)
        {
            throw new InvalidOperationException(
                "BallotNotes authoring command rendered to an empty command.");
        }

        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["FHIR_AUGURY_AUTHORING_WORKER"] = "1",
            ["FHIR_AUGURY_AUTHORING_RUN_ID"] = item.RunItem.RunId,
            ["FHIR_AUGURY_AUTHORING_ITEM_ID"] = item.RunItem.Id,
            ["FHIR_AUGURY_AUTHORING_CALLBACK_URL"] = callbackUrl,
            ["FHIR_AUGURY_AUTHORING_OPERATION_ID"] = claim.OperationId,
            ["FHIR_AUGURY_AUTHORING_OPERATION_TOKEN"] = claim.OperationToken,
            ["FHIR_AUGURY_AUTHORING_SOURCE_REVISION"] =
                item.RunItem.ExpectedSourceRevision,
            ["FHIR_AUGURY_BALLOT_NOTE_ID"] = item.RunItem.BusinessKey,
            ["FHIR_AUGURY_BALLOT_NOTE_TYPE"] = item.RunItem.ItemKind,
            ["FHIR_AUGURY_BALLOT_NOTES_EXECUTION_ID"] =
                item.HydrationItem.ExecutionId,
        };
        return new BallotNotesAuthoringCommand(
            parts[0],
            parts.Skip(1).ToArray(),
            environment,
            claim.OperationToken);
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        List<string> args = [];
        StringBuilder current = new();
        bool inQuotes = false;
        char quote = '\0';
        for (int index = 0; index < commandLine.Length; index++)
        {
            char character = commandLine[index];
            if (character is '"' or '\'' &&
                (!inQuotes || character == quote))
            {
                inQuotes = !inQuotes;
                quote = inQuotes ? character : '\0';
                continue;
            }
            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (inQuotes)
        {
            throw new InvalidOperationException(
                "BallotNotes authoring command has an unterminated quote.");
        }
        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}

public sealed class BallotNotesAuthoringCommandRunner
    : IBallotNotesAuthoringCommandRunner
{
    private const int TailLength = 4096;

    public async Task<BallotNotesAuthoringCommandResult> RunAsync(
        BallotNotesAuthoringCommand command,
        CancellationToken ct)
    {
        ProcessStartInfo startInfo = new(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach ((string key, string value) in command.Environment)
        {
            startInfo.Environment[key] = value;
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        bool canceled = false;
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        string stdout = await SafeReadAsync(stdoutTask);
        string stderr = await SafeReadAsync(stderrTask);
        return new BallotNotesAuthoringCommandResult(
            canceled ? -1 : process.ExitCode,
            Tail(Redact(stdout, command.OperationToken)),
            Tail(Redact(stderr, command.OperationToken)),
            canceled);
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Tail(string value)
        => value.Length <= TailLength ? value : value[^TailLength..];

    private static string Redact(string value, string secret)
        => value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
}
