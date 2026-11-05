using System.Buffers.Binary;

namespace Egap.Documents.Odf;

/// <summary>
/// The Blowfish block cipher (Schneier, "Description of a New Variable-Length Key, 64-Bit Block Cipher", 1993) in cipher feedback
/// mode, which old OpenDocument and OpenOffice.org files encrypt with (ODF 1.3 Part 2, 3.4.2). ODF names 8-bit feedback;
/// OpenOffice.org and LibreOffice write 64-bit feedback, so both are here.
/// </summary>
internal sealed class Blowfish
{
    private readonly uint[] p = new uint[18];
    private readonly uint[] s = new uint[1024];

    /// <summary>A cipher with <paramref name="key"/>, 4 to 56 bytes.</summary>
    public Blowfish(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 1 or > 72)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        BlowfishTables.P.CopyTo(p, 0);
        BlowfishTables.S.CopyTo(s, 0);
        int k = 0;
        for (int i = 0; i < 18; i++)
        {
            uint word = 0;
            for (int j = 0; j < 4; j++)
            {
                word = (word << 8) | key[k];
                k = (k + 1) % key.Length;
            }

            p[i] ^= word;
        }

        uint left = 0;
        uint right = 0;
        for (int i = 0; i < 18; i += 2)
        {
            Encrypt(ref left, ref right);
            p[i] = left;
            p[i + 1] = right;
        }

        for (int i = 0; i < 1024; i += 2)
        {
            Encrypt(ref left, ref right);
            s[i] = left;
            s[i + 1] = right;
        }
    }

    /// <summary>Encrypts one 8-byte block in place, its halves read big-endian.</summary>
    public void EncryptBlock(Span<byte> block)
    {
        uint left = BinaryPrimitives.ReadUInt32BigEndian(block);
        uint right = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);
        Encrypt(ref left, ref right);
        BinaryPrimitives.WriteUInt32BigEndian(block, left);
        BinaryPrimitives.WriteUInt32BigEndian(block[4..], right);
    }

    /// <summary>Decrypts one 8-byte block in place.</summary>
    public void DecryptBlock(Span<byte> block)
    {
        uint left = BinaryPrimitives.ReadUInt32BigEndian(block);
        uint right = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);
        for (int i = 17; i > 1; i--)
        {
            left ^= p[i];
            right ^= F(left);
            (left, right) = (right, left);
        }

        (left, right) = (right, left);
        right ^= p[1];
        left ^= p[0];
        BinaryPrimitives.WriteUInt32BigEndian(block, left);
        BinaryPrimitives.WriteUInt32BigEndian(block[4..], right);
    }

    /// <summary>Decrypts <paramref name="data"/> in place in 8-bit cipher feedback mode from the 8-byte <paramref name="iv"/>.</summary>
    public void DecryptCfb8(ReadOnlySpan<byte> iv, Span<byte> data)
    {
        Cfb8(iv, data, decrypt: true);
    }

    /// <summary>Encrypts <paramref name="data"/> in place in 8-bit cipher feedback mode from the 8-byte <paramref name="iv"/>.</summary>
    public void EncryptCfb8(ReadOnlySpan<byte> iv, Span<byte> data)
    {
        Cfb8(iv, data, decrypt: false);
    }

    /// <summary>Decrypts <paramref name="data"/> in place in 64-bit cipher feedback mode from the 8-byte <paramref name="iv"/>.</summary>
    public void DecryptCfb64(ReadOnlySpan<byte> iv, Span<byte> data)
    {
        Cfb64(iv, data, decrypt: true);
    }

    /// <summary>Encrypts <paramref name="data"/> in place in 64-bit cipher feedback mode from the 8-byte <paramref name="iv"/>.</summary>
    public void EncryptCfb64(ReadOnlySpan<byte> iv, Span<byte> data)
    {
        Cfb64(iv, data, decrypt: false);
    }

    private void Cfb64(ReadOnlySpan<byte> iv, Span<byte> data, bool decrypt)
    {
        if (iv.Length != 8)
        {
            throw new ArgumentException("Blowfish's initialisation vector is 8 bytes.", nameof(iv));
        }

        // Each 8-byte block is XORed with the encryption of the previous cipher block (the vector for the first).
        Span<byte> register = stackalloc byte[8];
        iv.CopyTo(register);
        for (int i = 0; i < data.Length; i += 8)
        {
            EncryptBlock(register);
            Span<byte> block = data.Slice(i, Math.Min(8, data.Length - i));
            for (int k = 0; k < block.Length; k++)
            {
                byte input = block[k];
                block[k] = (byte)(input ^ register[k]);
                register[k] = decrypt ? input : block[k];
            }
        }
    }

    private void Cfb8(ReadOnlySpan<byte> iv, Span<byte> data, bool decrypt)
    {
        if (iv.Length != 8)
        {
            throw new ArgumentException("Blowfish's initialisation vector is 8 bytes.", nameof(iv));
        }

        // The shift register holds the last 8 cipher bytes; each byte is XORed with the first byte of its encryption.
        ulong register = BinaryPrimitives.ReadUInt64BigEndian(iv);
        for (int i = 0; i < data.Length; i++)
        {
            uint left = (uint)(register >> 32);
            uint right = (uint)register;
            Encrypt(ref left, ref right);
            byte input = data[i];
            byte output = (byte)(input ^ (left >> 24));
            data[i] = output;
            register = (register << 8) | (decrypt ? input : output);
        }
    }

    private void Encrypt(ref uint left, ref uint right)
    {
        uint l = left;
        uint r = right;
        for (int i = 0; i < 16; i++)
        {
            l ^= p[i];
            r ^= F(l);
            (l, r) = (r, l);
        }

        (l, r) = (r, l);
        r ^= p[16];
        l ^= p[17];
        left = l;
        right = r;
    }

    private uint F(uint x)
    {
        return ((s[x >> 24] + s[256 + ((x >> 16) & 0xFF)]) ^ s[512 + ((x >> 8) & 0xFF)]) + s[768 + (x & 0xFF)];
    }
}
