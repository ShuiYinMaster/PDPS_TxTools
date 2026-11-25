using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using TxTools.CatiaPartTree;
using TxTools.CatiaPartTree.Core;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;
using TxTools.ResourceImporter;

namespace TxTools.ProcessDesigner
{
    internal sealed partial class DesignerModelForm
    {
        private CojtFolderEdits _folderEdits;
        private readonly List<TreeNode> _placementNodes = new List<TreeNode>();
        private readonly Dictionary<string, ResourcePrototype> _resourceTypes =
            new Dictionary<string, ResourcePrototype>(StringComparer.OrdinalIgnoreCase);
        private ResourcePrototype[] _prototypeChoices = { ResourcePrototype.Default };
        private ImageList _previewImages;
        private ContextMenuStrip _prototypeMenu;
        private ResourcePrototype DefaultPrototype =>
            _prototypeChoices.FirstOrDefault(type => type.Type == ResourcePrototype.BaseType) ?? ResourcePrototype.Default;

        private void LoadPreviewImages()
        {
            if (_parts)
            {
                _previewImages = StructureTreeImages.Create(DeviceDpi);
                _structure.ImageList = _previewImages;
            }
            else
            {
                try { _prototypeChoices = ResourcePrototype.ReadDesigner(TxApplication.ActiveDocument); }
                catch (Exception error) { _status.Text = "读取资源原型失败。" + error.GetBaseException().Message; }
                int size = Math.Max(16, (int)Math.Round(16 * DeviceDpi / 96.0));
                _previewImages = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
                foreach (var type in _prototypeChoices) ResourcePrototypeImages.Add(_previewImages, type, Font.FontFamily);
                _files.SmallImageList = _previewImages;
                UpdateFolderDisplays();
            }
            CojtFolderTree.AddFolderImage(_previewImages);
            _folders.ImageList = _previewImages;
            Disposed += (sender, args) => { _prototypeMenu?.Dispose(); _previewImages.Dispose(); };
        }

        private void ShowPrototypeMenu(ListViewItem[] items, Point location)
        {
            if (_work != null || items.Length == 0 || _previewImages == null) return;
            _prototypeMenu?.Dispose();
            _prototypeMenu = new ContextMenuStrip { Font = FormUiKit.BaseFont, ImageScalingSize = _previewImages.ImageSize };
            foreach (var type in _prototypeChoices)
            {
                var option = new ToolStripMenuItem(type.Label, _previewImages.Images[type.Type]);
                option.Click += (sender, args) =>
                {
                    foreach (var item in items) _resourceTypes[(string)item.Tag] = type;
                    UpdateFolderDisplays();
                    RefreshFolderPreview();
                };
                _prototypeMenu.Items.Add(option);
            }
            _prototypeMenu.Show(location);
        }

        private void ConfigureFolderMenu()
        {
            _folderEdits = new CojtFolderEdits(_folders, () => _work == null, message => _status.Text = message, () =>
            {
                UpdateFolderDisplays();
                RefreshFolderPreview();
            });
        }

        private void SourceLabelEdited(object sender, NodeLabelEditEventArgs args)
        {
            args.CancelEdit = args.Label != null && string.IsNullOrWhiteSpace(args.Label);
            if (args.Label != null && !args.CancelEdit) BeginInvoke((Action)RefreshFolderPreview);
        }

        private void RefreshFolderPreview()
        {
            _folders.BeginUpdate();
            try
            {
                foreach (var node in _placementNodes) node.Remove();
                _placementNodes.Clear();
                if (_assignedFolders.Count == 0) return;
                if (_parts)
                {
                    var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (TreeNode node in _structure.Nodes) PreviewAssignedParts(node, shown);
                }
                else
                {
                    foreach (ListViewItem row in _files.Items)
                        AddPlacement((string)row.Tag, StructureEngine.Clean(row.Text), row.ImageKey, (string)row.Tag);
                }
            }
            finally { _folders.EndUpdate(); }
        }

        private void PreviewAssignedParts(TreeNode node, HashSet<string> shown)
        {
            if (!node.Checked) return;
            var source = (StructureNode)node.Tag;
            if (_source.Parts.TryGetValue(source.Key, out var part) && shown.Add(part.AssetKey))
                AddPlacement(part.AssetKey, string.IsNullOrWhiteSpace(source.PartNumber) ? StructureEngine.Clean(node.Text) : source.PartNumber,
                    "part", part.File);
            foreach (TreeNode child in node.Nodes) PreviewAssignedParts(child, shown);
        }

        private void AddPlacement(string asset, string name, string image, string source)
        {
            if (!_assignedFolders.TryGetValue(asset, out var folder)) return;
            CojtFolderTree.Populate(folder, message => _status.Text = message);
            var node = new TreeNode(CatiaLibraryDestination.FolderSegment(name) + ".cojt")
            {
                Tag = new object(), ImageKey = image, SelectedImageKey = image, ToolTipText = source
            };
            folder.Nodes.Add(node);
            _placementNodes.Add(node);
        }
    }
}
