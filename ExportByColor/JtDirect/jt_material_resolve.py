"""Resolve diffuse RGB/alpha fields using JT LSG replacement accumulation."""
import math


def resolve_diffuse(path, nodes, materials):
    rgb = alpha = None
    rgb_id = alpha_id = None
    final = 0
    chain = []
    for node_id in path:
        node = nodes[node_id]
        for attr_id in node.get('node', {}).get('attribute_ids', []):
            if attr_id not in materials:
                continue
            m = materials[attr_id]
            if m.get('error') or m.get('material_version') != 1:
                raise ValueError(f'Unsupported material layout: {attr_id}')
            rgba = m.get('diffuse_color')
            if not rgba or len(rgba) != 4 or any(not math.isfinite(v) or v<0 or v>1 for v in rgba):
                raise ValueError(f'Invalid diffuse RGBA: {attr_id}')
            state = m['state_flags']
            inhibit = m['field_inhibit_flags']
            fields_final = m['field_final_flags']
            if state & 4:
                continue
            allowed = ~inhibit & (~final if not state & 2 else ~0)
            applied = []
            if allowed & (1 << 6):
                rgb, rgb_id = rgba[:3], attr_id
                applied.append('rgb')
            if allowed & (1 << 7):
                alpha, alpha_id = rgba[3], attr_id
                applied.append('alpha')
            final |= fields_final & allowed
            chain.append(dict(node_id=node_id, node_type=node.get('type'), material_id=attr_id,
                              diffuse_rgba=rgba, applied_fields=applied))
    return dict(rgba=rgb+[alpha if alpha is not None else 1.0] if rgb is not None else None,
                rgb_material_id=rgb_id, alpha_material_id=alpha_id, material_chain=chain)
