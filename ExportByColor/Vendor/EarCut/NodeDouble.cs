using System;
using System.Collections.Generic;
using System.Text;

namespace TxTools.ExportByColor.Vendor.EarCut {

internal class Node
{
    internal readonly int I;
    internal readonly double X;
    internal readonly double Y;
    internal double Z;
    internal bool Steiner;

    internal Node Prev;
    internal Node Next;
    internal Node PrevZ;
    internal Node NextZ;

    internal Node(int i, double x, double y)
    {
        // vertex index in coordinates array
        I = i;

        // vertex coordinates
        X = x;
        Y = y;

        // previous and next vertex nodes in a polygon ring
        Prev = null;
        Next = null;

        // z-order curve value
        Z = double.MinValue;

        // previous and next nodes in z-order
        PrevZ = null;
        NextZ = null;

        // indicates whether this is a steiner point
        Steiner = false;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("{i: ")
            .Append(I)
            .Append(", x: ")
            .Append(X)
            .Append(", y: ")
            .Append(Y)
            .Append(", prev: ")
            .Append(ToString(Prev))
            .Append(", next: ")
            .Append(ToString(Next))
            .Append("}");

        return sb.ToString();
    }

    private static string ToString(Node node){
        if(node == null){
            return "null";
        }
        return "{i: " + node.I + ", x: " + node.X + ", y: " + node.Y + "}";
    }
}
}