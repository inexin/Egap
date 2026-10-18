using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Signatures.Cms;

/// <summary>An algorithm identifier: the OID and its encoded parameters, if any (RFC 5280, 4.1.1.2).</summary>
internal readonly record struct CmsAlgorithm(string Oid, ReadOnlyMemory<byte>? Parameters = null)
{
    public void Write(AsnWriter writer)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(Oid);
            if (Parameters is { } parameters)
            {
                writer.WriteEncodedValue(parameters.Span);
            }
        }
    }

    public static CmsAlgorithm Read(AsnReader reader)
    {
        AsnReader sequence = reader.ReadSequence();
        string oid = sequence.ReadObjectIdentifier();
        ReadOnlyMemory<byte>? parameters = sequence.HasData ? sequence.ReadEncodedValue() : null;
        return new CmsAlgorithm(oid, parameters);
    }
}

/// <summary>
/// The digest and signature algorithms of Phase 5 design Q4: SHA-256 (default), SHA-384 and SHA-512 written; SHA-1 and
/// SHA-224 read too. RSA PKCS #1 v1.5, RSA-PSS and ECDSA on P-256, P-384 and P-521 signed and verified with .NET's own RSA and
/// ECDsa, so a subclass of either reaches an HSM or a cloud key (design Q3).
/// </summary>
internal static class CmsAlgorithms
{
    private static readonly byte[] DerNull = [0x05, 0x00];

    /// <summary>The hash for a digest algorithm OID, or null for one Egap does not know.</summary>
    public static HashAlgorithmName? HashFromDigestOid(string oid)
    {
        return oid switch
        {
            CmsOids.Sha1 => HashAlgorithmName.SHA1,
            CmsOids.Sha256 => HashAlgorithmName.SHA256,
            CmsOids.Sha384 => HashAlgorithmName.SHA384,
            CmsOids.Sha512 => HashAlgorithmName.SHA512,
            _ => null,
        };
    }

    /// <summary>The digest algorithm OID for a hash Egap writes.</summary>
    public static string DigestOid(HashAlgorithmName hash)
    {
        return hash.Name switch
        {
            "SHA256" => CmsOids.Sha256,
            "SHA384" => CmsOids.Sha384,
            "SHA512" => CmsOids.Sha512,
            "SHA1" => CmsOids.Sha1,
            _ => throw new ArgumentException($"The digest algorithm {hash.Name} is not supported.", nameof(hash)),
        };
    }

    /// <summary>Whether the hash is too weak to trust today (SHA-1): read and verified, with a warning, never written (design Q4).</summary>
    public static bool IsWeak(HashAlgorithmName hash)
    {
        return hash == HashAlgorithmName.SHA1;
    }

    /// <summary>The hash of the data with the named algorithm.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "SHA-1 is read in old signatures and reported as weak (design Q4); Egap never signs with it.")]
    public static byte[] Hash(HashAlgorithmName hash, ReadOnlySpan<byte> data)
    {
        return hash.Name switch
        {
            "SHA1" => SHA1.HashData(data),
            "SHA256" => SHA256.HashData(data),
            "SHA384" => SHA384.HashData(data),
            "SHA512" => SHA512.HashData(data),
            _ => throw new ArgumentException($"The digest algorithm {hash.Name} is not supported.", nameof(hash)),
        };
    }

    /// <summary>The hash of a stream with the named algorithm, read once.</summary>
    public static byte[] Hash(HashAlgorithmName hash, Stream data)
    {
        using IncrementalHash incremental = IncrementalHash.CreateHash(hash);
        byte[] buffer = new byte[256 << 10];
        for (int read; (read = data.Read(buffer)) > 0;)
        {
            incremental.AppendData(buffer, 0, read);
        }

        return incremental.GetHashAndReset();
    }

    /// <summary>
    /// Signs <paramref name="data"/> (the encoded signed attributes) with the key and returns the signature with the algorithm
    /// identifier to write: <c>sha*WithRSAEncryption</c> for PKCS #1 v1.5, <c>RSASSA-PSS</c> with its parameters (salt as long
    /// as the hash), or <c>ecdsa-with-SHA*</c> with a DER signature.
    /// </summary>
    public static (byte[] Signature, CmsAlgorithm Algorithm) Sign(AsymmetricAlgorithm key, HashAlgorithmName hash, bool pss, ReadOnlySpan<byte> data)
    {
        switch (key)
        {
            case RSA rsa when pss:
                return (rsa.SignData(data, hash, RSASignaturePadding.Pss), PssAlgorithm(hash));
            case RSA rsa:
                return (rsa.SignData(data, hash, RSASignaturePadding.Pkcs1), new CmsAlgorithm(RsaOid(hash), DerNull));
            case ECDsa ecdsa:
                return (ecdsa.SignData(data, hash, DSASignatureFormat.Rfc3279DerSequence), new CmsAlgorithm(EcdsaOid(hash)));
            default:
                throw new ArgumentException($"Signing with a {key.GetType().Name} key is not supported; use an RSA or ECDsa key.", nameof(key));
        }
    }

    /// <summary>
    /// Whether <paramref name="signature"/> over <paramref name="data"/> verifies with the certificate's public key, by the
    /// signature algorithm identifier and the signer's digest algorithm. An algorithm Egap does not know does not verify.
    /// </summary>
    public static bool Verify(CmsAlgorithm algorithm, HashAlgorithmName digest, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, X509Certificate2 certificate)
    {
        try
        {
            switch (algorithm.Oid)
            {
                case CmsOids.RsaEncryption or CmsOids.Sha1WithRsa or CmsOids.Sha256WithRsa or CmsOids.Sha384WithRsa or CmsOids.Sha512WithRsa:
                    {
                        using RSA? rsa = certificate.GetRSAPublicKey();
                        HashAlgorithmName hash = algorithm.Oid == CmsOids.RsaEncryption ? digest : RsaHash(algorithm.Oid);
                        return rsa is not null && rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1);
                    }

                case CmsOids.RsaPss:
                    {
                        using RSA? rsa = certificate.GetRSAPublicKey();
                        if (rsa is null || ReadPss(algorithm.Parameters) is not (HashAlgorithmName hash, int salt))
                        {
                            return false;
                        }

                        // .NET verifies PSS with the salt as long as the hash, which RFC 4055 recommends and signers use.
                        return salt == HashSize(hash) && rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pss);
                    }

                case CmsOids.EcdsaWithSha1 or CmsOids.EcdsaWithSha256 or CmsOids.EcdsaWithSha384 or CmsOids.EcdsaWithSha512:
                    {
                        using ECDsa? ecdsa = certificate.GetECDsaPublicKey();
                        return ecdsa is not null && ecdsa.VerifyData(data, signature, EcdsaHash(algorithm.Oid), DSASignatureFormat.Rfc3279DerSequence);
                    }

                case CmsOids.EcPublicKey:
                    {
                        using ECDsa? ecdsa = certificate.GetECDsaPublicKey();
                        return ecdsa is not null && ecdsa.VerifyData(data, signature, digest, DSASignatureFormat.Rfc3279DerSequence);
                    }

                default:
                    return false;
            }
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            // macOS throws ArgumentException for a public key it cannot load, where other systems throw CryptographicException.
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="signature"/> verifies over a precomputed hash, for a signer without signed attributes, whose
    /// signature covers the content itself (RFC 5652, 5.4), hashed as the file streams past.
    /// </summary>
    public static bool VerifyHash(CmsAlgorithm algorithm, HashAlgorithmName digest, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, X509Certificate2 certificate)
    {
        try
        {
            if (SignatureHash(algorithm, digest) != digest)
            {
                return false;
            }

            switch (SignatureName(algorithm))
            {
                case "RSA":
                    {
                        using RSA? rsa = certificate.GetRSAPublicKey();
                        return rsa is not null && rsa.VerifyHash(hash, signature, digest, RSASignaturePadding.Pkcs1);
                    }

                case "RSA-PSS":
                    {
                        using RSA? rsa = certificate.GetRSAPublicKey();
                        return rsa is not null && ReadPss(algorithm.Parameters) is (_, int salt) && salt == HashSize(digest) && rsa.VerifyHash(hash, signature, digest, RSASignaturePadding.Pss);
                    }

                case "ECDSA":
                    {
                        using ECDsa? ecdsa = certificate.GetECDsaPublicKey();
                        return ecdsa is not null && ecdsa.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence);
                    }

                default:
                    return false;
            }
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            // macOS throws ArgumentException for a public key it cannot load, where other systems throw CryptographicException.
            return false;
        }
    }

    /// <summary>The hash a signature algorithm identifier names, or the signer's digest for one that names none.</summary>
    public static HashAlgorithmName? SignatureHash(CmsAlgorithm algorithm, HashAlgorithmName digest)
    {
        return algorithm.Oid switch
        {
            CmsOids.RsaEncryption or CmsOids.EcPublicKey => digest,
            CmsOids.Sha1WithRsa or CmsOids.Sha256WithRsa or CmsOids.Sha384WithRsa or CmsOids.Sha512WithRsa => RsaHash(algorithm.Oid),
            CmsOids.EcdsaWithSha1 or CmsOids.EcdsaWithSha256 or CmsOids.EcdsaWithSha384 or CmsOids.EcdsaWithSha512 => EcdsaHash(algorithm.Oid),
            CmsOids.RsaPss => ReadPss(algorithm.Parameters)?.Hash,
            _ => null,
        };
    }

    /// <summary>A short name for a signature algorithm, for reports: <c>RSA</c>, <c>RSA-PSS</c>, <c>ECDSA</c> or the OID.</summary>
    public static string SignatureName(CmsAlgorithm algorithm)
    {
        return algorithm.Oid switch
        {
            CmsOids.RsaEncryption or CmsOids.Sha1WithRsa or CmsOids.Sha256WithRsa or CmsOids.Sha384WithRsa or CmsOids.Sha512WithRsa => "RSA",
            CmsOids.RsaPss => "RSA-PSS",
            CmsOids.EcPublicKey or CmsOids.EcdsaWithSha1 or CmsOids.EcdsaWithSha256 or CmsOids.EcdsaWithSha384 or CmsOids.EcdsaWithSha512 => "ECDSA",
            _ => algorithm.Oid,
        };
    }

    private static string RsaOid(HashAlgorithmName hash)
    {
        return hash.Name switch
        {
            "SHA256" => CmsOids.Sha256WithRsa,
            "SHA384" => CmsOids.Sha384WithRsa,
            "SHA512" => CmsOids.Sha512WithRsa,
            _ => throw new ArgumentException($"Signing with {hash.Name} is not supported.", nameof(hash)),
        };
    }

    private static string EcdsaOid(HashAlgorithmName hash)
    {
        return hash.Name switch
        {
            "SHA256" => CmsOids.EcdsaWithSha256,
            "SHA384" => CmsOids.EcdsaWithSha384,
            "SHA512" => CmsOids.EcdsaWithSha512,
            _ => throw new ArgumentException($"Signing with {hash.Name} is not supported.", nameof(hash)),
        };
    }

    private static HashAlgorithmName RsaHash(string oid)
    {
        return oid switch
        {
            CmsOids.Sha1WithRsa => HashAlgorithmName.SHA1,
            CmsOids.Sha384WithRsa => HashAlgorithmName.SHA384,
            CmsOids.Sha512WithRsa => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };
    }

    private static HashAlgorithmName EcdsaHash(string oid)
    {
        return oid switch
        {
            CmsOids.EcdsaWithSha1 => HashAlgorithmName.SHA1,
            CmsOids.EcdsaWithSha384 => HashAlgorithmName.SHA384,
            CmsOids.EcdsaWithSha512 => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };
    }

    private static int HashSize(HashAlgorithmName hash)
    {
        return hash.Name switch
        {
            "SHA1" => 20,
            "SHA384" => 48,
            "SHA512" => 64,
            _ => 32,
        };
    }

    /// <summary>
    /// RSASSA-PSS parameters (RFC 4055, 3.1) for the hash, with MGF1 over the same hash and a salt as long as the hash, the
    /// only form .NET signs.
    /// </summary>
    private static CmsAlgorithm PssAlgorithm(HashAlgorithmName hash)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                new CmsAlgorithm(DigestOid(hash), DerNull).Write(writer);
            }

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
            {
                AsnWriter inner = new(AsnEncodingRules.DER);
                new CmsAlgorithm(DigestOid(hash), DerNull).Write(inner);
                new CmsAlgorithm(CmsOids.Mgf1, inner.Encode()).Write(writer);
            }

            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
            {
                writer.WriteInteger(HashSize(hash));
            }
        }

        return new CmsAlgorithm(CmsOids.RsaPss, writer.Encode());
    }

    /// <summary>The hash and salt length of RSASSA-PSS parameters; defaults are SHA-1 and 20 (RFC 4055); null when unreadable or the MGF hash differs.</summary>
    private static (HashAlgorithmName Hash, int Salt)? ReadPss(ReadOnlyMemory<byte>? parameters)
    {
        if (parameters is not { } encoded)
        {
            return null;
        }

        try
        {
            AsnReader sequence = new AsnReader(encoded, AsnEncodingRules.BER).ReadSequence();
            HashAlgorithmName hash = HashAlgorithmName.SHA1;
            HashAlgorithmName mgfHash = HashAlgorithmName.SHA1;
            int salt = 20;
            Asn1Tag hashTag = new(TagClass.ContextSpecific, 0), mgfTag = new(TagClass.ContextSpecific, 1), saltTag = new(TagClass.ContextSpecific, 2);
            if (sequence.HasData && sequence.PeekTag().HasSameClassAndValue(hashTag))
            {
                hash = HashFromDigestOid(CmsAlgorithm.Read(sequence.ReadSequence(hashTag)).Oid) ?? throw new AsnContentException();
            }

            if (sequence.HasData && sequence.PeekTag().HasSameClassAndValue(mgfTag))
            {
                CmsAlgorithm mgf = CmsAlgorithm.Read(sequence.ReadSequence(mgfTag));
                if (mgf.Oid != CmsOids.Mgf1 || mgf.Parameters is not { } mgfParameters)
                {
                    return null;
                }

                mgfHash = HashFromDigestOid(CmsAlgorithm.Read(new AsnReader(mgfParameters, AsnEncodingRules.BER)).Oid) ?? throw new AsnContentException();
            }

            if (sequence.HasData && sequence.PeekTag().HasSameClassAndValue(saltTag))
            {
                salt = sequence.ReadSequence(saltTag).ReadInteger() is var value && value >= 0 && value <= 1024 ? (int)value : throw new AsnContentException();
            }

            return mgfHash == hash ? (hash, salt) : null;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }
}
