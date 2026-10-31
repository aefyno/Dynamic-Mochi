using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>Something that briefly takes over the collapsed capsule: reminder, timer, notification, screenshot.</summary>
public sealed class Notice
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Sub { get; init; }
    public Func<string>? Right { get; init; }        // refreshed every second ("через 5 мин")
    public Color? Dot { get; init; }
    public ImageSource? Picture { get; init; }
    public bool RoundPicture { get; init; } = true;
    public double PictureWidth { get; init; } = 32;
    public double PictureHeight { get; init; } = 32;
    public string? Glyph { get; init; }
    public string? BadgeText { get; init; }
    public Color BadgeColor { get; init; } = Colors.SteelBlue;
    public double Width { get; init; } = 380;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);
    public Action? Click { get; init; }
    public bool HoverOpens { get; init; }            // calendar reminders: hovering opens the event
    public bool Blink { get; init; }                 // timer done: the capsule flashes in the accent colour
    public DateTime Created { get; } = DateTime.Now;
    public DateTime Until { get; set; }
}

public partial class IslandWindow
{
    private Notice? _notice;
    private readonly List<Notice> _noticeQueue = new();

    /// <summary>Show now, or after the island collapses if it is open.</summary>
    private void ShowNotice(Notice n)
    {
        if (_expanded || _hidden)
        {
            _noticeQueue.RemoveAll(q => q.Kind == n.Kind && q.Title == n.Title);
            _noticeQueue.Add(n);
            return;
        }
        _notice = n;
        n.Until = DateTime.Now + n.Duration;
        Log.Write($"notice: {n.Kind} \"{n.Title}\"");

        ActivityDot.Visibility = n.Dot != null && n.Picture == null && n.Glyph == null ? Visibility.Visible : Visibility.Collapsed;
        if (n.Dot is Color dot) ActivityDot.Fill = new SolidColorBrush(dot);
        ActivityPicBox.Visibility = n.Picture != null ? Visibility.Visible : Visibility.Collapsed;
        if (n.Picture != null)
        {
            ActivityPic.Source = n.Picture;
            double w = n.PictureWidth, h = n.PictureHeight;
            double r = n.RoundPicture ? 16 : 7;
            ActivityPicBox.Width = w;
            ActivityPicBox.Height = h;
            ActivityPicBox.CornerRadius = new CornerRadius(r);
            ActivityPicClip.Rect = new Rect(0, 0, w, h);
            ActivityPicClip.RadiusX = ActivityPicClip.RadiusY = r;
        }
        ActivityGlyph.Visibility = n.Glyph != null && n.Picture == null ? Visibility.Visible : Visibility.Collapsed;
        ActivityGlyph.Text = n.Glyph ?? "";
        ActivityBadge.Visibility = n.BadgeText != null ? Visibility.Visible : Visibility.Collapsed;
        ActivityBadge.Background = new SolidColorBrush(n.BadgeColor);
        ActivityBadgeText.Text = n.BadgeText ?? "";
        ActivityTitle.SetForegroundResource("FgBrush");
        ActivityTitle.Text = n.Title;
        ActivitySub.Text = n.Sub ?? "";
        ActivitySub.Visibility = string.IsNullOrEmpty(n.Sub) ? Visibility.Collapsed : Visibility.Visible;
        ActivityWhen.Text = n.Right?.Invoke() ?? "";
        ActivityRow.Cursor = n.Click != null && !n.HoverOpens ? System.Windows.Input.Cursors.Hand : null;

        UpdateCollapsedRow();
        UpdateStrip();
        // a short pulse so the change is noticed
        var pulse = new DoubleAnimationUsingKeyFrames();
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.04, Dur(160), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, Dur(420), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }));
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        if (n.Blink) BlinkAccent(3);
    }

    /// <summary>Accent outline flashes a few times (timer done, file over the capsule).</summary>
    private void BlinkAccent(int times)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        for (int i = 0; i < times; i++)
        {
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(i * 500 + 150)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(i * 500 + 400)));
        }
        DropRing.BeginAnimation(OpacityProperty, a);
    }

    private void DismissNotice()
    {
        if (_notice == null) return;
        _notice = null;
        UpdateCollapsedRow();
        UpdateStrip();
        ShowQueuedNotice();
    }

    /// <summary>After the island collapses: the next waiting notice that is still fresh (≤ 2 min).</summary>
    private void ShowQueuedNotice()
    {
        if (_notice != null || _expanded) return;
        while (_noticeQueue.Count > 0)
        {
            var n = _noticeQueue[0];
            _noticeQueue.RemoveAt(0);
            if (DateTime.Now - n.Created < TimeSpan.FromMinutes(2)) { ShowNotice(n); return; }
        }
    }

    private void NoticeSecondTick(DateTime now)
    {
        if (_notice == null) return;
        if (now > _notice.Until && !Capsule.IsMouseOver) DismissNotice();
        else if (_notice.Right != null) ActivityWhen.Text = _notice.Right();
    }
}
