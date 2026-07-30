// ============================================================================
// ReachabilityCheckerForm.Helpers.cs
//
// 小型 UI 控件工厂：MkLabel / MkButton / MkFuncButton + 自适应宽度辅助。
// 这些方法不持有状态，纯粹为 Layout 文件减负。
// ============================================================================
using System.Drawing;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using TxTools.Common;
using static TxTools.RobotReachabilityChecker.Ui.Theme;

namespace TxTools.RobotReachabilityChecker.Ui
{
    public partial class ReachabilityCheckerForm
    {
        // =====================================================================
        // 控件工厂
        // =====================================================================
        private Label MkLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = FormUiKit.BaseFont,
            ForeColor = ClrMuted,
            Margin = new Padding(0, 7, 4, 0)
        };

        private Button MkButton(string text, int width)
        {
            return new FormUiKit.FlatColorButton
            {
                Text = text,
                Width = width,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BgColor = SystemColors.ControlLight,
                ForeColor = SystemColors.ControlText,
                BorderColor = FormUiKit.Theme.BtnBorder,
                HoverColor = FormUiKit.Theme.BtnHover,
                Font = FormUiKit.BaseFont,
                Margin = new Padding(0, 2, 4, 2)
            };
        }

        /// <summary>功能区按钮：自适应文本宽度、单行、带背景色。fixedWidth &gt; 0 时使用固定宽度（按钮等宽）。</summary>
        private Button MkFuncButton(string text, Color bgColor, int fixedWidth = 0)
        {
            var btn = new FormUiKit.FlatColorButton
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = FormUiKit.BaseFont,
                BgColor = bgColor,
                // 文字色按背景亮度自动取黑/白，避免深色按钮 + 黑字不可读（如 Flat 主题下 TxClrBtn* 灰底 + BtnFore 黑字）
                ForeColor = bgColor.GetBrightness() > 0.62f ? FormUiKit.Theme.BtnFore : System.Drawing.Color.White,
                BorderColor = FormUiKit.Theme.BtnBorder,
                HoverColor = FormUiKit.Theme.BtnHover,
                Margin = new Padding(0, 2, 4, 2),
                Padding = new Padding(8, 2, 8, 2),
                Cursor = Cursors.Hand
            };
            if (fixedWidth > 0)
            {
                btn.AutoSize = false;
                btn.Width = fixedWidth;
            }
            return btn;
        }

        // =====================================================================
        // 自适应宽度
        // =====================================================================

        /// <summary>根据 ComboBox 最长项文本自动设定宽度</summary>
        private void AutoFitComboBoxWidth(ComboBox cb)
        {
            if (cb == null || cb.Items.Count == 0) return;
            using (var g = CreateGraphics())
            {
                float maxW = 0;
                foreach (var item in cb.Items)
                {
                    float w = g.MeasureString(item.ToString(), cb.Font).Width;
                    if (w > maxW) maxW = w;
                }
                // 加上下拉箭头宽度(20) + 边距(8)
                cb.Width = (int)maxW + 28;
            }
        }

        /// <summary>根据 NumericUpDown 的 Maximum 位数自动设定宽度</summary>
        private void AutoFitNumericWidth(NumericUpDown nud)
        {
            if (nud == null) return;
            string maxText = nud.Maximum.ToString("F" + nud.DecimalPlaces);
            using (var g = CreateGraphics())
            {
                float w = g.MeasureString(maxText, nud.Font).Width;
                // 加上上下箭头宽度(18) + 边距(8)
                nud.Width = (int)w + 26;
            }
        }
    }
}
