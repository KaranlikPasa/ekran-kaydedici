using System;
using System.Drawing;
using System.Windows.Forms;

namespace EkranKaydedici.UI;

/// <summary>
/// Full-screen translucent overlay allowing the user to drag-select a recording region.
/// </summary>
public sealed class RegionSelectForm : Form
{
    private Point _startPoint;
    private Rectangle _selection;
    private bool _dragging;

    public Rectangle? SelectedRegion { get; private set; }

    public RegionSelectForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Opacity = 0.35;
        Cursor = Cursors.Cross;
        KeyPreview = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            SelectedRegion = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            SelectedRegion = null;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _startPoint = e.Location;
            _selection = new Rectangle(e.Location, Size.Empty);
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            var x = Math.Min(_startPoint.X, e.X);
            var y = Math.Min(_startPoint.Y, e.Y);
            var w = Math.Abs(e.X - _startPoint.X);
            var h = Math.Abs(e.Y - _startPoint.Y);
            _selection = new Rectangle(x, y, w, h);
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left)
        {
            _dragging = false;
            if (_selection.Width >= 16 && _selection.Height >= 16)
            {
                var screenRect = new Rectangle(
                    Bounds.X + _selection.X,
                    Bounds.Y + _selection.Y,
                    _selection.Width,
                    _selection.Height);
                SelectedRegion = screenRect;
                DialogResult = DialogResult.OK;
            }
            else
            {
                SelectedRegion = null;
                DialogResult = DialogResult.Cancel;
            }
            Close();
        }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(70, 56, 189, 248));
            using var pen = new Pen(Color.FromArgb(240, 56, 189, 248), 2);
            e.Graphics.FillRectangle(fill, _selection);
            e.Graphics.DrawRectangle(pen, _selection);
        }
    }
}
