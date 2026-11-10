using System.Buffers.Binary;

using Egap.Documents;
using Egap.Documents.Doc;

namespace Egap.Spreadsheets.Xls;

/// <summary>
/// Decrypts the workbook stream of an encrypted Excel 97-2003 file ([MS-XLS] 2.2.10, [MS-OFFCRYPTO] 2.3.5 and 2.3.6). The
/// FILEPASS record after the first BOF holds an RC4 or RC4 CryptoAPI header; the stream is then encrypted as a whole in
/// 1024-byte blocks, each with its own key, counted from the stream's start, except for the record headers, a few records
/// that stay clear and the stream offset that starts each BOUNDSHEET record.
/// </summary>
internal static class XlsEncryption
{
    private const int BlockLength = 1024;

    /// <summary>
    /// The stream decrypted, or the stream as it is when it has no FILEPASS record. Without a password, Excel's default one
    /// is tried, as Excel does for a workbook encrypted only to protect it from writing.
    /// </summary>
    /// <exception cref="DocumentPasswordException">No password was given and the default one is not it, or the password is wrong.</exception>
    /// <exception cref="DocumentException">The encryption is one Egap does not read, or its header is damaged.</exception>
    public static byte[] Decrypt(byte[] data, string? password)
    {
        if (FilePass(data) is not { } filePass)
        {
            return data;
        }

        ReadOnlySpan<byte> body = data.AsSpan(filePass.Start, filePass.Length);
        int type = body.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(body) : -1;
        if (type == 0)
        {
            throw new DocumentException("The workbook is encrypted with XOR obfuscation, which Egap does not read; open it in Excel and save it with a password again.");
        }

        if (type != 1)
        {
            throw new DocumentException("The workbook's FILEPASS record is damaged.");
        }

        Func<string, DocEncryption.IDecryptor?> keyFor = DocEncryption.RcKeyMaker(body[2..], BlockLength, "workbook");
        DocEncryption.IDecryptor? decryptor = keyFor(password ?? XlsReader.DefaultPassword);
        if (decryptor is null)
        {
            throw password is null
                ? new DocumentPasswordException("The workbook is encrypted: give its password in the load options.", wrongPassword: false)
                : new DocumentPasswordException("The password is wrong: the workbook is encrypted with another one.", wrongPassword: true);
        }

        byte[] plain = [.. data];
        decryptor.Apply(plain, 0);

        // The headers and the records that stay clear are taken from the stream as it was.
        byte[] result = [.. data];
        for (int offset = 0; offset <= data.Length - 4;)
        {
            int record = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            int start = offset + 4;
            int length = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2)), data.Length - start);
            if (!StaysClear(record))
            {
                int from = record == BiffType.BoundSheet ? Math.Min(start + 4, start + length) : start;
                plain.AsSpan(from, start + length - from).CopyTo(result.AsSpan(from));
            }

            offset = start + length;
        }

        return result;
    }

    /// <summary>The FILEPASS record among the globals' first records, or null.</summary>
    private static (int Start, int Length)? FilePass(byte[] data)
    {
        BiffStream biff = new(data);
        for (int i = 0; i < 16 && biff.Read(); i++)
        {
            if (biff.Type == BiffType.FilePass)
            {
                return (biff.Start, biff.Length);
            }

            if (biff.Type == BiffType.Eof)
            {
                break;
            }
        }

        return null;
    }

    // BOF, FILEPASS, USREXCL, FILELOCK, INTERFACEHDR, RRDINFO and RRDHEAD are never encrypted.
    private static bool StaysClear(int record) => record is BiffType.Bof or BiffType.FilePass or 0x0194 or 0x0195 or 0x00E1 or 0x0196 or 0x0138;
}
