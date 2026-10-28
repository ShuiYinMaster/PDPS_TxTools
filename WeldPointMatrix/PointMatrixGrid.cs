using System;
using System.Windows.Forms;

namespace TxTools.WeldPointMatrix
{
    internal class PointMatrixGrid : DataGridView
    {
        internal event EventHandler KeyboardCellSelected;
        private int _keyDepth;

        protected override bool ProcessDataGridViewKey(KeyEventArgs e)
            => ProcessPointKey(e.KeyData, () => base.ProcessDataGridViewKey(e));

        protected override bool ProcessDialogKey(Keys keyData)
            => ProcessPointKey(keyData, () => base.ProcessDialogKey(keyData));

        private bool ProcessPointKey(Keys keyData, Func<bool> processDefault)
        {
            Keys key = keyData & Keys.KeyCode;
            bool activate = (key == Keys.Enter || key == Keys.Space)
                && (keyData & (Keys.Control | Keys.Alt)) == Keys.None;
            bool navigate = key == Keys.Up || key == Keys.Down || key == Keys.Left
                || key == Keys.Right || key == Keys.Tab || key == Keys.Home
                || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown;
            DataGridViewCell before = CurrentCell;
            bool beforeSelected = before != null && before.Selected;
            bool handled = false;
            _keyDepth++;
            try
            {
                // Enter/Space activates the current point without moving to the next row.
                if (activate && CurrentCell != null) handled = true;
                else handled = processDefault();
            }
            finally { _keyDepth--; }

            // Run after DataGridView finishes navigation: refreshing during its internal
            // selection event would rebuild cells while the keyboard handler is using them.
            if (_keyDepth == 0 && handled && ContainsFocus && CurrentCell != null
                && CurrentCell.Selected && (keyData & Keys.Alt) == Keys.None
                && (activate || (navigate && (!ReferenceEquals(before, CurrentCell) || !beforeSelected))))
                KeyboardCellSelected?.Invoke(this, EventArgs.Empty);
            return handled;
        }
    }
}
