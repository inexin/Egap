using System.Buffers.Binary;

namespace Egap.Documents.Rtf;

/// <summary>
/// Editing restrictions: <c>\readprot</c>, <c>\annotprot</c>, <c>\revprot</c>, <c>\formprot</c> with <c>\enforceprot</c> and
/// <c>\protlevel</c>, the password hash in <c>{\*\passwordhash}</c>, the editors in <c>{\*\protusertbl}</c> and the editable
/// ranges' <c>{\*\protstart}</c> and <c>{\*\protend}</c>.
/// </summary>
internal sealed partial class RtfReader
{
    private readonly List<string> protectionUsers = [];
    private bool readProtected;
    private bool commentsProtected;
    private bool revisionsProtected;
    private bool formsProtected;
    private int? enforceProtection;
    private int? protectionLevel;
    private byte[]? passwordHash;

    private bool ProtectionWord(string word, bool has, int p)
    {
        switch (word)
        {
            case "readprot":
                readProtected = true;
                return true;
            case "annotprot":
                commentsProtected = true;
                return true;
            case "revprot":
                revisionsProtected = true;
                return true;
            case "formprot":
                formsProtected = true;
                return true;
            case "allprot":
                return true;
            case "enforceprot":
                enforceProtection = has ? p : 1;
                return true;
            case "protlevel" when has:
                protectionLevel = p;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads <c>{\*\protusertbl}</c>: a group per editor name, in order; ranges name them from 1.</summary>
    private void ReadProtectionUsers()
    {
        int groupDepth = depth;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    if (protectionUsers.Count < 65_536)
                    {
                        protectionUsers.Add(ReadText());
                    }
                    else
                    {
                        SkipDestination();
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// <c>{\*\protstart}</c> or <c>{\*\protend}</c>: eight bytes in hex, a 16-bit editor (-1 everyone, -2 administrators, -3
    /// contributors, -4 owners, -5 editors, -6 current; from 1 an index into the user table), a 16-bit 1 and a 32-bit id.
    /// </summary>
    private void ReadProtectionRange(bool start)
    {
        string text = ReadText();
        Span<byte> bytes = stackalloc byte[8];
        if (commentDepth > 0 || !TryHex(text, bytes))
        {
            return;
        }

        short editor = BinaryPrimitives.ReadInt16LittleEndian(bytes);
        int id = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        if (!start)
        {
            AddInline(new EditableRangeEnd(document, id));
            return;
        }

        EditableRangeStart range = editor >= 1 && editor <= protectionUsers.Count && protectionUsers[editor - 1].Length > 0
            ? new EditableRangeStart(document, id, protectionUsers[editor - 1])
            : new EditableRangeStart(document, id, editor switch
            {
                -2 => EditorGroup.Administrators,
                -3 => EditorGroup.Contributors,
                -4 => EditorGroup.Owners,
                -5 => EditorGroup.Editors,
                -6 => EditorGroup.Current,
                _ => EditorGroup.Everyone,
            });
        AddInline(range);
    }

    private void ReadPasswordHash()
    {
        string text = ReadText();
        if (text.Length is > 0 and <= 4096 && text.Length % 2 == 0)
        {
            byte[] bytes = new byte[text.Length / 2];
            if (TryHex(text, bytes))
            {
                passwordHash = bytes;
            }
        }
    }

    /// <summary>Sets the document's protection from the words and the hash, once the whole file is read.</summary>
    private void FinishProtection()
    {
        ProtectionKind kind = readProtected ? ProtectionKind.ReadOnly
            : formsProtected ? ProtectionKind.Forms
            : revisionsProtected ? ProtectionKind.TrackedChanges
            : commentsProtected ? ProtectionKind.Comments
            : protectionLevel switch
            {
                0 when enforceProtection is not null => ProtectionKind.TrackedChanges,
                1 when enforceProtection is not null => ProtectionKind.Comments,
                2 when enforceProtection is not null => ProtectionKind.Forms,
                3 when enforceProtection is not null => ProtectionKind.ReadOnly,
                _ => ProtectionKind.None,
            };
        if (kind == ProtectionKind.None && passwordHash is null)
        {
            return;
        }

        // Word leaves \enforceprot out for forms; the restriction words alone mean it is on.
        bool enforced = enforceProtection is not 0;
        string? algorithm = null;
        byte[]? hash = null;
        byte[]? salt = null;
        int spins = 0;

        // The blob: version 2, its size, the provider type, the CryptoAPI hash id, the spin count, the hash and salt sizes and
        // their offsets, then the hash and the salt.
        if (passwordHash is { Length: >= 40 } blob)
        {
            ReadOnlySpan<byte> span = blob;
            int algorithmId = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
            spins = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
            int hashLength = BinaryPrimitives.ReadInt32LittleEndian(span[20..]);
            int saltLength = BinaryPrimitives.ReadInt32LittleEndian(span[24..]);
            int hashOffset = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
            int saltOffset = BinaryPrimitives.ReadInt32LittleEndian(span[32..]);
            if (hashLength is > 0 and <= 512 && saltLength is >= 0 and <= 512 && hashOffset >= 0 && saltOffset >= 0
                && (long)hashOffset + hashLength <= blob.Length && (long)saltOffset + saltLength <= blob.Length)
            {
                algorithm = RtfProtection.AlgorithmFromId(algorithmId);
                hash = span.Slice(hashOffset, hashLength).ToArray();
                salt = span.Slice(saltOffset, saltLength).ToArray();
            }
        }

        document.Protection.Set(kind, enforced, algorithm, hash, salt, spins);
    }

    private static bool TryHex(string text, Span<byte> bytes)
    {
        text = text.Trim();
        if (text.Length != bytes.Length * 2)
        {
            return false;
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            int high = HexValue(text[2 * i]);
            int low = HexValue(text[(2 * i) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            bytes[i] = (byte)((high << 4) | low);
        }

        return true;

        static int HexValue(char c)
        {
            return c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
        }
    }
}

/// <summary>The CryptoAPI hash ids of RTF's password hash.</summary>
internal static class RtfProtection
{
    public static string? AlgorithmFromId(int id)
    {
        return id switch
        {
            0x8003 => "MD5",
            0x8004 => "SHA-1",
            0x800C => "SHA-256",
            0x800D => "SHA-384",
            0x800E => "SHA-512",
            _ => null,
        };
    }

    public static int IdFromAlgorithm(string algorithm)
    {
        return DocumentProtection.SidFromAlgorithm(algorithm) switch
        {
            3 => 0x8003,
            4 => 0x8004,
            12 => 0x800C,
            13 => 0x800D,
            14 => 0x800E,
            _ => 0,
        };
    }
}
