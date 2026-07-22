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

    private readonly AccountWatcher _account = new();
    private List<string>? _usageChats;

    private UpdateInfo? _update;
    private bool _updateBusy;
    // Not at t=0: the first seconds belong to the backfill, not to a network round-trip.
    private DateTimeOffset _nextUpdateCheck = DateTimeOffset.Now.AddSeconds(20);

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

        // Not "someone else is using your account" — Anthropic exposes no device list — but
        // a switch here does mean the numbers now describe a different account.
        _account.Switched += (was, now) => ToastWindow.Show(
            "Claude account changed",
            $"Signed in as {now.Label} ({now.Plan}). Was {was?.Label ?? "someone else"}.",
            ToastWindow.Severity.Warn, _cfg.NotificationSound);
        _account.Poll();

        _store.Changed += () => Dispatcher.BeginInvoke(Render);
        _tick.Tick += (_, _) =>
        {
            _account.Poll();         // self-throttled to every 30 s
            Render();
            _store.SaveState();      // self-throttled to every 30 s
            MaybeAutoCheckUpdate();
        };
        _tick.Start();

        // If the app was moved or reinstalled, repoint the launch-on-login entry.
        StartupManager.RefreshIfStale();

        VersionText.Text = Updater.Display;
        Updater.CleanupPrevious();   // clear the build we replaced, and any part-download

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

            // A budget estimate is NOT a stand-in for a plan percentage. They measure
            // different things and diverge wildly — a fresh 5 h window read 14% of budget
            // while the plan was at 82%. So once a real reading has landed, keep showing
            // it (labelled stale if old) rather than swapping in an unrelated number, and
            // only fall back to budget when /usage is off or has never worked.
            var haveReading = plan is { HasReading: true };
            var stale = haveReading && !plan!.IsFresh;

            double? sessionPct = haveReading ? plan!.SessionPercent : null;
            double? weekPct    = haveReading ? plan!.WeeklyPercent  : null;
            double? fablePct   = haveReading ? plan!.FableWeeklyPercent : null;

            var sessionIsPlan = sessionPct.HasValue;
            var weekIsPlan    = weekPct.HasValue;

            // "waiting" only while /usage is enabled and still expected to answer.
            var waiting = _cfg.EnableUsageCmd && !haveReading &&
                          DateTimeOffset.Now - _startedAt < TimeSpan.FromMinutes(2);

            if (!sessionIsPlan && !waiting) sessionPct = Percent(session.TotalTokens, _cfg.SessionTokenBudget);
            if (!weekIsPlan    && !waiting) weekPct    = Percent(week.TotalTokens,    _cfg.WeeklyTokenBudget);

            var isReal = sessionIsPlan || weekIsPlan;

            // When there is no plan reading, say *why* on the label itself. Silently showing
            // budget numbers is how the overlay ends up contradicting the Claude app.
            var why = !_cfg.EnableUsageCmd ? "budget"
                    : waiting ? "reading…"
                    : _stUsageCmd.Detail is "claude.exe not found" ? "no Claude Code — see ⚙"
                    : _stUsageCmd.Detail is "not signed in" ? "not signed in — see ⚙"
                    : !_stUsageCmd.Healthy ? "budget · /usage failed"
                    : "budget";

            SessionLabel.Text = sessionIsPlan
                ? (stale ? "SESSION · 5h · plan (stale)" : "SESSION · 5h · plan")
                : $"SESSION · 5h · {why}";
            WeekLabel.Text = weekIsPlan
                ? (stale ? "WEEK · plan (stale)" : "WEEK · plan")
                : $"WEEK · {why}";

            SetBar(SessionFill, SessionRest, SessionBar, sessionPct);
            SetBar(WeekFill, WeekRest, WeekBar, weekPct);

            // Anthropic prints the real reset moment; only derive one when it doesn't.
            SessionReset.Text = ResetLabel(plan?.SessionResetAt, plan?.SessionResetText, session);

            SessionDetail.Text = Describe(session, sessionPct);
            WeekPct.Text = weekPct is { } wp
                ? $"{wp:0}%{(plan?.WeeklyResetAt is { } wr ? $" · {Until(wr)}" : "")}"
                : "";
            WeekDetail.Text = Describe(week, null);
            RenderFable(fablePct, plan);

            RenderBreakdown(session);
            RenderRates(session, sessionPct);
            RenderModels(session, week);
            RenderProjects(session);
            RenderSources();
            RenderAccount();
            ApplySectionVisibility();

            // Live dot pulses green when something billed in the last two minutes.
            var recent = _store.LastActivity is { } last &&
                         (DateTimeOffset.Now - last) < TimeSpan.FromMinutes(2);
            LiveDot.Fill = recent ? Brush("Accent") : Brush("FgDim");

            // Don't alert off the budget fallback while a real-percentage source is still
            // starting up — /usage takes a few seconds, and firing early produces bogus
            // alerts ("weekly at 3499%") that a moment later resolve to the true figure.
            if (!waiting)
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

    /// <summary>Prefers Anthropic's own reset moment over the one derived from the ledger.</summary>
    private static string ResetLabel(DateTimeOffset? at, string? text, WindowStats session)
    {
        if (at is { } t && t > DateTimeOffset.Now) return $"resets in {Until(t)}";
        if (!string.IsNullOrWhiteSpace(text)) return $"resets {text}";
        if (session.Remaining is { } r) return $"resets in {NotificationEngine.Fmt(r)}";
        return session.Requests == 0 ? "idle" : "";
    }

    private static string Until(DateTimeOffset t)
    {
        var d = t - DateTimeOffset.Now;
        if (d <= TimeSpan.Zero) return "now";
        return d.TotalDays >= 1
            ? $"{(int)d.TotalDays}d {d.Hours}h"
            : NotificationEngine.Fmt(d);
    }

    /// <summary>
    /// Fable draws on its own weekly allowance rather than the shared pool, so it gets its
    /// own bar — but only when /usage actually reports one.
    /// </summary>
    private void RenderFable(double? pct, PlanUsage? plan)
    {
        if (!_cfg.ShowFable || _cfg.Compact || pct is null) return;

        SetBar(FableFill, FableRest, FableBar, pct);
        FablePct.Text = $"{pct:0}%";
        FableLabel.Text = plan?.WeeklyResetAt is { } r && r > DateTimeOffset.Now
            ? $"FABLE · week · resets in {Until(r)}"
            : "FABLE · week";
    }

    private void RenderAccount()
    {
        if (!_cfg.ShowAccount) return;

        var a = _account.Current;
        if (a is null)
        {
            AccountName.Text = "";
            PlanPill.Visibility = Visibility.Collapsed;
            return;
        }

        // The email is opt-in: this window sits on top of everything, screen shares included.
        AccountName.Text = _cfg.ShowAccountEmail && !string.IsNullOrWhiteSpace(a.Email)
            ? $"{a.Label} · {a.Email}"
            : a.Label;

        AccountPlan.Text = a.Plan;
        PlanPill.Visibility = string.IsNullOrWhiteSpace(a.Plan) ? Visibility.Collapsed : Visibility.Visible;

        var tip = string.Join("\n", new[]
        {
            string.IsNullOrWhiteSpace(a.Email) ? null : a.Email,
            string.IsNullOrWhiteSpace(a.Organization) ? null : a.Organization,
            string.IsNullOrWhiteSpace(a.Plan) ? null : $"Plan: {a.Plan}",
            a.ExtraUsage ? "Extra usage enabled" : null,
            "Signed in on this machine — Anthropic exposes no list of other devices."
        }.Where(x => x is not null));
        AccountRow.ToolTip = tip;
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
        // Only ever shown when /usage actually reports a Fable allowance.
        FableSection.Visibility     = V(_cfg.ShowFable) == Visibility.Visible &&
                                      _usageCmd?.Plan.FableWeeklyPercent is not null
                                      ? Visibility.Visible : Visibility.Collapsed;
        AccountRow.Visibility       = _cfg.ShowAccount && _account.Current is not null
                                      ? Visibility.Visible : Visibility.Collapsed;
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
        // Read the live registry state rather than a cached setting, so the box always
        // reflects what Windows will actually do.
        CbStartup.IsChecked     = StartupManager.IsEnabled();
        CbAutoUpdate.IsChecked  = _cfg.AutoCheckUpdates;
        RefreshUpdateHint();

        CbCompact.IsChecked     = _cfg.Compact;
        CbShowWeekly.IsChecked    = _cfg.ShowWeekly;
        CbShowBreakdown.IsChecked = _cfg.ShowTokenBreakdown;
        CbShowRates.IsChecked     = _cfg.ShowRates;
        CbShowCost.IsChecked      = _cfg.ShowCost;
        CbShowModels.IsChecked    = _cfg.ShowModels;
        CbShowProjects.IsChecked  = _cfg.ShowProjects;
        CbShowSources.IsChecked   = _cfg.ShowSources;
        CbShowUnused.IsChecked    = _cfg.ShowUnusedModels;
        CbShowFable.IsChecked     = _cfg.ShowFable;
        CbShowAccount.IsChecked   = _cfg.ShowAccount;
        CbShowEmail.IsChecked     = _cfg.ShowAccountEmail;

        AccountHint.Text = _account.Current is { } acct
            ? $"{acct.Email} · {acct.Plan}{(acct.ExtraUsage ? " · extra usage on" : "")}. " +
              "Read from this machine's Claude Code sign-in; there is no API listing other devices."
            : "No signed-in account found in ~/.claude.json.";

        TxtOtelPort.Text          = _cfg.OtelPort.ToString();
        TxtAdminKey.Password      = _cfg.AdminApiKey;
        TxtSessionBudget.Text     = _cfg.SessionTokenBudget.ToString();
        TxtWeeklyBudget.Text      = _cfg.WeeklyTokenBudget.ToString();
        TxtSessionThresholds.Text = string.Join(",", _cfg.SessionThresholds);
        TxtWeeklyThresholds.Text  = string.Join(",", _cfg.WeeklyThresholds);
        SldOpacity.Value          = _cfg.Opacity;

        RefreshClaudeStatus();

        ConfigHint.Text = "OTEL setup: set CLAUDE_CODE_ENABLE_TELEMETRY=1, " +
                          "OTEL_METRICS_EXPORTER=otlp, OTEL_EXPORTER_OTLP_PROTOCOL=http/json, " +
                          $"OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:{_cfg.OtelPort}";

        // Walks the whole transcript tree, so keep it off the UI thread.
        if (_usageChats is null)
        {
            Task.Run(UsageChats.Find).ContinueWith(t =>
            {
                _usageChats = t.Result;
                Dispatcher.BeginInvoke(() => RefreshClaudeStatus());
            }, TaskScheduler.Default);
        }
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
        StartupManager.Set(CbStartup.IsChecked == true);
        _cfg.AutoCheckUpdates     = CbAutoUpdate.IsChecked == true;

        _cfg.Compact           = CbCompact.IsChecked == true;
        _cfg.ShowWeekly        = CbShowWeekly.IsChecked == true;
        _cfg.ShowTokenBreakdown = CbShowBreakdown.IsChecked == true;
        _cfg.ShowRates         = CbShowRates.IsChecked == true;
        _cfg.ShowCost          = CbShowCost.IsChecked == true;
        _cfg.ShowModels        = CbShowModels.IsChecked == true;
        _cfg.ShowProjects      = CbShowProjects.IsChecked == true;
        _cfg.ShowSources       = CbShowSources.IsChecked == true;
        _cfg.ShowUnusedModels  = CbShowUnused.IsChecked == true;
        _cfg.ShowFable         = CbShowFable.IsChecked == true;
        _cfg.ShowAccount       = CbShowAccount.IsChecked == true;
        _cfg.ShowAccountEmail  = CbShowEmail.IsChecked == true;

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
    //  updates
    // ==================================================================
    private void MaybeAutoCheckUpdate()
    {
        if (!_cfg.AutoCheckUpdates || _updateBusy) return;
        if (_update != null) return;                       // already found one; nothing to re-poll for
        if (DateTimeOffset.Now < _nextUpdateCheck) return;

        _nextUpdateCheck = DateTimeOffset.Now.AddHours(Math.Max(1, _cfg.UpdateCheckIntervalHours));
        _ = CheckForUpdateAsync(announce: false);
    }

    /// <param name="announce">Report "up to date" / failures. Background polls stay silent.</param>
    private async Task CheckForUpdateAsync(bool announce)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        if (announce) SetUpdateStatus("checking…", "FgDim");

        try
        {
            var result = await Updater.CheckAsync();
            _cfg.LastUpdateCheck = DateTimeOffset.Now;
            _cfg.Save();

            if (result is { Available: true, Info: { } info })
            {
                _update = info;
                BtnUpdate.Content = $"Update to {info.Display}";
                BtnUpdate.Visibility = Visibility.Visible;
                SetUpdateStatus($"{info.Display} available", "Accent");
                Log.Info($"update {info.Tag} offered");
            }
            else if (announce)
            {
                SetUpdateStatus(result.Status, "FgDim");
            }

            if (ConfigPanel.Visibility == Visibility.Visible) RefreshUpdateHint();
        }
        finally { _updateBusy = false; }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        _nextUpdateCheck = DateTimeOffset.Now.AddHours(Math.Max(1, _cfg.UpdateCheckIntervalHours));
        await CheckForUpdateAsync(announce: true);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } info || _updateBusy) return;

        if (!Updater.CanSelfUpdate(out var why))
        {
            SetUpdateStatus(why, "Warn");
            return;
        }

        _updateBusy = true;
        BtnUpdate.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => SetUpdateStatus($"downloading {p:0}%", "FgDim"));
            var staged = await Updater.DownloadAsync(info, progress);

            SetUpdateStatus("restarting…", "Accent");

            // Hand over cleanly: the successor inherits this position and ledger.
            _cfg.Left = Left;
            _cfg.Top = Top;
            _cfg.Save();
            _tick.Stop();
            StopCollectors();
            _store.SaveState(force: true);

            Updater.ApplyAndRestart(staged);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("update failed", ex);
            SetUpdateStatus("update failed — see log", "Danger");
            // The swap rolls itself back on failure, so this build is still intact: resume.
            _tick.Start();
            StartCollectors();
        }
        finally
        {
            _updateBusy = false;
            BtnUpdate.IsEnabled = true;
        }
    }

    private void SetUpdateStatus(string text, string brushKey)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Foreground = Brush(brushKey);
    }

    private void RefreshUpdateHint()
    {
        var last = _cfg.LastUpdateCheck is { } t
            ? $"last checked {t.LocalDateTime:d MMM HH:mm}"
            : "not checked yet";
        UpdateHint.Text = _update is { } u
            ? $"{u.Display} is available — {Updater.RepoUrl}/releases"
            : $"{Updater.Display} · {last} · {Updater.RepoUrl}";
    }

    // ==================================================================
    //  Claude Code setup
    // ==================================================================
    private void RefreshClaudeStatus(string? testResult = null)
    {
        // Cached deliberately: a forced re-probe here would run on every settings repaint.
        var exe = ClaudeLocator.Resolve(_cfg);
        TxtClaudePath.Text = _cfg.ClaudeExePath;

        if (testResult is not null)
        {
            ClaudeStatus.Text = testResult;
            ClaudeStatus.Foreground = Brush(testResult.StartsWith("Connected") ? "Accent" : "Warn");
        }
        else if (string.IsNullOrEmpty(exe))
        {
            // Transcripts prove Claude Code runs here, which turns "install it" (wrong and
            // annoying) into "it is installed, I just cannot see where" (true and actionable).
            var transcripts = ClaudeLocator.TranscriptCount();
            ClaudeStatus.Text = transcripts > 0
                ? $"Claude Code has run on this PC ({transcripts} transcripts) but its program file " +
                  "wasn't found. Press Search my PC, or use Find… if you know where it is."
                : "Not found — real plan percentages are unavailable.";
            ClaudeStatus.Foreground = Brush("Warn");
        }
        else
        {
            ClaudeStatus.Text = $"Found: {exe}";
            ClaudeStatus.Foreground = Brush("FgDim");
        }

        ClaudeSteps.Text = string.IsNullOrEmpty(exe)
            ? "Nothing is logged into here — Session Limit asks your own Claude Code, which is " +
              "already signed in. To find it yourself, open a terminal and run:\n" +
              "    where claude\n" +
              "then paste the path above and press Test. If that prints nothing, Claude Code " +
              "isn't on PATH — Search my PC will look for it.\n" +
              "Looked in: " + string.Join("; ", ClaudeLocator.LastSearched.Take(5)) + " …"
            : "Nothing is logged into here — Session Limit asks your own Claude Code for the " +
              "numbers, so it needs to be installed and signed in. Press Test to check.";

        var junk = _usageChats?.Count ?? 0;
        BtnClearUsageChats.Content = junk > 0 ? $"Clear {junk} /usage chats" : "Clear /usage chats";
        BtnClearUsageChats.IsEnabled = junk > 0;
        UsageChatsHint.Text = junk > 0
            ? $"{junk} saved conversation(s) left by older builds checking your usage. " +
              "Current builds no longer create them."
            : "No leftover /usage conversations.";
    }

    private async void SearchClaude_Click(object sender, RoutedEventArgs e)
    {
        BtnSearchPc.IsEnabled = false;
        ClaudeStatus.Text = "searching… (up to 30s)";
        ClaudeStatus.Foreground = Brush("FgDim");
        try
        {
            var hit = await Task.Run(() => ClaudeLocator.DeepSearch(TimeSpan.FromSeconds(30)));
            if (string.IsNullOrEmpty(hit))
            {
                RefreshClaudeStatus("Nothing found. Run `where claude` in a terminal and paste the " +
                                    "path above — if that prints nothing either, Claude Code isn't " +
                                    "installed for this Windows user (a WSL install won't be visible).");
                return;
            }

            _cfg.ClaudeExePath = hit;
            _cfg.Save();
            ClaudeLocator.Invalidate();
            var result = await UsageCommandCollector.DiagnoseAsync(_cfg);
            RefreshClaudeStatus(result);
            if (result.StartsWith("Connected")) StartCollectors();
        }
        finally { BtnSearchPc.IsEnabled = true; }
    }

    private void FindClaude_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate claude.exe",
            Filter = "Claude Code (claude.exe;claude.cmd;claude.bat)|claude.exe;claude.cmd;claude.bat|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        _cfg.ClaudeExePath = dlg.FileName;
        _cfg.Save();
        ClaudeLocator.Invalidate();
        RefreshClaudeStatus();
        StartCollectors();
    }

    private async void TestClaude_Click(object sender, RoutedEventArgs e)
    {
        // Take the typed path first, so Test checks what is on screen rather than what was saved.
        var typed = TxtClaudePath.Text.Trim();
        if (typed != _cfg.ClaudeExePath)
        {
            _cfg.ClaudeExePath = typed;
            _cfg.Save();
            ClaudeLocator.Invalidate();
        }

        ClaudeStatus.Text = "testing…";
        ClaudeStatus.Foreground = Brush("FgDim");
        var result = await UsageCommandCollector.DiagnoseAsync(_cfg);
        RefreshClaudeStatus(result);

        if (result.StartsWith("Connected")) StartCollectors();
    }

    /// <summary>Opens a terminal running Claude Code, which is where signing in happens.</summary>
    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var exe = _cfg.ResolveClaudeExe(out _);
        if (string.IsNullOrEmpty(exe))
        {
            RefreshClaudeStatus("Install Claude Code first, then sign in.");
            return;
        }
        try
        {
            // /k keeps the window open so the login prompt is usable and its output readable.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\" /login\"")
            { UseShellExecute = true });
            RefreshClaudeStatus("A terminal opened — sign in there, then press Test.");
        }
        catch (Exception ex)
        {
            Log.Error("sign-in launch failed", ex);
            RefreshClaudeStatus("Could not open a terminal. Run `claude /login` yourself, then press Test.");
        }
    }

    private void GetClaude_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://claude.com/product/claude-code")
            { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open claude code page failed", ex); }
    }

    private void ClearUsageChats_Click(object sender, RoutedEventArgs e)
    {
        var files = _usageChats ?? new List<string>();
        if (files.Count == 0) return;

        // Deleting files out of someone's Claude Code history warrants asking, even when
        // this app is what created them.
        var answer = MessageBox.Show(this,
            $"Delete {files.Count} saved \"/usage\" conversation(s)?\n\n" +
            "These were left behind by older versions of Session Limit checking your usage. " +
            "Each contains only the /usage command and no replies. Real conversations are not touched.",
            "Clear /usage chats", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var (deleted, bytes) = UsageChats.Delete(files);
        _usageChats = UsageChats.Find();
        RefreshClaudeStatus();
        UsageChatsHint.Text = $"Removed {deleted} conversation(s), {bytes / 1024} KB.";
    }

    private void Watermark_Click(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/SuperCatCraze") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open profile failed", ex); }
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
