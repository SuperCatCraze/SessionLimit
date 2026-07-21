using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SessionLimit;

public partial class ToastWindow : Window
{
    private static readonly List<ToastWindow> Open = new();
    private const double Gap = 4;

    private readonly DispatcherTimer _life = new();

    public ToastWindow(string title, string body, Severity severity)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        AccentBar.Background = severity switch
        {
            Severity.Danger => (Brush)Application.Current.Resources["Danger"],
            Severity.Warn   => (Brush)Application.Current.Resources["Warn"],
            _               => (Brush)Application.Current.Resources["Accent"]
        };

        Loaded += OnLoaded;
        _life.Interval = TimeSpan.FromSeconds(9);
        _life.Tick += (_, _) => Dismiss();
    }

    public enum Severity { Info, Warn, Danger }

    public static void Show(string title, string body, Severity severity, bool sound)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                var t = new ToastWindow(title, body, severity);
                t.Show();
                if (sound) System.Media.SystemSounds.Exclamation.Play();
            }
            catch (Exception ex) { Log.Error("toast failed", ex); }
        });
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Open.Add(this);
        Reflow();

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;

        var slide = new DoubleAnimation(Left + 40, Left, TimeSpan.FromMilliseconds(220))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(LeftProperty, slide);
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        _life.Start();
    }

    /// <summary>Stacks open toasts upward from the bottom-right corner.</summary>
    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        var y = area.Bottom - 12;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            y -= t.ActualHeight > 0 ? t.ActualHeight : 90;
            t.Top = y;
            y -= Gap;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void Dismiss()
    {
        _life.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            Open.Remove(this);
            Reflow();
            try { Close(); } catch { /* already closing */ }
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
