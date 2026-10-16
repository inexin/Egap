# Signed test files

`base.pdf` is a one-page file written by hand (egap-compare's `SignedReferences.BaseFile`); `base-form.pdf` is the same with
a text field `Name`. Every other PDF here is one of them signed by pyHanko 0.37 (MIT) in egap-compare's Phase 5 harness
(`docker/pyhanko/sign.py`), with keys from its test PKI (`docker/pki`), made on 2026-10-16. The certificates are test
certificates for these files only: `root.crt` is the PKI's root (valid to 2046), `inter.crt` the intermediate that issued
every signer, `tsa.crt` the timestamp authority's certificate. The signer certificates are valid to 2031-10-15.

| File | Signature | sign.py options |
| --- | --- | --- |
| `pyhanko-bb-rsa.pdf` | PAdES B-B, RSA 2048, SHA-256 | `--pfx signer-rsa.pfx` |
| `pyhanko-bb-pkcs7.pdf` | adbe.pkcs7.detached, RSA, SHA-256 | `--pfx signer-rsa.pfx --subfilter pkcs7` |
| `pyhanko-bb-ec384-sha384.pdf` | PAdES B-B, ECDSA P-384, SHA-384 | `--pfx signer-ec384.pfx --md sha384` |
| `pyhanko-bt-ec.pdf` | PAdES B-T, ECDSA P-256 | `--pfx signer-ec.pfx --tsa .../tsa` |
| `pyhanko-blt-rsa.pdf` | PAdES B-LT: DSS with certificates, OCSP and CRLs | `--pfx signer-rsa.pfx --tsa .../tsa --ltv` |
| `pyhanko-blta-rsa.pdf` | PAdES B-LTA: B-LT plus a document timestamp | `--pfx signer-rsa.pfx --tsa .../tsa --lta` |
| `pyhanko-certify-1.pdf` | Certification, no changes allowed | `--pfx signer-rsa.pfx --certify 1` |
| `pyhanko-certify-2.pdf` | Certification, form filling and signing | `--pfx signer-rsa.pfx --certify 2` |
| `pyhanko-certify-3.pdf` | Certification, also annotations | `--pfx signer-rsa.pfx --certify 3` |
| `pyhanko-revoked.pdf` | PAdES B-B by a revoked signer | `--pfx signer-revoked.pfx` |
| `pyhanko-doc-timestamp.pdf` | A document timestamp only | `--doc-timestamp --tsa .../tsa --field DocTimeStamp1` |
| `pyhanko-two-signatures.pdf` | `pyhanko-certify-2.pdf` signed again by the EC signer | `--pfx signer-ec.pfx --field Signature2` |
| `pyhanko-form-certify-1.pdf` | `base-form.pdf` certified, no changes allowed | `--pfx signer-rsa.pfx --certify 1` |
| `pyhanko-form-certify-2.pdf` | `base-form.pdf` certified, form filling and signing | `--pfx signer-rsa.pfx --certify 2` |
| `pyhanko-form-certify-3.pdf` | `base-form.pdf` certified, also annotations | `--pfx signer-rsa.pfx --certify 3` |
| `pyhanko-form-approval.pdf` | `base-form.pdf` with an approval signature | `--pfx signer-rsa.pfx` |
