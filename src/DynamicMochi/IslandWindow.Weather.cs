using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>Weather tab (Open-Meteo) and the place picker in Settings (city → district, auto-detect, up to 3 places).</summary>
public partial class IslandWindow
{
    private static readonly Color RainBlue = Color.FromRgb(0x64, 0xD2, 0xFF);

    private WeatherData? _weather;
    private string _weatherFor = "";          // place + unit the data belongs to
    private bool _weatherBusy;
    private bool _idleWeatherReady;
    private DateTime _weatherTriedUtc = DateTime.MinValue;

    private void InitWeather()
    {
        LoadCachedWeather();
        ShowWeather();
        RefreshWeatherIfStale(TimeSpan.FromMinutes(10));
    }

    private string WeatherKey(Place p) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2},{3}", p.Latitude, p.Longitude, _settings.Fahrenheit, _settings.WindUnit);

    /// <summary>Forecast for the current place and units, or null.</summary>
    private WeatherData? CurrentWeather =>
        _settings.CurrentPlace is { } p && _weatherFor == WeatherKey(p) ? _weather : null;

    private void LoadCachedWeather()
    {
        var place = _settings.CurrentPlace;
        _weather = null;
        _weatherFor = "";
        if (place == null) return;
        _weather = WeatherService.Cached(place, _settings.Fahrenheit, _settings.WindUnit);
        _weatherFor = WeatherKey(place);
    }

    private void RefreshWeatherIfStale(TimeSpan maxAge)
    {
        var place = _settings.CurrentPlace;
        if (place == null) return;
        bool stale = _weather == null || _weatherFor != WeatherKey(place) || DateTime.UtcNow - _weather.FetchedUtc > maxAge;
        // after a failure, wait a minute before trying again
        if (stale && DateTime.UtcNow - _weatherTriedUtc > TimeSpan.FromMinutes(1)) _ = FetchWeatherAsync();
    }

    private void WeatherSecondTick() => RefreshWeatherIfStale(TimeSpan.FromMinutes(15));

    private async Task FetchWeatherAsync()
    {
        var place = _settings.CurrentPlace;
        if (place == null || _weatherBusy) return;
        _weatherBusy = true;
        _weatherTriedUtc = DateTime.UtcNow;
        Spin(WeatherRefresh);
        string key = WeatherKey(place);
        try
        {
            var data = await WeatherService.FetchAsync(place, _settings.Fahrenheit, _settings.WindUnit);
            if (_settings.CurrentPlace != null && WeatherKey(_settings.CurrentPlace) == key)
            {
                _weather = data;
                _weatherFor = key;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"weather fetch failed for {place.Name}: {ex.Message}");
        }
        finally
        {
            _weatherBusy = false;
            ShowWeather();
        }
    }

    private void WeatherRefresh_Click(object sender, RoutedEventArgs e)
    {
        _weatherTriedUtc = DateTime.MinValue;
        _ = FetchWeatherAsync();
    }

    /// <summary>Refresh button: one 600 ms turn.</summary>
    private static void Spin(Button b)
    {
        var rt = new RotateTransform();
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        b.RenderTransform = rt;
        rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, Dur(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    // ================= Render

    private void ShowWeather()
    {
        var place = _settings.CurrentPlace;
        bool has = place != null;
        WeatherEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        WeatherContent.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        RenderPlaceDots();

        var w = has && _weatherFor == WeatherKey(place!) ? _weather : null;
        if (place == null || w == null)
        {
            _idleWeatherReady = false;
            UpdateIdleLine();
            HomeWeather.Visibility = Visibility.Collapsed;
            if (place != null)
            {
                WeatherPlace.Text = place.Name;
                WeatherUpdated.Text = Loc.T(_weatherBusy ? "weather.updating" : "weather.nodata");
                NowTemp.Text = "—";
                NowDesc.Text = Loc.T(_weatherBusy ? "weather.loading" : "weather.failed");
                NowFeels.Text = NowWind.Text = NowHumidity.Text = NowGusts.Text = "";
                HourPanel.Children.Clear();
                DaysPanel.Children.Clear();
            }
            UpdateTint(300);
            return;
        }

        WeatherPlace.Text = place.Name;
        var fetchedLocal = w.FetchedUtc.ToLocalTime();
        WeatherUpdated.Text = _weatherBusy ? Loc.T("weather.updating") :
            Loc.F("weather.updated", (fetchedLocal.Date == DateTime.Today ? "" : fetchedLocal.ToString(Loc.English ? "MMM d " : "d MMM ", Ru)) + Loc.Clock(fetchedLocal, _settings.Clock24));

        NowIcon.Icon = WeatherIcon.For(w.Code, w.IsDay);
        NowTemp.Text = WeatherService.Temp(w.Temp);
        NowDesc.Text = WeatherService.Describe(w.Code);
        NowFeels.Text = Loc.F("weather.feels", WeatherService.Temp(w.Feels));
        NowHumidity.Text = Loc.F("weather.humidity", w.Humidity);
        ShowWind(w);

        RenderHours(w);
        RenderDays(w);

        // idle capsule ("Облачно · +16°") and Home
        IdleWeatherIcon.Icon = NowIcon.Icon;
        IdleTemp.Text = WeatherService.Describe(w.Code) + " · " + NowTemp.Text;
        _idleWeatherReady = true;
        UpdateIdleLine();
        HomeWeatherIcon.Icon = NowIcon.Icon;
        HomeWeatherText.Text = NowTemp.Text + " · " + place.Name.Split(',')[0].Trim();
        HomeWeather.Visibility = Visibility.Visible;

        UpdateTint(300);
        UpdateWeatherFx();
    }

    /// <summary>"Ветер 4 м/с · СЗ" with an arrow turned to where it blows, "порывы до 9 м/с · умеренный" below (SPEC-v1.4 §16).</summary>
    private void ShowWind(WeatherData w)
    {
        string unit = WeatherService.UnitLabel(w.WindUnit);
        double ms = WeatherService.ToMs(w.Wind, w.WindUnit);
        string speed = Math.Round(w.Wind).ToString("0", CultureInfo.InvariantCulture);
        if (ms < 0.3)
        {
            NowWind.Text = Loc.T("wind.calm");
            WindArrow.Visibility = Visibility.Collapsed;
        }
        else
        {
            NowWind.Text = Loc.F("wind.now", speed, unit, WeatherService.Compass(w.WindDir));
            WindArrow.Visibility = Visibility.Visible;
            WindArrowAngle.Angle = w.WindDir + 180;   // the arrow points where the wind goes
        }
        NowWind.SetResourceReference(TextBlock.ForegroundProperty, ms >= 8 ? "AccentBrush" : "SubBrush");
        NowGusts.Text = Loc.F("wind.gusts", Math.Round(w.Gusts).ToString("0", CultureInfo.InvariantCulture), unit, WeatherService.Beaufort(ms));
    }

    private void RenderHours(WeatherData w)
    {
        HourPanel.Children.Clear();
        for (int i = 0; i < w.Hours.Count; i++)
        {
            var h = w.Hours[i];
            var col = new StackPanel { Width = 42 };
            var time = new TextBlock { Text = i == 0 ? Loc.T("weather.now") : h.Time.ToString(_settings.Clock24 ? "HH" : "h tt", Ru), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            time.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
            var icon = new WeatherIcon { Icon = WeatherIcon.For(h.Code, h.IsDay), Width = 18, Height = 18, Margin = new Thickness(0, 4, 0, 2) };
            icon.SetResourceReference(WeatherIcon.ForegroundProperty, "FgBrush");
            var temp = new TextBlock { Text = WeatherService.Temp(h.Temp), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
            Typography.SetNumeralAlignment(temp, FontNumeralAlignment.Tabular);
            temp.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            col.Children.Add(time);
            col.Children.Add(icon);
            col.Children.Add(temp);
            var precip = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(RainBlue), Height = 13 };
            if (h.Precip >= 30) precip.Text = h.Precip + "%";
            col.Children.Add(precip);
            // wind every hour, accent from 8 m/s
            var wind = new TextBlock { Text = Math.Round(h.Wind).ToString("0", CultureInfo.InvariantCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = WeatherService.UnitLabel(w.WindUnit) };
            Typography.SetNumeralAlignment(wind, FontNumeralAlignment.Tabular);
            wind.SetResourceReference(TextBlock.ForegroundProperty, WeatherService.ToMs(h.Wind, w.WindUnit) >= 8 ? "AccentBrush" : "FaintBrush");
            col.Children.Add(wind);

            var plate = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(0, 5, 0, 3), Margin = new Thickness(0, 0, 4, 0), Child = col };
            if (i == 0) plate.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            HourPanel.Children.Add(plate);
        }
        HourScroll.ScrollToHorizontalOffset(0);
    }

    private void RenderDays(WeatherData w)
    {
        DaysPanel.Children.Clear();
        if (w.Days.Count == 0) return;
        double weekMin = w.Days.Min(d => d.Min), weekMax = w.Days.Max(d => d.Max);
        double span = Math.Max(1, weekMax - weekMin);
        var today = w.PlaceNow.Date;

        foreach (var d in w.Days)
        {
            var row = new Grid { Height = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            var name = new TextBlock
            {
                Text = d.Date == today ? Loc.T("day.today") : Capital(d.Date.ToString("ddd", Ru).Replace(".", "")),
                FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            row.Children.Add(name);

            var icon = new WeatherIcon { Icon = WeatherIcon.For(d.Code, true), Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
            icon.SetResourceReference(WeatherIcon.ForegroundProperty, "FgBrush");
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);

            var min = new TextBlock { Text = WeatherService.Temp(d.Min), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
            Typography.SetNumeralAlignment(min, FontNumeralAlignment.Tabular);
            min.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            Grid.SetColumn(min, 2);
            row.Children.Add(min);

            // range bar: week scale, the day's segment filled blue → orange
            var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
            var track = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.3 };
            track.SetResourceReference(Border.BackgroundProperty, "FaintBrush");
            Grid.SetColumnSpan(track, 3);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, d.Min - weekMin) / span, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.04, (d.Max - d.Min) / span), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, weekMax - d.Max) / span, GridUnitType.Star) });
            bar.Children.Add(track);
            var seg = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = new LinearGradientBrush(RainBlue, Color.FromRgb(0xFF, 0x9F, 0x0A), 0),
            };
            Grid.SetColumn(seg, 1);
            bar.Children.Add(seg);
            Grid.SetColumn(bar, 3);
            row.Children.Add(bar);

            var max = new TextBlock { Text = WeatherService.Temp(d.Max), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Typography.SetNumeralAlignment(max, FontNumeralAlignment.Tabular);
            max.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            Grid.SetColumn(max, 4);
            row.Children.Add(max);

            // max wind of the day
            var windMax = new TextBlock
            {
                Text = Math.Round(d.WindMax).ToString("0", CultureInfo.InvariantCulture) + " " + WeatherService.UnitLabel(w.WindUnit),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            };
            Typography.SetNumeralAlignment(windMax, FontNumeralAlignment.Tabular);
            windMax.SetResourceReference(TextBlock.ForegroundProperty, WeatherService.ToMs(d.WindMax, w.WindUnit) >= 8 ? "AccentBrush" : "FaintBrush");
            Grid.SetColumn(windMax, 5);
            row.Children.Add(windMax);

            DaysPanel.Children.Add(row);
        }
    }

    private void RenderPlaceDots()
    {
        PlaceDots.Children.Clear();
        PlaceDots.Visibility = _settings.Places.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _settings.Places.Count; i++)
        {
            int index = i;
            var hit = new Grid { Width = 16, Height = 12, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = _settings.Places[i].Name };
            var dot = new Ellipse { Width = 6, Height = 6 };
            dot.SetResourceReference(Shape.FillProperty, i == _settings.PlaceIndex ? "FgBrush" : "FaintBrush");
            hit.Children.Add(dot);
            hit.MouseLeftButtonUp += (_, _) => SelectPlace(index);
            PlaceDots.Children.Add(hit);
        }
    }

    /// <summary>Weather blob colour for the background (null = none).</summary>
    private Color? WeatherTint()
    {
        var w = _weather;
        if (w == null || _settings.CurrentPlace == null || _weatherFor != WeatherKey(_settings.CurrentPlace)) return null;
        double k = _theme.IsLight ? 0.8 : 1;
        Color A(byte r, byte g, byte b, double a) => Color.FromArgb((byte)Math.Round(255 * a * k), r, g, b);
        return WeatherService.Kind(w.Code) switch
        {
            WeatherKind.Clear or WeatherKind.PartlyCloudy => w.IsDay ? A(0xFF, 0xD6, 0x0A, 0.15) : A(0x0A, 0x84, 0xFF, 0.12),
            WeatherKind.Rain or WeatherKind.Drizzle or WeatherKind.Thunder => A(0x5E, 0x7A, 0x99, 0.15),
            WeatherKind.Snow => A(0xBF, 0xE6, 0xFF, 0.12),
            _ => null,
        };
    }

    private void HourScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        HourScroll.ScrollToHorizontalOffset(HourScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void HomeWeather_Click(object sender, RoutedEventArgs e) => SelectTab("weather", animate: true);

    /// <summary>Place name / "Выбрать": the settings window opens on Weather with the add-place dialog.</summary>
    private void ChoosePlace_Click(object sender, RoutedEventArgs e) => App.ShowSettings("weather", addPlace: true);

    // ================= Places (managed in the settings window)

    private void SelectPlace(int index)
    {
        if (index < 0 || index >= _settings.Places.Count) return;
        _settings.PlaceIndex = index;
        _settings.Save();
        PlacesChanged();
        App.SettingsChangedFromIsland();
    }

    /// <summary>Places, the current place or units changed: reload the cache, redraw and refetch.</summary>
    public void PlacesChanged()
    {
        LoadCachedWeather();
        ShowWeather();
        _weatherTriedUtc = DateTime.MinValue;
        RefreshWeatherIfStale(TimeSpan.FromMinutes(10));
    }

    private string _unitsKey = "";

    private void WeatherSettingsChanged()
    {
        string key = $"{_settings.Fahrenheit}{_settings.WindUnit}|{_settings.PlaceIndex}|{string.Join(";", _settings.Places.Select(p => WeatherKey(p)))}";
        if (key != _unitsKey) { _unitsKey = key; PlacesChanged(); }
        else ShowWeather();
    }
}
