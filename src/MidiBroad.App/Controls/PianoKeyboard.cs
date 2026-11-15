using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MidiBroad.Models;

namespace MidiBroad.Controls;

public sealed class PianoNoteEventArgs : EventArgs
{
    public PianoNoteEventArgs(int note, int velocity)
    {
        Note = note;
        Velocity = velocity;
    }

    public int Note { get; }
    public int Velocity { get; }
}

public readonly record struct PianoKeyLayout(int Note, Rect Rect, bool IsBlack);

public sealed class PianoKeyboard : FrameworkElement
{
    private readonly List<PianoKeyLayout> _keys = new();
    private readonly HashSet<int> _activeNotes = new();
    private readonly Dictionary<int, string> _labels = new();
    private readonly Dictionary<int, int> _velocities = new();
    private int _lowNote = 48;
    private int _octaveCount = 2;
    private bool _showLabels = true;
    private int? _mouseNote;
    private int? _hoverNote;

    public event EventHandler<PianoNoteEventArgs>? NotePressed;
    public event EventHandler<int>? NoteReleased;

    public PianoKeyboard()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
    }

    public int LowNote
    {
        get => _lowNote;
        set
        {
            if (_lowNote == value)
            {
                return;
            }

            _lowNote = value;
            InvalidateVisual();
        }
    }

    public int OctaveCount
    {
        get => _octaveCount;
        set
        {
            if (_octaveCount == value)
            {
                return;
            }

            _octaveCount = Math.Clamp(value, 1, 4);
            InvalidateVisual();
        }
    }

    public bool ShowLabels
    {
        get => _showLabels;
        set
        {
            if (_showLabels == value)
            {
                return;
            }

            _showLabels = value;
            InvalidateVisual();
        }
    }

    public void SetMapping(int lowNote, int octaveCount, IReadOnlyDictionary<int, string> labels)
    {
        _lowNote = lowNote;
        _octaveCount = Math.Clamp(octaveCount, 1, 4);
        _labels.Clear();
        foreach (var pair in labels)
        {
            _labels[pair.Key] = pair.Value;
        }

        InvalidateVisual();
    }

    public IReadOnlyList<PianoKeyLayout> GetKeyLayouts()
    {
        var layouts = new List<PianoKeyLayout>();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20)
        {
            return layouts;
        }

        var whiteNotes = GetWhiteNotes();
        if (whiteNotes.Count == 0)
        {
            return layouts;
        }

        var whiteWidth = width / whiteNotes.Count;
        var whiteRects = new List<Rect>(whiteNotes.Count);
        for (var i = 0; i < whiteNotes.Count; i++)
        {
            var rect = new Rect(i * whiteWidth, 0, whiteWidth + 0.5, height);
            whiteRects.Add(rect);
        }

        for (var i = 0; i < whiteNotes.Count - 1; i++)
        {
            var note = whiteNotes[i] + 1;
            if (note > 127 || !NoteNames.IsBlack(note))
            {
                continue;
            }

            var blackWidth = whiteWidth * 0.61;
            var x = (i + 1) * whiteWidth - blackWidth / 2;
            layouts.Add(new PianoKeyLayout(note, new Rect(x, 0, blackWidth, height * 0.62), true));
        }

        for (var i = 0; i < whiteNotes.Count; i++)
        {
            layouts.Add(new PianoKeyLayout(whiteNotes[i], whiteRects[i], false));
        }

        return layouts;
    }

    private List<int> GetWhiteNotes()
    {
        var whiteNotes = new List<int>();
        var maxNote = Math.Min(127, _lowNote + _octaveCount * 12);
        for (var note = _lowNote; note <= maxNote; note++)
        {
            if (!NoteNames.IsBlack(note))
            {
                whiteNotes.Add(note);
            }
        }

        return whiteNotes;
    }

    public void SetActiveNotes(IReadOnlyCollection<int> notes, IReadOnlyDictionary<int, int>? velocities = null)
    {
        _activeNotes.Clear();
        foreach (var note in notes)
        {
            _activeNotes.Add(note);
        }

        _velocities.Clear();
        if (velocities is not null)
        {
            foreach (var pair in velocities)
            {
                _velocities[pair.Key] = pair.Value;
            }
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _keys.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20)
        {
            return;
        }

        var whiteBrush = (Brush)FindResource("PianoWhiteBrush");
        var whiteActiveBrush = (Brush)FindResource("PianoWhiteActiveBrush");
        var blackBrush = (Brush)FindResource("PianoBlackBrush");
        var blackActiveBrush = (Brush)FindResource("PianoBlackActiveBrush");
        var lineBrush = (Brush)FindResource("PianoLineBrush");
        var labelBrush = (Brush)FindResource("PianoLabelBrush");
        var activeTextBrush = (Brush)FindResource("PianoActiveTextBrush");
        var accentBrush = (Brush)FindResource("AccentBrush");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var whiteNotes = new List<int>();
        var maxNote = Math.Min(127, _lowNote + _octaveCount * 12);
        for (var note = _lowNote; note <= maxNote; note++)
        {
            if (!NoteNames.IsBlack(note))
            {
                whiteNotes.Add(note);
            }
        }

        if (whiteNotes.Count == 0)
        {
            return;
        }

        var whiteWidth = width / whiteNotes.Count;
        var linePen = new Pen(lineBrush, 1);
        linePen.Freeze();

        var whiteRects = new List<Rect>(whiteNotes.Count);
        for (var i = 0; i < whiteNotes.Count; i++)
        {
            var note = whiteNotes[i];
            var rect = new Rect(i * whiteWidth, 0, whiteWidth + 0.5, height);
            whiteRects.Add(rect);

            var active = _activeNotes.Contains(note);
            var fill = active ? whiteActiveBrush : whiteBrush;
            dc.DrawRoundedRectangle(fill, linePen, rect, 5, 5);

            // A soft top edge helps the keys read as physical keys.
            var topShade = new LinearGradientBrush(
                Color.FromArgb(38, 0, 0, 0),
                Color.FromArgb(0, 0, 0, 0),
                90);
            dc.DrawRectangle(topShade, null, new Rect(rect.X + 1, 0, rect.Width - 2, Math.Min(18, height * 0.16)));

            if (active)
            {
                var bar = new Rect(rect.X + 4, height - 7, Math.Max(0, rect.Width - 8), 3);
                dc.DrawRoundedRectangle(accentBrush, null, bar, 2, 2);
            }

            if (_showLabels)
            {
                if (_labels.TryGetValue(note, out var label))
                {
                    DrawCenteredText(dc, label, new Point(rect.X + rect.Width / 2, height - 26), 11,
                        active ? activeTextBrush : labelBrush, dpi, FontWeights.SemiBold);
                }

                if (note % 12 == 0)
                {
                    DrawCenteredText(dc, NoteNames.Get(note), new Point(rect.X + rect.Width / 2, height - 10), 9,
                        active ? activeTextBrush : labelBrush, dpi, FontWeights.Normal);
                }
            }
        }

        // Black keys are drawn after the white keys and hit-tested first.
        for (var i = 0; i < whiteNotes.Count - 1; i++)
        {
            var note = whiteNotes[i] + 1;
            if (note > 127 || !NoteNames.IsBlack(note))
            {
                continue;
            }

            var blackWidth = whiteWidth * 0.61;
            var blackHeight = height * 0.62;
            var x = (i + 1) * whiteWidth - blackWidth / 2;
            var rect = new Rect(x, 0, blackWidth, blackHeight);
            var active = _activeNotes.Contains(note);
            var fill = active ? blackActiveBrush : blackBrush;
            dc.DrawRoundedRectangle(fill, null, rect, 4, 4);

            if (active)
            {
                var glow = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                dc.DrawRoundedRectangle(glow, null, new Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, 3), 2, 2);
            }

            if (_showLabels && _labels.TryGetValue(note, out var label))
            {
                DrawCenteredText(dc, label, new Point(rect.X + rect.Width / 2, rect.Bottom - 13), 9,
                    active ? Brushes.White : new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), dpi, FontWeights.SemiBold);
            }

            _keys.Add(new PianoKeyLayout(note, rect, true));
        }

        for (var i = 0; i < whiteNotes.Count; i++)
        {
            _keys.Add(new PianoKeyLayout(whiteNotes[i], whiteRects[i], false));
        }

        if (_hoverNote is not null && !_activeNotes.Contains(_hoverNote.Value))
        {
            var hover = _keys.FirstOrDefault(k => k.Note == _hoverNote.Value);
            if (hover.Rect != Rect.Empty)
            {
                var overlay = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                dc.DrawRoundedRectangle(overlay, null, hover.Rect, 5, 5);
            }
        }
    }

    private static void DrawCenteredText(
        DrawingContext dc,
        string text,
        Point center,
        double size,
        Brush brush,
        double dpi,
        FontWeight weight)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            dpi);

        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        var point = e.GetPosition(this);
        var key = HitTestKey(point);
        if (key is null)
        {
            return;
        }

        _mouseNote = key.Value.Note;
        NotePressed?.Invoke(this, new PianoNoteEventArgs(
            key.Value.Note,
            VelocityFromPoint(point, key.Value.Rect)));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var key = HitTestKey(point);
        var note = key?.Note;

        if (!IsMouseCaptured)
        {
            if (_hoverNote != note)
            {
                _hoverNote = note;
                InvalidateVisual();
            }

            return;
        }

        if (note == _mouseNote)
        {
            return;
        }

        if (_mouseNote is not null)
        {
            NoteReleased?.Invoke(this, _mouseNote.Value);
        }

        _mouseNote = note;
        if (key is not null)
        {
            NotePressed?.Invoke(this, new PianoNoteEventArgs(
                key.Value.Note,
                VelocityFromPoint(point, key.Value.Rect)));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (_mouseNote is not null)
        {
            NoteReleased?.Invoke(this, _mouseNote.Value);
            _mouseNote = null;
        }

        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured && _hoverNote is not null)
        {
            _hoverNote = null;
            InvalidateVisual();
        }
    }

    private PianoKeyLayout? HitTestKey(Point point)
    {
        foreach (var key in _keys)
        {
            if (key.IsBlack && key.Rect.Contains(point))
            {
                return key;
            }
        }

        foreach (var key in _keys)
        {
            if (!key.IsBlack && key.Rect.Contains(point))
            {
                return key;
            }
        }

        return null;
    }

    private static int VelocityFromPoint(Point point, Rect rect)
    {
        if (rect.Height <= 0)
        {
            return 100;
        }

        var relative = Math.Clamp((point.Y - rect.Top) / rect.Height, 0, 1);

        // Higher on the key is softer, lower is louder: the physical impression
        // of pressing nearer the front edge produces more velocity.
        return 1 + (int)Math.Round(relative * 126);
    }


}
