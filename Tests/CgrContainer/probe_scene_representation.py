"""Controlled scene-only variants; never changes mesh/picking payload bytes."""
import argparse
from copy import deepcopy
from dataclasses import replace
import hashlib
import json
from pathlib import Path

from cfv2 import Container, Extent, parse
from cfv2_metadata import named_node
from scene_records import Link, parse_scene


def digest(data):
    return hashlib.sha256(data).hexdigest()


def stream(c):
    owner = named_node(c, c.nodes[1].id, "SceneGraph")
    return named_node(c, owner.id, "MainDataStream")


def replace_scene(source, scene):
    """Rebuild directory and extent locations, preserving all other streams."""
    nodes, physical = deepcopy(source.nodes), bytearray(64)
    scene_id = stream(source).id
    for node in nodes[1:]:
        payload = scene if node.id == scene_id else source.payload(node)
        node.size = len(payload)
        node.extents = [Extent(len(physical), len(payload), 0)] if payload else []
        physical.extend(payload)
    data = Container(bytes(physical), len(physical), 0, nodes, source.control21).rebuilt()
    check = parse(data)
    assert check.rebuilt() == data
    for a, b in zip(source.nodes, check.nodes):
        expected = source.payload(a) if a.id != scene_id else scene
        assert check.payload(b) == expected
    return data


def main():
    p = argparse.ArgumentParser()
    p.add_argument("baseline", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--leaf-factorial", action="store_true")
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"method": "typed scene-only controlled variants", "variants": []}
    for model in ("box", "cylinder"):
        sources, scenes = {}, {}
        for mode in ("compact", "features"):
            source = parse((args.baseline / f"{model}_{mode}_after.cgr").read_bytes())
            sources[mode] = source
            scenes[mode] = parse_scene(source.payload(stream(source)))
        variants = []
        c, f = scenes["compact"], scenes["features"]
        compact_leaf = next(n for n in c.nodes if n.tag == 0x95)
        feature_leaf = next(n for n in f.nodes if n.tag == 0x95)
        assert sum(n.tag == 0x95 for n in c.nodes) == sum(n.tag == 0x95 for n in f.nodes) == 1
        variants += [("compact_control", "compact", deepcopy(c)), ("feature_control", "features", deepcopy(f))]
        for label, updates in (
            ("root_flags", {"flags": f.root.header.flags}),
            ("root_field11", {"field11": f.root.header.field11}),
            ("root_scalar16", {"scalar16": f.root.header.scalar16}),
            ("root_all", {"flags": f.root.header.flags, "field11": f.root.header.field11, "scalar16": f.root.header.scalar16}),
        ):
            s = deepcopy(c)
            s.root.header = replace(s.root.header, **updates)
            variants.append((label, "compact", s))
        s = deepcopy(c)
        leaf = next(n for n in s.nodes if n.tag == 0x95)
        leaf.header = replace(leaf.header, flags=feature_leaf.header.flags,
                              encoding=feature_leaf.header.encoding,
                              attribute_word_be=feature_leaf.header.attribute_word_be)
        variants.append(("compact_leaf_attributes", "compact", s))
        # Use the existing field-serialized feature tree; replace only its leaf.
        for label, feature_header in (("compact_in_feature_tree", False), ("compact_in_feature_tree_attributes", True)):
            s = deepcopy(f)
            leaf = next(n for n in s.nodes if n.tag == 0x95)
            leaf.geometry, leaf.pick_offset, leaf.pick_extended = compact_leaf.geometry, compact_leaf.pick_offset, compact_leaf.pick_extended
            if not feature_header:
                leaf.header = compact_leaf.header
            variants.append((label, "compact", s))
        # Reverse intervention: remove A3/branches but retain feature geometry.
        s = deepcopy(c)
        s.root.header = f.root.header
        s.root.children = [Link(0, None, False, deepcopy(feature_leaf), True)]
        variants.append(("feature_without_a3", "features", s))
        s = deepcopy(f)
        s.root.header = c.root.header
        variants.append(("feature_compact_root", "features", s))
        if args.leaf_factorial:
            variants = []
            for mask in range(8):
                for mode, original, target in (("compact", c, feature_leaf), ("features", f, compact_leaf)):
                    s = deepcopy(original)
                    leaf = next(n for n in s.nodes if n.tag == 0x95)
                    updates = {field: getattr(target.header, field) for bit, field in enumerate(
                        ("flags", "encoding", "attribute_word_be")) if mask & (1 << bit)}
                    leaf.header = replace(leaf.header, **updates)
                    variants.append((f"{mode}_leaf_mask{mask}", mode, s))
        for label, mode, scene in variants:
            encoded = scene.encode()
            checked = parse_scene(encoded)
            original = scenes[mode]
            before = [digest(n.geometry) for n in original.nodes if n.tag == 0x95]
            after = [digest(n.geometry) for n in checked.nodes if n.tag == 0x95]
            assert before == after
            path = args.output / f"{model}_{label}.cgr"
            path.write_bytes(replace_scene(sources[mode], encoded))
            report["variants"].append({"model": model, "variant": label, "payload_mode": mode,
                "path": str(path.resolve()), "scene_bytes": len(encoded), "geometry_hashes": after,
                "other_streams_unchanged": True})
    (args.output / "variants.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"variants": len(report["variants"])}))


if __name__ == "__main__":
    main()
