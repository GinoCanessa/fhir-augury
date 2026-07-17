using System.Diagnostics;

namespace FhirAugury.Processing.Jira.Common.Agent;

public sealed class JiraAgentCliRunner : IJiraAgentCliRunner
{
    private const int TailLength = 4096;

    public async Task<JiraAgentResult> RunAsync(JiraAgentCommand command, JiraAgentCommandContext context, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
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

        startInfo.Environment["FHIR_AUGURY_PROCESSING_DB"] = context.DatabasePath;
        startInfo.Environment["FHIR_AUGURY_TICKET_KEY"] = context.TicketKey;
        startInfo.Environment["FHIR_AUGURY_SOURCE_TICKET_ID"] = context.SourceTicketId;
        startInfo.Environment["FHIR_AUGURY_SOURCE_TICKET_SHAPE"] = context.SourceTicketShape;

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
        stopwatch.Stop();
        int exitCode = canceled ? -1 : process.ExitCode;
        return new JiraAgentResult(exitCode, Tail(stdout), Tail(stderr), stopwatch.Elapsed, canceled);
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

    private static string Tail(string value) => value.Length <= TailLength ? value : value[^TailLength..];
}
