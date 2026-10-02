"""Small JT 10.x Int32CDP3 decoder used to validate the first mesh payload.

The decoder follows the v10 layout: a value count and codec byte, a 32-bit
word-reversed code-text stream, and (for arithmetic coding) a bit-packed
probability context table.  It is intentionally independent of the CGR writer;
its output is residual data that is later consumed by the TopoMesh parser.
"""
from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from jt_analyze import analyze
from jt_lsg import parse_element


class BitReader:
    def __init__(self, data: bytes, bit_pos: int = 0, bit_length: int | None = None):
        self.data = data
        self.pos = bit_pos
        self.end = len(data) * 8 if bit_length is None else bit_pos + bit_length

    def read_u(self, n: int) -> int:
        if n <= 0:
            return 0
        if self.pos + n > self.end:
            raise ValueError(f"bitstream underrun: need {n} at {self.pos}, end {self.end}")
        out = 0
        for _ in range(n):
            byte = self.data[self.pos >> 3]
            out = (out << 1) | ((byte >> (7 - (self.pos & 7))) & 1)
            self.pos += 1
        return out

    def read_i(self, n: int) -> int:
        value = self.read_u(n)
        if n and (value & (1 << (n - 1))):
            value -= 1 << n
        return value

    def align_byte(self) -> None:
        self.pos = (self.pos + 7) & ~7


@dataclass
class ProbEntry:
    symbol: int
    occurrence: int
    associated: int


def _word_reversed_code_text(data: bytes, pos: int, bit_length: int) -> tuple[bytes, int]:
    words = (bit_length + 31) // 32
    end = pos + words * 4
    if end > len(data):
        raise ValueError("code text extends past element")
    # JTReader reverses each stored little-endian word before feeding the
    # MSB-first bitstream decoder.
    text = b"".join(data[p : p + 4][::-1] for p in range(pos, end, 4))
    return text, end


def decode_arithmetic(
    code_text: bytes,
    bit_length: int,
    entries: list[ProbEntry],
    count: int,
    out_of_band: list[int] | None = None,
) -> list[int]:
    if not entries:
        raise ValueError("empty arithmetic probability table")
    cumulative: list[int] = []
    total = 0
    for entry in entries:
        total += entry.occurrence
        cumulative.append(total)
    bits = BitReader(code_text, 0, bit_length)
    # The reference implementation uses a 16-bit arithmetic state.
    code = bits.read_u(16)
    low = 0
    high = 0xFFFF
    result: list[int] = []
    out_of_band = list(out_of_band or [])
    oob_pos = 0
    for _ in range(count):
        rescaled = ((((code - low) + 1) * total - 1) // ((high - low) + 1))
        idx = 0
        while idx < len(cumulative) and cumulative[idx] <= rescaled:
            idx += 1
        if idx >= len(entries):
            raise ValueError(f"arithmetic symbol outside table: {rescaled}/{total}")
        upper = cumulative[idx]
        lower = upper - entries[idx].occurrence
        span = high - low + 1
        high = low + ((span * upper) // total - 1)
        low = low + ((span * lower) // total)
        while True:
            if ((~(high ^ low)) & 0x8000) != 0:
                pass
            elif (low & 0x4000) and not (high & 0x4000):
                code ^= 0x4000
                low &= 0x3FFF
                high |= 0x4000
            else:
                break
            low = (low << 1) & 0xFFFF
            high = ((high << 1) | 1) & 0xFFFF
            code = (code << 1) & 0xFFFF
            code |= bits.read_u(1) if bits.pos < bits.end else 0
        if entries[idx].symbol == -2:
            if oob_pos >= len(out_of_band):
                raise ValueError("arithmetic stream references missing out-of-band value")
            result.append(out_of_band[oob_pos])
            oob_pos += 1
        else:
            result.append(entries[idx].associated)
    return result


def _nibbler_get(bits: BitReader, width: int) -> int:
    value = 0
    nibbles = 0
    while True:
        value |= bits.read_u(width) << (nibbles * width)
        more = bits.read_u(1)
        nibbles += 1
        if more == 0:
            break
    total = nibbles * width
    if total and (value & (1 << (total - 1))):
        value -= 1 << total
    return value


def _field_width(symbol: int) -> int:
    # Int32CDP3's GetBitFieldWidth uses symbol ^ (symbol >> 31), then
    # 32-leading-zero count.  For the observed non-negative spans this is
    # simply the unsigned bit length.
    symbol &= 0xFFFFFFFF
    return symbol.bit_length()


def decode_bitlength3(code_text: bytes, bit_length: int, count: int) -> list[int]:
    bits = BitReader(code_text, 0, bit_length)
    values: list[int] = []
    if bits.read_u(1) == 0:
        minimum = _nibbler_get(bits, 4)
        maximum = _nibbler_get(bits, 4)
        width = _field_width(maximum - minimum)
        for _ in range(count):
            values.append(bits.read_u(width) + minimum)
    else:
        mean = _nibbler_get(bits, 4)
        current_width = 0
        while len(values) < count:
            # Width changes use +/-8 and +7 as continuation sentinels.  The
            # earlier probe treated the first delta as final, which only
            # works for simple packets and misreads the mask7 stream.
            while True:
                delta = bits.read_i(4)
                current_width += delta
                if delta not in (-8, 7):
                    break
            run_len = bits.read_u(4)
            if len(values) + run_len > count:
                raise ValueError("invalid bitlength run length")
            for _ in range(run_len):
                values.append(bits.read_i(current_width) + mean)
    if len(values) != count:
        raise ValueError("bitlength count mismatch")
    return values


def decode_int32cdp3(data: bytes, pos: int) -> tuple[list[int], int, dict]:
    if pos + 4 > len(data):
        raise ValueError("missing Int32CDP3 header")
    count = struct.unpack_from("<i", data, pos)[0]
    if count < 0 or count > 10_000_000:
        raise ValueError(f"invalid Int32CDP3 value count {count} at {pos}")
    if count == 0:
        # Int32CDP3 returns immediately after the value count; no codec byte
        # is present for an empty vector.
        return [], pos + 4, {"count": 0, "codec": None}
    if pos + 5 > len(data):
        raise ValueError("missing Int32CDP3 codec byte")
    codec = data[pos + 4]
    pos += 5
    if codec == 0:
        byte_len = struct.unpack_from("<i", data, pos)[0]
        pos += 4
        if byte_len < 0 or byte_len % 4 or pos + byte_len > len(data):
            raise ValueError("invalid null codec byte length")
        values = [struct.unpack_from("<i", data, pos + i)[0] for i in range(0, byte_len, 4)]
        if len(values) != count:
            raise ValueError(f"null codec count mismatch {len(values)} != {count}")
        return values, pos + byte_len, {"count": count, "codec": codec, "byte_len": byte_len}
    if codec == 4:
        chop_bits = data[pos]
        bias = struct.unpack_from("<i", data, pos + 1)[0]
        span_bits = data[pos + 5]
        pos += 6
        msb, pos, _ = decode_int32cdp3(data, pos)
        lsb, pos, _ = decode_int32cdp3(data, pos)
        if len(msb) != len(lsb):
            raise ValueError("chopper vector length mismatch")
        values = [((lo | (hi << (span_bits - chop_bits))) + bias) for hi, lo in zip(msb, lsb)]
        if len(values) != count:
            raise ValueError("chopper count mismatch")
        return values, pos, {"count": count, "codec": codec, "chop_bits": chop_bits, "span_bits": span_bits}
    if codec == 5:
        values_raw, pos, _ = decode_int32cdp3(data, pos)
        offsets, pos, _ = decode_int32cdp3(data, pos)
        window: list[int] = []
        values: list[int] = []
        literal_index = 0
        for offset in offsets:
            if offset == -1:
                if len(values_raw) <= literal_index:
                    raise ValueError("move-to-front literal underflow")
                value = values_raw[literal_index]
                literal_index += 1
                window.insert(0, value)
                del window[16:]
            else:
                if offset < 0 or offset >= len(window):
                    raise ValueError(f"move-to-front offset {offset} outside window")
                value = window.pop(offset)
                window.insert(0, value)
            values.append(value)
        if len(values) != count:
            raise ValueError("move-to-front count mismatch")
        return values, pos, {"count": count, "codec": codec, "literal_count": literal_index}
    if codec not in (1, 3):
        raise ValueError(f"unsupported Int32CDP3 codec {codec}")
    bit_length = struct.unpack_from("<i", data, pos)[0]
    pos += 4
    if bit_length < 0:
        raise ValueError("negative arithmetic code length")
    code_text, pos = _word_reversed_code_text(data, pos, bit_length)
    if codec == 1:
        values = decode_bitlength3(code_text, bit_length, count)
        return values, pos, {"count": count, "codec": codec, "bit_length": bit_length}
    context_bits = BitReader(data, pos * 8)
    entry_count = context_bits.read_u(16)
    occurrence_bits = context_bits.read_u(6)
    value_bits = context_bits.read_u(7)
    minimum = context_bits.read_i(32)
    entries: list[ProbEntry] = []
    for _ in range(entry_count):
        symbol = 0 if context_bits.read_u(1) == 0 else -2
        occurrence = context_bits.read_u(occurrence_bits)
        associated = context_bits.read_u(value_bits) + minimum
        entries.append(ProbEntry(symbol, occurrence, associated))
    context_bits.align_byte()
    pos = context_bits.pos // 8
    # v10's arithmetic stream carries out-of-band values as another nested
    # Int32CDP3 packet immediately after the probability table.
    oob: list[int] = []
    if any(e.symbol == -2 for e in entries):
        oob, pos, _ = decode_int32cdp3(data, pos)
    values = decode_arithmetic(code_text, bit_length, entries, count, oob)
    return values, pos, {
        "count": count,
        "codec": codec,
        "bit_length": bit_length,
        "context_entries": entry_count,
        "oob_count": len(oob),
    }


def skip_int32cdp3(data: bytes, pos: int) -> tuple[int, dict]:
    """Advance over an Int32CDP3 vector without decoding its values.

    This is useful while mapping a packet stream whose value codec is still
    being audited.  It follows the same framing as ``decode_int32cdp3`` and
    therefore gives an independent byte boundary check for later records.
    """
    if pos + 4 > len(data):
        raise ValueError("missing Int32CDP3 header")
    count = struct.unpack_from("<i", data, pos)[0]
    if count < 0 or count > 10_000_000:
        raise ValueError(f"invalid Int32CDP3 value count {count} at {pos}")
    if count == 0:
        return pos + 4, {"count": 0, "codec": None}
    if pos + 5 > len(data):
        raise ValueError("missing Int32CDP3 codec byte")
    codec = data[pos + 4]
    pos += 5
    if codec == 0:
        if pos + 4 > len(data):
            raise ValueError("missing null codec byte length")
        byte_len = struct.unpack_from("<i", data, pos)[0]
        pos += 4
        if byte_len < 0 or byte_len % 4 or pos + byte_len > len(data):
            raise ValueError("invalid null codec byte length")
        return pos + byte_len, {"count": count, "codec": codec, "byte_len": byte_len}
    if codec == 4:
        if pos + 6 > len(data):
            raise ValueError("truncated chopper header")
        chop_bits = data[pos]
        span_bits = data[pos + 5]
        pos += 6
        pos, _ = skip_int32cdp3(data, pos)
        pos, _ = skip_int32cdp3(data, pos)
        return pos, {"count": count, "codec": codec, "chop_bits": chop_bits, "span_bits": span_bits}
    if codec == 5:
        pos, _ = skip_int32cdp3(data, pos)
        pos, _ = skip_int32cdp3(data, pos)
        return pos, {"count": count, "codec": codec}
    if codec not in (1, 3):
        raise ValueError(f"unsupported Int32CDP3 codec {codec}")
    if pos + 4 > len(data):
        raise ValueError("missing code length")
    bit_length = struct.unpack_from("<i", data, pos)[0]
    pos += 4
    if bit_length < 0:
        raise ValueError("negative arithmetic code length")
    words = (bit_length + 31) // 32
    if pos + words * 4 > len(data):
        raise ValueError("code text extends past element")
    pos += words * 4
    if codec == 1:
        return pos, {"count": count, "codec": codec, "bit_length": bit_length}
    # Arithmetic coding stores a bit-packed probability context table after
    # the code text.  Reuse BitReader so alignment is handled exactly as in
    # the value decoder, then skip the optional nested out-of-band vector.
    context_bits = BitReader(data, pos * 8)
    entry_count = context_bits.read_u(16)
    occurrence_bits = context_bits.read_u(6)
    value_bits = context_bits.read_u(7)
    context_bits.read_u(32)  # minimum
    has_oob = False
    for _ in range(entry_count):
        if context_bits.read_u(1):
            has_oob = True
        context_bits.read_u(occurrence_bits)
        context_bits.read_u(value_bits)
    context_bits.align_byte()
    pos = context_bits.pos // 8
    if has_oob:
        pos, _ = skip_int32cdp3(data, pos)
    return pos, {
        "count": count,
        "codec": codec,
        "bit_length": bit_length,
        "context_entries": entry_count,
        "oob": has_oob,
    }


def tri_body(path: Path, ordinal: int) -> bytes:
    inv = analyze(path)
    raw = path.read_bytes()
    entry = next(e for e in inv["segments"]["entries"] if e["ordinal"] == ordinal)
    data = raw[int(entry["offset"]) + 24 : int(entry["offset"]) + int(entry["bytes"])]
    element, end = parse_element(data, 0)
    if element["type_id"] != "10dd10ab-2ac8-11d1-9b6b-0080c7bb5997":
        raise ValueError("selected segment is not TriStripSetShapeLOD")
    return data[25:end]


def _unpack_residuals(residuals: list[int], predictor: str) -> list[int]:
    values: list[int] = []
    for i, value in enumerate(residuals):
        if predictor == "null" or i < 4:
            values.append(value)
            continue
        if predictor == "lag1":
            pred = values[i - 1]
        elif predictor == "lag2":
            pred = values[i - 2]
        elif predictor == "stride1":
            pred = values[i - 1] + (values[i - 1] - values[i - 2])
        else:
            raise ValueError(predictor)
        values.append(value + pred)
    return values


def decode_topological_prefix(body: bytes) -> tuple[dict, int]:
    pos = 41
    report: dict = {"face_degrees": [], "face_attr_masks": []}
    for i in range(8):
        residuals, pos, meta = decode_int32cdp3(body, pos)
        vals = _unpack_residuals(residuals, "null")
        report["face_degrees"].append({"index": i, "meta": meta, "min": min(vals) if vals else None, "max": max(vals) if vals else None})
    for name, predictor in (("vertex_valences", "null"), ("vertex_groups", "null"), ("vertex_flags", "lag1")):
        residuals, pos, meta = decode_int32cdp3(body, pos)
        vals = _unpack_residuals(residuals, predictor)
        report[name] = {"meta": meta, "min": min(vals) if vals else None, "max": max(vals) if vals else None, "first": vals[:8]}
    for i in range(8):
        residuals, pos, meta = decode_int32cdp3(body, pos)
        vals = _unpack_residuals(residuals, "null")
        report["face_attr_masks"].append({"index": i, "meta": meta, "min": min(vals) if vals else None, "max": max(vals) if vals else None, "first": vals[:8]})
    residuals, pos, meta = decode_int32cdp3(body, pos)
    vals = _unpack_residuals(residuals, "null")
    report["face_attr_masks_next"] = {"meta": meta, "min": min(vals) if vals else None, "max": max(vals) if vals else None, "first": vals[:8]}
    if pos + 4 > len(body):
        raise ValueError("missing high-degree mask vector count")
    high_count = struct.unpack_from("<I", body, pos)[0]
    pos += 4
    if pos + high_count * 4 > len(body):
        raise ValueError("high-degree mask vector extends past element")
    report["high_degree_face_attribute_masks"] = {
        "count": high_count,
        "first": [struct.unpack_from("<I", body, pos + i * 4)[0] for i in range(min(high_count, 8))],
    }
    pos += high_count * 4
    residuals, pos, meta = decode_int32cdp3(body, pos)
    vals = _unpack_residuals(residuals, "lag1")
    report["split_face_syms"] = {"meta": meta, "min": min(vals) if vals else None, "max": max(vals) if vals else None, "first": vals[:8]}
    residuals, pos, meta = decode_int32cdp3(body, pos)
    vals = _unpack_residuals(residuals, "null")
    report["split_face_positions"] = {"meta": meta, "min": min(vals) if vals else None, "max": max(vals) if vals else None, "first": vals[:8]}
    if pos + 4 > len(body):
        raise ValueError("missing topological hash")
    report["hash_u32"] = struct.unpack_from("<I", body, pos)[0]
    pos += 4
    return report, pos


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("input", type=Path)
    ap.add_argument("--ordinal", type=int, default=1)
    ap.add_argument("--vectors", type=int, default=1)
    args = ap.parse_args()
    body = tri_body(args.input, args.ordinal)
    # BaseShape + VertexShapeLOD + nested TopoMeshLOD headers:
    # body[35] = TopoMesh LOD version, body[36:40] vertex-record object ID,
    # body[40] = TopologicallyCompressedLOD version.
    if args.vectors == 0:
        report, pos = decode_topological_prefix(body)
        report["next_offset"] = pos
        print(report)
        return
    pos = 41
    reports = []
    for _ in range(args.vectors):
        values, pos, meta = decode_int32cdp3(body, pos)
        reports.append({"meta": meta, "next_offset": pos, "first_values": values[:12], "last_values": values[-4:]})
    print(reports)


if __name__ == "__main__":
    main()
