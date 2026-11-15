"""PS planar JT 10.6 polylines -> millimetre DXF, with strict read-back audit.

Reuses this workspace's JT container/LSG/Int32CDP3 readers. Unsupported
representations fail explicitly; no mesh reconstruction. Closed polylines that
are a complete circle within a caller-supplied tolerance are emitted as CIRCLE
entities; every other polyline is written unchanged.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import sys
import tempfile

import ezdxf
import numpy as np

JT_READER = Path(__file__).resolve().parent
sys.path.insert(0, str(JT_READER))
from jt_analyze import analyze
from jt_lsg import parse_element, parse_lsg
from jt_codec_probe import decode_int32cdp3, _unpack_residuals
from jt_instance_inventory import IDENTITY, mat_mul, transform_point
from jt_material_resolve import resolve_diffuse

POLYLINE = '10dd10a1-2ac8-11d1-9b6b-0080c7bb5997'
END = 'ffffffff-ffff-ffff-ffff-ffffffffffff'
TOPO_MESH_GUID = bytes.fromhex('322dc111f938ba4593ba66f9d538ddfb')


def require(condition, message):
    if not condition:
        raise ValueError(message)


class Cursor:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def read(self, fmt):
        size = struct.calcsize('<' + fmt)
        require(self.pos + size <= len(self.data), f'Truncated shape at byte {self.pos}')
        values = struct.unpack_from('<' + fmt, self.data, self.pos)
        self.pos += size
        return values[0] if len(values) == 1 else values

    def vector(self, predictor='lag1'):
        values, self.pos, _ = decode_int32cdp3(self.data, self.pos)
        return _unpack_residuals(values, predictor)


def split_lines(groups, breaks, indices, vertices, counts):
    ng, np_, ni = counts
    require(len(groups) == ng + 1 and groups[0] == 0 and groups[-1] == np_, 'Invalid group boundaries')
    require(all(a <= b for a, b in zip(groups, groups[1:])), 'Nonmonotonic groups')
    require(len(breaks) == np_ + 1 and breaks[0] == 0 and breaks[-1] == ni, 'Invalid primitive boundaries')
    require(all(a + 2 <= b for a, b in zip(breaks, breaks[1:])), 'Polyline has fewer than two vertices')
    require(len(indices) == ni and all(0 <= i < len(vertices) for i in indices), 'Invalid vertex index')
    return [[vertices[i] for i in indices[a:b]] for a, b in zip(breaks, breaks[1:])]


def decode_polyline(body):
    c = Cursor(body)
    require(c.read('B') == 1 and c.read('B') == 1, 'Unsupported shape/vertex LOD version')
    require(c.read('Q') == 2, 'Only XYZ coordinate binding is supported')
    nested_length = c.read('i')
    nested_guid = c.read('16s')
    require(nested_guid == TOPO_MESH_GUID and c.read('B') == 9, 'Invalid nested TopoMesh header')
    nested_object_id = c.read('i')
    require(nested_length >= 21 and 10 + 4 + nested_length == len(body) - 1, 'Nested TopoMesh length mismatch')
    require(c.read('B') == 1, 'Unsupported TopoMesh LOD version')
    vertex_record_id = c.read('I')
    require(c.read('B') == 1, 'Only TopoMesh compressed representation v1 is supported')
    counts = c.read('iii')
    require(all(0 < n <= 10_000_000 for n in counts), 'Invalid topology counts')
    groups, breaks, indices = c.vector(), c.vector(), c.vector()
    topology_hash = c.read('I')
    require(c.read('Q') == 2, 'Only XYZ records without extra attributes are supported')
    quantization = c.read('4B')
    require(quantization[0] == 0, 'Quantized coordinates are not supported in this first version')
    record_count = c.read('i')
    lengths = c.vector('null')
    map_hash = c.read('I')
    unique_count, components = c.read('iB')
    require(components == 3 and record_count == unique_count > 0, 'Unsupported vertex record mapping')
    require(lengths == [1] * unique_count, 'Only one vertex record per unique coordinate is supported')
    quantizers = [c.read('ffB') for _ in range(3)]
    require(all(bits == 0 and math.isfinite(lo) and math.isfinite(hi) and lo <= hi for lo, hi, bits in quantizers), 'Unsupported coordinate quantizer')
    arrays = []
    for axis in range(3):
        codes = c.vector()
        require(len(codes) == unique_count, 'Coordinate count mismatch')
        values = [struct.unpack('<f', struct.pack('<I', v & 0xffffffff))[0] for v in codes]
        require(all(math.isfinite(v) for v in values), 'Nonfinite coordinate')
        lo, hi, _ = quantizers[axis]
        require(min(values) == lo and max(values) == hi, 'Decoded coordinates disagree with stored bounds')
        arrays.append(values)
    coordinate_hash = c.read('I')
    require(c.read('B') == 1 and c.pos == len(body), 'Unsupported Polyline version or trailing data')
    vertices = [list(p) for p in zip(*arrays)]
    lines = split_lines(groups, breaks, indices, vertices, counts)
    return lines, dict(groups=counts[0], polylines=len(lines), index_count=counts[2],
                      unique_vertices=unique_count, nested_object_id=nested_object_id,
                      vertex_record_id=vertex_record_id,
                      stored_hashes=dict(topology=topology_hash, mapping=map_hash, coordinates=coordinate_hash))


def load_shapes(path, inventory, progress=None):
    raw = path.read_bytes()
    result = {}
    total = sum(e['type'] == 7 for e in inventory['segments']['entries'])
    for entry in inventory['segments']['entries']:
        if entry['type'] not in {1, 4, 7, 31}:
            raise ValueError(f'Unsupported segment type {entry["type"]}; conversion aborted')
        if entry['type'] != 7:
            continue
        segment = raw[entry['offset'] + 24:entry['offset'] + entry['bytes']]
        element, end = parse_element(segment, 0)
        require(element['type_id'] == POLYLINE, 'Non-polyline geometry is unsupported')
        marker, marker_end = parse_element(segment, end)
        require(marker['type_id'] == END and segment[marker_end:] == b'\x01\0\0\0\0\0', 'Unexpected shape segment tail')
        try:
            lines, report = decode_polyline(segment[25:end])
        except (ValueError, struct.error, IndexError) as error:
            raise ValueError(f'Shape segment {entry["ordinal"]}: {error}') from error
        report.update(segment_id=entry['id'], ordinal=entry['ordinal'])
        result[entry['id']] = (lines, report)
        if progress:
            progress(15 + int(45 * len(result) / max(1, total)), 'decode')
    require(result, 'No polyline geometry found')
    return result


def scene_lines(lsg, shapes):
    require(lsg['parse']['error'] is None and lsg['parse']['remaining_bytes'] == 0, 'Incomplete scene graph parse')
    elements = [e for e in lsg['elements'] if 'object_id' in e]
    require(not any(e.get('error') or e.get('partition_tail_error') for e in elements), 'Scene element parse error')
    nodes = {e['object_id']: e for e in elements}
    require(len(nodes) == len(elements), 'Duplicate scene object IDs')
    roots = [e for e in elements if e['type'] == 'PartitionNode']
    require(len(roots) == 1, 'External/multiple partitions are unsupported')
    units = [p['value_text'] for row in lsg['property_table']['entries'] for p in row['properties']
             if p.get('key_text') == 'JT_PROP_MEASUREMENT_UNITS']
    require(units and set(units) == {'millimeters'}, 'Missing, mixed or unsupported units; expected millimeters')
    bindings = {b['shape_object_id']: b for b in lsg['shape_bindings']}
    materials = {e['object_id']: e for e in elements if e['type'] == 'MaterialAttribute'}
    lines, reached, segments = [], set(), set()
    allowed = {'PartitionNode', 'MetaDataNode', 'InstanceNode', 'PartNode', 'RangeLODNode', 'LODNode', 'GroupNode', 'PolylineSetShapeNode'}

    def walk(nid, matrix, path):
        require(nid not in path, 'Cycle in scene graph')
        require(len(path) < 256 and nid in nodes, 'Missing scene node or excessive depth')
        node = nodes[nid]
        require(node['type'] in allowed, f'Unsupported scene node {node["type"]}')
        next_matrix = matrix
        for aid in node.get('node', {}).get('attribute_ids', []):
            require(aid in nodes, 'Missing scene attribute')
            attribute = nodes[aid]
            if attribute['type'] == 'GeometricTransformAttribute':
                require(node['type'] == 'InstanceNode', 'Transform on non-instance node is unsupported')
                require(attribute.get('state_flags') == 8 and not attribute.get('field_inhibit_flags') and not attribute.get('field_final_flags'), 'Unsupported transform inheritance flags')
                local = np.asarray(attribute.get('matrix'), dtype=float)
                require(local.shape == (16,) and np.isfinite(local).all(), 'Invalid transform')
                require(np.allclose(local[[3, 7, 11, 15]], [0, 0, 0, 1], rtol=0, atol=1e-12), 'Projective transform is unsupported')
                require(abs(np.linalg.det(local.reshape(4, 4)[:3, :3])) > 1e-12, 'Singular transform')
                next_matrix = mat_mul(local.tolist(), matrix)
        path = path + [nid]
        if node['type'] == 'PolylineSetShapeNode':
            binding = bindings.get(nid, {})
            sid = binding.get('segment_id')
            require(sid in shapes and binding.get('payload_object_id') == 0, 'Unresolved polyline geometry reference')
            rgba = resolve_diffuse(path, nodes, materials)['rgba']
            reached.add(nid); segments.add(sid)
            for primitive, points in enumerate(shapes[sid][0]):
                lines.append(dict(points=[transform_point(p, next_matrix) for p in points],
                                  layer=f'JT_{nid}', rgba=rgba, shape_id=nid, primitive=primitive,
                                  segment_id=sid, scene_path=path))
            return
        children = [node['instance_child']] if node['type'] == 'InstanceNode' else node.get('children', [])
        if node['type'] in {'RangeLODNode', 'LODNode'}:
            require(len(children) == 1, 'Multiple LOD alternatives are unsupported')
        require(children, f'Empty container {nid}; possible external reference')
        for child in children:
            walk(child, next_matrix, path)

    walk(roots[0]['object_id'], IDENTITY, [])
    require(reached == {e['object_id'] for e in elements if e['type'] == 'PolylineSetShapeNode'}, 'Unreachable line geometry')
    require(segments == set(shapes), 'Unbound geometry segments')
    return lines, roots[0]


def flatten(lines, tolerance):
    require(math.isfinite(tolerance) and tolerance > 0, 'Plane tolerance must be positive and finite')
    points = np.asarray([p for line in lines for p in line['points']], dtype=float)
    require(len(points) >= 2 and np.isfinite(points).all(), 'Invalid/empty world coordinates')
    # Keep absolute XY coordinates whenever the source is already parallel to XY.
    if np.ptp(points[:, 2]) <= tolerance:
        origin = np.array([0., 0., (points[:, 2].min() + points[:, 2].max()) / 2])
        u, v, normal = np.eye(3)
    else:
        origin = points.mean(axis=0)
        _, singular, vh = np.linalg.svd(points - origin, full_matrices=False)
        require(len(singular) >= 2 and singular[1] > 1e-12, 'Cannot determine a unique plane for collinear points')
        normal = vh[-1]
        if normal[np.argmax(abs(normal))] < 0:
            normal = -normal
        ref = np.eye(3)[int(np.argmin(abs(normal)))]
        u = ref - np.dot(ref, normal) * normal
        u /= np.linalg.norm(u)
        v = np.cross(normal, u)
    residual = float(np.max(abs((points - origin) @ normal)))
    require(residual <= tolerance, f'Nonplanar geometry: {residual:.9g} mm exceeds {tolerance} mm')
    output = []
    for line in lines:
        points3 = np.asarray(line['points'])
        points2 = np.column_stack(((points3 - origin) @ u, (points3 - origin) @ v))
        # Closure is based on exact source endpoint equality, never proximity.
        closed = np.array_equal(points3[0], points3[-1])
        output.append(dict(line, xy=points2[:-1].tolist() if closed else points2.tolist(), closed=closed))
    return output, dict(origin=origin.tolist(), u=u.tolist(), v=v.tolist(), normal=normal.tolist(),
                        max_residual_mm=residual, tolerance_mm=tolerance)


def length(points, closed=False):
    return sum(math.dist(a, b) for a, b in zip(points, points[1:])) + (math.dist(points[-1], points[0]) if closed else 0)


def fit_circle(xy):
    """Kasa algebraic fit refined by Gauss-Newton on radial distance.

    Returns (cx, cy, r, point_count) or None when the points are degenerate.
    Only meant for complete loops: arcs would be biased by the algebraic step.
    """
    points = np.asarray(xy, dtype=float)
    if len(points) < 3:
        return None
    keep = np.ones(len(points), dtype=bool)
    keep[1:] = np.linalg.norm(np.diff(points, axis=0), axis=1) > 1e-9
    points = points[keep]
    if len(points) < 3:
        return None
    x, y = points[:, 0], points[:, 1]
    design = np.column_stack((2 * x, 2 * y, np.ones(len(points))))
    try:
        (cx, cy, constant), *_ = np.linalg.lstsq(design, x * x + y * y, rcond=None)
    except np.linalg.LinAlgError:
        return None
    squared = constant + cx * cx + cy * cy
    if not math.isfinite(squared) or squared <= 0:
        return None
    radius = math.sqrt(squared)
    for _ in range(8):
        dx, dy = x - cx, y - cy
        distance = np.hypot(dx, dy)
        distance = np.where(distance < 1e-12, 1e-12, distance)
        jacobian = np.column_stack((-dx / distance, -dy / distance, -np.ones(len(points))))
        try:
            step, *_ = np.linalg.lstsq(jacobian, radius - distance, rcond=None)
        except np.linalg.LinAlgError:
            break
        cx, cy, radius = cx + step[0], cy + step[1], radius + step[2]
        if float(np.max(np.abs(step))) < 1e-12:
            break
    if not (math.isfinite(cx) and math.isfinite(cy) and math.isfinite(radius)) or radius <= 0:
        return None
    return cx, cy, radius, len(points)


def detect_circles(lines, tolerance, min_points, snap_tolerance=0.2, snap_relative=0.0002,
                   relative=0.01):
    """Mark closed polylines that form a complete circle as circle entities.

    A loop qualifies only when every vertex lies within `tolerance` of the fitted
    circle, the vertices really wind around the centre once (no angular gap of
    180 degrees or more), and the loop is dense enough: a true circle in JT is
    tessellated into many short chords, while a real hexagon/octagon/square has
    its vertices on one circle too and can only be told apart by chord length.
    Open polylines are never considered, so a section cut that merely looks
    curved cannot silently become a circle.

    A fitted diameter that is within `max(snap_tolerance, snap_relative*diameter)`
    of an integer is snapped to it: tessellation puts nominal holes at values like
    24.98 instead of 25. Diameters that sit well away from an integer (thread
    minor diameters such as 20.74) are left untouched.
    """
    require(math.isfinite(tolerance) and tolerance > 0, 'Circle tolerance must be positive and finite')
    require(isinstance(min_points, int) and min_points >= 3, 'Circle minimum point count must be at least 3')
    require(math.isfinite(snap_tolerance) and snap_tolerance >= 0, 'Snap tolerance must not be negative')
    require(math.isfinite(snap_relative) and snap_relative >= 0, 'Snap ratio must not be negative')
    require(math.isfinite(relative) and relative >= 0, 'Circle relative tolerance must not be negative')
    detected = 0
    for line in lines:
        if not line['closed'] or len(line['xy']) < min_points:
            continue
        fitted = fit_circle(line['xy'])
        if fitted is None:
            continue
        cx, cy, radius, used = fitted
        points = np.asarray(line['xy'], dtype=float)
        deviation = np.abs(np.hypot(points[:, 0] - cx, points[:, 1] - cy) - radius)
        worst = float(deviation.max())
        # Tessellated holes are not perfect circles: a 14 mm bore can show 0.12 mm
        # of form error. Allow a small relative window on top of the absolute
        # tolerance, capped so an ellipse still fails.
        limit = min(max(tolerance, relative * 2 * radius), tolerance * 5)
        if worst > limit:
            continue
        angles = np.sort(np.arctan2(points[:, 1] - cy, points[:, 0] - cx))
        gaps = np.diff(np.concatenate((angles, [angles[0] + 2 * math.pi])))
        if float(gaps.max()) >= math.pi:
            continue
        # 密度：均匀 n 边形的弦长为 2r·sin(pi/n)，留 25% 余量容纳自适应细分。
        chord_limit = 2 * radius * math.sin(math.pi / min_points) * 1.25
        chords = np.linalg.norm(np.diff(np.vstack((points, points[:1])), axis=0), axis=1)
        if float(chords.max()) > chord_limit:
            continue
        # 直径贴整数值时取整：细分会让名义 25 的孔落在 24.98。偏离较远的
        # 直径（如螺纹小径 20.74）保持原值。
        diameter = 2 * radius
        snapped = False
        limit = max(snap_tolerance, snap_relative * diameter)
        nearest = float(round(diameter))
        if nearest > 0 and abs(diameter - nearest) <= limit:
            radius = nearest / 2
            snapped = True
            deviation = np.abs(np.hypot(points[:, 0] - cx, points[:, 1] - cy) - radius)
            worst = float(deviation.max())
        line['circle'] = dict(cx=float(cx), cy=float(cy), r=float(radius), points=used,
                              diameter_mm=float(2 * radius), snapped=snapped,
                              deviation_limit_mm=float(max(tolerance, worst)),
                              max_deviation_mm=worst,
                              max_chord_mm=float(chords.max()),
                              rms_mm=float(math.sqrt(float(np.mean(deviation ** 2)))))
        detected += 1
    return detected


def simplify_indices(points, tolerance):
    """Douglas-Peucker over a 2D polyline; returns the indices that survive."""
    count = len(points)
    if count <= 2:
        return list(range(count))
    keep = np.zeros(count, dtype=bool)
    keep[0] = keep[-1] = True
    stack = [(0, count - 1)]
    while stack:
        start, end = stack.pop()
        if end <= start + 1:
            continue
        a, b = points[start], points[end]
        segment = b - a
        span = float(np.linalg.norm(segment))
        chunk = points[start + 1:end]
        if span <= 1e-12:
            distance = np.linalg.norm(chunk - a, axis=1)
        else:
            distance = np.abs((chunk[:, 0] - a[0]) * segment[1] - (chunk[:, 1] - a[1]) * segment[0]) / span
        index = int(np.argmax(distance))
        if float(distance[index]) > tolerance:
            split = start + 1 + index
            keep[split] = True
            stack.append((start, split))
            stack.append((split, end))
    return [int(i) for i in np.flatnonzero(keep)]


def is_straight(points, start, end, tolerance):
    """True when every point between start and end stays within tolerance of the chord."""
    a, b = points[start], points[end]
    segment = b - a
    span = float(np.linalg.norm(segment))
    chunk = points[start:end + 1]
    if span <= 1e-12:
        return float(np.max(np.linalg.norm(chunk - a, axis=1))) <= tolerance
    offset = np.abs((chunk[:, 0] - a[0]) * segment[1] - (chunk[:, 1] - a[1]) * segment[0]) / span
    projection = ((chunk - a) @ segment) / (span * span)
    return (float(offset.max()) <= tolerance
            and float(projection.min()) >= -1e-9 and float(projection.max()) <= 1 + 1e-9)


def arc_from_points(points, start, end, tolerance, density, min_angle_deg):
    """Fit an arc through points[start:end+1]; None when it is not arc-like.

    `density` is the same chord bound used for circles: a real arc is tessellated
    into many short chords, while a rectangle's corners also sit on one circle and
    can only be rejected by chord length. Requires at least four distinct points,
    a bounded radial error, a minimum sweep and a single turning direction.
    """
    chunk = points[start:end + 1]
    if len(chunk) < 4:
        return None
    fitted = fit_circle(chunk)
    if fitted is None:
        return None
    cx, cy, radius, used = fitted
    if used < 4:
        return None
    deviation = np.abs(np.hypot(chunk[:, 0] - cx, chunk[:, 1] - cy) - radius)
    if float(deviation.max()) > tolerance:
        return None
    chords = np.linalg.norm(np.diff(chunk, axis=0), axis=1)
    if float(chords.max()) > 2 * radius * math.sin(math.pi / max(4, density)) * 1.25:
        return None
    angles = np.unwrap(np.arctan2(chunk[:, 1] - cy, chunk[:, 0] - cx))
    sweep = float(angles[-1] - angles[0])
    if abs(sweep) < math.radians(min_angle_deg):
        return None
    steps = np.diff(angles)
    if bool(np.any(steps > 0)) and bool(np.any(steps < 0)):
        return None
    return dict(cx=float(cx), cy=float(cy), r=float(radius), sweep=sweep,
                max_deviation_mm=float(deviation.max()))


def segment_chain(points, tolerance, min_points, min_angle_deg):
    """Split one polyline into straight and circular-arc parts, or return None."""
    if len(points) < 3:
        return None
    # Simplify first: the scan then runs on a few characters instead of thousands
    # of tessellation points. Half the tolerance is spent here and half on fitting.
    keep = simplify_indices(points, tolerance * 0.5)
    segments = len(points) - 1
    if len(keep) < 2:
        return None
    # A profile that still needs this many characters is not a clean CAD outline;
    # skipping keeps the greedy scan bounded.
    if len(keep) > 1200:
        return None
    if len(keep) == 2:
        # A perfectly straight chain collapses to its two endpoints.
        if not is_straight(points, 0, segments, tolerance):
            return None
        return [dict(kind='line', a=[float(points[0][0]), float(points[0][1])],
                     b=[float(points[-1][0]), float(points[-1][1])],
                     source=[[float(p[0]), float(p[1])] for p in points])]
    reduced = np.asarray([points[i] for i in keep], dtype=float)
    part_tolerance = tolerance * 0.5
    parts = []
    index = 0
    total = len(keep) - 1
    while index < total:
        # Straight run: grow while every reduced vertex stays on the chord.
        straight = index + 1
        while straight + 1 <= total and is_straight(reduced, index, straight + 1, part_tolerance):
            straight += 1
        # Arc run: grow the seed until it spans the minimum angle (a dense
        # tessellation needs many points to cover 15 degrees), then keep growing
        # while the reduced vertices stay on one circle.
        arc = None
        if index + 3 <= total:
            seeded = None
            span = index + 3
            limit = min(total, index + 64)
            while span <= limit:
                seeded = arc_from_points(reduced, index, span, part_tolerance, min_points, min_angle_deg)
                if seeded is not None:
                    break
                span += 1
            if seeded is not None:
                while span + 1 <= total:
                    trial = arc_from_points(reduced, index, span + 1, part_tolerance, min_points, min_angle_deg)
                    if trial is None:
                        break
                    seeded, span = trial, span + 1
                arc = (span, seeded)
        if arc is not None and arc[0] > straight:
            span, fitted = arc
        else:
            span, fitted = straight, None
        first, last = keep[index], keep[span]
        source = points[first:last + 1]
        if fitted is None:
            if not is_straight(points, first, last, tolerance):
                return None
            parts.append(dict(kind='line', a=[float(points[first][0]), float(points[first][1])],
                              b=[float(points[last][0]), float(points[last][1])],
                              source=[[float(p[0]), float(p[1])] for p in source]))
        else:
            verified = arc_from_points(points, first, last, tolerance, min_points, min_angle_deg)
            if verified is None:
                return None
            start_deg, end_deg = dxf_angles(points[first], points[last], verified)
            parts.append(dict(kind='arc', cx=verified['cx'], cy=verified['cy'], r=verified['r'],
                              start_deg=start_deg, end_deg=end_deg, sweep=verified['sweep'],
                              source=[[float(p[0]), float(p[1])] for p in source]))
        index = span
    if len(parts) * 2 > segments:  # no merging gain, keep the original polyline
        return None
    return parts


def dxf_angles(first, last, arc):
    """DXF arcs always run counter-clockwise, so swap ends for a clockwise chain."""
    start = math.degrees(math.atan2(first[1] - arc['cy'], first[0] - arc['cx'])) % 360
    end = math.degrees(math.atan2(last[1] - arc['cy'], last[0] - arc['cx'])) % 360
    return (start, end) if arc['sweep'] >= 0 else (end, start)


def bulged_ring(parts):
    """Turn line/arc parts into one closed polyline with bulge values.

    A fillet becomes a single vertex plus a bulge (tan of a quarter of the
    included angle), so a 40x40 plate with four r5 corners drops from 27 vertices
    to 8 while staying one editable LWPOLYLINE.
    """
    vertices = []
    for part in parts:
        source = part['source']
        if not source:
            return None
        start = source[0]
        if part['kind'] == 'arc':
            bulge = math.tan(part['sweep'] / 4)
            if not math.isfinite(bulge) or abs(bulge) <= 1e-12:
                return None
            vertices.append([float(start[0]), float(start[1]), float(bulge)])
        else:
            vertices.append([float(start[0]), float(start[1]), 0.0])
    return vertices if len(vertices) >= 3 else None


def simplified_loop(xy, tolerance):
    """Reduce a closed ring to its corner points, keeping it a single polyline.

    A closed contour is easier to edit and much smaller as one LWPOLYLINE with the
    redundant points on straight edges removed, so closed loops are never split
    into LINE/ARC parts.
    """
    points = np.asarray(xy, dtype=float)
    if len(points) < 4:
        return None
    ring = np.vstack((points, points[:1]))
    keep = simplify_indices(ring, tolerance)
    reduced = ring[keep[:-1]]
    if len(reduced) < 3 or len(reduced) >= len(points):
        return None
    return [[float(p[0]), float(p[1])] for p in reduced]


def parameterize_lines(lines, tolerance, min_points, min_angle_deg):
    """Simplify closed rings and split open polylines into line/arc parts."""
    require(math.isfinite(tolerance) and tolerance > 0, 'Shape tolerance must be positive and finite')
    require(math.isfinite(min_angle_deg) and min_angle_deg > 0, 'Minimum arc angle must be positive')
    require(isinstance(min_points, int) and min_points >= 4, 'Arc minimum point count must be at least 4')
    count = 0
    for line in lines:
        if line.get('circle') is not None:
            continue
        points = np.asarray(line['xy'], dtype=float)
        if line['closed']:
            # A closed contour stays one polyline. Prefer keeping fillets as
            # bulge arcs; fall back to plain vertex reduction when the loop does
            # not resolve into line/arc parts.
            ring = np.vstack((points, points[:1]))
            parts = segment_chain(ring, tolerance, min_points, min_angle_deg)
            if parts:
                vertices = bulged_ring(parts)
                if vertices and len(vertices) < len(points):
                    line['parts'] = [dict(kind='ring', vertices=vertices, segments=parts,
                                          closed=True, source=line['xy'])]
                    count += 1
                    continue
            reduced = simplified_loop(line['xy'], tolerance)
            if reduced:
                line['parts'] = [dict(kind='poly', xy=reduced, closed=True, source=line['xy'])]
                count += 1
            continue
        parts = segment_chain(points, tolerance, min_points, min_angle_deg)
        if parts:
            line['parts'] = parts
            count += len(parts)
    return count


def verify_source_bounds(lines, root):
    points = np.asarray([p for line in lines for p in line['points']])
    bbox = dict(min=points.min(axis=0).tolist(), max=points.max(axis=0).tolist())
    stored = root.get('transformed_bbox')
    counts = root.get('vertex_count_range')
    require(stored is not None and counts is not None, 'Missing root bounds/count oracle')
    require(counts[0] <= len(points) <= counts[1], 'Decoded vertex indices disagree with root count range')
    require(all(math.isfinite(x) for k in ('min', 'max') for x in stored[k]), 'Invalid root bounds')
    error = max(abs(bbox[k][i] - stored[k][i]) for k in ('min', 'max') for i in range(3))
    # Root boxes are float32 and can round independently of local geometry +
    # double precision placement. Allow two ULPs, never change coordinates.
    bound_tolerance = max(1e-6, max(2 * abs(float(np.spacing(np.float32(x)))) for k in ('min', 'max') for x in stored[k]))
    require(error <= bound_tolerance, f'Decoded world bounds disagree with root: {error} mm')
    return bbox, error, bound_tolerance


def emissions(lines):
    """Flat list of (source line, part) pairs; one output entity per part."""
    result = []
    for line in lines:
        circle = line.get('circle')
        if circle is not None:
            result.append((line, dict(kind='circle', **circle)))
        elif line.get('parts'):
            for part in line['parts']:
                result.append((line, part))
        else:
            result.append((line, dict(kind='poly', xy=line['xy'], closed=line['closed'])))
    return result


def angle_inside(value, start, end):
    return ((value - start) % 360) <= ((end - start) % 360) + 1e-7


def straight_deviation(source, a, b):
    """Maximum distance of the source vertices from the segment a-b."""
    chunk = np.asarray(source, dtype=float)
    start = np.asarray(a, dtype=float)
    end = np.asarray(b, dtype=float)
    span_vector = end - start
    span = float(np.linalg.norm(span_vector))
    if span <= 1e-12:
        return float(np.max(np.linalg.norm(chunk - start, axis=1)))
    return float(np.max(np.abs((chunk[:, 0] - start[0]) * span_vector[1]
                               - (chunk[:, 1] - start[1]) * span_vector[0]) / span))


def arc_deviation(source, cx, cy, radius):
    chunk = np.asarray(source, dtype=float)
    return float(np.max(np.abs(np.hypot(chunk[:, 0] - cx, chunk[:, 1] - cy) - radius)))


def emitted_vertices(lines):
    """Number of coordinate pairs actually written to the DXF."""
    total = 0
    for line in lines:
        if line.get('circle') is not None:
            continue
        parts = line.get('parts')
        if not parts:
            total += len(line['xy'])
            continue
        for part in parts:
            if part['kind'] == 'line':
                total += 2
            elif part['kind'] == 'ring':
                total += len(part['vertices'])
            elif part['kind'] == 'poly':
                total += len(part['xy'])
    return total


def ring_distance(source, xy, closed):
    """Maximum distance from every source vertex to the emitted polyline."""
    ring = np.asarray(xy, dtype=float)
    if closed:
        ring = np.vstack((ring, ring[:1]))
    src = np.asarray(source, dtype=float)
    best = np.full(len(src), np.inf)
    for index in range(len(ring) - 1):
        a, b = ring[index], ring[index + 1]
        span_vector = b - a
        span = float(np.linalg.norm(span_vector))
        if span <= 1e-12:
            best = np.minimum(best, np.linalg.norm(src - a, axis=1))
            continue
        projection = np.clip(((src - a) @ span_vector) / (span * span), 0, 1)
        closest = a + projection[:, None] * span_vector
        best = np.minimum(best, np.linalg.norm(src - closest, axis=1))
    return float(best.max())


def write_and_verify(lines, plane, output, circle_tolerance=0.1, shape_tolerance=0.1):
    doc = ezdxf.new('R2010', setup=True)
    doc.units = 4
    doc.header['$MEASUREMENT'] = 1
    doc.header['$INSBASE'] = (0, 0, 0)
    msp = doc.modelspace()
    for line in lines:
        if line['layer'] not in doc.layers:
            rgb = [round(max(0, min(1, x)) * 255) for x in line['rgba'][:3]]
            doc.layers.new(line['layer'], dxfattribs={'color': 7, 'true_color': ezdxf.colors.rgb2int(rgb)})
    items = emissions(lines)
    for line, part in items:
        attrs = {'layer': line['layer']}
        kind = part['kind']
        if kind == 'circle':
            msp.add_circle((part['cx'], part['cy'], 0), part['r'], dxfattribs=attrs)
        elif kind == 'line':
            msp.add_line((*part['a'], 0), (*part['b'], 0), dxfattribs=attrs)
        elif kind == 'arc':
            msp.add_arc((part['cx'], part['cy'], 0), part['r'], part['start_deg'], part['end_deg'],
                        dxfattribs=attrs)
        elif kind == 'ring':
            msp.add_lwpolyline([(v[0], v[1], v[2]) for v in part['vertices']], format='xyb',
                               close=True, dxfattribs=attrs)
        else:
            xy = part['xy']
            require(len(xy) >= 2, 'Degenerate line with fewer than two output vertices')
            if len(xy) == 2 and not part['closed']:
                msp.add_line((*xy[0], 0), (*xy[1], 0), dxfattribs=attrs)
            else:
                msp.add_lwpolyline(xy, close=part['closed'], dxfattribs=attrs)
    output.parent.mkdir(parents=True, exist_ok=True)
    # Validate a temporary file before replacing the requested output.
    with tempfile.TemporaryDirectory(dir=output.parent) as tmp:
        candidate = Path(tmp) / 'candidate.dxf'
        doc.saveas(candidate)
        check = ezdxf.readfile(candidate)
        audit = check.audit()
        require(not audit.errors and not audit.fixes, 'DXF structural audit failed or required repairs')
        entities = list(check.modelspace())
        require(check.units == 4 and len(entities) == len(items), 'DXF unit/entity count mismatch')
        max_xy, max_world, max_circle, max_line, max_arc, max_simplify = 0., 0., 0., 0., 0., 0.
        origin, u, v = (np.asarray(plane[k]) for k in ('origin', 'u', 'v'))
        for (source, part), entity in zip(items, entities):
            require(entity.dxf.layer == source['layer'], 'DXF layer mismatch')
            kind = part['kind']
            if kind == 'circle':
                require(entity.dxftype() == 'CIRCLE', 'Circle entity expected for fitted loop')
                require(entity.dxf.center.z == 0, 'Nonzero output Z on circle')
                require(abs(entity.dxf.radius - part['r']) <= 1e-9, 'Circle radius round-trip failed')
                max_xy = max(max_xy, math.hypot(entity.dxf.center.x - part['cx'],
                                                entity.dxf.center.y - part['cy']))
                centre = origin + part['cx'] * u + part['cy'] * v
                points = np.asarray(source['points'][:-1] if source['closed'] else source['points'])
                deviation = float(np.max(abs(np.linalg.norm(points - centre, axis=1) - part['r'])))
                require(deviation <= part.get('deviation_limit_mm', circle_tolerance) + 1e-9,
                        'Circle deviates from its source vertices beyond the snapped tolerance')
                max_circle = max(max_circle, deviation)
                continue
            if kind == 'line':
                require(entity.dxftype() == 'LINE', 'LINE entity expected for a straight part')
                require(entity.dxf.start.z == entity.dxf.end.z == 0, 'Nonzero output Z')
                max_xy = max(max_xy, math.dist((entity.dxf.start.x, entity.dxf.start.y), part['a']),
                             math.dist((entity.dxf.end.x, entity.dxf.end.y), part['b']))
                segment = np.asarray([part['a'], part['b']])
                span = float(np.linalg.norm(segment[1] - segment[0]))
                chunk = np.asarray(part['source'])
                if span <= 1e-12:
                    max_line = max(max_line, float(np.max(np.linalg.norm(chunk - segment[0], axis=1))))
                else:
                    direction = (segment[1] - segment[0]) / span
                    offsets = np.abs((chunk - segment[0])[:, 0] * direction[1]
                                     - (chunk - segment[0])[:, 1] * direction[0])
                    max_line = max(max_line, float(offsets.max()))
                continue
            if kind == 'ring':
                require(entity.dxftype() == 'LWPOLYLINE' and entity.closed and entity.dxf.elevation == 0,
                        'Closed bulged polyline expected')
                written = [(p[0], p[1], p[2]) for p in entity.get_points('xyb')]
                require(len(written) == len(part['vertices']), 'Bulged polyline vertex count mismatch')
                for (x, y, bulge), (vx, vy, vb) in zip(written, part['vertices']):
                    max_xy = max(max_xy, math.hypot(x - vx, y - vy))
                    require(abs(bulge - vb) <= 1e-9, 'Bulge value round-trip failed')
                for segment in part['segments']:
                    if segment['kind'] == 'line':
                        max_line = max(max_line, straight_deviation(segment['source'],
                                                                    segment['a'], segment['b']))
                        continue
                    max_arc = max(max_arc, arc_deviation(segment['source'], segment['cx'],
                                                         segment['cy'], segment['r']))
                    chunk = np.asarray(segment['source'], dtype=float)
                    angles = np.degrees(np.arctan2(chunk[:, 1] - segment['cy'],
                                                   chunk[:, 0] - segment['cx'])) % 360
                    require(all(angle_inside(a, segment['start_deg'], segment['end_deg']) for a in angles),
                            'Arc source vertex falls outside the arc sweep')
                continue
            if kind == 'arc':
                require(entity.dxftype() == 'ARC', 'ARC entity expected for a fitted arc')
                require(entity.dxf.center.z == 0, 'Nonzero output Z on arc')
                require(abs(entity.dxf.radius - part['r']) <= 1e-9, 'Arc radius round-trip failed')
                require(abs(entity.dxf.start_angle - part['start_deg']) <= 1e-7
                        and abs(entity.dxf.end_angle - part['end_deg']) <= 1e-7, 'Arc angle round-trip failed')
                max_xy = max(max_xy, math.hypot(entity.dxf.center.x - part['cx'],
                                                entity.dxf.center.y - part['cy']))
                chunk = np.asarray(part['source'])
                radial = np.abs(np.hypot(chunk[:, 0] - part['cx'], chunk[:, 1] - part['cy']) - part['r'])
                max_arc = max(max_arc, float(radial.max()))
                angles = np.degrees(np.arctan2(chunk[:, 1] - part['cy'], chunk[:, 0] - part['cx'])) % 360
                require(all(angle_inside(a, part['start_deg'], part['end_deg']) for a in angles),
                        'Arc source vertex falls outside the arc sweep')
                continue
            if kind == 'poly':
                xy = part['xy']
                if len(xy) == 2 and not part['closed']:
                    require(entity.dxftype() == 'LINE', 'LINE entity expected for a two point segment')
                    require(entity.dxf.start.z == entity.dxf.end.z == 0, 'Nonzero output Z')
                    xy = [[entity.dxf.start.x, entity.dxf.start.y], [entity.dxf.end.x, entity.dxf.end.y]]
                else:
                    require(entity.dxftype() == 'LWPOLYLINE' and entity.dxf.elevation == 0,
                            'Unexpected output entity')
                    xy = [list(p) for p in entity.get_points('xy')]
                    require(entity.closed == part['closed'], 'DXF closure mismatch')
                require(len(xy) == len(part['xy']), 'DXF vertex count mismatch')
                max_xy = max(max_xy, max(math.dist(a, b) for a, b in zip(xy, part['xy'])))
                if part.get('source') is not None:
                    # Simplified ring: every source vertex must still be within
                    # the shape tolerance of the emitted polyline.
                    max_simplify = max(max_simplify, ring_distance(part['source'], xy, part['closed']))
                else:
                    points = np.asarray(xy)
                    reconstructed = origin + points[:, 0, None] * u + points[:, 1, None] * v
                    expected = np.asarray(source['points'][:-1] if part['closed'] else source['points'])
                    max_world = max(max_world, float(np.max(np.linalg.norm(reconstructed - expected, axis=1))))
        require(max_xy <= 1e-8 and max_world <= plane['tolerance_mm'] + 1e-8, 'DXF coordinate round-trip failed')
        require(max_line <= shape_tolerance + 1e-8, 'Straight segment deviation exceeds tolerance')
        require(max_arc <= shape_tolerance + 1e-8, 'Arc deviation exceeds tolerance')
        require(max_simplify <= shape_tolerance + 1e-8, 'Simplified polyline deviation exceeds tolerance')
        candidate.replace(output)
    return dict(entity_count=len(entities), audit_errors=0, audit_fixes=0,
                max_xy_roundtrip_error_mm=max_xy, max_world_roundtrip_error_mm=max_world,
                max_circle_deviation_mm=max_circle, max_line_deviation_mm=max_line,
                max_arc_deviation_mm=max_arc, max_simplify_deviation_mm=max_simplify,
                circle_entities=sum(1 for _, part in items if part['kind'] == 'circle'),
                line_entities=sum(1 for _, part in items if part['kind'] == 'line'),
                arc_entities=sum(1 for _, part in items if part['kind'] == 'arc'),
                polyline_entities=sum(1 for _, part in items if part['kind'] in ('poly', 'ring')),
                bulged_polylines=sum(1 for _, part in items if part['kind'] == 'ring'),
                snapped_circles=sum(1 for _, part in items if part['kind'] == 'circle' and part.get('snapped')))


def convert(source, output, tolerance=0.001, progress=None, circles=True,
            circle_tolerance=0.1, circle_min_points=16, shapes=True,
            shape_tolerance=0.1, arc_min_angle=15.0,
            snap_tolerance=0.2, snap_relative=0.0002, circle_relative=0.01):
    progress = progress or (lambda percent, stage: None)
    progress(2, 'read')
    source, output = Path(source), Path(output)
    require(source.resolve() != output.resolve() and output.suffix.lower() == '.dxf', 'Output must be a distinct .dxf file')
    raw = source.read_bytes()
    require(raw[:80].startswith(b'Version 10.6 JT') and len(raw) > 109 and raw[80] == 0, 'This version supports little-endian JT 10.6 only')
    inventory = analyze(source)
    progress(10, 'inventory')
    require(not inventory['toc']['overlapping_segments'] and not inventory['segments']['xz_failures'], 'Invalid JT container')
    shapes = load_shapes(source, inventory, progress)
    lines3, root = scene_lines(parse_lsg(source), shapes)
    progress(70, 'scene')
    bbox, bbox_error, bbox_tolerance = verify_source_bounds(lines3, root)
    lines2, plane = flatten(lines3, tolerance)
    progress(80, 'plane')
    circle_enabled = bool(circles)
    circles = (detect_circles(lines2, circle_tolerance, circle_min_points, snap_tolerance,
                              snap_relative, circle_relative)
               if circle_enabled else 0)
    shape_enabled = bool(shapes)
    parts = parameterize_lines(lines2, shape_tolerance, circle_min_points, arc_min_angle) if shape_enabled else 0
    verification = write_and_verify(lines2, plane, output, circle_tolerance, shape_tolerance if shape_enabled else circle_tolerance)
    progress(95, 'verify')
    verification.update(root_index_count_matches=True, root_bounds_within_float32_tolerance=True,
                        root_bbox_tolerance_mm=bbox_tolerance)
    xy = np.asarray([p for line in lines2 for p in line['xy']])
    stored_bbox = root.get('transformed_bbox')
    report = dict(input=str(source.resolve()), input_sha256=hashlib.sha256(raw).hexdigest(),
                  output=str(output.resolve()), profile='PS planar Polyline / JT 10.6 / unquantized XYZ', units='millimeters',
                  shape_segments=len(shapes), instance_polylines=len(lines2),
                  unique_local_vertices=sum(s[1]['unique_vertices'] for s in shapes.values()),
                  source_index_count=sum(len(l['points']) for l in lines3),
                  output_vertices=sum(len(l['xy']) for l in lines2 if l.get('circle') is None),
                  dxf_vertices=emitted_vertices(lines2),
                  closed_polylines=sum(l['closed'] for l in lines2),
                  circles=circles,
                  circle_options=dict(enabled=circle_enabled, tolerance_mm=circle_tolerance,
                                      relative=circle_relative, min_points=circle_min_points),
                  shape_parts=parts,
                  shape_options=dict(enabled=shape_enabled, tolerance_mm=shape_tolerance,
                                     min_arc_angle_deg=arc_min_angle),
                  snap_options=dict(tolerance_mm=snap_tolerance, relative=snap_relative),
                  segment_count=sum(len(l['points']) - 1 for l in lines3),
                  zero_length_segments=sum(a == b for l in lines3 for a, b in zip(l['points'], l['points'][1:])),
                  world_bbox_mm=bbox, stored_world_bbox_mm=stored_bbox, bbox_max_difference_mm=bbox_error,
                  stored_vertex_count_range=root.get('vertex_count_range'),
                  dxf_bbox_mm=dict(min=xy.min(axis=0).tolist(), max=xy.max(axis=0).tolist()),
                  source_length_mm=sum(length(l['points']) for l in lines3),
                  output_length_mm=sum(length(l['xy'], l['closed']) for l in lines2),
                  plane=plane, verification=verification,
                  shapes=[s[1] for s in shapes.values()],
                  entities=[dict({k: l[k] for k in ('shape_id', 'primitive', 'layer', 'segment_id', 'scene_path', 'closed')},
                                 circle=l.get('circle'), parts=len(l.get('parts') or [])) for l in lines2],
                  limitations=['Stored JT hashes are recorded, not recomputed.',
                               'Polylines are split into LINE/ARC only when every part stays within the '
                               'configured tolerance and the split reduces the entity count; otherwise the '
                               'original polyline is kept. Joining, deduplication and precision recovery are '
                               'not applied.',
                               'Only the explicit PS JT 10.6 profile is supported; other layouts fail.'])
    report_path = output.with_suffix('.report.json')
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    progress(100, 'done')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--plane-tolerance', type=float, default=0.001, help='Maximum flattening distance in mm (default: 0.001)')
    parser.add_argument('--circle-tolerance', type=float, default=0.1,
                        help='Maximum radial deviation in mm for a closed loop to become a CIRCLE (default: 0.1)')
    parser.add_argument('--circle-min-points', type=int, default=16,
                        help='Minimum vertex count before a closed loop is considered a circle (default: 16); '
                             'it also bounds the chord length, so regular polygons stay polylines')
    parser.add_argument('--circle-relative', type=float, default=0.01,
                        help='Extra relative form-error window for circles, scaled by the diameter and '
                             'capped at 5x the absolute tolerance (default: 0.01)')
    parser.add_argument('--no-circles', dest='circles', action='store_false',
                        help='Keep every closed loop as a polyline')
    parser.add_argument('--shape-tolerance', type=float, default=0.1,
                        help='Maximum deviation in mm when splitting a polyline into LINE/ARC (default: 0.1)')
    parser.add_argument('--arc-min-angle', type=float, default=15.0,
                        help='Minimum arc sweep in degrees; shorter arcs stay polylines (default: 15)')
    parser.add_argument('--no-shapes', dest='shapes', action='store_false',
                        help='Write every non-circular polyline unchanged')
    parser.add_argument('--snap-tolerance', type=float, default=0.2,
                        help='Absolute diameter snapping window in mm (default: 0.2): a bore fitted at '
                             '19.84 becomes 20, while a 20.74 thread minor diameter stays as is')
    parser.add_argument('--snap-relative', type=float, default=0.0002,
                        help='Relative diameter snapping window, scaled by the diameter (default: 0.0002)')
    parser.add_argument('--no-snap', dest='snap', action='store_false',
                        help='Keep the fitted diameter of every circle')
    parser.add_argument('--progress', action='store_true', help='Write machine-readable progress lines to stdout')
    args = parser.parse_args()
    try:
        if hasattr(sys.stdout, 'reconfigure'):
            sys.stdout.reconfigure(encoding='utf-8')
            sys.stderr.reconfigure(encoding='utf-8')
        callback = (lambda percent, stage: print(f'::progress::{percent}|{stage}', flush=True)) if args.progress else None
        report = convert(args.input, args.output, args.plane_tolerance, callback,
                         circles=args.circles, circle_tolerance=args.circle_tolerance,
                         circle_min_points=args.circle_min_points, shapes=args.shapes,
                         shape_tolerance=args.shape_tolerance, arc_min_angle=args.arc_min_angle,
                         snap_tolerance=args.snap_tolerance if args.snap else 0.0,
                         snap_relative=args.snap_relative if args.snap else 0.0,
                         circle_relative=args.circle_relative)
    except (ValueError, OSError, struct.error, IndexError) as error:
        parser.exit(1, f'Conversion failed: {error}\n')
    print(json.dumps({k: report[k] for k in ('output', 'shape_segments', 'instance_polylines', 'closed_polylines',
                                             'circles', 'shape_parts', 'segment_count', 'verification')}, ensure_ascii=False))


if __name__ == '__main__':
    main()
