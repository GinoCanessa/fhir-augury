using System.Net;

namespace FhirAugury.Tools.PreparerSite.Tests;

/// <summary>
/// Minimal in-process HTTP server used to back the orchestrator URL the
/// hydration preflight talks to. By default every request 404s, exercising
/// the hydrator's "unresolved" row path. Tests don't need the resolved-row
/// path here — the preflight contract is that hydration runs and rows
/// land, not what they contain. Owned per-test via <see cref="IDisposable"/>.
/// </summary>
internal sealed class FakeHydrationServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeHydrationServer()
    {
        int port = FindFreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public string BaseUrl { get; }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            try
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                ctx.Response.ContentLength64 = 0;
            }
            catch { /* best-effort */ }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }
    }

    private static int FindFreeTcpPort()
    {
        System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
