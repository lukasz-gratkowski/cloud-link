using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CloudLink;

/// <summary>
/// The brand's theme-dependent colours. Windows' own controls follow the system theme through
/// ThemeMode; this keeps CloudLink's surfaces, text and status colours in step with it.
/// </summary>
public static class Theme
{
    const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark => Equals(Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1), 0);

    /// <summary>False when the user has turned animations off in Windows, or over a remote session.</summary>
    public static bool Motion => !Still && SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;

    /// <summary>Set for documentation pictures (--demo): nothing fades in or moves, so a capture shows the final state.</summary>
    public static bool Still { get; set; }

    /// <summary>Set by the --light and --dark development flags; otherwise Windows decides.</summary>
    public static bool? Forced { get; set; }

    public static void Apply()
    {
        if (SystemParameters.HighContrast && Forced is null)
        {
            var r = Application.Current.Resources;
            r["TextBrush"] = SystemColors.WindowTextBrush;
            r["CardBrush"] = SystemColors.WindowBrush;
            r["CardStrokeBrush"] = SystemColors.WindowTextBrush;
            r["TrackBrush"] = SystemColors.GrayTextBrush;
            r["HoverBrush"] = SystemColors.HighlightBrush;
            r["OkBrush"] = r["WarnBrush"] = r["ErrorBrush"] = r["ActiveBrush"] = SystemColors.HighlightBrush;
            r["IdleBrush"] = SystemColors.GrayTextBrush;
            r["AuroraOpacity"] = 0.0;
            return;
        }
        bool dark = Forced ?? IsDark;
        Set("TextBrush", dark ? "#F5FFFFFF" : "#E6101828");
        Set("CardBrush", dark ? "#12FFFFFF" : "#D9FFFFFF");
        Set("CardStrokeBrush", dark ? "#1FFFFFFF" : "#17000000");
        Set("TrackBrush", dark ? "#24FFFFFF" : "#1C000000");
        Set("HoverBrush", dark ? "#14FFFFFF" : "#0F000000");
        Set("OkBrush", dark ? "#3DD68C" : "#1A9F53");
        Set("WarnBrush", dark ? "#FFB84D" : "#B86E00");
        Set("ErrorBrush", dark ? "#FF6B6B" : "#D13438");
        Set("ActiveBrush", dark ? "#6AA5FF" : "#2E6BE6");
        Set("IdleBrush", dark ? "#7F8794" : "#8A8F98");
        Application.Current.Resources["AuroraOpacity"] = dark ? 0.38 : 0.24;
    }

    public static void Watch()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    static void Set(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}
