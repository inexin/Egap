using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Egap.Documents.Html;

/// <summary>
/// An EPUB's Open Container (EPUB 3.3 OCF): the ZIP read whole into memory within <see cref="DocumentLimits"/> (entries,
/// part and total sizes, compression ratio), names checked so nothing points outside the container, fonts the book
/// obfuscates put back, and books under DRM refused (Egap 6.6 design Q6, Q11).
/// </summary>
internal sealed class EpubContainer
{
    public const string IdpfObfuscation = "http://www.idpf.org/2008/embedding";
    public const string AdobeObfuscation = "http://ns.adobe.com/pdf/enc#RC";

    private readonly Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> folded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> obfuscated = new(StringComparer.Ordinal);

    private EpubContainer(DocumentLimits limits)
    {
        Limits = limits;
    }

    public DocumentLimits Limits { get; }

    /// <summary>The entry names, in the ZIP's order.</summary>
    public List<string> Names { get; } = [];

    /// <exception cref="DocumentException">Not a ZIP, or a book under DRM.</exception>
    /// <exception cref="DocumentLimitException">Past a limit.</exception>
    public static EpubContainer Read(Stream stream, DocumentLimits limits)
    {
        EpubContainer container = new(limits);
        try
        {
            using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > limits.MaxEntries)
            {
                throw new DocumentLimitException($"The book has {zip.Entries.Count} entries, more than the limit of {limits.MaxEntries}.");
            }

            long total = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                // Zip slip: a name that climbs out of the container or is absolute is refused (OCF 4.2.1 forbids them).
                string name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal) || name.Split('/').Any(s => s == ".."))
                {
                    throw new DocumentException($"The book has an entry named \"{entry.FullName}\", which points outside it.");
                }

                if (entry.Length > limits.MaxPartSize)
                {
                    throw new DocumentLimitException($"Entry {name} is {entry.Length} bytes, more than the limit of {limits.MaxPartSize}.");
                }

                if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                {
                    throw new DocumentLimitException($"Entry {name} expands {entry.Length / entry.CompressedLength} times, past the limit of {limits.MaxCompressionRatio}.");
                }

                total += entry.Length;
                if (total > limits.MaxTotalSize)
                {
                    throw new DocumentLimitException($"The book's entries add up to more than the limit of {limits.MaxTotalSize} bytes.");
                }

                byte[] data = ReadEntry(entry, limits);
                if (container.entries.TryAdd(name, data))
                {
                    container.Names.Add(name);
                    container.folded.TryAdd(name, name);
                }
            }
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException("The file is not a valid ZIP container: " + e.Message, e);
        }

        return container;
    }

    /// <summary>
    /// An entry's bytes by its full name, obfuscation undone; names are matched exactly first, then ignoring case, as books
    /// made on Windows often get the case wrong.
    /// </summary>
    public byte[]? Get(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (!entries.TryGetValue(name, out byte[]? data) && folded.TryGetValue(name, out string? real))
        {
            name = real;
            data = entries[real];
        }

        if (data is not null && obfuscated.TryGetValue(name, out string? key))
        {
            data = Deobfuscate(data, key);
        }

        return data;
    }

    public bool Contains(string name) => entries.ContainsKey(name) || folded.ContainsKey(name);

    public XDocument? GetXml(string? name)
    {
        return Get(name) is { } bytes ? LoadXml(bytes, Limits) : null;
    }

    /// <summary>XML read safely: DTDs ignored and never fetched, no external entities, a size cap.</summary>
    public static XDocument LoadXml(byte[] bytes, DocumentLimits limits)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = limits.MaxPartSize,
            CloseInput = true,
        };

        try
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            throw new DocumentException("The book has XML that does not parse: " + e.Message, e);
        }
    }

    /// <summary>
    /// <paramref name="href"/> (percent-encoded, relative to the entry <paramref name="from"/>) as a full entry name, its
    /// fragment in <paramref name="fragment"/>; null when it points outside the container or is a URL of its own.
    /// </summary>
    public static string? Resolve(string from, string href, out string? fragment)
    {
        fragment = null;
        int hash = href.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = Uri.UnescapeDataString(href[(hash + 1)..]);
            href = href[..hash];
        }

        int query = href.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            href = href[..query];
        }

        if (href.Length == 0)
        {
            return from;
        }

        if (href.StartsWith('/') || (href.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon && href[..colon].All(char.IsAsciiLetterOrDigit)))
        {
            return null;
        }

        string folder = from.LastIndexOf('/') is >= 0 and var slash ? from[..(slash + 1)] : "";
        List<string> segments = [.. folder.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string segment in Uri.UnescapeDataString(href).Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Reads META-INF/encryption.xml: fonts obfuscated with the IDPF or Adobe algorithm are noted for undoing with the key
    /// the book's identifiers give; anything else encrypted is DRM, and the book is refused naming the scheme.
    /// </summary>
    /// <exception cref="DocumentException">The book is under DRM.</exception>
    public void ReadEncryption(string uniqueIdentifier, IEnumerable<string> identifiers)
    {
        if (GetXml("META-INF/encryption.xml") is not { Root: { } root })
        {
            return;
        }

        XNamespace xenc = "http://www.w3.org/2001/04/xmlenc#";
        foreach (XElement data in root.Descendants(xenc + "EncryptedData"))
        {
            string algorithm = (string?)data.Element(xenc + "EncryptionMethod")?.Attribute("Algorithm") ?? "";
            string? uri = (string?)data.Descendants(xenc + "CipherReference").FirstOrDefault()?.Attribute("URI");
            if (uri is null)
            {
                continue;
            }

            string? name = Resolve("", uri, out _);
            if (name is null)
            {
                continue;
            }

            if (algorithm == IdpfObfuscation)
            {
                obfuscated[RealName(name)] = "idpf:" + uniqueIdentifier;
            }
            else if (algorithm == AdobeObfuscation)
            {
                string uuid = identifiers.FirstOrDefault(i => i.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) ?? uniqueIdentifier;
                obfuscated[RealName(name)] = "adobe:" + uuid;
            }
            else
            {
                throw new DocumentException($"The book is protected by {DrmScheme(algorithm)}, which Egap does not remove: only its publisher's reading systems open it.");
            }
        }
    }

    private string RealName(string name) => folded.TryGetValue(name, out string? real) ? real : name;

    /// <summary>The DRM scheme the container's files name: Adobe ADEPT, Readium LCP, Apple FairPlay, or the algorithm.</summary>
    private string DrmScheme(string algorithm)
    {
        if (Contains("META-INF/license.lcpl"))
        {
            return "Readium LCP DRM";
        }

        if (Contains("META-INF/sinf.xml"))
        {
            return "Apple FairPlay DRM";
        }

        if (Get("META-INF/rights.xml") is { } rights && Encoding.UTF8.GetString(rights).Contains("ns.adobe.com/adept", StringComparison.Ordinal))
        {
            return "Adobe ADEPT DRM";
        }

        return $"encryption ({(algorithm.Length > 0 ? algorithm : "an unnamed algorithm")})";
    }

    /// <summary>
    /// Undoes font obfuscation (EPUB 3.3 OCF 4.6 for IDPF: the first 1040 bytes XORed with the SHA-1 of the unique identifier
    /// without white space; Adobe's: the first 1024 bytes XORed with the 16 bytes of the book's urn:uuid identifier).
    /// </summary>
    internal static byte[] Deobfuscate(byte[] data, string key)
    {
        byte[] mask;
        int count;
        if (key.StartsWith("idpf:", StringComparison.Ordinal))
        {
            string id = new([.. key[5..].Where(c => c is not (' ' or '\t' or '\r' or '\n'))]);
#pragma warning disable CA5350 // The OCF obfuscation algorithm is defined with SHA-1; it hides fonts from copying, it does not protect secrets.
            mask = SHA1.HashData(Encoding.UTF8.GetBytes(id));
#pragma warning restore CA5350
            count = 1040;
        }
        else
        {
            string hex = new([.. key[6..].Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase).Where(char.IsAsciiHexDigit)]);
            if (hex.Length < 32)
            {
                return data;
            }

            mask = Convert.FromHexString(hex[..32]);
            count = 1024;
        }

        byte[] result = (byte[])data.Clone();
        for (int i = 0; i < Math.Min(count, result.Length); i++)
        {
            result[i] ^= mask[i % mask.Length];
        }

        return result;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, DocumentLimits limits)
    {
        using Stream input = entry.Open();
        using MemoryStream output = new(capacity: (int)Math.Min(Math.Max(entry.Length, 0), 64L * 1024 * 1024));
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            total += read;
            if (total > limits.MaxPartSize)
            {
                throw new DocumentLimitException($"Entry {entry.FullName} grows past the limit of {limits.MaxPartSize} bytes when decompressed.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
