using Xunit;

namespace WinSystemHelper.Tests;

public sealed class UpdateSignatureTests : TestWorkspace
{
    [Fact(DisplayName = "Signed update package verifies")]
    public async Task SignedUpdatePackageVerifies()
    {
        Assert.True(await OtaSignatureAsync(nameof(SignedUpdatePackageVerifies)));
    }

    [Fact(DisplayName = "Signature from a different key is rejected")]
    public async Task SignatureFromADifferentKeyIsRejected()
    {
        Assert.True(await OtaSignatureAsync(nameof(SignatureFromADifferentKeyIsRejected)));
    }

    [Fact(DisplayName = "Malformed or missing signature input is rejected")]
    public async Task MalformedOrMissingSignatureInputIsRejected()
    {
        Assert.True(await OtaSignatureAsync(nameof(MalformedOrMissingSignatureInputIsRejected)));
    }

    [Fact(DisplayName = "Modified update package is rejected")]
    public async Task ModifiedUpdatePackageIsRejected()
    {
        Assert.True(await OtaSignatureAsync(nameof(ModifiedUpdatePackageIsRejected)));
    }

    private async Task<bool> OtaSignatureAsync(string check)
    {
        var (privateKey, publicKey) = OtaSignature.GenerateKeyPair();
        string package = Path.Combine(TestRoot, "package.zip");
        byte[] content = new byte[4096];
        Random.Shared.NextBytes(content);
        await System.IO.File.WriteAllBytesAsync(package, content);
        string signature = OtaSignature.Sign(package, privateKey);
        bool observed = OtaSignature.Verify(package, signature, publicKey);
        if (check == nameof(SignedUpdatePackageVerifies))
        {
            return observed;
        }
        observed = !OtaSignature.Verify(package, signature, OtaSignature.GenerateKeyPair().PublicKey);
        if (check == nameof(SignatureFromADifferentKeyIsRejected))
        {
            return observed;
        }
        observed = !OtaSignature.Verify(package, "not-base64!", publicKey) && !OtaSignature.Verify(package, null, publicKey) &&
            !OtaSignature.Verify(package, signature, "AAAA");
        if (check == nameof(MalformedOrMissingSignatureInputIsRejected))
        {
            return observed;
        }
        content[100] ^= 1;
        await System.IO.File.WriteAllBytesAsync(package, content);
        return !OtaSignature.Verify(package, signature, publicKey);
    }
}
