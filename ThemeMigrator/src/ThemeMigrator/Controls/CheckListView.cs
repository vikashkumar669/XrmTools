using System.Windows.Forms;

namespace ThemeMigrator.Controls
{
    /// <summary>
    /// ListView with checkboxes that doesn't toggle an item's tick when its row is double-clicked
    /// (standard WinForms behavior, which makes accidental selections easy). Ticks change only by
    /// clicking the checkbox itself or pressing Space.
    /// </summary>
    public class CheckListView : ListView
    {
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;

        public CheckListView()
        {
            DoubleBuffered = true;
        }

        protected override void WndProc(ref Message m)
        {
            // Treat a double-click as a plain click
            if (m.Msg == WM_LBUTTONDBLCLK) m.Msg = WM_LBUTTONDOWN;
            base.WndProc(ref m);
        }
    }
}
