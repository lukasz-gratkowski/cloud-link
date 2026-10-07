using System.Diagnostics;
using System.IO;
using System.Windows;
using CloudLink.Core;

namespace CloudLink;

public partial class SettingsWindow : Window
{
    readonly Settings _settings;

    public SettingsWindow(Settings settings, string? reason)
    {
        InitializeComponent();
        _settings = settings;
        if (reason is not null)
        {
            ReasonText.Text = reason;
            ReasonCard.Visibility = Visibility.Visible;
        }
        VersionText.Text = "CloudLink " + AppInfo.Display;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        GoogleApiKeyBox.Password = settings.GoogleApiKey;
        GoogleClientIdBox.Text = settings.GoogleClientId;
        GoogleClientSecretBox.Password = settings.GoogleClientSecret;
        MicrosoftClientIdBox.Text = settings.MicrosoftClientId;
        if (Settings.BuiltInMicrosoftClientId.Length > 0)
        {
            MicrosoftIntro.Text = "Works out of the box with a personal Microsoft account. A work or school account usually needs its " +
                "administrator to approve CloudLink once. Enter an application (client) ID here only if you want CloudLink to sign in " +
                "through your own Microsoft Entra app registration instead.";
        }
        else
        {
            MicrosoftIntro.Text = "This build has no Microsoft application ID built in. Paste the Application (client) ID of a Microsoft Entra " +
                "app registration that allows personal and work accounts and has the redirect address http://localhost. " +
                "Registering one is free; with only a personal Microsoft account you first need a free Azure account. The guide has the steps.";
            MicrosoftSetupButtons.Visibility = Visibility.Visible;
            MicrosoftCaption.Text = "APPLICATION (CLIENT) ID";
        }
        ParallelBox.ItemsSource = Enumerable.Range(1, 8).ToList();
        ParallelBox.SelectedItem = Math.Clamp(settings.ParallelDownloads, 1, 8);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        string microsoftId = MicrosoftClientIdBox.Text.Trim();
        if (microsoftId.Length > 0 && !Guid.TryParse(microsoftId, out _))
        {
            MicrosoftError.Text = "That is not an application ID. It looks like 00000000-0000-0000-0000-000000000000 " +
                "and is shown as \"Application (client) ID\" on the registration's Overview page.";
            MicrosoftError.Visibility = Visibility.Visible;
            MicrosoftClientIdBox.Focus();
            return;
        }
        _settings.GoogleApiKey = GoogleApiKeyBox.Password.Trim();
        _settings.GoogleClientId = GoogleClientIdBox.Text.Trim();
        _settings.GoogleClientSecret = GoogleClientSecretBox.Password.Trim();
        _settings.MicrosoftClientId = Settings.CanonicalId(microsoftId);
        _settings.ParallelDownloads = ParallelBox.SelectedItem is int n ? n : 4;
        _settings.Save();
        DialogResult = true;
    }

    // The same guide in both places; the Store version opens the copy on the product website.
    void OpenGuide_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPackage.IsPackaged
            ? "https://amgcloud.io/apps/cloudlink/guide/#google-drive"
            : "https://github.com/lukasz-gratkowski/cloud-link/blob/main/docs/USER-GUIDE.md#google-drive") { UseShellExecute = true });

    void OpenEntra_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade") { UseShellExecute = true });

    void OpenRegistrationGuide_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/lukasz-gratkowski/cloud-link/blob/main/docs/APP-REGISTRATION.md#creating-a-registration") { UseShellExecute = true });

    void OpenGoogleConsole_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/library/drive.googleapis.com") { UseShellExecute = true });
}
