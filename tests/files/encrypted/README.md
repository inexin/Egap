# Encrypted test files

`plain.pdf` is written by hand for these tests. Every other file is `plain.pdf` encrypted by qpdf 12 (Apache-2.0) with
`--static-id`, so the files are the same on every run:

| File | Encryption | qpdf options |
| --- | --- | --- |
| `rc4-40.pdf` | RC4 40-bit, revision 2 | `--allow-weak-crypto --encrypt user owner 40` |
| `rc4-40-empty.pdf` | RC4 40-bit, empty passwords | `--allow-weak-crypto --encrypt "" "" 40` |
| `rc4-128.pdf` | RC4 128-bit, revision 3 | `--allow-weak-crypto --encrypt user owner 128 --use-aes=n` |
| `aes-128.pdf` | AES-128, revision 4 | `--encrypt user owner 128 --use-aes=y` |
| `aes-128-clear-metadata.pdf` | AES-128, metadata not encrypted | `--encrypt user owner 128 --use-aes=y --cleartext-metadata` |
| `aes-128-object-streams.pdf` | AES-128, empty user password, object streams | `--object-streams=generate --encrypt "" owner 128 --use-aes=y` |
| `aes-256-r5.pdf` | AES-256, revision 5 (deprecated) | `--allow-weak-crypto --encrypt user owner 256 --force-R5` |
| `aes-256.pdf` | AES-256, revision 6 | `--encrypt user owner 256` |
| `aes-256-empty-user.pdf` | AES-256, empty user password, no printing or extraction | `--encrypt "" owner 256 --print=none --extract=n` |
| `aes-256-object-streams.pdf` | AES-256, object streams | `--object-streams=generate --encrypt user owner 256` |

The user password is `user` and the owner password `owner` unless the table says otherwise.

`libreoffice-standard.docx` is a short document of Egap's own text (a line of Latin, Norwegian and Japanese, then
"Paragraph 0" to "Paragraph 399") that LibreOffice (Windows, November 2026) saved as Word 2007-365 with the password `Lo pässwörd`, through UNO
with the `Password` property. LibreOffice writes Office 2007's standard encryption (AES-128 with SHA-1), so the file checks
the standard decryption against a writer that is not Egap's.

The three ODT files are `tests/fuzz-seeds/odt/own-features.fodt`, Egap's own document, that LibreOffice 26.8 (Windows,
November 2026) saved as ODT with the password `pw`, through UNO with the `Password` property and the ODF version option
set before each save:

| File | ODF version option | Encryption |
| --- | --- | --- |
| `libreoffice-blowfish.odt` | ODF 1.0/1.1 | Each file with Blowfish in 64-bit CFB mode, PBKDF2 from a SHA-1 start key, SHA-1/1K checksums |
| `libreoffice-aes-cbc.odt` | ODF 1.3 | Each file with AES-256 in CBC mode, PBKDF2 from a SHA-256 start key, SHA-256/1K checksums |
| `libreoffice-aes-gcm.odt` | ODF 1.4 Extended (LibreOffice's default) | The whole package in `encrypted-package` with AES-256 in GCM mode, Argon2id from a SHA-256 start key |
