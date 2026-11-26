using RigControl.iPhone.Plist;
using RigControl.iPhone.Signing;
using static RigControl.iPhone.Tests.Signing.SigningFixtures;

namespace RigControl.iPhone.Tests.Signing;

public class MachOSignerTests
{
    private static MachOSigner Signer() => new(new CmsSignature(Certificate, AppleCertificates.DefaultChain));

    private static PlistDict Entitlements(string id) => new()
    {
        ["application-identifier"] = $"{TeamId}.{id}",
        ["get-task-allow"] = true,
        ["keychain-access-groups"] = new List<object> { $"{TeamId}.*" },
    };

    [Fact]
    public void Signs_a_thin_executable_with_every_slot()
    {
        var info = "info"u8.ToArray();
        var resources = "resources"u8.ToArray();
        var signed = Signer().Sign(MachO(), new CodeSignOptions("com.example.app", TeamId, "Apple Development: T",
            info, resources, Entitlements("com.example.app")));

        SignatureVerifier.VerifyFile(signed, new SignatureVerifier.Expect("com.example.app", TeamId, Certificate,
            info, resources, $"{TeamId}.com.example.app", Executable: true));
    }

    [Fact]
    public void Re_signing_replaces_the_old_signature_in_place()
    {
        var signer = Signer();
        var once = signer.Sign(MachO(), new CodeSignOptions("a.b", TeamId, null, null, null, null));
        var twice = signer.Sign(once, new CodeSignOptions("a.b.c", TeamId, null, null, null, Entitlements("a.b.c")));

        SignatureVerifier.VerifyFile(twice, new SignatureVerifier.Expect("a.b.c", TeamId, Certificate,
            ApplicationIdentifier: $"{TeamId}.a.b.c", Executable: true));
        // Still exactly one LC_CODE_SIGNATURE, and the code before the signature is unchanged in size.
        Assert.Equal(MachOImage.Parse(once).CommandCount, MachOImage.Parse(twice).CommandCount);
        Assert.Equal(MachOImage.Parse(once).CodeSignatureOffset, MachOImage.Parse(twice).CodeSignatureOffset);
    }

    [Fact]
    public void Signs_every_slice_of_a_universal_file()
    {
        var fat = Fat(MachO(fileType: 6, seed: 7), MachO(fileType: 6, seed: 8, cpuSubtype: 2, linkeditSize: 0x5123));
        var signed = Signer().Sign(fat, new CodeSignOptions("libthing", TeamId, null, null, null, null));

        var slices = SignatureVerifier.VerifyFile(signed, new SignatureVerifier.Expect("libthing", TeamId, Certificate));
        Assert.Equal(2, slices.Count);
    }

    [Fact]
    public void A_library_gets_no_main_binary_flag_and_no_entitlements()
    {
        var signed = Signer().Sign(MachO(fileType: 6), new CodeSignOptions("lib", null, null, null, null, null));
        SignatureVerifier.VerifyFile(signed, new SignatureVerifier.Expect("lib", null, Certificate));
    }

    [Fact]
    public void Refuses_a_binary_without_room_for_the_signature_command()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            Signer().Sign(MachO(headerRoom: false), new CodeSignOptions("x", TeamId, null, null, null, null)));
        Assert.Contains("no room", e.Message);
    }

    [Fact]
    public void Refuses_data_after_linkedit()
    {
        var file = MachO().Concat(new byte[100]).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            Signer().Sign(file, new CodeSignOptions("x", TeamId, null, null, null, null)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(200)]
    public void Refuses_truncated_or_damaged_headers(int length)
    {
        var file = MachO().AsSpan(0, length).ToArray();
        Assert.Throws<InvalidDataException>(() => MachOImage.Parse(file));
    }

    [Fact]
    public void Refuses_load_commands_that_point_outside_the_file()
    {
        var file = MachO();
        BitConverter.GetBytes(0xFFFFu).CopyTo(file, 20);   // sizeofcmds
        Assert.Throws<InvalidDataException>(() => MachOImage.Parse(file));
        file = MachO();
        BitConverter.GetBytes(0x7FFFFFFFFul).CopyTo(file, 32 + 72 + 80 + 48);   // __LINKEDIT filesize
        Assert.Throws<InvalidDataException>(() => MachOImage.Parse(file));
    }

    [Fact]
    public void Der_entitlements_match_apples_layout()
    {
        var der = EntitlementsDer.Encode(new PlistDict { ["a"] = true });
        Assert.Equal(Convert.FromHexString("700D020101B00830060C01610101FF"), der);

        var mixed = EntitlementsDer.Encode(new PlistDict
        {
            ["z"] = new List<object> { "x", 5L },
            ["b"] = new PlistDict { ["k"] = false },
        });
        var back = SignatureVerifier.ReadDerEntitlements(mixed);
        Assert.Equal(new[] { "b", "z" }, back.Keys);
        Assert.Equal(new List<object> { "x", 5L }, back["z"]);
        Assert.Throws<InvalidDataException>(() => EntitlementsDer.Encode(new PlistDict { ["d"] = new byte[1] }));
    }

    [Fact]
    public void Designated_requirement_has_apples_wwdr_form()
    {
        var blob = Requirements.Designated("com.example.app", "Apple Development: T (X)");
        Assert.Equal(0xfade0c01u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob));
        Assert.Equal((uint)blob.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(4)));
        // One requirement of type 3 (designated) at offset 20, an expression-form Requirement blob.
        Assert.Equal(Convert.FromHexString("000000010000000300000014FADE0C00"), blob.AsSpan(8, 16).ToArray());
        Assert.Equal(12, Requirements.Empty().Length);
    }

    [Fact]
    public void Apple_certificates_load_and_chain()
    {
        var chain = AppleCertificates.DefaultChain;
        Assert.Contains("Worldwide Developer Relations", chain[0].Subject);
        Assert.Equal(chain[1].Subject, chain[0].Issuer);
        Assert.Equal(chain[1].Subject, chain[1].Issuer);
    }
}
