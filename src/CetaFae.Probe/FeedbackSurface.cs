using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>A bounded transparent margin joins the bubble to its validated control.</summary>
internal sealed class FeedbackSurface : Grid
{
    readonly GuideCard _card;
    readonly Pointer _pointer = new() { IsHitTestVisible = false };

    internal FeedbackSurface(GuideCard card)
    {
        _card = card;
        Children.Add(card); Children.Add(_pointer);
    }

    internal void SetBubble(bool enabled, Point aim = default)
    {
        _card.Margin = enabled ? new Thickness(10) : new Thickness(0);
        _pointer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _pointer.Aim = aim;
        _pointer.InvalidateVisual();
    }

    sealed class Pointer : FrameworkElement
    {
        internal Point Aim;
        protected override void OnRender(DrawingContext draw)
        {
            var body = new Rect(10, 10, Math.Max(0, ActualWidth - 20), Math.Max(0, ActualHeight - 20));
            Point a, tip, b;
            if (Aim.Y >= ActualHeight || Aim.Y <= 0)
            {
                var x = Math.Clamp(Aim.X, 32, Math.Max(32, ActualWidth - 32));
                var y = Aim.Y >= ActualHeight ? body.Bottom - 1 : body.Top + 1;
                a = new Point(x - 7, y); b = new Point(x + 7, y);
                tip = new Point(x, Aim.Y >= ActualHeight ? ActualHeight - 1 : 1);
            }
            else
            {
                var y = Math.Clamp(Aim.Y, 32, Math.Max(32, ActualHeight - 32));
                var x = Aim.X >= ActualWidth ? body.Right - 1 : body.Left + 1;
                a = new Point(x, y - 7); b = new Point(x, y + 7);
                tip = new Point(Aim.X >= ActualWidth ? ActualWidth - 1 : 1, y);
            }
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                path.BeginFigure(a, true, false);
                path.LineTo(tip, true, false); path.LineTo(b, true, false);
            }
            draw.DrawGeometry(GuideTheme.Surface, new Pen(GuideTheme.WhaleBlue, 2), shape);
        }
    }
}
