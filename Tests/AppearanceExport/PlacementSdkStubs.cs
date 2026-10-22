// Used only by PlacementRegression.exe. Production is separately compiled
// against the real 2402 SDK. Its native Implementation.dll is unavailable offline.
using System;
using System.Collections.Generic;

namespace Tecnomatix.Planning
{
    public interface ITxPlanningObject { Tecnomatix.Engineering.ITxObject EngineeringRepresentation { get; } }
}

namespace Tecnomatix.Engineering
{
    public interface ITxObject { string Id { get; } string Name { get; } }
    public interface ITxLocatableObject : ITxObject { TxTransformation AbsoluteLocation { get; } }
    public interface ITxGeometry : ITxObject { }
    public interface ITxStorable : ITxObject { TxStorage StorageObject { get; } }
    public interface ITxComponent : ITxObject { TxRepresentationType RepresentationType { get; } }
    public enum TxRepresentationType { NotDefined = -20, United = 4, Detailed = 5, Modeling = 6 }
    public enum TxRepresentationLevel { NotDefined = -20, United = 4, Detailed = 5 }
    public abstract class TxStorage { }
    public sealed class TxLibraryStorage : TxStorage
    {
        internal Action<TxRepresentationLevel> ReloadAction { get; set; }
        public void Reload(TxRepresentationLevel level) { ReloadAction(level); }
    }
    public interface ITxPartAppearance : ITxObject { Tecnomatix.Planning.ITxPlanningObject OriginPart { get; } }
    public interface ITxSkidAppearance : ITxObject { Tecnomatix.Planning.ITxPlanningObject OriginSkid { get; } }
    public interface ITxObjectCollection : ITxObject
    {
        TxObjectList GetDirectDescendants(TxNoTypeFilter filter);
        TxObjectList GetAllDescendants(TxNoTypeFilter filter);
    }
    public interface ITxDisplayableObject : ITxObject
    {
        TxDisplayableObjectVisibility Visibility { get; }
        void Display();
        void Blank();
    }
    public enum TxDisplayableObjectVisibility { All, Partial, None, CannotBeDisplayed }
    public sealed class TxNoTypeFilter { }
    public sealed class TxObjectList : List<ITxObject> { }
    public abstract class TxKinematicLink : ITxGeometry
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
    }

    // Independent homogeneous-matrix implementation used as the SDK test double.
    // Gauss-Jordan inversion covers rotations, offsets, scale and reflection.
    public sealed class TxTransformation
    {
        private readonly double[,] matrix = new double[4, 4];
        public TxTransformation(params double[] rows)
        {
            if (rows.Length != 12) throw new ArgumentException("Expected 3x4 rows");
            for (int i = 0; i < 3; i++) for (int j = 0; j < 4; j++) matrix[i, j] = rows[i * 4 + j];
            matrix[3, 3] = 1;
        }
        public static TxTransformation Multiply(TxTransformation a, TxTransformation b)
        {
            var values = new double[12];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++) values[i * 4 + j] += a.matrix[i, k] * b.matrix[k, j];
            return new TxTransformation(values);
        }
        public TxTransformation Inverse
        {
            get
            {
                var augmented = new double[4, 8];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                { augmented[i, j] = matrix[i, j]; augmented[i, j + 4] = i == j ? 1 : 0; }
                for (int column = 0; column < 4; column++)
                {
                    int pivot = column;
                    for (int row = column + 1; row < 4; row++)
                        if (Math.Abs(augmented[row, column]) > Math.Abs(augmented[pivot, column])) pivot = row;
                    if (Math.Abs(augmented[pivot, column]) < 1e-12) throw new InvalidOperationException("Singular transform");
                    for (int j = 0; j < 8; j++)
                    { double value = augmented[column, j]; augmented[column, j] = augmented[pivot, j]; augmented[pivot, j] = value; }
                    double divisor = augmented[column, column];
                    for (int j = 0; j < 8; j++) augmented[column, j] /= divisor;
                    for (int row = 0; row < 4; row++)
                    {
                        if (row == column) continue;
                        double factor = augmented[row, column];
                        for (int j = 0; j < 8; j++) augmented[row, j] -= factor * augmented[column, j];
                    }
                }
                var values = new double[12];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 4; j++) values[i * 4 + j] = augmented[i, j + 4];
                return new TxTransformation(values);
            }
        }
        internal double[] Apply(double x, double y, double z)
        {
            var input = new[] { x, y, z, 1.0 };
            var result = new double[3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 4; j++) result[i] += matrix[i, j] * input[j];
            return result;
        }
    }
}
