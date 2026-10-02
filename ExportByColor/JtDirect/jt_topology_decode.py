"""Decode JT's dual vertex-facet topology into polygon face rings.

The traversal mirrors the MeshCoderDriver/MeshCodec/MeshDecoder algorithm in
the JT v10 reference.  It is intentionally kept separate from the attribute
decoder so a topology failure cannot be mistaken for a coordinate failure.
"""
from __future__ import annotations

import argparse
import json
import struct
from dataclasses import dataclass, field
from pathlib import Path

from jt_codec_probe import decode_int32cdp3, tri_body, _unpack_residuals


def decode_vec(data: bytes, pos: int, predictor: str, unsigned: bool = False) -> tuple[list[int], int]:
    residuals, pos, _ = decode_int32cdp3(data, pos)
    if unsigned:
        residuals = [value & 0xFFFFFFFF for value in residuals]
        values: list[int] = []
        for i, value in enumerate(residuals):
            if i < 4 or predictor == "null":
                values.append(value)
            elif predictor == "lag1":
                values.append((value + values[i - 1]) & 0xFFFFFFFF)
            else:
                raise ValueError(predictor)
        return values, pos
    return _unpack_residuals(residuals, predictor), pos


def read_symbols(body: bytes) -> dict:
    pos = 41
    degree: list[list[int]] = []
    for _ in range(8):
        values, pos = decode_vec(body, pos, "null")
        degree.append(values)
    valence, pos = decode_vec(body, pos, "null")
    groups, pos = decode_vec(body, pos, "null")
    flags, pos = decode_vec(body, pos, "lag1")
    masks: list[list[int]] = []
    for _ in range(8):
        values, pos = decode_vec(body, pos, "null", unsigned=True)
        masks.append(values)
    mask_next, pos = decode_vec(body, pos, "null", unsigned=True)
    high_count = struct.unpack_from("<I", body, pos)[0]
    pos += 4
    high = [struct.unpack_from("<I", body, pos + i * 4)[0] for i in range(high_count)]
    pos += high_count * 4
    split_face, pos = decode_vec(body, pos, "lag1")
    split_pos, pos = decode_vec(body, pos, "null")
    composite_hash = struct.unpack_from("<I", body, pos)[0]
    return {
        "degree": degree,
        "valence": valence,
        "groups": groups,
        "flags": flags,
        "masks": masks,
        "mask_next": mask_next,
        "high": high,
        "split_face": split_face,
        "split_pos": split_pos,
        "composite_hash": composite_hash,
    }


@dataclass
class Vertex:
    valence: int
    group: int
    flags: int
    faces: list[int] = field(init=False)

    def __post_init__(self) -> None:
        if self.valence < 0 or self.valence > 100000:
            raise ValueError(f"invalid vertex valence {self.valence}")
        self.faces = [-1] * self.valence


@dataclass
class Face:
    degree: int
    attr_mask: int = 0
    vertices: list[int] = field(init=False)
    attr_indices: list[int] = field(init=False)
    empty: int = field(init=False)

    def __post_init__(self) -> None:
        if self.degree <= 0 or self.degree > 100000:
            raise ValueError(f"invalid face degree {self.degree}")
        self.vertices = [-1] * self.degree
        self.attr_indices = [-1] * self.degree
        self.empty = self.degree


class DualMesh:
    def __init__(self) -> None:
        self.vertices: list[Vertex] = []
        self.faces: list[Face] = []
        self.face_attribute_count = 0

    def new_vertex(self, valence: int, group: int, flags: int) -> int:
        index = len(self.vertices)
        self.vertices.append(Vertex(valence, group, flags))
        return index

    def new_face(self, degree: int, attr_mask: int) -> int:
        index = len(self.faces)
        face = Face(degree, attr_mask)
        for slot in range(degree):
            if (attr_mask >> slot) & 1:
                face.attr_indices[slot] = self.face_attribute_count
                self.face_attribute_count += 1
        self.faces.append(face)
        return index

    def face_attribute(self, face: int, vertex: int) -> int:
        """Return the JT corner-attribute ordinal for a dual face/vertex pair."""
        record = self.faces[face]
        try:
            slot = record.vertices.index(vertex)
        except ValueError:
            return -1
        return record.attr_indices[slot]

    def face(self, vertex: int, slot: int) -> int:
        return self.vertices[vertex].faces[slot]

    def vtx(self, face: int, slot: int) -> int:
        return self.faces[face].vertices[slot]

    def set_vtx_face(self, vertex: int, slot: int, face: int) -> None:
        self.vertices[vertex].faces[slot] = face

    def set_face_vtx(self, face: int, slot: int, vertex: int) -> None:
        record = self.faces[face]
        if record.vertices[slot] != vertex:
            # JTReader's DualVFMesh decrements for every replacement, not
            # only for an empty slot.  A propagated edge can legitimately
            # replace a provisional vertex and the active-face heuristic
            # relies on preserving that exact counter semantics.
            record.empty -= 1
            record.vertices[slot] = vertex

    def find_vtx_slot(self, face: int, vertex: int) -> int:
        try:
            return self.faces[face].vertices.index(vertex)
        except ValueError:
            return -1

    def find_face_slot(self, vertex: int, face: int) -> int:
        try:
            return self.vertices[vertex].faces.index(face)
        except ValueError:
            return -1


class Decoder:
    def __init__(self, symbols: dict) -> None:
        self.s = symbols
        self.mesh = DualMesh()
        self.degree_pos = [0] * 8
        self.valence_pos = 0
        self.group_pos = 0
        self.flag_pos = 0
        self.mask_pos = [0] * 8
        self.mask_next_pos = 0
        self.high_pos = 0
        self.split_face_pos = 0
        self.split_pos_pos = 0
        self.active: list[int] = []
        self.removed: set[int] = set()

    @staticmethod
    def inc(value: int, n: int) -> int:
        return (value + 1) % n

    @staticmethod
    def dec(value: int, n: int) -> int:
        return (value - 1) % n

    def next_valence(self) -> int:
        if self.valence_pos >= len(self.s["valence"]):
            return -1
        value = self.s["valence"][self.valence_pos]
        self.valence_pos += 1
        return value

    def next_group(self) -> int:
        if self.group_pos >= len(self.s["groups"]):
            return -1
        value = self.s["groups"][self.group_pos]
        self.group_pos += 1
        return value

    def next_flag(self) -> int:
        if self.flag_pos >= len(self.s["flags"]):
            return 0
        value = self.s["flags"][self.flag_pos]
        self.flag_pos += 1
        return value

    def io_vertex(self) -> int:
        value = self.next_valence()
        if value < 0:
            return -1
        return self.mesh.new_vertex(value, self.next_group(), self.next_flag())

    def context(self, vertex: int) -> int:
        v = self.mesh.vertices[vertex]
        known = [face for face in v.faces if face >= 0 and self.mesh.faces[face].degree > 0]
        known_count = len(known)
        total_degree = sum(self.mesh.faces[face].degree for face in known)
        if v.valence == 3:
            return 0 if total_degree < known_count * 6 else 1 if total_degree == known_count * 6 else 2
        if v.valence == 4:
            return 3 if total_degree < known_count * 4 else 4 if total_degree == known_count * 4 else 5
        if v.valence == 5:
            return 6
        return 7

    def next_degree(self, context: int) -> int:
        index = self.degree_pos[context]
        values = self.s["degree"][context]
        if index >= len(values):
            return -1
        self.degree_pos[context] += 1
        return values[index]

    def next_mask(self, context: int) -> int:
        index = self.mask_pos[context]
        values = self.s["masks"][context]
        if index >= len(values):
            return 0
        self.mask_pos[context] += 1
        value = values[index]
        if context == 7 and self.mask_next_pos < len(self.s["mask_next"]):
            # The v10 stream splits the 34..64 mask bits into a 30-bit
            # vector plus a parallel four-bit vector.
            value |= self.s["mask_next"][self.mask_next_pos] << 30
            self.mask_next_pos += 1
        return value

    def next_large_mask(self, degree: int) -> int:
        words = (degree + 31) // 32
        if self.high_pos + words > len(self.s["high"]):
            raise ValueError("large face attribute mask stream ended")
        value = 0
        for word in range(words):
            value |= int(self.s["high"][self.high_pos + word]) << (32 * word)
        self.high_pos += words
        return value

    def io_face(self, vertex: int) -> int:
        context = self.context(vertex)
        degree = self.next_degree(context)
        if degree == 0:
            return -1
        if degree < 0:
            return -1
        attr_context = min(7, max(0, degree - 2))
        mask = self.next_mask(attr_context) if degree <= 64 else self.next_large_mask(degree)
        return self.mesh.new_face(degree, mask)

    def io_split_face(self) -> int:
        if self.split_face_pos >= len(self.s["split_face"]):
            return -1
        offset = self.s["split_face"][self.split_face_pos]
        self.split_face_pos += 1
        if offset < 1 or offset > len(self.active):
            raise ValueError(f"split face offset {offset} outside active queue of {len(self.active)}")
        return self.active[len(self.active) - offset]

    def io_split_pos(self) -> int:
        if self.split_pos_pos >= len(self.s["split_pos"]):
            return -1
        value = self.s["split_pos"][self.split_pos_pos]
        self.split_pos_pos += 1
        return value

    def add_active(self, face: int) -> None:
        self.active.append(face)

    def activate_f(self, vertex: int, vertex_slot: int) -> int:
        face = self.io_face(vertex)
        if face >= 0:
            self.mesh.set_vtx_face(vertex, vertex_slot, face)
            self.mesh.set_face_vtx(face, 0, vertex)
            self.add_active(face)
            return face
        if face == -1:
            face = self.io_split_face()
            split_slot = self.io_split_pos()
            if face == -1 or split_slot == -1:
                return -2
            self.mesh.set_vtx_face(vertex, vertex_slot, face)
            self.add_vertex_to_face(vertex, vertex_slot, face, split_slot)
            return face
        return -2

    def activate_v(self, face: int, face_slot: int) -> int:
        vertex = self.io_vertex()
        if vertex < 0:
            return -1
        self.mesh.set_vtx_face(vertex, 0, face)
        self.add_vertex_to_face(vertex, 0, face, face_slot)
        return vertex

    def add_vertex_to_face(self, vertex: int, vertex_face_slot: int, face: int, face_vertex_slot: int) -> None:
        degree = self.mesh.faces[face].degree
        slot_cw = self.dec(face_vertex_slot, degree)
        slot_ccw = self.inc(face_vertex_slot, degree)
        self.mesh.set_face_vtx(face, face_vertex_slot, vertex)
        previous_vertex = self.mesh.vtx(face, slot_cw)
        if previous_vertex != -1:
            previous_face_slot = self.mesh.find_face_slot(previous_vertex, face)
            vertex_slot_ccw = self.inc(vertex_face_slot, self.mesh.vertices[vertex].valence)
            if self.mesh.face(vertex, vertex_slot_ccw) == -1:
                previous_face_slot = self.dec(previous_face_slot, self.mesh.vertices[previous_vertex].valence)
                propagated = self.mesh.face(previous_vertex, previous_face_slot)
                self.mesh.set_vtx_face(vertex, vertex_slot_ccw, propagated)
        next_vertex = self.mesh.vtx(face, slot_ccw)
        if next_vertex != -1:
            next_face_slot = self.mesh.find_face_slot(next_vertex, face)
            vertex_slot_cw = self.dec(vertex_face_slot, self.mesh.vertices[vertex].valence)
            if self.mesh.face(vertex, vertex_slot_cw) == -1:
                next_face_slot = self.inc(next_face_slot, self.mesh.vertices[next_vertex].valence)
                propagated = self.mesh.face(next_vertex, next_face_slot)
                self.mesh.set_vtx_face(vertex, vertex_slot_cw, propagated)

    def complete_v(self, vertex: int, vertex_slot: int) -> None:
        valence = self.mesh.vertices[vertex].valence
        previous_face = self.mesh.face(vertex, 0)
        previous_face_slot = vertex_slot
        index = 1
        while index < valence and self.mesh.face(vertex, index) != -1:
            next_face = self.mesh.face(vertex, index)
            previous_face_slot = self.dec(previous_face_slot, self.mesh.faces[previous_face].degree)
            adjacent_vertex = self.mesh.vtx(previous_face, previous_face_slot)
            if adjacent_vertex == -1:
                break
            next_face_slot = self.mesh.find_vtx_slot(next_face, adjacent_vertex)
            if next_face_slot < 0:
                raise ValueError(f"parallel vertex ring is inconsistent: v={vertex} valence={valence} index={index} prev_face={previous_face} next_face={next_face} adjacent={adjacent_vertex} prev_slot={previous_face_slot} prev_vertices={self.mesh.faces[previous_face].vertices} next_vertices={self.mesh.faces[next_face].vertices} vfaces={self.mesh.vertices[vertex].faces}")
            next_face_slot = self.dec(next_face_slot, self.mesh.faces[next_face].degree)
            self.add_vertex_to_face(vertex, index, next_face, next_face_slot)
            previous_face, previous_face_slot = next_face, next_face_slot
            index += 1
        if index >= valence:
            return
        last = index
        previous_face = self.mesh.face(vertex, 0)
        previous_face_slot = vertex_slot
        index = valence - 1
        while index >= 0 and self.mesh.face(vertex, index) != -1:
            next_face = self.mesh.face(vertex, index)
            previous_face_slot = self.inc(previous_face_slot, self.mesh.faces[previous_face].degree)
            adjacent_vertex = self.mesh.vtx(previous_face, previous_face_slot)
            if adjacent_vertex == -1:
                break
            next_face_slot = self.mesh.find_vtx_slot(next_face, adjacent_vertex)
            if next_face_slot < 0:
                raise ValueError(f"parallel vertex ring is inconsistent: v={vertex} valence={valence} index={index} prev_face={previous_face} next_face={next_face} adjacent={adjacent_vertex} prev_slot={previous_face_slot} prev_vertices={self.mesh.faces[previous_face].vertices} next_vertices={self.mesh.faces[next_face].vertices} vfaces={self.mesh.vertices[vertex].faces}")
            next_face_slot = self.inc(next_face_slot, self.mesh.faces[next_face].degree)
            self.add_vertex_to_face(vertex, index, next_face, next_face_slot)
            previous_face, previous_face_slot = next_face, next_face_slot
            index -= 1
            if index < last:
                return
        for slot in range(last, index + 1):
            # Complete the unresolved incident faces by consuming face
            # symbols (the PDF's activateV name is a known variable-label
            # typo in this routine; the operation is activateF).
            self.activate_f(vertex, slot)

    def complete_f(self, face: int) -> None:
        # The reference decoder searches for an actual -1 slot.  The
        # ``empty`` counter is a heuristic for active-face selection and can
        # go negative when an already linked slot is replaced during edge
        # propagation.
        while -1 in self.mesh.faces[face].vertices:
            slot = self.mesh.faces[face].vertices.index(-1)
            vertex = self.activate_v(face, slot)
            if vertex < 0:
                raise ValueError("vertex stream ended while completing a face")
            self.complete_v(vertex, slot)

    def next_active_face(self) -> int:
        while self.active and self.active[-1] in self.removed:
            self.active.pop()
        best = -1
        best_empty = 1 << 30
        index = len(self.active) - 1
        while index >= max(0, len(self.active) - 16):
            face = self.active[index]
            if face in self.removed:
                self.active.pop(index)
                index -= 1
                continue
            empty = self.mesh.faces[face].empty
            if empty < best_empty:
                best, best_empty = face, empty
            index -= 1
        return best

    def run(self) -> None:
        while True:
            vertex = self.io_vertex()
            if vertex < 0:
                break
            for slot in range(self.mesh.vertices[vertex].valence):
                if self.activate_f(vertex, slot) == -2:
                    raise ValueError("mesh traversal failed while activating seed faces")
            while True:
                face = self.next_active_face()
                if face < 0:
                    break
                self.complete_f(face)
                self.removed.add(face)
        for vertex in self.mesh.vertices:
            if any(face < 0 for face in vertex.faces):
                raise ValueError("decoded vertex has unresolved incident face")
        for face in self.mesh.faces:
            if face.empty:
                raise ValueError("decoded face has unresolved vertex slot")


def decode(body: bytes) -> dict:
    symbols = read_symbols(body)
    decoder = Decoder(symbols)
    decoder.run()
    mesh = decoder.mesh
    return {
        "composite_hash_u32": symbols["composite_hash"],
        "dual_vertices": len(mesh.vertices),
        "dual_faces": len(mesh.faces),
        "dual_vertex_valence_min": min(v.valence for v in mesh.vertices) if mesh.vertices else None,
        "dual_vertex_valence_max": max(v.valence for v in mesh.vertices) if mesh.vertices else None,
        "dual_face_degree_min": min(f.degree for f in mesh.faces) if mesh.faces else None,
        "dual_face_degree_max": max(f.degree for f in mesh.faces) if mesh.faces else None,
        "cover_face_vertices": sum(1 for v in mesh.vertices if v.flags),
        "consumed": {
            "valences": decoder.valence_pos,
            "groups": decoder.group_pos,
            "flags": decoder.flag_pos,
            "split_face": decoder.split_face_pos,
            "split_pos": decoder.split_pos_pos,
            "degree_contexts": decoder.degree_pos,
            "attribute_contexts": decoder.mask_pos,
            "attribute_mask_next": decoder.mask_next_pos,
            "large_attribute_mask_words": decoder.high_pos,
            "face_attributes": mesh.face_attribute_count,
        },
        "sample_dual_faces": [face.vertices for face in mesh.faces[:5]],
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("input", type=Path)
    ap.add_argument("output", type=Path, nargs="?")
    ap.add_argument("--ordinal", type=int, default=1)
    args = ap.parse_args()
    report = decode(tri_body(args.input, args.ordinal))
    text = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(text + "\n", encoding="utf-8")
    else:
        print(text)


if __name__ == "__main__":
    main()
