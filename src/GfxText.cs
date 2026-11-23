using System.Drawing;
using System.Windows.Forms;

namespace Fontes
{
    /// <summary>Crisp Segoe UI text rendering helpers with subtle dark drop-shadows.</summary>
    public static class GfxText
    {
        static readonly Font DefaultUiFont = new Font("Segoe UI", 8.75f, FontStyle.Regular);

        public static void Center(Graphics g, string text, Rectangle r, Brush brush, Font font = null, Color? textColor = null)
        {
            var f = font ?? DefaultUiFont;
            var fg = textColor ?? Color.FromArgb(242, 245, 252);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            var shadowRect = new Rectangle(r.X + 1, r.Y + 1, r.Width, r.Height);
            TextRenderer.DrawText(g, text, f, shadowRect, Color.FromArgb(160, 0, 0, 0), flags);
            TextRenderer.DrawText(g, text, f, r, fg, flags);
        }
    }
}
