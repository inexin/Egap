namespace Egap.Pdf.Security;

/// <summary>
/// The RC4 stream cipher, which older PDF encryption uses (ISO 32000-2, 7.6.3). .NET has no RC4, so this is a small one of
/// our own. RC4 is broken as a cipher; it is here only to read and write files that use it.
/// </summary>
internal static class Rc4
{
    /// <summary>Encrypts or decrypts (the same operation) <paramref name="data"/> with <paramref name="key"/>.</summary>
    public static byte[] Apply(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        byte[] output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + state[i]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
            output[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 0xFF]);
        }

        return output;
    }
}
