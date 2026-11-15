using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TxTools.Common.TreeWorkflow;

namespace TxTools.CatiaPartTree
{
    internal sealed class StructureSourceTree : TreeView
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Item
        {
            internal uint Mask;
            internal IntPtr Handle;
            internal uint State, StateMask;
            internal IntPtr Text;
            internal int TextLength, Image, SelectedImage, Children;
            internal IntPtr Data;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref Item item);

        internal void RefreshChecks()
        {
            if (!IsHandleCreated) return;
            foreach (var node in WorkflowUi.Nodes(Nodes))
            {
                var source = node.Tag as StructureNode;
                if (source == null || source.IsContainer) continue;
                var item = new Item { Mask = 8, Handle = node.Handle, StateMask = 0xf000, State = 0 };
                SendMessage(Handle, 0x113f, IntPtr.Zero, ref item);
            }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RefreshChecks(); }
        protected override void OnBeforeCheck(TreeViewCancelEventArgs e)
        {
            var source = e.Node.Tag as StructureNode;
            if (source != null && !source.IsContainer)
            { e.Cancel = true; if (IsHandleCreated) BeginInvoke(new Action(RefreshChecks)); return; }
            base.OnBeforeCheck(e);
        }
    }
}
