using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>
/// v1.8: clipboard history tab. Every copied text lands at the top of the list; a click copies it again,
/// ✕ removes it. Copies that apps mark as private (password managers) are skipped, like Windows' own history.
/// </summary>
public partial class IslandWindow
{
    private ClipHistory? _clips;
    private string? _ownCopy;   // text we put on the clipboard ourselves: not added again

    private ClipHistory Clips => _clips ??= new ClipHistory();

    private int ClipLimit => _settings.ClipboardSize is 10 or 25 or 50 ? _settings.ClipboardSize : 25;

    /// <summary>Called on every clipboard change (WM_CLIPBOARDUPDATE).</summary>
    private void RecordClipboard()
    {
        if (!_settings.ClipboardHistory) return;
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(120);   // let the source app finish writing all formats
            try
            {
                if (!Clipboard.ContainsText() || IsPrivateCopy()) return;
                string text = Clipboard.GetText();
                if (_ownCopy != null && text == _ownCopy) { _ownCopy = null; return; }
                if (Clips.Add(text, ClipLimit) && _page == "clipboard" && _expanded) RenderClips();
            }
            catch (Exception ex) { Log.Write("clipboard history read failed: " + ex.Message); }
        }, DispatcherPriority.Background);
    }

    /// <summary>Password managers and similar apps ask clipboard tools not to keep their copies.</summary>
    private static bool IsPrivateCopy()
    {
        if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return true;
        if (Clipboard.ContainsData("Clipboard Viewer Ignore")) return true;
        foreach (var name in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (!Clipboard.ContainsData(name)) continue;
            if (Clipboard.GetData(name) is MemoryStream ms && ms.Length >= 4)
            {
                var b = ms.ToArray();
                if (BitConverter.ToInt32(b, 0) == 0) return true;
            }
        }
        return false;
    }

    private void RenderClips()
    {
        ClipList.Children.Clear();
        var items = Clips.Items;
        ClipEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipClear.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClipCount.Text = items.Count == 0 ? "" : $"{items.Count} / {ClipLimit}";
        foreach (var item in items) ClipList.Children.Add(MakeClipRow(item));
    }

    private FrameworkElement MakeClipRow(ClipItem item)
    {
        string preview = string.Join(" ", item.Text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var text = new TextBlock
        {
            Text = preview.Length > 300 ? preview[..300] : preview,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 36,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");

        var time = new TextBlock
        {
            Text = When(item.Time),
            FontSize = 11,
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        time.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");

        var remove = new Button { Content = "", FontSize = 10, Width = 24, Height = 24, ToolTip = Loc.T("clip.remove") };
        remove.SetResourceReference(StyleProperty, "IconButton");
        remove.SetResourceReference(ForegroundProperty, "SubBrush");
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            Clips.Remove(item);
            RenderClips();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        var row = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7, 6, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = item.Text.Length > 600 ? item.Text[..600] + "…" : item.Text,
        };
        row.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverBrush");
        row.MouseLeave += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        row.MouseLeftButtonUp += (_, _) => CopyBack(item, time);
        return row;
    }

    private void CopyBack(ClipItem item, TextBlock time)
    {
        try
        {
            _ownCopy = item.Text;
            Clipboard.SetText(item.Text);
            time.Text = Loc.T("clip.copied");
            time.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Clips.Add(item.Text, ClipLimit);   // to the top of the list
                if (_page == "clipboard") RenderClips();
            };
            t.Start();
        }
        catch (Exception ex) { Log.Write("clipboard copy failed: " + ex.Message); }
    }

    private void ClipClear_Click(object sender, RoutedEventArgs e)
    {
        Clips.Clear();
        RenderClips();
    }

    private static string When(DateTime t)
    {
        var now = DateTime.Now;
        if ((now - t).TotalMinutes < 1) return Loc.T("clip.now");
        if (t.Date == now.Date) return t.ToString("HH:mm");
        if (t.Date == now.Date.AddDays(-1)) return Loc.T("clip.yesterday");
        return t.ToString("dd.MM");
    }
}
