using System.Text;

using Egap.Documents.Odf;

namespace Egap.Documents.Tests;

/// <summary>The ciphers and hashes behind ODF encryption, against their published test vectors.</summary>
public class OdfCryptoTests
{
    [Theory]
    [InlineData("0000000000000000", "0000000000000000", "4EF997456198DD78")]
    [InlineData("FFFFFFFFFFFFFFFF", "FFFFFFFFFFFFFFFF", "51866FD5B85ECB8A")]
    [InlineData("3000000000000000", "1000000000000001", "7D856F9A613063F2")]
    [InlineData("0123456789ABCDEF", "1111111111111111", "61F9C3802281B096")]
    public void BlowfishMatchesTheVectors(string key, string plain, string cipher)
    {
        Blowfish blowfish = new(Convert.FromHexString(key));
        byte[] block = Convert.FromHexString(plain);
        blowfish.EncryptBlock(block);
        Assert.Equal(cipher, Convert.ToHexString(block));
        blowfish.DecryptBlock(block);
        Assert.Equal(plain, Convert.ToHexString(block));
    }

    [Fact]
    public void BlowfishCfb8RoundTrips()
    {
        Blowfish blowfish = new(Encoding.ASCII.GetBytes("a sixteen-byte k"));
        byte[] iv = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] data = Encoding.ASCII.GetBytes("Cipher feedback, one byte at a time.");
        byte[] copy = (byte[])data.Clone();
        blowfish.EncryptCfb8(iv, copy);
        Assert.NotEqual(data, copy);
        blowfish.DecryptCfb8(iv, copy);
        Assert.Equal(data, copy);
    }

    [Theory]
    [InlineData("abc", 64, "BA80A53F981C4D0D6A2797B69F12F6E94C212F14685AC4B74B12BB6FDBFFA2D17D87C5392AAB792DC252D5DE4533CC9518D38AA8DBF1925AB92386EDD4009923")]
    [InlineData("", 64, "786A02F742015903C6C6FD852552D272912F4740E15847618A86E217F71F5419D25E1031AFEE585313896444934EB04B903A685B1448B755D56F701AFE9BE2CE")]
    public void Blake2bMatchesTheVectors(string input, int length, string expected)
    {
        byte[] output = new byte[length];
        Blake2b.Hash(Encoding.ASCII.GetBytes(input), output);
        Assert.Equal(expected, Convert.ToHexString(output));
    }

    [Fact]
    public void Blake2bHashesAcrossBlocks()
    {
        // 300 bytes in one call and split in two give the same hash.
        byte[] input = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];
        byte[] whole = new byte[64];
        byte[] split = new byte[64];
        Blake2b.Hash(input, whole);
        Blake2b.Hash(input.AsSpan(0, 128), input.AsSpan(128), split);
        Assert.Equal(whole, split);
    }

    [Fact]
    public void Argon2idMatchesRfc9106()
    {
        // RFC 9106, 5.3.
        byte[] tag = Argon2id.Hash(
            Enumerable.Repeat((byte)1, 32).ToArray(),
            Enumerable.Repeat((byte)2, 16).ToArray(),
            iterations: 3,
            memoryKib: 32,
            lanes: 4,
            tagLength: 32,
            secret: Enumerable.Repeat((byte)3, 8).ToArray(),
            associated: Enumerable.Repeat((byte)4, 12).ToArray());
        Assert.Equal("0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659", Convert.ToHexString(tag));
    }
}
