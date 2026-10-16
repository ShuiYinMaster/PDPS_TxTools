"""Sphere union hypothesis verified on controlled native boxes, not a CGR spec."""
import math


def merge_spheres(center_a, radius_a, center_b, radius_b):
    if len(center_a) != 3 or len(center_b) != 3:
        raise ValueError("Expected three coordinates")
    if not all(math.isfinite(v) for v in (*center_a, *center_b, radius_a, radius_b)) or min(radius_a, radius_b) < 0:
        raise ValueError("Invalid sphere")
    distance = math.dist(center_a, center_b)
    if radius_a >= distance + radius_b:
        return tuple(center_a), radius_a
    if radius_b >= distance + radius_a:
        return tuple(center_b), radius_b
    radius = (distance + radius_a + radius_b) / 2
    shift = (radius - radius_a) / distance
    return tuple(a + (b - a) * shift for a, b in zip(center_a, center_b)), radius
