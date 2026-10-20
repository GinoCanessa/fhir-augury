using FhirAugury.Processing.Common.Authoring;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringOperationTokenVerifierTests
{
    [Fact]
    public void Issue_ReturnsSeparateRandomTokenAndOneWayVerifier()
    {
        IssuedAuthoringOperationToken first = AuthoringOperationTokenVerifier.Issue();
        IssuedAuthoringOperationToken second = AuthoringOperationTokenVerifier.Issue();

        Assert.NotEqual(first.Token, first.Verifier);
        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.True(AuthoringOperationTokenVerifier.Verify(first.Token, first.Verifier));
        Assert.False(AuthoringOperationTokenVerifier.Verify(second.Token, first.Verifier));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    public void Verify_RejectsInvalidVerifier(string verifier)
    {
        Assert.False(AuthoringOperationTokenVerifier.Verify("token", verifier));
    }
}
