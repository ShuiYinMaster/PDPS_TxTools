using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;

namespace TxTools.Common
{
    /// <summary>在 TUNE 和 WinForms 特殊键预处理之前，处理拾取控件的 Esc。</summary>
    public class PickAwareTxForm : TxForm
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            // TUNE 直接派发到这里；必须先于 TxForm.OnKeyDown 内部的特殊键预处理。
            if (PickFocus.TryCancel(this, e.KeyData))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            return PickFocus.TryCancel(this, keyData) || base.ProcessDialogKey(keyData);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (PickFocus.TryCancel(this, keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
