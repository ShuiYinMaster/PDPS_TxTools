// ============================================================================
// Theme.cs  —  UI 配色统一收编点（转发到 TxTools.Common.FormUiKit.Theme）
//
// 所有 UI 颜色常量现在集中在 TxTools.Common.FormUiKit.Theme（套件统一 GUI
// 规范），本文件仅做成员级转发，保持旧命名（TxClrXxx / ClrXxx）与
// "using static ...Theme;" 用法兼容，避免改动大量引用点。
// ============================================================================
using System.Drawing;
using Tecnomatix.Engineering;

namespace TxTools.RobotReachabilityChecker.Ui
{
    internal static class Theme
    {
        // ── 主色调 ─────────────────────────────────────────────
        public static readonly TxColor TxClrAccent  = TxTools.Common.FormUiKit.Theme.TxClrAccent;
        public static readonly TxColor TxClrSuccess = TxTools.Common.FormUiKit.Theme.TxClrSuccess;
        public static readonly TxColor TxClrDanger  = TxTools.Common.FormUiKit.Theme.TxClrDanger;
        public static readonly TxColor TxClrWarning = TxTools.Common.FormUiKit.Theme.TxClrWarning;

        // ── 功能区按钮色 ────────────────────────────────────────
        public static readonly TxColor TxClrBtnCheck  = TxTools.Common.FormUiKit.Theme.TxClrBtnCheck;
        public static readonly TxColor TxClrBtnAll    = TxTools.Common.FormUiKit.Theme.TxClrBtnAll;
        public static readonly TxColor TxClrBtnExport = TxTools.Common.FormUiKit.Theme.TxClrBtnExport;
        public static readonly TxColor TxClrBtnReset  = TxTools.Common.FormUiKit.Theme.TxClrBtnReset;
        public static readonly TxColor TxClrBtnClose  = TxTools.Common.FormUiKit.Theme.TxClrBtnClose;

        // ── 表格色 ─────────────────────────────────────────────
        public static readonly TxColor TxClrGridHeader     = TxTools.Common.FormUiKit.Theme.TxClrGridHeader;
        public static readonly TxColor TxClrGridHeaderText = TxTools.Common.FormUiKit.Theme.TxClrGridHeaderText;
        public static readonly TxColor TxClrGridAlt        = TxTools.Common.FormUiKit.Theme.TxClrGridAlt;
        public static readonly TxColor TxClrGridHighlight  = TxTools.Common.FormUiKit.Theme.TxClrGridHighlight;
        public static readonly TxColor TxClrRowOk          = TxTools.Common.FormUiKit.Theme.TxClrRowOk;
        public static readonly TxColor TxClrRowFail        = TxTools.Common.FormUiKit.Theme.TxClrRowFail;
        public static readonly TxColor TxClrRowWarn        = TxTools.Common.FormUiKit.Theme.TxClrRowWarn;
        public static readonly TxColor TxClrRowSingular    = TxTools.Common.FormUiKit.Theme.TxClrRowSingular;
        public static readonly TxColor TxClrRowCritical    = TxTools.Common.FormUiKit.Theme.TxClrRowCritical;

        // ── 单元格级（轴级）问题高亮色 ────────────────────────
        public static readonly TxColor TxClrCellOver     = TxTools.Common.FormUiKit.Theme.TxClrCellOver;
        public static readonly TxColor TxClrCellNear     = TxTools.Common.FormUiKit.Theme.TxClrCellNear;
        public static readonly TxColor TxClrCellSingular = TxTools.Common.FormUiKit.Theme.TxClrCellSingular;
        public static readonly TxColor TxClrCellCritical = TxTools.Common.FormUiKit.Theme.TxClrCellCritical;

        // ── 日志面板 ───────────────────────────────────────────
        public static readonly TxColor TxClrLogBg   = TxTools.Common.FormUiKit.Theme.TxClrLogBg;
        public static readonly TxColor TxClrLogText = TxTools.Common.FormUiKit.Theme.TxClrLogText;
        public static readonly TxColor TxClrLogErr  = TxTools.Common.FormUiKit.Theme.TxClrLogErr;
        public static readonly TxColor TxClrLogWarn = TxTools.Common.FormUiKit.Theme.TxClrLogWarn;
        public static readonly TxColor TxClrLogOk   = TxTools.Common.FormUiKit.Theme.TxClrLogOk;

        // ── 点位编辑头栏 ────────────────────────────────────────
        public static readonly TxColor TxClrEditHeader = TxTools.Common.FormUiKit.Theme.TxClrEditHeader;

        // ── WinForms 快捷引用（.Color 转换） ────────────────────
        public static readonly Color ClrAccent  = TxTools.Common.FormUiKit.Theme.ClrAccent;
        public static readonly Color ClrSuccess = TxTools.Common.FormUiKit.Theme.ClrSuccess;
        public static readonly Color ClrDanger  = TxTools.Common.FormUiKit.Theme.ClrDanger;
        public static readonly Color ClrWarning = TxTools.Common.FormUiKit.Theme.ClrWarning;
        public static readonly Color ClrMuted   = TxTools.Common.FormUiKit.Theme.ClrMuted;
        public static readonly Color ClrText    = TxTools.Common.FormUiKit.Theme.ClrText;
        public static readonly Color ClrBg      = TxTools.Common.FormUiKit.Theme.ClrBg;
    }
}
