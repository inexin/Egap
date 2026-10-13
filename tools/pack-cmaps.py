"""Packs Adobe's predefined CMaps (cmap-resources, BSD-3-Clause) into the compact form Egap.Pdf embeds (Phase 3 design Q9).

Usage, from the repo root:
  python tools/pack-cmaps.py path/to/cmap-resources-20231115.zip

Writes src/Egap.Pdf/Fonts/Data/cmaps.bin: every CMap of the release (deprecated ones left out) as codespaces and CID ranges,
plus, for each character collection, the CIDs to Unicode from its cid2code.txt (UTF-32 columns, then UCS-2). Each entry is
deflated on its own so a reader inflates only the CMaps a file names. Layout (all integers unsigned LEB128 varints unless
noted):

  file   = "EGCM" (4 bytes), version 1, count, then count x (name length, name UTF-8, offset, length) relative to the end
           of the index, then the deflated entries
  cmap   = kind 0, wmode, usecmap length + name (0 for none), codespace count x (bytes, low..., high...),
           range count x (bytes, low delta, span, cid zigzag delta), the ranges sorted by (bytes, low)
  unicode= kind 1, run count x (cid delta from the previous run's end, run length - 1, code point zigzag delta)
"""
import io
import re
import struct
import sys
import zipfile
import zlib

TOKENS = re.compile(rb"<([0-9A-Fa-f\s]*)>|/([^\s/<>\[\]()%]+)|(-?\d+)|([A-Za-z][A-Za-z0-9]*)|%[^\n]*")
ORDERINGS = ["Japan1", "GB1", "CNS1", "Korea1", "KR"]


def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def zigzag(n):
    return (n << 1) ^ (n >> 63) if n < 0 else n << 1


def parse(text):
    """Returns (wmode, usecmap, codespaces, ranges) from a CMap's PostScript text."""
    wmode, use, spaces, ranges = 0, None, [], []
    operands, block = [], None
    for m in TOKENS.finditer(text):
        hexs, name, num, word = m.group(1), m.group(2), m.group(3), m.group(4)
        if hexs is not None:
            operands.append(bytes.fromhex(re.sub(rb"\s", b"", hexs).decode()))
        elif name is not None:
            operands.append(name.decode())
        elif num is not None:
            operands.append(int(num))
        elif word is not None:
            w = word.decode()
            if w in ("begincodespacerange", "begincidrange", "begincidchar"):
                operands, block = [], w
            elif w == "endcodespacerange":
                spaces += [(operands[i], operands[i + 1]) for i in range(0, len(operands) - 1, 2)]
                operands, block = [], None
            elif w == "endcidrange":
                for i in range(0, len(operands) - 2, 3):
                    lo, hi, cid = operands[i], operands[i + 1], operands[i + 2]
                    ranges.append((len(lo), int.from_bytes(lo, "big"), int.from_bytes(hi, "big"), cid))
                operands, block = [], None
            elif w == "endcidchar":
                for i in range(0, len(operands) - 1, 2):
                    code, cid = operands[i], operands[i + 1]
                    ranges.append((len(code), int.from_bytes(code, "big"), int.from_bytes(code, "big"), cid))
                operands, block = [], None
            elif block is None:
                if w == "usecmap" and operands and isinstance(operands[-1], str):
                    use = operands[-1]
                elif w == "def" and len(operands) >= 2 and operands[-2] == "WMode" and isinstance(operands[-1], int):
                    wmode = operands[-1]
                operands = []
    return wmode, use, spaces, ranges


def encode_cmap(wmode, use, spaces, ranges):
    out = bytearray([0])
    out += varint(wmode)
    name = (use or "").encode()
    out += varint(len(name)) + name
    out += varint(len(spaces))
    for lo, hi in spaces:
        out += varint(len(lo)) + lo + hi
    ranges = sorted(ranges)
    out += varint(len(ranges))
    prev_len, prev_end, prev_cid_end = 0, -1, -1
    for n, lo, hi, cid in ranges:
        if n != prev_len:
            prev_end = -1
        out += varint(n) + varint(lo - prev_end - 1) + varint(hi - lo) + varint(zigzag(cid - prev_cid_end - 1))
        prev_len, prev_end, prev_cid_end = n, hi, cid + (hi - lo)
    return bytes(out)


def encode_unicode(pairs):
    """pairs: cid -> code point. Runs of consecutive CIDs with consecutive code points."""
    runs = []
    for cid in sorted(pairs):
        cp = pairs[cid]
        if runs and cid == runs[-1][0] + runs[-1][1] and cp == runs[-1][2] + runs[-1][1]:
            runs[-1][1] += 1
        else:
            runs.append([cid, 1, cp])
    out = bytearray([1]) + varint(len(runs))
    prev_cid_end, prev_cp = -1, 0
    for cid, length, cp in runs:
        out += varint(cid - prev_cid_end - 1) + varint(length - 1) + varint(zigzag(cp - prev_cp))
        prev_cid_end, prev_cp = cid + length - 1, cp + length - 1
    return bytes(out)


def main():
    archive = zipfile.ZipFile(sys.argv[1])
    texts = {}
    for path in archive.namelist():
        if "/CMap/" in path and not path.endswith("/") and "/deprecated/" not in path:
            texts[path.rsplit("/", 1)[1]] = archive.read(path)
    entries = {}
    parsed = {}
    for name, text in sorted(texts.items()):
        parsed[name] = parse(text)
        entries[name] = encode_cmap(*parsed[name])
    for ordering in ORDERINGS:
        # cid2code.txt: each CID's codes in every CMap of the collection; Unicode from the UTF-32 columns first, then UCS-2
        # (half-width forms included), the first code where a CID has several.
        path = next(n for n in archive.namelist() if re.search(rf"/Adobe-{ordering}-\d+/cid2code\.txt$", n) and "/deprecated/" not in n)
        lines = archive.read(path).decode("latin-1").splitlines()
        header = next(l for l in lines if l.startswith("CID")).split("	")
        columns = [i for i, h in enumerate(header) if "UTF32" in h] + [i for i, h in enumerate(header) if "UCS2" in h]
        pairs = {}
        for line in lines:
            if line.startswith("#") or line.startswith("CID"):
                continue
            fields = line.split("	")
            for i in columns:
                if i < len(fields) and fields[i] != "*":
                    first = fields[i].split(",")[0].rstrip("v")
                    cp = int(first, 16)
                    if 0 < cp <= 0x10FFFF and not 0xD800 <= cp <= 0xDFFF:
                        pairs[int(fields[0])] = cp
                        break
        entries["ToUnicode/" + ordering] = encode_unicode(pairs)
    blobs = []
    index = bytearray()
    offset = 0
    for name, raw in entries.items():
        compressed = zlib.compress(raw, 9)
        encoded = name.encode()
        index += varint(len(encoded)) + encoded + varint(offset) + varint(len(compressed))
        blobs.append(compressed)
        offset += len(compressed)
    data = b"EGCM" + varint(1) + varint(len(entries)) + bytes(index) + b"".join(blobs)
    with open("src/Egap.Pdf/Fonts/Data/cmaps.bin", "wb") as out:
        out.write(data)
    print(f"{len(entries)} entries, {len(data):,} bytes")


if __name__ == "__main__":
    main()
