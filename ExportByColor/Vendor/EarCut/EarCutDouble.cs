using System;
using System.Collections.Generic;
// ReSharper disable CognitiveComplexity
using TxTools.ExportByColor.Vendor.EarCut.Extensions;

namespace TxTools.ExportByColor.Vendor.EarCut {

internal static class EarCut
{
    public static List<int> Calculate(double[] data, int[] holeIndices = null, int dim = 2)
    {
        if (data == null)
        {
            return new List<int>();
        }

        var hasHoles = holeIndices != null && holeIndices.Length > 0;
        var outerLen = hasHoles ? holeIndices[0] * dim : data.Length;

        var outerNode = LinkedList(data, 0, outerLen, dim, true);

        List<int> triangles = new List<int>();

        if (outerNode == null || outerNode.Next == outerNode.Prev)
            return triangles;

        double minX = 0;
        double minY = 0;
        var invSize = double.MinValue;

        if (hasHoles)
            outerNode = EliminateHoles(data, holeIndices, outerNode, dim);

        // if the shape is not too simple, we'll use z-order curve hash later;
        // calculate polygon bbox
        if (data.Length > 80 * dim) {
            double maxX;
            double maxY;

            minX = maxX = data[0];
            minY = maxY = data[1];

            for (int i = dim; i < outerLen; i += dim) {
                double x = data[i];
                double y = data[i + 1];
                if (x < minX)
                    minX = x;
                if (y < minY)
                    minY = y;
                if (x > maxX)
                    maxX = x;
                if (y > maxY)
                    maxY = y;
            }

            // minX, minY and size are later used to transform coords into
            // integers for z-order calculation
            invSize = Math.Max(maxX - minX, maxY - minY);
            invSize = invSize != 0.0 ? 1.0 / invSize : 0.0;
        }

        EarCutLinked(outerNode, triangles, dim, minX, minY, invSize, int.MinValue);

        return triangles;
    }

    private static void EarCutLinked(Node ear, List<int> triangles, int dim, double minX, double minY, double invSize, int pass) {
        if (ear == null)
            return;

        // interlink polygon nodes in z-order
        if (pass == int.MinValue && invSize.NotEqual(double.MinValue))
            IndexCurve(ear, minX, minY, invSize);

        var stop = ear;

        // iterate through ears, slicing them one by one
        while (ear.Prev != ear.Next) {
            var prev = ear.Prev;
            var next = ear.Next;

            if (invSize.NotEqual(double.MinValue) ? IsEarHashed(ear, minX, minY, invSize) : IsEar(ear)) {
                // cut off the triangle
                triangles.Add(prev.I / dim);
                triangles.Add(ear.I / dim);
                triangles.Add(next.I / dim);

                RemoveNode(ear);

                // skipping the next vertex leads to less sliver triangles
                ear = next.Next;
                stop = next.Next;

                continue;
            }

            ear = next;

            // if we looped through the whole remaining polygon and can't find
            // any more ears
            if (ear == stop) {
                // try filtering points and slicing again
                if (pass == int.MinValue) {
                    EarCutLinked(FilterPoints(ear, null), triangles, dim, minX, minY, invSize, 1);

                    // if this didn't work, try curing all small
                    // self-intersections locally
                } else if (pass == 1) {
                    ear = CureLocalIntersections(FilterPoints(ear, null), triangles, dim);
                    EarCutLinked(ear, triangles, dim, minX, minY, invSize, 2);

                    // as a last resort, try splitting the remaining polygon
                    // into two
                } else if (pass == 2) {
                    SplitEarCut(ear, triangles, dim, minX, minY, invSize);
                }

                break;
            }
        }
    }

    private static void SplitEarCut(Node start, List<int> triangles, int dim, double minX, double minY, double size) {
        // look for a valid diagonal that divides the polygon into two
        var a = start;
        do {
            var b = a.Next.Next;
            while (b != a.Prev) {
                if (a.I != b.I && IsValidDiagonal(a, b)) {
                    // split the polygon in two by the diagonal
                    var c = SplitPolygon(a, b);

                    // Filter collinear points around the cuts
                    a = FilterPoints(a, a.Next);
                    c = FilterPoints(c, c.Next);

                    // run ear cut on each half
                    EarCutLinked(a, triangles, dim, minX, minY, size, int.MinValue);
                    EarCutLinked(c, triangles, dim, minX, minY, size, int.MinValue);
                    return;
                }
                b = b.Next;
            }
            a = a.Next;
        } while (a != start);
    }

    private static bool IsValidDiagonal(Node a, Node b) {
        //return a.next.i != b.i && a.prev.i != b.i && !intersectsPolygon(a, b) && locallyInside(a, b) && locallyInside(b, a) && middleInside(a, b);

        return a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b) && // doesn't intersect other edges
               (LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) && // locally visible
                (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0) || // does not create opposite-facing sectors
                Equals(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0); // special zero-length case
    }

    private static bool MiddleInside(Node a, Node b) {
        var p = a;
        var inside = false;
        var px = (a.X + b.X) / 2;
        var py = (a.Y + b.Y) / 2;
        do {
            if (((p.Y > py) != (p.Next.Y > py)) && (px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X))
                inside = !inside;
            p = p.Next;
        } while (p != a);

        return inside;
    }

    private static bool IntersectsPolygon(Node a, Node b) {
        Node p = a;
        do {
            if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b))
                return true;
            p = p.Next;
        } while (p != a);

        return false;
    }

    private static bool Intersects(Node p1, Node q1, Node p2, Node q2) {
        if ((Equals(p1, p2) && Equals(q1, q2)) || (Equals(p1, q2) && Equals(p2, q1)))
            return true;
        double o1 = Sign(Area(p1, q1, p2));
        double o2 = Sign(Area(p1, q1, q2));
        double o3 = Sign(Area(p2, q2, p1));
        double o4 = Sign(Area(p2, q2, q1));

        if (o1.NotEqual(o2) && o3.NotEqual(o4))
            return true; // general case

        if (o1 == 0 && OnSegment(p1, p2, q1))
            return true; // p1, q1 and p2 are collinear and p2 lies on p1q1
        if (o2 == 0 && OnSegment(p1, q2, q1))
            return true; // p1, q1 and q2 are collinear and q2 lies on p1q1
        if (o3 == 0 && OnSegment(p2, p1, q2))
            return true; // p2, q2 and p1 are collinear and p1 lies on p2q2
        if (o4 == 0 && OnSegment(p2, q1, q2))
            return true; // p2, q2 and q1 are collinear and q1 lies on p2q2

        return false;
    }

    // for collinear points p, q, r, check if point q lies on segment pr
    private static bool OnSegment(Node p, Node q, Node r) {
        return q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);
    }

    private static double Sign(double num) {
        return num > 0 ? 1 : num < 0 ? -1 : 0;
    }

    private static Node CureLocalIntersections(Node start, List<int> triangles, int dim) {
        var p = start;
        do {
            Node a = p.Prev, b = p.Next.Next;

            if (!Equals(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a)) {

                triangles.Add(a.I / dim);
                triangles.Add(p.I / dim);
                triangles.Add(b.I / dim);

                // remove two nodes involved
                RemoveNode(p);
                RemoveNode(p.Next);

                p = start = b;
            }
            p = p.Next;
        } while (p != start);

        return FilterPoints(p, null);
    }

    private static bool IsEar(Node ear) {
        Node a = ear.Prev, b = ear, c = ear.Next;

        if (Area(a, b, c) >= 0)
            return false; // reflex, can't be an ear

        // now make sure we don't have other points inside the potential ear
        var p = ear.Next.Next;

        while (p != ear.Prev) {
            if (PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
                return false;
            p = p.Next;
        }

        return true;
    }

    private static bool IsEarHashed(Node ear, double minX, double minY, double invSize) {
        var a = ear.Prev;
        var b = ear;
        var c = ear.Next;

        if (Area(a, b, c) >= 0)
            return false; // reflex, can't be an ear

        // triangle bbox; min & max are calculated like this for speed
        double minTx = a.X < b.X ? (a.X < c.X ? a.X : c.X) : (b.X < c.X ? b.X : c.X), minTy = a.Y < b.Y ? (a.Y < c.Y ? a.Y : c.Y) : (b.Y < c.Y ? b.Y : c.Y),
                maxTx = a.X > b.X ? (a.X > c.X ? a.X : c.X) : (b.X > c.X ? b.X : c.X), maxTy = a.Y > b.Y ? (a.Y > c.Y ? a.Y : c.Y) : (b.Y > c.Y ? b.Y : c.Y);

        // z-order range for the current triangle bbox;
        var minZ = ZOrder(minTx, minTy, minX, minY, invSize);
        var maxZ = ZOrder(maxTx, maxTy, minX, minY, invSize);

        // first look for points inside the triangle in increasing z-order
        var p = ear.PrevZ;
        var n = ear.NextZ;

        while (p != null && p.Z >= minZ && n != null && n.Z <= maxZ) {
            if (p != ear.Prev && p != ear.Next && PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
                return false;
            p = p.PrevZ;

            if (n != ear.Prev && n != ear.Next && PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, n.X, n.Y) && Area(n.Prev, n, n.Next) >= 0)
                return false;
            n = n.NextZ;
        }

        // look for remaining points in decreasing z-order
        while (p != null && p.Z >= minZ) {
            if (p != ear.Prev && p != ear.Next && PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
                return false;
            p = p.PrevZ;
        }

        // look for remaining points in increasing z-order
        while (n != null && n.Z <= maxZ) {
            if (n != ear.Prev && n != ear.Next && PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, n.X, n.Y) && Area(n.Prev, n, n.Next) >= 0)
                return false;
            n = n.NextZ;
        }

        return true;
    }

    // z-order of a point given coords and inverse of the longer side of data bbox
    private static double ZOrder(double x, double y, double minX, double minY, double invSize) {
        // coords are transformed into non-negative 15-bit integer range
        int lx = (int)(32767 * (x - minX) * invSize);
        int ly = (int)(32767 * (y - minY) * invSize);

        lx = (lx | (lx << 8)) & 0x00FF00FF;
        lx = (lx | (lx << 4)) & 0x0F0F0F0F;
        lx = (lx | (lx << 2)) & 0x33333333;
        lx = (lx | (lx << 1)) & 0x55555555;

        ly = (ly | (ly << 8)) & 0x00FF00FF;
        ly = (ly | (ly << 4)) & 0x0F0F0F0F;
        ly = (ly | (ly << 2)) & 0x33333333;
        ly = (ly | (ly << 1)) & 0x55555555;

        return lx | (ly << 1);
    }

    private static void IndexCurve(Node start, double minX, double minY, double invSize) {
        var p = start;
        do {
            if (p.Z.Equal(double.MinValue))
                p.Z = ZOrder(p.X, p.Y, minX, minY, invSize);
            p.PrevZ = p.Prev;
            p.NextZ = p.Next;
            p = p.Next;
        } while (p != start);

        p.PrevZ.NextZ = null;
        p.PrevZ = null;

        SortLinked(p);
    }

    private static void SortLinked(Node list) {
        var inSize = 1;

        int numMerges;
        do {
            var p = list;
            list = null;
            Node tail = null;
            numMerges = 0;

            while (p != null) {
                numMerges++;
                var q = p;
                var pSize = 0;
                for (var i = 0; i < inSize; i++) {
                    pSize++;
                    q = q.NextZ;
                    if (q == null)
                        break;
                }

                var qSize = inSize;

                while (pSize > 0 || (qSize > 0 && q != null)) {
                    Node e;
                    if (pSize == 0) {
                        e = q;
                        q = q.NextZ;
                        qSize--;
                    } else if (qSize == 0 || q == null) {
                        e = p;
                        p = p.NextZ;
                        pSize--;
                    } else if (p.Z <= q.Z) {
                        e = p;
                        p = p.NextZ;
                        pSize--;
                    } else {
                        e = q;
                        q = q.NextZ;
                        qSize--;
                    }

                    if (tail != null)
                        tail.NextZ = e;
                    else
                        list = e;

                    e.PrevZ = tail;
                    tail = e;
                }

                p = q;
            }

            tail.NextZ = null;
            inSize *= 2;

        } while (numMerges > 1);
    }

    private static Node EliminateHoles(double[] data, int[] holeIndices, Node outerNode, int dim)
    {
        var queue = new List<Node>();

        var len = holeIndices?.Length ?? 0;
        for (var i = 0; i < len; i++) {
            var start = holeIndices[i] * dim;
            var end = i < len - 1 ? holeIndices[i + 1] * dim : data.Length;
            var list = LinkedList(data, start, end, dim, false);
            if (list == list.Next)
                list.Steiner = true;
            queue.Add(GetLeftmost(list));
        }

        queue.Sort((o1, o2) =>
        {
            if (o1.X - o2.X > 0)
            {
                return 1;
            }

            if (o1.X - o2.X < 0)
            {
                return -2;
            }

            return 0;
        });

        foreach (var node in queue)
        {
            EliminateHole(node, outerNode);
            outerNode = FilterPoints(outerNode, outerNode.Next);
        }

        return outerNode;
    }

    private static Node FilterPoints(Node start, Node end) {
        if (start == null)
            return start;
        if (end == null) end = start;

        var p = start;
        bool again;

        do {
            again = false;

            if (!p.Steiner && Equals(p, p.Next) || Area(p.Prev, p, p.Next) == 0) {
                RemoveNode(p);
                p = end = p.Prev;
                if (p == p.Next)
                    break;
                again = true;
            } else {
                p = p.Next;
            }
        } while (again || p != end);

        return end;
    }

    private static bool Equals(Node p1, Node p2) {
        if (p1 == null || p2 == null)
        {
            return false;
        }

        return p1.X.Equal(p2.X) && p1.Y.Equal(p2.Y);
    }

    private static double Area(Node p, Node q, Node r) {
        return (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);
    }

    private static void EliminateHole(Node hole, Node outerNode) {
        outerNode = FindHoleBridge(hole, outerNode);
        if (outerNode != null) {
            Node b = SplitPolygon(outerNode, hole);

            // filter collinear points around the cuts
            FilterPoints(outerNode, outerNode.Next);
            FilterPoints(b, b.Next);
        }
    }

    private static Node SplitPolygon(Node a, Node b) {
        var a2 = new Node(a.I, a.X, a.Y);
        var b2 = new Node(b.I, b.X, b.Y);
        var an = a.Next;
        var bp = b.Prev;

        a.Next = b;
        b.Prev = a;

        a2.Next = an;
        an.Prev = a2;

        b2.Next = a2;
        a2.Prev = b2;

        bp.Next = b2;
        b2.Prev = bp;

        return b2;
    }

    // David Eberly's algorithm for finding a bridge between hole and outer
    // polygon
    private static Node FindHoleBridge(Node hole, Node outerNode) {
        var p = outerNode;
        var hx = hole.X;
        var hy = hole.Y;
        var qx = -Double.MaxValue;
        Node m = null;

        // find a segment intersected by a ray from the hole's leftmost point to
        // the left;
        // segment's endpoint with lesser x will be potential connection point
        do {
            if (hy <= p.Y && hy >= p.Next.Y) {
                double x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
                if (x <= hx && x > qx) {
                    qx = x;
                    if (x.Equal(hx)) {
                        if (hy.Equal(p.Y))
                            return p;
                        if (hy.Equal(p.Next.Y))
                            return p.Next;
                    }
                    m = p.X < p.Next.X ? p : p.Next;
                }
            }
            p = p.Next;
        } while (p != outerNode);

        if (m == null)
            return null;

        if (hx.Equal(qx))
            return m; // hole touches outer segment; pick leftmost endpoint

        // look for points inside the triangle of hole point, segment
        // intersection and endpoint;
        // if there are no points found, we have a valid connection;
        // otherwise choose the point of the minimum angle with the ray as
        // connection point

        var stop = m;
        var mx = m.X;
        var my = m.Y;
        var tanMin = double.MaxValue;

        p = m;

        do {
            if (hx >= p.X && p.X >= mx && PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
            {
                var tan = Math.Abs(hy - p.Y) / (hx - p.X);

                if (LocallyInside(p, hole) && (tan < tanMin || (tan.Equal(tanMin) && (p.X > m.X || (p.X.Equal(m.X) && SectorContainsSector(m, p)))))) {
                    m = p;
                    tanMin = tan;
                }
            }

            p = p.Next;
        } while (p != stop);

        return m;
    }

    private static bool LocallyInside(Node a, Node b) {
        return Area(a.Prev, a, a.Next) < 0 ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0 : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;
    }

    // whether sector in vertex m contains sector in vertex p in the same
    // coordinates
    private static bool SectorContainsSector(Node m, Node p) {
        return Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;
    }

    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) {
        return (cx - px) * (ay - py) - (ax - px) * (cy - py) >= 0 && (ax - px) * (by - py) - (bx - px) * (ay - py) >= 0
                                                                  && (bx - px) * (cy - py) - (cx - px) * (by - py) >= 0;
    }

    private static Node GetLeftmost(Node start) {
        var p = start;
        var leftmost = start;
        do {
            if (p.X < leftmost.X || (p.X.Equal(leftmost.X) && p.Y < leftmost.Y))
                leftmost = p;
            p = p.Next;
        } while (p != start);
        return leftmost;
    }

    private static Node LinkedList(double[] data, int start, int end, int dim, bool clockwise) {
        Node last = null;
        if (clockwise == (SignedArea(data, start, end, dim) > 0)) {
            for (int i = start; i < end; i += dim) {
                last = InsertNode(i, data[i], data[i + 1], last);
            }
        } else {
            for (int i = (end - dim); i >= start; i -= dim) {
                last = InsertNode(i, data[i], data[i + 1], last);
            }
        }

        if (last != null && Equals(last, last.Next)) {
            RemoveNode(last);
            last = last.Next;
        }
        return last;
    }

    private static void RemoveNode(Node p) {
        p.Next.Prev = p.Prev;
        p.Prev.Next = p.Next;

        if (p.PrevZ != null) {
            p.PrevZ.NextZ = p.NextZ;
        }
        if (p.NextZ != null) {
            p.NextZ.PrevZ = p.PrevZ;
        }
    }

    private static Node InsertNode(int i, double x, double y, Node last) {
        var p = new Node(i, x, y);

        if (last == null) {
            p.Prev = p;
            p.Next = p;
        } else {
            p.Next = last.Next;
            p.Prev = last;
            last.Next.Prev = p;
            last.Next = p;
        }
        return p;
    }

    private static double SignedArea(double[] data, int start, int end, int dim) {
        double sum = 0;
        var j = end - dim;
        for (var i = start; i < end; i += dim) {
            sum += (data[j] - data[i]) * (data[i + 1] + data[j + 1]);
            j = i;
        }
        return sum;
    }
}
}