using System;
using System.Windows.Forms;

namespace TxTools.Common
{
    internal static class FileDropInput
    {
        internal static void Wire(Func<bool> canReceive, Func<string[], bool> receive,
            Action<string> reportError, params Control[] targets)
        {
            DragEventHandler hover = (sender, args) =>
            {
                // A delayed drag source may only provide local paths after release.
                if (HasFileDrop(args))
                    args.Effect = canReceive() ? args.AllowedEffect & DragDropEffects.Copy : DragDropEffects.None;
            };
            DragEventHandler drop = (sender, args) =>
            {
                if (!HasFileDrop(args)) return;
                args.Effect = DragDropEffects.None;
                if (!canReceive() || (args.AllowedEffect & DragDropEffects.Copy) == 0) return;
                try
                {
                    var paths = args.Data.GetData(DataFormats.FileDrop, false) as string[];
                    if (!canReceive()) return;
                    if (paths == null || paths.Length == 0)
                        reportError("未收到文件路径，请确认文件已传输完成后重试。");
                    else if (receive(paths))
                        args.Effect = DragDropEffects.Copy;
                }
                catch (Exception error)
                {
                    reportError("接收拖放文件失败。" + error.GetBaseException().Message);
                }
            };
            foreach (Control target in targets)
            {
                target.AllowDrop = true;
                target.DragEnter += hover;
                target.DragOver += hover;
                target.DragDrop += drop;
            }
        }

        private static bool HasFileDrop(DragEventArgs args) =>
            args.Data != null && args.Data.GetDataPresent(DataFormats.FileDrop, false);
    }
}
