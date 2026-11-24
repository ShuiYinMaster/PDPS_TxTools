using System;
using System.Collections.Generic;
using System.Linq;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.DataTypes;
using TxTools.Common;

namespace TxTools.WeldPointImporter
{
    internal sealed class PointWriteResult
    {
        internal string OperationName, Error;
        internal int Created;
        internal bool SceneMayHaveChanged;
    }

    internal static class PsPointWriter
    {
        internal static PointWriteResult Create(TxDocument document, long generation, string operationName, IReadOnlyList<ImportPoint> points)
        {
            if (!Equals(document, TxApplication.ActiveDocument) || generation != SceneUndoScope.DocumentGeneration)
                throw new InvalidOperationException("目标工程已切换或重载。请在目标工程重新打开焊点提取命令。");
            if (points.Count == 0 || string.IsNullOrWhiteSpace(operationName)) throw new ArgumentException("请填写操作名称并保留至少一个点。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in points)
            {
                if (string.IsNullOrWhiteSpace(point.Name)) throw new InvalidOperationException("点列表含空名称。");
                if (!names.Add(point.Name)) throw new InvalidOperationException("待创建点存在同名 “" + point.Name + "”。请编辑名称或使用自定义编号。");
            }
            var root = document.OperationRoot;
            foreach (ITxObject operation in root.GetDirectDescendants(new TxTypeFilter(typeof(ITxOperation))))
                if (string.Equals(operation.Name, operationName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("操作树根节点已存在 “" + operationName + "”。请更改操作名称。");
            foreach (ITxObject point in document.MfgRoot.GetAllDescendants(new TxTypeFilter(typeof(TxWeldPoint))))
                if (names.Contains(point.Name)) throw new InvalidOperationException("目标工程已有同名制造焊点 “" + point.Name + "”。请修改待创建点名。");

            var result = new PointWriteResult { OperationName = operationName };
            var operationData = new TxCompoundOperationCreationData(operationName);
            if (!root.CanCreateCompoundOperation(operationData)) throw new InvalidOperationException("当前工程无法创建复合操作，请确认研究处于可编辑状态。");
            string step = "创建复合操作";
            try
            {
                using (var transaction = SceneUndoScope.Begin(document, "焊点提取"))
                {
                    transaction.EnsureDocument();
                    result.SceneMayHaveChanged = true;
                    var operation = root.CreateCompoundOperation(operationData);
                    if (operation == null) throw new InvalidOperationException("SDK 未返回复合操作。");
                    result.OperationName = operation.Name;
                    foreach (var point in points)
                    {
                        step = "创建焊点 “" + point.Name + "”";
                        transaction.EnsureDocument();
                        var position = new TxVector(point.X, point.Y, point.Z);
                        using (var data = new TxWeldLocationOperationCreationData(point.Name))
                        {
                            data.WeldPointCreationData = TxMfgCreationDataFactory.CreateWeldPointCreationData(point.Name, position);
                            data.ProjectedLocation = new TxTransformation(position, TxTransformation.TxTransformationType.Translate);
                            if (!operation.CanCreateWeldLocationOperation(data)) throw new InvalidOperationException("PS 拒绝创建该点。");
                            var weld = operation.CreateWeldLocationOperation(data);
                            if (weld?.WeldPoint == null) throw new InvalidOperationException("SDK 未返回完整焊点。");
                            result.Created++;
                        }
                    }
                    step = "结束撤销事务";
                }
            }
            catch (Exception ex) { result.Error = step + "失败。" + ex.Message; }
            return result;
        }
    }
}
