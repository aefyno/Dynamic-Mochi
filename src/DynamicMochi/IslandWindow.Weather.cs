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
    private DateTime _weatherTriedUtc = DateTime.MinValue;

    // place search
    private readonly DispatcherTimer _cityDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _districtDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? _searchCts;
    private List<PlaceHit> _hits = new();
    private int _hitIndex = -1;
    private bool _hitsForDistrict;
    private bool _suppressSearch;
    private Place? _districtBase;
    private string _districtCity = "";

    private void InitWeather()
    {
        _cityDebounce.Tick += async (_, _) => { _cityDebounce.Stop(); await SearchAsync(district: false); };
        _districtDebounce.Tick += async (_, _) => { _districtDebounce.Stop(); await SearchAsync(district: true); };
        RefreshChips();
        RenderPlaces();
        LoadCachedWeather();
        ShowWeather();
        RefreshWeatherIfStale(TimeSpan.FromMinutes(10));
    }

    private string WeatherKey(Place p) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2}", p.Latitude, p.Longitude, _settings.Fahrenheit);

    private void LoadCachedWeather()
    {
        var place = _settings.CurrentPlace;
        _weather = null;
        _weatherFor = "";
        if (place == null) return;
        _weather = WeatherService.Cached(place, _settings.Fahrenheit);
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
            var data = await WeatherService.FetchAsync(place, _settings.Fahrenheit);
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
        rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(600))
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
            IdleWeather.Visibility = Visibility.Collapsed;
            HomeWeather.Visibility = Visibility.Collapsed;
            if (place != null)
            {
                WeatherPlace.Text = place.Name;
                WeatherUpdated.Text = _weatherBusy ? "обновляю…" : "нет данных";
                NowTemp.Text = "—";
                NowDesc.Text = _weatherBusy ? "Загружаю погоду" : "Не удалось загрузить";
                NowFeels.Text = NowWind.Text = NowHumidity.Text = "";
                HourPanel.Children.Clear();
                DaysPanel.Children.Clear();
            }
            UpdateTint(300);
            return;
        }

        WeatherPlace.Text = place.Name;
        var fetchedLocal = w.FetchedUtc.ToLocalTime();
        WeatherUpdated.Text = _weatherBusy ? "обновляю…" :
            "обновлено " + (fetchedLocal.Date == DateTime.Today ? fetchedLocal.ToString("HH:mm", Ru) : fetchedLocal.ToString("d MMM HH:mm", Ru));

        NowIcon.Icon = WeatherIcon.For(w.Code, w.IsDay);
        NowTemp.Text = WeatherService.Temp(w.Temp);
        NowDesc.Text = WeatherService.Describe(w.Code);
        NowFeels.Text = "Ощущается " + WeatherService.Temp(w.Feels);
        NowWind.Text = "Ветер " + Math.Round(w.Wind).ToString("0", CultureInfo.InvariantCulture) + " м/с";
        NowHumidity.Text = "Влажность " + w.Humidity + "%";

        RenderHours(w);
        RenderDays(w);

        // idle capsule and Home
        IdleWeatherIcon.Icon = NowIcon.Icon;
        IdleTemp.Text = NowTemp.Text;
        IdleWeather.Visibility = Visibility.Visible;
        HomeWeatherIcon.Icon = NowIcon.Icon;
        HomeWeatherText.Text = NowTemp.Text + " · " + place.Name.Split(',')[0].Trim();
        HomeWeather.Visibility = Visibility.Visible;

        UpdateTint(300);
    }

    private void RenderHours(WeatherData w)
    {
        HourPanel.Children.Clear();
        for (int i = 0; i < w.Hours.Count; i++)
        {
            var h = w.Hours[i];
            var col = new StackPanel { Width = 42 };
            var time = new TextBlock { Text = i == 0 ? "Сейчас" : h.Time.ToString("HH", Ru), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
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

            var name = new TextBlock
            {
                Text = d.Date == today ? "Сегодня" : Capital(d.Date.ToString("ddd", Ru).Replace(".", "")),
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

    private void ChoosePlace_Click(object sender, RoutedEventArgs e)
    {
        SelectTab("settings", animate: true);
        Dispatcher.InvokeAsync(() =>
        {
            WeatherSection.BringIntoView(new Rect(0, 0, 10, 200));
            if (_settings.Places.Count < Settings.MaxPlaces) FocusField(CityBox);
        }, DispatcherPriority.Loaded);
    }

    // ================= Places (settings)

    private void SelectPlace(int index)
    {
        if (index < 0 || index >= _settings.Places.Count) return;
        _settings.PlaceIndex = index;
        _settings.Save();
        RenderPlaces();
        LoadCachedWeather();
        ShowWeather();
        _weatherTriedUtc = DateTime.MinValue;
        RefreshWeatherIfStale(TimeSpan.FromMinutes(10));
    }

    private void RenderPlaces()
    {
        PlacesList.Children.Clear();
        for (int i = 0; i < _settings.Places.Count; i++)
        {
            int index = i;
            var p = _settings.Places[i];
            bool current = i == _settings.PlaceIndex;

            var row = new Grid();
            var radio = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            var outer = new Ellipse { Width = 16, Height = 16, StrokeThickness = 1.5 };
            outer.SetResourceReference(Shape.StrokeProperty, current ? "AccentBrush" : "FaintBrush");
            radio.Children.Add(outer);
            if (current)
            {
                var inner = new Ellipse { Width = 8, Height = 8 };
                inner.SetResourceReference(Shape.FillProperty, "AccentBrush");
                radio.Children.Add(inner);
            }
            row.Children.Add(radio);
            var name = new TextBlock { Text = p.Name, FontSize = 13, Margin = new Thickness(28, 0, 36, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            row.Children.Add(name);

            var remove = new Button { Style = (Style)FindResource("IconButton"), Width = 26, Height = 26, FontSize = 10, Content = "", ToolTip = "Удалить место", HorizontalAlignment = HorizontalAlignment.Right };
            remove.SetResourceReference(ForegroundProperty, "SubBrush");
            remove.Click += (_, e) => { e.Handled = true; RemovePlace(index); };
            row.Children.Add(remove);

            var button = new Button { Style = (Style)FindResource("SettingRow"), Height = 36, Content = row };
            button.Click += (_, _) => SelectPlace(index);
            PlacesList.Children.Add(button);
        }

        bool full = _settings.Places.Count >= Settings.MaxPlaces;
        CityBox.IsEnabled = !full;
        LocateButton.IsEnabled = !full;
        CityBox.Tag = full ? "Можно сохранить до 3 мест" : _settings.Places.Count == 0 ? "Найти город" : "Добавить город";
        RenderPlaceDots();
    }

    private void RemovePlace(int index)
    {
        if (index < 0 || index >= _settings.Places.Count) return;
        var removed = _settings.Places[index];
        _settings.Places.RemoveAt(index);
        if (_settings.PlaceIndex >= _settings.Places.Count) _settings.PlaceIndex = Math.Max(0, _settings.Places.Count - 1);
        _settings.Save();
        if (_districtBase == removed) { _districtBase = null; DistrictRow.Visibility = Visibility.Collapsed; }
        RenderPlaces();
        LoadCachedWeather();
        ShowWeather();
        _weatherTriedUtc = DateTime.MinValue;
        RefreshWeatherIfStale(TimeSpan.FromMinutes(10));
    }

    private void AddPlace(Place p)
    {
        if (_settings.Places.Count >= Settings.MaxPlaces) { ShowHint("Можно сохранить до 3 мест. Удали одно, чтобы добавить новое."); return; }
        _settings.Places.Add(p);
        _settings.PlaceIndex = _settings.Places.Count - 1;
        _settings.Save();
        RenderPlaces();
        LoadCachedWeather();
        ShowWeather();
        _weatherTriedUtc = DateTime.MinValue;
        RefreshWeatherIfStale(TimeSpan.Zero);
    }

    private void ShowHint(string? text, bool showSettingsButton = false)
    {
        PlaceHint.Text = text ?? "";
        PlaceHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        LocationSettingsButton.Visibility = showSettingsButton ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- search: city (Open-Meteo geocoding) and district (Nominatim)

    private void CityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;
        _cityDebounce.Stop();
        _cityDebounce.Start();
    }

    private void DistrictBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;
        _districtDebounce.Stop();
        _districtDebounce.Start();
    }

    private async Task SearchAsync(bool district)
    {
        var box = district ? DistrictBox : CityBox;
        string q = box.Text.Trim();
        _searchCts?.Cancel();
        if (q.Length < 2 || (district && _districtBase == null))
        {
            ShowHits(new List<PlaceHit>(), district);
            return;
        }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var (spinner, spin) = district ? (DistrictSpinner, DistrictSpin) : (CitySpinner, CitySpin);
        SetSpinner(spinner, spin, true);
        ShowHint(null);
        try
        {
            var hits = district
                ? await PlaceService.SearchDistrictAsync(q, _districtBase!, cts.Token)
                : await PlaceService.SearchCityAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowHits(hits, district);
            if (hits.Count == 0) ShowHint(district ? "Ничего не нашлось в этом городе" : "Город не найден");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("place search failed: " + ex.Message);
            if (!cts.IsCancellationRequested) ShowHint("Не получилось найти. Проверь интернет.");
        }
        finally
        {
            if (_searchCts == cts) SetSpinner(spinner, spin, false);
        }
    }

    private static void SetSpinner(Ellipse spinner, RotateTransform spin, bool on)
    {
        spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        spin.BeginAnimation(RotateTransform.AngleProperty, on
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void ShowHits(List<PlaceHit> hits, bool district)
    {
        _hits = hits;
        _hitsForDistrict = district;
        _hitIndex = hits.Count > 0 ? 0 : -1;
        CityResults.Children.Clear();
        DistrictResults.Children.Clear();
        var panel = district ? DistrictResults : CityResults;
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var title = new TextBlock { Text = hit.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            var sub = new TextBlock { Text = hit.Subtitle, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            stack.Children.Add(title);
            if (hit.Subtitle.Length > 0) stack.Children.Add(sub);
            var b = new Button { Style = (Style)FindResource("FlatButton"), Content = stack, Padding = new Thickness(10, 0, 10, 0) };
            b.Click += (_, _) => ChooseHit(hit, district);
            panel.Children.Add(b);
        }
        HighlightHit();
    }

    private void HighlightHit()
    {
        var panel = _hitsForDistrict ? DistrictResults : CityResults;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not Button b) continue;
            if (i == _hitIndex) b.SetResourceReference(BackgroundProperty, "SurfaceBrush");
            else b.Background = Brushes.Transparent;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool district = sender == DistrictBox;
        if (district != _hitsForDistrict && _hits.Count > 0 && e.Key is Key.Up or Key.Down or Key.Enter) return;
        switch (e.Key)
        {
            case Key.Down when _hits.Count > 0:
                _hitIndex = Math.Min(_hits.Count - 1, _hitIndex + 1); HighlightHit(); e.Handled = true; break;
            case Key.Up when _hits.Count > 0:
                _hitIndex = Math.Max(0, _hitIndex - 1); HighlightHit(); e.Handled = true; break;
            case Key.Enter when _hitIndex >= 0 && _hitIndex < _hits.Count:
                ChooseHit(_hits[_hitIndex], _hitsForDistrict); e.Handled = true; break;
            case Key.Escape:
                ClearPlaceSearch(); DisableKeyboard(); e.Handled = true; break;
        }
    }

    private void ChooseHit(PlaceHit hit, bool district)
    {
        if (!district)
        {
            var place = new Place { Name = hit.Title, Latitude = hit.Latitude, Longitude = hit.Longitude, Timezone = hit.Timezone, Country = hit.Country };
            if (_settings.Places.Count >= Settings.MaxPlaces) { ShowHint("Можно сохранить до 3 мест. Удали одно, чтобы добавить новое."); return; }
            SetBoxText(CityBox, "");
            ShowHits(new List<PlaceHit>(), false);
            AddPlace(place);
            // step 2: optional district
            _districtBase = place;
            _districtCity = hit.Title;
            SetBoxText(DistrictBox, "");
            DistrictRow.Visibility = Visibility.Visible;
            ShowHint("Готово. Можно уточнить район, чтобы погода была точно для твоего места.");
            FocusField(DistrictBox);
        }
        else if (_districtBase != null)
        {
            _districtBase.Name = hit.Title + ", " + _districtCity;
            _districtBase.Latitude = hit.Latitude;
            _districtBase.Longitude = hit.Longitude;
            _settings.Save();
            SetBoxText(DistrictBox, "");
            ShowHits(new List<PlaceHit>(), true);
            DistrictRow.Visibility = Visibility.Collapsed;
            _districtBase = null;
            ShowHint(null);
            DisableKeyboard();
            RenderPlaces();
            LoadCachedWeather();
            ShowWeather();
            _weatherTriedUtc = DateTime.MinValue;
            RefreshWeatherIfStale(TimeSpan.Zero);
        }
    }

    private void SetBoxText(TextBox box, string text)
    {
        _suppressSearch = true;
        box.Text = text;
        _suppressSearch = false;
    }

    private void ClearPlaceSearch()
    {
        _searchCts?.Cancel();
        _cityDebounce.Stop();
        _districtDebounce.Stop();
        SetSpinner(CitySpinner, CitySpin, false);
        SetSpinner(DistrictSpinner, DistrictSpin, false);
        SetBoxText(CityBox, "");
        SetBoxText(DistrictBox, "");
        _hits = new();
        CityResults.Children.Clear();
        DistrictResults.Children.Clear();
        DistrictRow.Visibility = Visibility.Collapsed;
        _districtBase = null;
        ShowHint(null);
    }

    // ----- auto-detect

    private async void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Places.Count >= Settings.MaxPlaces) { ShowHint("Можно сохранить до 3 мест. Удали одно, чтобы добавить новое."); return; }
        LocateButton.IsEnabled = false;
        ShowHint("Определяю место…");
        try
        {
            var (result, lat, lon) = await PlaceService.LocateAsync();
            if (result == PlaceService.LocateResult.Denied)
            {
                ShowHint("Разреши доступ к местоположению в Параметрах Windows (Конфиденциальность → Расположение).", showSettingsButton: true);
                return;
            }
            if (result != PlaceService.LocateResult.Ok)
            {
                ShowHint("Не получилось определить место. Найди город вручную.");
                return;
            }
            string name;
            string country = "";
            try { (name, country) = await PlaceService.ReverseAsync(lat, lon, CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("reverse geocoding failed: " + ex.Message);
                name = string.Format(CultureInfo.InvariantCulture, "{0:F2}, {1:F2}", lat, lon);
            }
            AddPlace(new Place { Name = name, Latitude = lat, Longitude = lon, Timezone = "auto", Country = country });
            ShowHint(null);
        }
        finally
        {
            LocateButton.IsEnabled = _settings.Places.Count < Settings.MaxPlaces;
        }
    }

    private void LocationSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open location settings failed: " + ex.Message); }
    }

    private void UnitC_Click(object sender, RoutedEventArgs e) => SetUnits(false);
    private void UnitF_Click(object sender, RoutedEventArgs e) => SetUnits(true);

    private void SetUnits(bool fahrenheit)
    {
        if (_settings.Fahrenheit == fahrenheit) return;
        _settings.Fahrenheit = fahrenheit;
        _settings.Save();
        RefreshChips();
        LoadCachedWeather();
        ShowWeather();
        _weatherTriedUtc = DateTime.MinValue;
        RefreshWeatherIfStale(TimeSpan.Zero);
    }
}
