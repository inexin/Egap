using System.Buffers.Binary;

namespace Egap.Documents.Rtf;

/// <summary>Editing restrictions, the password hash, the editors' table and the editable ranges, as Word writes them.</summary>
internal sealed partial class RtfWriter
{
    private readonly List<string> protectionUsers = [];
    private readonly Dictionary<int, short> rangeEditors = [];

    private void WriteEditableRange(int id, EditableRangeStart? start)
    {
        short editor;
        if (start is null)
        {
            // Word repeats the start's editor at the end.
            editor = rangeEditors.GetValueOrDefault(id, (short)-1);
        }
        else if (start.Editor is { } name)
        {
            int index = protectionUsers.IndexOf(name);
            if (index < 0)
            {
                index = protectionUsers.Count;
                protectionUsers.Add(name);
            }

            editor = (short)Math.Min(index + 1, short.MaxValue);
        }
        else
        {
            editor = start.Group switch
            {
                EditorGroup.Administrators => -2,
                EditorGroup.Contributors => -3,
                EditorGroup.Owners => -4,
                EditorGroup.Editors => -5,
                EditorGroup.Current => -6,
                _ => -1,
            };
        }

        if (start is not null)
        {
            rangeEditors[id] = editor;
        }

        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, editor);
        BinaryPrimitives.WriteInt16LittleEndian(bytes[2..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], id);
        Destination(start is null ? "protend" : "protstart");
        Space();
        output.Append(Convert.ToHexStringLower(bytes));
        Close();
    }

    /// <summary>The editors the ranges name, after the style sheet; the ranges number them from 1.</summary>
    private void WriteProtectionUsers()
    {
        if (protectionUsers.Count == 0)
        {
            return;
        }

        Destination("protusertbl");
        foreach (string user in protectionUsers)
        {
            // Word writes the backslash in DOMAIN\user as a hex escape.
            Open();
            string[] parts = user.Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    output.Append(@"\'5c");
                    pendingSpace = false;
                }

                Text(parts[i]);
            }

            Close();
        }

        Close();
        output.AppendLine();
    }

    /// <summary>The password hash, in the information group: a header of ten 32-bit values, then the hash and the salt.</summary>
    private void WritePasswordHash()
    {
        DocumentProtection protection = document.Protection;
        if (!protection.IsEnforced || !protection.HasPassword || protection.HashAlgorithm is not { } algorithm
            || RtfProtection.IdFromAlgorithm(algorithm) is not (> 0 and int algorithmId))
        {
            return;
        }

        int hashLength = protection.Hash.Length;
        int saltLength = protection.Salt.Length;
        byte[] blob = new byte[40 + hashLength + saltLength];
        Span<byte> span = blob;
        BinaryPrimitives.WriteInt32LittleEndian(span, 2);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], blob.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], algorithmId);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], protection.SpinCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], hashLength);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], saltLength);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], 40 + hashLength);
        protection.Hash.Span.CopyTo(span[40..]);
        protection.Salt.Span.CopyTo(span[(40 + hashLength)..]);
        Destination("passwordhash");
        Space();
        output.Append(Convert.ToHexStringLower(blob));
        Close();
    }

    /// <summary>The restriction's words, as Word writes them: forms with \allprot, the others with \enforceprot1\protlevel3.</summary>
    private void WriteProtectionWords()
    {
        DocumentProtection protection = document.Protection;
        if (!protection.IsEnforced)
        {
            return;
        }

        switch (protection.Kind)
        {
            case ProtectionKind.Forms:
                Word("formprot");
                Word("allprot");
                return;
            case ProtectionKind.ReadOnly:
                Word("annotprot");
                Word("readprot");
                break;
            case ProtectionKind.Comments:
                Word("annotprot");
                break;
            case ProtectionKind.TrackedChanges:
                Word("revprot");
                break;
            default:
                return;
        }

        Word("enforceprot", 1);
        Word("protlevel", 3);
    }
}
