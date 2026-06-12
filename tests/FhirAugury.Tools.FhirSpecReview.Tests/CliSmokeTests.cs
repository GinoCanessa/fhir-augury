using System.Threading.Tasks;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// CLI smoke tests for the <c>fhir-spec-review</c> entry point. Redirects the
/// console, so it joins the shared <c>ConsoleRedirect</c> collection to keep
/// console redirection serialized across parallel xUnit test classes.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class CliSmokeTests
{
    private static async Task<int> RunAsync(params string[] args)
    {
        TextWriter origOut = Console.Out;
        TextWriter origErr = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return await Program.Main(args).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Help_Exits_Zero()
    {
        Assert.Equal(0, await RunAsync("--help"));
    }

    [Fact]
    public async Task UnknownVerb_Exits_NonZero()
    {
        Assert.NotEqual(0, await RunAsync("frobnicate"));
    }

    [Fact]
    public async Task NoArgs_Exits_NonZero()
    {
        Assert.NotEqual(0, await RunAsync());
    }
}
