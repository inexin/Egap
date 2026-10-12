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
