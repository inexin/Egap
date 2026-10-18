namespace Egap.Pdf.Optimizing;

/// <summary>
/// The optimizer (Phase 7 design 7.1, Build Plan task 8): its passes in their fixed order, each switchable in
/// <see cref="PdfOptimizeOptions"/>: images, duplicates, fonts, clean-up; then the document is marked so that every save
/// is a compact rewrite (object streams, a cross-reference stream, unused objects left out, modification date kept). A
/// signed document is left alone unless the options allow breaking its signatures. A PDF/A document keeps its conformance:
/// PDF/A-1, which forbids object and cross-reference streams, is written with a classic table, and fonts whose CIDSet or
/// CharSet the rules require to be right are left whole.
/// </summary>
internal static class Optimizer
{
    /// <summary>The image settings a preset's switches give.</summary>
    internal static ImageSettings Images(PdfOptimizeOptions options) => new()
    {
        Lossy = options.LossyImages,
        TargetResolution = options.LossyImages ? options.ImageResolution : null,
        Threshold = options.DownsampleThreshold,
        JpegQuality = options.JpegQuality,
    };

    public static PdfOptimizeReport Run(PdfDocument document, PdfOptimizeOptions options)
    {
        long before = document.SourceLength;
        List<string> skipped = [];
        if (!options.AllowInvalidatingSignatures && document.Signatures.Count > 0)
        {
            skipped.Add("everything: the document is signed, and optimizing would break its signatures (AllowInvalidatingSignatures)");
            return new PdfOptimizeReport(before, before, [], new PdfOptimizeSavings(0, 0, 0, 0, 0, 0, 0, 0), skipped);
        }

        bool pdfa1 = PdfA.PdfAWriter.ClaimedPart(document) == 1;
        List<PdfOptimizedImage> images = [];
        long imageBytes = 0;
        if (options.OptimizeImages)
        {
            ImageSettings settings = Images(options);
            foreach (SurveyedImage image in ImageSurvey.Run(document))
            {
                if (ImageRecompressor.Optimize(document, image, settings) is ImageChange change)
                {
                    images.Add(new PdfOptimizedImage(image.Pages.Count > 0 ? image.Pages[0] + 1 : 0, change.OldWidth, change.OldHeight, change.NewWidth,
                        change.NewHeight, change.OldBytes, change.NewBytes, change.OldResolution, change.NewResolution, change.Encoding));
                    imageBytes += change.OldBytes - change.NewBytes;
                }
            }
        }

        (int merged, long mergedBytes) = options.MergeDuplicates ? DuplicateStreams.Run(document) : (0, 0);
        if (options.MergeDuplicates && pdfa1)
        {
            skipped.Add("duplicates: PDF/A-1 is written without object streams, and only a compact rewrite writes duplicates once");
        }

        (int fonts, long fontBytes) = (0, 0);
        if (options.SubsetFonts && pdfa1)
        {
            skipped.Add("fonts: PDF/A-1 subset fonts must list their glyphs in a CIDSet or CharSet, which is not written");
        }
        else if (options.SubsetFonts)
        {
            (fonts, fontBytes) = FontSubsetter.Run(document);
        }

        int thumbnails = 0, privateData = 0;
        if (options.RemoveThumbnails || options.RemovePrivateData)
        {
            PdfSanitizeResult removed = Redaction.DocumentSanitizer.Apply(document, new PdfSanitizeOptions
            {
                Metadata = false,
                JavaScript = false,
                Attachments = false,
                HiddenLayers = false,
                Comments = false,
                FormData = false,
                Thumbnails = options.RemoveThumbnails,
                PrivateData = options.RemovePrivateData,
            });
            (thumbnails, privateData) = (removed.Thumbnails, removed.PrivateData);
        }

        int empty = options.RemoveEmptyContent ? RemoveEmptyContent(document) : 0;
        document.MarkOptimized();

        using CountingStream counter = new();
        document.Save(counter, new PdfSaveOptions { AllowInvalidatingSignatures = options.AllowInvalidatingSignatures });
        document.SignaturesMayBreak = options.AllowInvalidatingSignatures;
        return new PdfOptimizeReport(before, counter.Length, images,
            new PdfOptimizeSavings(imageBytes, merged, mergedBytes, fonts, fontBytes, thumbnails, privateData, empty), skipped);
    }

    /// <summary>The save options an optimized document forces: a compact rewrite, unused objects dropped, the dates kept.</summary>
    internal static PdfSaveOptions Forced(PdfDocument document, PdfSaveOptions options)
    {
        bool pdfa1 = PdfA.PdfAWriter.ClaimedPart(document) == 1;
        return options with
        {
            Mode = PdfSaveMode.Rewrite,
            Compression = pdfa1 ? PdfCompression.None : PdfCompression.Compact,
            MergeIdenticalObjects = true,
            RemoveUnused = true,
            UpdateModificationDate = false,
            AllowInvalidatingSignatures = options.AllowInvalidatingSignatures || document.SignaturesMayBreak,
        };
    }

    /// <summary>Removes page content streams that hold nothing but white space; how many.</summary>
    private static int RemoveEmptyContent(PdfDocument document)
    {
        int removed = 0;
        foreach (PdfPage page in document.Pages)
        {
            switch (page.Dictionary.Get("Contents")?.Resolve())
            {
                case PdfStream single when Empty(single):
                    page.Dictionary.Remove("Contents");
                    removed++;
                    break;
                case PdfArray parts:
                    for (int i = parts.Count - 1; i >= 0; i--)
                    {
                        if (parts.Get(i)?.Resolve() is PdfStream part && Empty(part))
                        {
                            parts.RemoveAt(i);
                            removed++;
                        }
                    }

                    if (parts.Count == 0)
                    {
                        page.Dictionary.Remove("Contents");
                    }

                    break;
            }
        }

        return removed;
    }

    private static bool Empty(PdfStream stream)
    {
        try
        {
            return stream.GetDecodedBytes().All(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0);
        }
        catch (PdfException)
        {
            return false;
        }
    }

    /// <summary>A stream that only counts what is written to it.</summary>
    private sealed class CountingStream : Stream
    {
        private long length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => length;

        public override long Position
        {
            get => length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;
    }
}
