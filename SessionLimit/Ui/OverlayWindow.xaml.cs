using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SessionLimit;

public partial class OverlayWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly UsageStore _store;
    private readonly NotificationEngine _notifier;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly SourceStatus _stTranscripts = new() { Name = "transcripts" };
    private readonly SourceStatus _stOtel        = new() { Name = "otel" };
    private readonly SourceStatus _stUsageCmd    = new() { Name = "/usage" };
    private readonly SourceStatus _stAdminApi    = new() { Name = "admin api" };

    // Rebuilding the dynamic rows every tick is pure waste; only redraw on change.
    private string _modelSig = "", _sourceSig = "", _projectSig = "";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private TranscriptCollector? _transcripts;
    private OtelCollector? _otel;
    private UsageCommandCollector? _usageCmd;
    private AdminApiCollector? _adminApi;

    public OverlayWindow()
    {
        InitializeComponent();

        _cfg = AppConfig.Load();
        _cfg.Save();                 // materialise defaults on first run so they're editable on disk
        _store = new UsageStore();
        _notifier = new NotificationEngine(_cfg);

        ApplyAppearance();
        RestorePosition();

        HeaderBar.MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { /* mouse released mid-drag */ }
        };

        _store.Changed += () => Dispatcher.BeginInvoke(Render);
        _tick.Tick += (_, _) =>
        {
            Render();
            _store.SaveState();      // self-throttled to every 30 s
        };
        _tick.Start();

        StartCollectors();
        Render();
    }

    // ==================================================================
    //  collectors
    // ==================================================================
    private void StartCollectors()
    {
        StopCollectors();
        _modelSig = _sourceSig = "";      // force a redraw of the dynamic rows

        _stTranscripts.Enabled = _cfg.EnableTranscripts;
        _stOtel.Enabled        = _cfg.EnableOtel;
        _stUsageCmd.Enabled    = _cfg.EnableUsageCmd;
        _stAdminApi.Enabled    = _cfg.EnableAdminApi;

        foreach (var s in AllSources().Where(s => !s.Enabled))
        {
            s.Healthy = false;
            s.Detail = "off";
        }

        if (_cfg.EnableTranscripts)
        {
            _transcripts = new TranscriptCollector(_store, _stTranscripts);
            _transcripts.Start();
        }

        if (_cfg.EnableOtel)
        {
            // Transcripts are authoritative for tokens; OTEL only counts when they're off.
            _otel = new OtelCollector(_store, _stOtel, _cfg.OtelPort, () => !_cfg.EnableTranscripts);
            _otel.Start();
        }

        if (_cfg.EnableUsageCmd)
        {
            _usageCmd = new UsageCommandCollector(_cfg, _stUsageCmd);
            _usageCmd.Updated += () => Dispatcher.BeginInvoke(Render);
            _usageCmd.Start();
        }

        if (_cfg.EnableAdminApi)
        {
            _adminApi = new AdminApiCollector(_cfg, _stAdminApi);
            _adminApi.Updated += () => Dispatcher.BeginInvoke(Render);
            _adminApi.Start();
        }
    }

    private void StopCollectors()
    {
        _transcripts?.Dispose(); _transcripts = null;
        _otel?.Dispose();        _otel = null;
        _usageCmd?.Dispose();    _usageCmd = null;
        _adminApi?.Dispose();    _adminApi = null;
    }

    private IEnumerable<SourceStatus> AllSources()
    {
        yield return _stTranscripts;
        yield return _stOtel;
        yield return _stUsageCmd;
        yield return _stAdminApi;
    }

    // ==================================================================
    //  rendering
    // ==================================================================
    private void Render()
    {
        try
        {
            var session = _store.CurrentSession();
            var week = _store.CurrentWeek();
            var plan = _usageCmd?.Plan;
            var planFresh = plan is { IsFresh: true };

            // Real plan % beats a local budget estimate whenever we have it.
            double? sessionPct = planFresh ? plan!.SessionPercent : null;
            double? weekPct    = planFresh ? plan!.WeeklyPercent  : null;
            var isReal = sessionPct.HasValue || weekPct.HasValue;

            sessionPct ??= Percent(session.TotalTokens, _cfg.SessionTokenBudget);
            weekPct    ??= Percent(week.TotalTokens,    _cfg.WeeklyTokenBudget);

            SessionLabel.Text = isReal && plan!.SessionPercent.HasValue
                ? "SESSION · 5h · plan"
                : "SESSION · 5h · budget";
            WeekLabel.Text = isReal && plan!.WeeklyPercent.HasValue
                ? "WEEK · plan"
                : "WEEK · 7d rolling · budget";

            SetBar(SessionFill, SessionRest, SessionBar, sessionPct);
            SetBar(WeekFill, WeekRest, WeekBar, weekPct);

            SessionReset.Text = session.Remaining is { } r
                ? $"resets in {NotificationEngine.Fmt(r)}"
                : session.Requests == 0 ? "idle" : "";

            SessionDetail.Text = Describe(session, sessionPct);
            WeekPct.Text = weekPct is { } wp ? $"{wp:0}%" : "";
            WeekDetail.Text = Describe(week, null);

            RenderBreakdown(session);
            RenderRates(session, sessionPct);
            RenderModels(session, week);
            RenderProjects(session);
            RenderSources();
            ApplySectionVisibility();

            // Live dot pulses green when something billed in the last two minutes.
            var recent = _store.LastActivity is { } last &&
                         (DateTimeOffset.Now - last) < TimeSpan.FromMinutes(2);
            LiveDot.Fill = recent ? Brush("Accent") : Brush("FgDim");

            // Don't alert off the budget fallback while a real-percentage source is still
            // starting up — /usage takes a few seconds, and firing early produces bogus
            // alerts ("weekly at 3499%") that a moment later resolve to the true figure.
            var planPending = _cfg.EnableUsageCmd && !planFresh &&
                              DateTimeOffset.Now - _startedAt < TimeSpan.FromMinutes(2);
            if (!planPending)
                _notifier.Evaluate(sessionPct, weekPct, session, isReal);
        }
        catch (Exception ex) { Log.Error("render failed", ex); }
    }

    private string Describe(WindowStats s, double? pct)
    {
        var parts = new List<string> { $"{Tokens(s.TotalTokens)} tok" };
        // "≈$" because on a Pro/Max subscription this is notional API list-price value,
        // not money actually billed. Only meaningful as a relative burn signal.
        if (_cfg.ShowCost) parts.Add($"≈${s.Cost:0.00}");
        if (pct is { } p) parts.Add($"{p:0}%");
        parts.Add($"{s.Requests} req");
        return string.Join("  ·  ", parts);
    }

    private void RenderBreakdown(WindowStats s)
    {
        if (!_cfg.ShowTokenBreakdown || _cfg.Compact) return;
        StatIn.Text     = $"in      {Tokens(s.InputTokens)}";
        StatOut.Text    = $"out     {Tokens(s.OutputTokens)}";
        StatCacheR.Text = $"cache r {Tokens(s.CacheReadTokens)}";
        StatCacheW.Text = $"cache w {Tokens(s.CacheWriteTokens)}";
    }

    private void RenderRates(WindowStats s, double? pct)
    {
        if (!_cfg.ShowRates || _cfg.Compact) return;

        var rate = s.TokensPerMinute;
        RateLine.Text = rate > 0
            ? $"{Tokens((long)rate)}/min  ·  {s.CacheHitRate:0}% cached"
            : "no burn rate yet";

        // The single most useful number here: will this window run out before it resets?
        if (pct is { } p && s.ProjectedExhaustion(p) is { } eta)
        {
            var beforeReset = s.Remaining is { } rem && eta < rem;
            ProjectLine.Text = beforeReset
                ? $"⚠ hits 100% in {NotificationEngine.Fmt(eta)} — before reset"
                : $"on pace — window resets first";
            ProjectLine.Foreground = beforeReset ? Brush("Warn") : Brush("FgDim");
        }
        else
        {
            ProjectLine.Text = "";
        }
    }

    private void RenderModels(WindowStats session, WindowStats week)
    {
        if (!_cfg.ShowModels || _cfg.Compact) return;

        // Fall back to the week when the current session is quiet, so models used
        // recently (Fable, Haiku, …) still show rather than an empty panel.
        var source = session;
        var scope = "this session";
        if (!session.ByModel.Any(kv => kv.Value.Tokens > 0))
        {
            source = week;
            scope = "this week";
        }

        var rows = source.ByModel.Where(kv => kv.Value.Tokens > 0)
                                 .OrderByDescending(kv => kv.Value.Tokens)
                                 .Take(Math.Max(1, _cfg.MaxModelRows)).ToList();

        // Pad with known-but-unused models so it's visible they're tracked — otherwise
        // a model you haven't touched this window just silently isn't there.
        if (_cfg.ShowUnusedModels)
            rows.AddRange(Pricing.KnownModels
                .Where(m => !rows.Any(r => r.Key.Equals(m, StringComparison.OrdinalIgnoreCase)))
                .Select(m => new KeyValuePair<string, ModelSlice>(m, new ModelSlice())));

        var total = Math.Max(1, source.TotalTokens);

        var sig = scope + "|" + string.Join("|",
            rows.Select(r => $"{r.Key}:{r.Value.Tokens}:{r.Value.Cost:0.00}")) + _cfg.ShowCost;
        sig += _cfg.ShowUnusedModels;
        if (sig == _modelSig && ModelList.Children.Count > 0) return;
        _modelSig = sig;

        ModelHeader.Text = $"BY MODEL · {scope}";
        ModelList.Children.Clear();

        if (rows.Count == 0)
        {
            ModelList.Children.Add(Dim("no activity yet"));
            return;
        }

        foreach (var (name, slice) in rows)
        {
            if (slice.Tokens == 0)
            {
                var idle = Row(name, "unused");
                idle.Opacity = 0.45;
                ModelList.Children.Add(idle);
                continue;
            }

            var share = slice.Tokens * 100.0 / total;
            var value = _cfg.ShowCost
                ? $"{Tokens(slice.Tokens)}  ≈${slice.Cost:0.00}  {share:0}%"
                : $"{Tokens(slice.Tokens)}  {share:0}%";
            ModelList.Children.Add(Row(name, value));
        }
    }

    private void RenderProjects(WindowStats session)
    {
        if (!_cfg.ShowProjects || _cfg.Compact) return;

        var rows = session.ByProject.Where(kv => kv.Value.Tokens > 0)
                                    .OrderByDescending(kv => kv.Value.Tokens)
                                    .Take(Math.Max(1, _cfg.MaxProjectRows)).ToList();

        var sig = string.Join("|", rows.Select(r => $"{r.Key}:{r.Value.Tokens}"));
        if (sig == _projectSig && ProjectList.Children.Count > 0) return;
        _projectSig = sig;

        ProjectList.Children.Clear();
        if (rows.Count == 0)
        {
            ProjectList.Children.Add(Dim("no activity in this window"));
            return;
        }

        foreach (var (name, slice) in rows)
            ProjectList.Children.Add(Row(PrettyProject(name), $"{Tokens(slice.Tokens)}  {slice.Requests} req"));
    }

    /// <summary>Claude Code encodes the cwd into the folder name; show only the leaf.</summary>
    private static string PrettyProject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(unknown)";
        var parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var leaf = parts.Length > 0 ? parts[^1] : raw;
        return leaf.Length > 26 ? leaf[..26] + "…" : leaf;
    }

    private Grid Row(string left, string right)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var l = new TextBlock
        {
            Text = left,
            Style = (Style)FindResource("Val"),
            Foreground = Brush("FgDim"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var r = new TextBlock { Text = right, Style = (Style)FindResource("Val") };
        Grid.SetColumn(l, 0);
        Grid.SetColumn(r, 1);
        grid.Children.Add(l);
        grid.Children.Add(r);
        return grid;
    }

    private TextBlock Dim(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("Lbl"),
        Margin = new Thickness(0, 1, 0, 1)
    };

    private void ApplySectionVisibility()
    {
        Visibility V(bool on) => on && !_cfg.Compact ? Visibility.Visible : Visibility.Collapsed;

        BreakdownSection.Visibility = V(_cfg.ShowTokenBreakdown);
        RatesSection.Visibility     = V(_cfg.ShowRates);
        WeekSection.Visibility      = _cfg.ShowWeekly ? Visibility.Visible : Visibility.Collapsed;
        ModelSection.Visibility     = V(_cfg.ShowModels);
        ProjectSection.Visibility   = V(_cfg.ShowProjects);
        SourceSection.Visibility    = V(_cfg.ShowSources);
    }

    private void RenderSources()
    {
        if (!_cfg.ShowSources || _cfg.Compact) return;

        var sig = string.Join("|", AllSources().Select(s => $"{s.Name}:{s.Enabled}:{s.Healthy}:{s.Detail}"))
                + $"|{_adminApi?.OrgCost7d:0.00}";
        if (sig == _sourceSig && SourceList.Children.Count > 0) return;
        _sourceSig = sig;

        SourceList.Children.Clear();
        foreach (var s in AllSources())
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = !s.Enabled ? Brush("Stroke") : s.Healthy ? Brush("Accent") : Brush("Danger")
            };
            var name = new TextBlock { Text = s.Name, Style = (Style)FindResource("Lbl") };
            var detail = new TextBlock
            {
                Text = s.Detail,
                Style = (Style)FindResource("Lbl"),
                Foreground = Brush("FgDim")
            };

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(detail, 2);
            grid.Children.Add(dot);
            grid.Children.Add(name);
            grid.Children.Add(detail);
            SourceList.Children.Add(grid);
        }

        if (_cfg.EnableAdminApi && _adminApi is { } api && api.OrgCost7d > 0)
        {
            SourceList.Children.Add(new TextBlock
            {
                Text = $"org API billing (7d): ${api.OrgCost7d:0.00} · {Tokens(api.OrgTokens7d)} tok",
                Style = (Style)FindResource("Lbl"),
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void SetBar(ColumnDefinition fill, ColumnDefinition rest, Border bar, double? pct)
    {
        var p = Math.Clamp(pct ?? 0, 0, 100);
        fill.Width = new GridLength(p, GridUnitType.Star);
        rest.Width = new GridLength(100 - p, GridUnitType.Star);
        bar.Background = p >= 90 ? Brush("Danger") : p >= 75 ? Brush("Warn") : Brush("Accent");
    }

    private static double? Percent(long used, long budget) =>
        budget <= 0 ? null : used * 100.0 / budget;

    private static string Tokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.##}M" :
        n >= 1_000     ? $"{n / 1_000.0:0.#}k"      : n.ToString();

    private Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // ==================================================================
    //  settings panel
    // ==================================================================
    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        if (ConfigPanel.Visibility == Visibility.Visible)
        {
            ConfigPanel.Visibility = Visibility.Collapsed;
            return;
        }
        LoadConfigIntoFields();
        ConfigPanel.Visibility = Visibility.Visible;
    }

    private void LoadConfigIntoFields()
    {
        CbTranscripts.IsChecked = _cfg.EnableTranscripts;
        CbOtel.IsChecked        = _cfg.EnableOtel;
        CbUsageCmd.IsChecked    = _cfg.EnableUsageCmd;
        CbAdminApi.IsChecked    = _cfg.EnableAdminApi;
        CbNotify.IsChecked      = _cfg.NotificationsEnabled;
        CbSound.IsChecked       = _cfg.NotificationSound;
        CbTop.IsChecked         = _cfg.AlwaysOnTop;

        CbCompact.IsChecked     = _cfg.Compact;
        CbShowWeekly.IsChecked    = _cfg.ShowWeekly;
        CbShowBreakdown.IsChecked = _cfg.ShowTokenBreakdown;
        CbShowRates.IsChecked     = _cfg.ShowRates;
        CbShowCost.IsChecked      = _cfg.ShowCost;
        CbShowModels.IsChecked    = _cfg.ShowModels;
        CbShowProjects.IsChecked  = _cfg.ShowProjects;
        CbShowSources.IsChecked   = _cfg.ShowSources;
        CbShowUnused.IsChecked    = _cfg.ShowUnusedModels;

        TxtOtelPort.Text          = _cfg.OtelPort.ToString();
        TxtAdminKey.Password      = _cfg.AdminApiKey;
        TxtSessionBudget.Text     = _cfg.SessionTokenBudget.ToString();
        TxtWeeklyBudget.Text      = _cfg.WeeklyTokenBudget.ToString();
        TxtSessionThresholds.Text = string.Join(",", _cfg.SessionThresholds);
        TxtWeeklyThresholds.Text  = string.Join(",", _cfg.WeeklyThresholds);
        SldOpacity.Value          = _cfg.Opacity;

        var exe = _cfg.ResolveClaudeExe();
        ConfigHint.Text = string.IsNullOrEmpty(exe)
            ? "claude.exe not found — real plan % unavailable."
            : $"OTEL setup: set CLAUDE_CODE_ENABLE_TELEMETRY=1, " +
              $"OTEL_METRICS_EXPORTER=otlp, OTEL_EXPORTER_OTLP_PROTOCOL=http/json, " +
              $"OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:{_cfg.OtelPort}";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _cfg.EnableTranscripts    = CbTranscripts.IsChecked == true;
        _cfg.EnableOtel           = CbOtel.IsChecked == true;
        _cfg.EnableUsageCmd       = CbUsageCmd.IsChecked == true;
        _cfg.EnableAdminApi       = CbAdminApi.IsChecked == true;
        _cfg.NotificationsEnabled = CbNotify.IsChecked == true;
        _cfg.NotificationSound    = CbSound.IsChecked == true;
        _cfg.AlwaysOnTop          = CbTop.IsChecked == true;

        _cfg.Compact           = CbCompact.IsChecked == true;
        _cfg.ShowWeekly        = CbShowWeekly.IsChecked == true;
        _cfg.ShowTokenBreakdown = CbShowBreakdown.IsChecked == true;
        _cfg.ShowRates         = CbShowRates.IsChecked == true;
        _cfg.ShowCost          = CbShowCost.IsChecked == true;
        _cfg.ShowModels        = CbShowModels.IsChecked == true;
        _cfg.ShowProjects      = CbShowProjects.IsChecked == true;
        _cfg.ShowSources       = CbShowSources.IsChecked == true;
        _cfg.ShowUnusedModels  = CbShowUnused.IsChecked == true;

        if (int.TryParse(TxtOtelPort.Text, out var port) && port is > 0 and < 65536)
            _cfg.OtelPort = port;
        _cfg.AdminApiKey = TxtAdminKey.Password.Trim();

        if (long.TryParse(TxtSessionBudget.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var sb) && sb > 0)
            _cfg.SessionTokenBudget = sb;
        if (long.TryParse(TxtWeeklyBudget.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var wb) && wb > 0)
            _cfg.WeeklyTokenBudget = wb;

        _cfg.SessionThresholds = ParseThresholds(TxtSessionThresholds.Text, _cfg.SessionThresholds);
        _cfg.WeeklyThresholds  = ParseThresholds(TxtWeeklyThresholds.Text,  _cfg.WeeklyThresholds);
        _cfg.Opacity = SldOpacity.Value;

        _cfg.Save();
        _notifier.ResetFired();
        ApplyAppearance();
        _modelSig = _sourceSig = _projectSig = "";   // display toggles changed
        StartCollectors();
        Render();
        LoadConfigIntoFields();
    }

    private static List<int> ParseThresholds(string text, List<int> fallback)
    {
        var parsed = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => int.TryParse(p.Trim().TrimEnd('%'), out var v) ? v : -1)
                         .Where(v => v is > 0 and <= 100)
                         .Distinct().OrderBy(v => v).ToList();
        return parsed.Count > 0 ? parsed : fallback;
    }

    private void ApplyAppearance()
    {
        Opacity = Math.Clamp(_cfg.Opacity, 0.35, 1.0);
        Topmost = _cfg.AlwaysOnTop;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ConfigHint.Text = "refreshing…";
        if (_usageCmd != null) await _usageCmd.RefreshAsync();
        if (_adminApi != null) await _adminApi.RefreshAsync();
        Render();
        LoadConfigIntoFields();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open log failed", ex); }
    }

    // ==================================================================
    private void RestorePosition()
    {
        if (_cfg.Left is not { } savedLeft || _cfg.Top is not { } savedTop) return;

        // Only restore if the saved spot is still on a connected screen.
        var area = SystemParameters.VirtualScreenWidth > 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                       SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        if (area.Contains(new Point(savedLeft + 40, savedTop + 20)))
        {
            Left = savedLeft;
            Top = savedTop;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cfg.Left = Left;
        _cfg.Top = Top;
        _cfg.Save();
        _tick.Stop();
        StopCollectors();
        _store.SaveState(force: true);
        base.OnClosing(e);
    }
}
