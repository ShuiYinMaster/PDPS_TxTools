using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TxTools.CrossEnvIO;

namespace TxTools.ResourceImporter
{
    internal static class CojtFolderTree
    {
        internal const string FolderImage = "windows-folder";
        internal const string StagePrefix = ".tx-ri-";

        internal static TreeNode CreateNode(string path, string title)
        {
            var node = new TreeNode(title) { Tag = path, ToolTipText = path, ImageKey = FolderImage, SelectedImageKey = FolderImage };
            node.Nodes.Add(new TreeNode());
            return node;
        }

        internal static void AddFolderImage(ImageList images)
        {
            const uint icon = 0x100, smallIcon = 1;
            const int folder = 3;
            var info = new StockIconInfo { Size = (uint)Marshal.SizeOf(typeof(StockIconInfo)) };
            try
            {
                if (SHGetStockIconInfo(folder, icon | smallIcon, ref info) < 0 || info.Icon == IntPtr.Zero) return;
                using (var borrowed = Icon.FromHandle(info.Icon))
                    images.Images.Add(FolderImage, (Icon)borrowed.Clone());
            }
            finally
            {
                if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StockIconInfo
        {
            internal uint Size;
            internal IntPtr Icon;
            internal int SystemImageIndex, IconIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string Path;
        }

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetStockIconInfo(int type, uint flags, ref StockIconInfo info);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        internal static void Populate(TreeNode node, Action<string> report)
        {
            if (!(node.Tag is string path) || node.Nodes.Count == 0 || node.Nodes[0].Tag != null) return;
            try
            {
                var directories = new DirectoryInfo(path).GetDirectories()
                    .Where(dir => (dir.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        !dir.Name.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase) &&
                        !dir.Name.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase) &&
                        !dir.Name.StartsWith(".tx-resource-import-", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(dir => dir.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                node.Nodes[0].Remove();
                foreach (var dir in directories) node.Nodes.Add(CreateNode(dir.FullName, dir.Name));
            }
            catch (Exception error) { report("无法读取目录：" + error.Message); }
        }

        internal static TreeNode Find(TreeNode root, string path, Action<string> report)
        {
            string library = (string)root.Tag;
            if (string.Equals(path.TrimEnd('\\', '/'), library.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return root;
            string relative = CojtTransfer.RelativeToRoot(path, library);
            if (relative == null) return null;
            var node = root;
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
            {
                Populate(node, report);
                node = node.Nodes.Cast<TreeNode>().FirstOrDefault(child => child.Tag is string &&
                    string.Equals(child.Text, segment, StringComparison.OrdinalIgnoreCase));
                if (node == null) return null;
            }
            return node;
        }

        internal static TreeNode At(TreeView tree, DragEventArgs args)
        {
            var node = tree.GetNodeAt(tree.PointToClient(new Point(args.X, args.Y)));
            return node?.Tag is string ? node : node?.Parent;
        }
    }
}
