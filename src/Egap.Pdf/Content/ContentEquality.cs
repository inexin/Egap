namespace Egap.Pdf.Content;

/// <summary>
/// Whether two operations say the same thing: the same operator and operands of the same types and values, arrays and
/// dictionaries compared item by item, and for inline images the same parameters and data. Used to check round trips and to
/// tell which streams changed.
/// </summary>
internal static class ContentEquality
{
    public static bool Same(PdfOperation a, PdfOperation b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Operator != b.Operator || a.OperandArray.Length != b.OperandArray.Length)
        {
            return false;
        }

        if (a is PdfInlineImage imageA)
        {
            return b is PdfInlineImage imageB && imageA.Data.SequenceEqual(imageB.Data) && Same(imageA.Parameters, imageB.Parameters, 0);
        }

        if (b is PdfInlineImage)
        {
            return false;
        }

        for (int i = 0; i < a.OperandArray.Length; i++)
        {
            if (!Same(a.OperandArray[i], b.OperandArray[i], 0))
            {
                return false;
            }
        }

        return true;
    }

    public static bool Same(IReadOnlyList<PdfOperation> a, IReadOnlyList<PdfOperation> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!Same(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Same(PdfObject a, PdfObject b, int depth)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (depth > Objects.ObjectWriter.MaxDepth)
        {
            return false;
        }

        switch (a)
        {
            case PdfArray arrayA when b is PdfArray arrayB:
                if (arrayA.Count != arrayB.Count)
                {
                    return false;
                }

                for (int i = 0; i < arrayA.Count; i++)
                {
                    if (!Same(arrayA[i], arrayB[i], depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case PdfDictionary dictionaryA when b is PdfDictionary dictionaryB:
                if (dictionaryA.Count != dictionaryB.Count)
                {
                    return false;
                }

                foreach ((PdfName key, PdfObject value) in dictionaryA.Entries)
                {
                    if (dictionaryB.Get(key) is not PdfObject other || !Same(value, other, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case PdfString stringA when b is PdfString stringB:
                return stringA.Bytes.SequenceEqual(stringB.Bytes);
            default:
                return a.GetType() == b.GetType() && a.Equals(b);
        }
    }
}
