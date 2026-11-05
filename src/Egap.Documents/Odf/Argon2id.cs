using System.Buffers.Binary;
using System.Numerics;

namespace Egap.Documents.Odf;

/// <summary>
/// The Argon2id password hash, version 0x13 (RFC 9106), which LibreOffice's whole-package encryption derives its key with.
/// Lanes are filled in parallel within each slice.
/// </summary>
internal static class Argon2id
{
    private const int BlockWords = 128;
    private const int SyncPoints = 4;

    /// <summary>The most memory a hash may take: 4 GiB, past which its blocks no longer fit one array.</summary>
    public const int MaxMemoryKib = 4 * 1024 * 1024 - 1;

    /// <summary>
    /// Derives <paramref name="tagLength"/> bytes from <paramref name="password"/> and <paramref name="salt"/> with
    /// <paramref name="iterations"/> passes over <paramref name="memoryKib"/> KiB in <paramref name="lanes"/> lanes.
    /// </summary>
    public static byte[] Hash(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int memoryKib, int lanes, int tagLength, ReadOnlySpan<byte> secret = default, ReadOnlySpan<byte> associated = default)
    {
        if (lanes is < 1 or > 0xFFFFFF || iterations < 1 || tagLength < 4 || memoryKib < 8 * lanes || memoryKib > MaxMemoryKib)
        {
            throw new ArgumentOutOfRangeException(nameof(lanes), "The Argon2 parameters are out of range.");
        }

        // H0 hashes every parameter and input (RFC 9106, 3.2).
        List<byte> input = [];
        foreach (int value in (int[])[lanes, tagLength, memoryKib, iterations, 0x13, 2])
        {
            AddInt(input, value);
        }

        AddBytes(input, password);
        AddBytes(input, salt);
        AddBytes(input, secret);
        AddBytes(input, associated);

        byte[] h0 = new byte[72];
        Blake2b.Hash(input.ToArray(), h0.AsSpan(0, 64));

        int segment = memoryKib / (SyncPoints * lanes);
        int laneLength = segment * SyncPoints;
        int blocks = laneLength * lanes;
        ulong[] memory = new ulong[blocks * BlockWords];
        byte[] block = new byte[1024];
        for (int lane = 0; lane < lanes; lane++)
        {
            for (int i = 0; i < 2; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(h0.AsSpan(64), i);
                BinaryPrimitives.WriteInt32LittleEndian(h0.AsSpan(68), lane);
                VariableHash(h0, block);
                Load(block, memory.AsSpan(((lane * laneLength) + i) * BlockWords, BlockWords));
            }
        }

        Context context = new(memory, lanes, laneLength, segment, blocks, iterations);
        for (int pass = 0; pass < iterations; pass++)
        {
            for (int slice = 0; slice < SyncPoints; slice++)
            {
                int p = pass;
                int s = slice;
                if (lanes == 1)
                {
                    FillSegment(context, p, 0, s);
                }
                else
                {
                    Parallel.For(0, lanes, lane => FillSegment(context, p, lane, s));
                }
            }
        }

        // The tag hashes the XOR of every lane's last block.
        Span<ulong> last = memory.AsSpan((laneLength - 1) * BlockWords, BlockWords).ToArray();
        for (int lane = 1; lane < lanes; lane++)
        {
            ReadOnlySpan<ulong> other = memory.AsSpan(((lane * laneLength) + laneLength - 1) * BlockWords, BlockWords);
            for (int i = 0; i < BlockWords; i++)
            {
                last[i] ^= other[i];
            }
        }

        for (int i = 0; i < BlockWords; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8 * i), last[i]);
        }

        byte[] tag = new byte[tagLength];
        VariableHash(block, tag);
        return tag;
    }

    private sealed record Context(ulong[] Memory, int Lanes, int LaneLength, int Segment, int Blocks, int Iterations);

    private static void FillSegment(Context context, int pass, int lane, int slice)
    {
        ulong[] memory = context.Memory;
        int laneLength = context.LaneLength;
        int segment = context.Segment;

        // Argon2id addresses independently of the data in the first two slices of the first pass (as Argon2i does).
        bool independent = pass == 0 && slice < 2;
        ulong[] address = new ulong[BlockWords];
        ulong[] addressInput = new ulong[BlockWords];
        ulong[] zero = new ulong[BlockWords];
        if (independent)
        {
            addressInput[0] = (ulong)pass;
            addressInput[1] = (ulong)lane;
            addressInput[2] = (ulong)slice;
            addressInput[3] = (ulong)context.Blocks;
            addressInput[4] = (ulong)context.Iterations;
            addressInput[5] = 2;
        }

        int start = pass == 0 && slice == 0 ? 2 : 0;
        if (independent && start != 0)
        {
            NextAddresses(addressInput, address, zero);
        }

        ulong[] scratch = new ulong[BlockWords];
        for (int index = start; index < segment; index++)
        {
            int column = (slice * segment) + index;
            int current = (lane * laneLength) + column;
            int previous = column == 0 ? current + laneLength - 1 : current - 1;
            ulong random;
            if (independent)
            {
                if (index % BlockWords == 0)
                {
                    NextAddresses(addressInput, address, zero);
                }

                random = address[index % BlockWords];
            }
            else
            {
                random = memory[previous * BlockWords];
            }

            uint j1 = (uint)random;
            uint j2 = (uint)(random >> 32);
            int refLane = pass == 0 && slice == 0 ? lane : (int)(j2 % (uint)context.Lanes);
            bool sameLane = refLane == lane;

            // The blocks that may be referenced (RFC 9106, 3.4.1.2).
            long area;
            if (pass == 0)
            {
                area = sameLane ? column - 1 : (slice * segment) - (index == 0 ? 1 : 0);
            }
            else
            {
                area = sameLane ? laneLength - segment + index - 1 : laneLength - segment - (index == 0 ? 1 : 0);
            }

            ulong x = ((ulong)j1 * j1) >> 32;
            ulong y = ((ulong)area * x) >> 32;
            long relative = area - 1 - (long)y;
            long startPosition = pass == 0 || slice == SyncPoints - 1 ? 0 : (slice + 1) * segment;
            long refColumn = (startPosition + relative) % laneLength;
            int reference = (refLane * laneLength) + (int)refColumn;

            Compress(
                memory.AsSpan(previous * BlockWords, BlockWords),
                memory.AsSpan(reference * BlockWords, BlockWords),
                memory.AsSpan(current * BlockWords, BlockWords),
                scratch,
                xorInto: pass > 0);
        }
    }

    private static void NextAddresses(ulong[] input, ulong[] address, ulong[] zero)
    {
        input[6]++;
        ulong[] scratch = new ulong[BlockWords];
        ulong[] once = new ulong[BlockWords];
        Compress(zero, input, once, scratch, xorInto: false);
        Compress(zero, once, address, scratch, xorInto: false);
    }

    /// <summary>The compression function G: <paramref name="output"/> = P(X ^ Y) ^ X ^ Y, XORed into the old block on later passes.</summary>
    private static void Compress(ReadOnlySpan<ulong> x, ReadOnlySpan<ulong> y, Span<ulong> output, Span<ulong> scratch, bool xorInto)
    {
        Span<ulong> r = stackalloc ulong[BlockWords];
        for (int i = 0; i < BlockWords; i++)
        {
            r[i] = x[i] ^ y[i];
        }

        r.CopyTo(scratch);
        for (int row = 0; row < 8; row++)
        {
            int b = 16 * row;
            Round(scratch, b, b + 1, b + 2, b + 3, b + 4, b + 5, b + 6, b + 7, b + 8, b + 9, b + 10, b + 11, b + 12, b + 13, b + 14, b + 15);
        }

        for (int col = 0; col < 8; col++)
        {
            int b = 2 * col;
            Round(scratch, b, b + 1, b + 16, b + 17, b + 32, b + 33, b + 48, b + 49, b + 64, b + 65, b + 80, b + 81, b + 96, b + 97, b + 112, b + 113);
        }

        for (int i = 0; i < BlockWords; i++)
        {
            ulong value = scratch[i] ^ r[i];
            output[i] = xorInto ? output[i] ^ value : value;
        }
    }

    private static void Round(Span<ulong> v, int v0, int v1, int v2, int v3, int v4, int v5, int v6, int v7, int v8, int v9, int v10, int v11, int v12, int v13, int v14, int v15)
    {
        G(v, v0, v4, v8, v12);
        G(v, v1, v5, v9, v13);
        G(v, v2, v6, v10, v14);
        G(v, v3, v7, v11, v15);
        G(v, v0, v5, v10, v15);
        G(v, v1, v6, v11, v12);
        G(v, v2, v7, v8, v13);
        G(v, v3, v4, v9, v14);
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d)
    {
        v[a] = BlaMka(v[a], v[b]);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = BlaMka(v[c], v[d]);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = BlaMka(v[a], v[b]);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = BlaMka(v[c], v[d]);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }

    private static ulong BlaMka(ulong x, ulong y)
    {
        return x + y + (2 * (ulong)(uint)x * (uint)y);
    }

    /// <summary>The variable-length hash H' (RFC 9106, 3.3).</summary>
    private static void VariableHash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, output.Length);
        if (output.Length <= 64)
        {
            Blake2b.Hash(length, input, output);
            return;
        }

        Span<byte> v = stackalloc byte[64];
        Blake2b.Hash(length, input, v);
        int written = 0;
        while (output.Length - written > 64)
        {
            v[..32].CopyTo(output[written..]);
            written += 32;
            if (output.Length - written > 64)
            {
                Blake2b.Hash(v, v);
            }
        }

        Span<byte> tail = stackalloc byte[64];
        Blake2b.Hash(v, tail[..(output.Length - written)]);
        tail[..(output.Length - written)].CopyTo(output[written..]);
    }

    private static void Load(ReadOnlySpan<byte> bytes, Span<ulong> words)
    {
        for (int i = 0; i < BlockWords; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(8 * i)..]);
        }
    }

    private static void AddBytes(List<byte> to, ReadOnlySpan<byte> bytes)
    {
        AddInt(to, bytes.Length);
        foreach (byte b in bytes)
        {
            to.Add(b);
        }
    }

    private static void AddInt(List<byte> to, int value)
    {
        to.Add((byte)value);
        to.Add((byte)(value >> 8));
        to.Add((byte)(value >> 16));
        to.Add((byte)(value >> 24));
    }
}
