using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf.Content;

/// <summary>
/// Writes operations in the compact form of Phase 3 design Q4: one space between tokens, a newline after each operator, and
/// numbers in their shortest form that reads back to the same value. What it writes, <see cref="ContentParser"/> reads back to
/// equal operations.
/// </summary>
internal static class ContentSyntax
{
    public static void Write(PdfOperation operation, ByteWriter output)
    {
        if (operation is PdfInlineImage image)
        {
            output.Write("BI"u8);
            foreach ((PdfName key, PdfObject value) in image.Parameters.Entries)
            {
                output.Write((byte)' ');
                ObjectWriter.Write(key, output);
                output.Write((byte)' ');
                ObjectWriter.Write(value, output);
            }

            output.Write(" ID "u8);
            output.Write(image.Data);
            output.Write("\nEI\n"u8);
            return;
        }

        foreach (PdfObject operand in operation.OperandArray)
        {
            ObjectWriter.Write(operand, output);
            output.Write((byte)' ');
        }

        output.WriteAscii(operation.Operator);
        output.Write((byte)'\n');
    }

    public static byte[] ToBytes(IEnumerable<PdfOperation> operations)
    {
        ByteWriter output = new(4096);
        foreach (PdfOperation operation in operations)
        {
            Write(operation, output);
        }

        return output.ToArray();
    }
}
