using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CloudLink.Core;

namespace CloudLink;

public partial class HelpWindow : Window
{
    // Icon glyph, title, text. Kept short on purpose: one idea per card.
    static readonly (string Glyph, string Title, string Text)[] Sections =
    [
        ("", "Share links",
            "A share link is the address someone sends you for a folder or file in OneDrive or Google Drive. " +
            "Paste it into the box at the top, one link per line. A whole folder is downloaded with all its subfolders, " +
            "into a folder of the same name inside the place you chose under Save to."),
        ("", "Signing in",
            "The services only hand out files to a signed-in account, so the first download opens your browser to sign in. " +
            "CloudLink never sees your password, and it stays signed in afterwards. The buttons at the top right show which " +
            "accounts are connected; click one to sign out. Google Drive needs a one-time setup first, explained in Settings."),
        ("", "Stopping and continuing",
            "You can press Stop, close the program, lose the connection or restart the PC at any point. Unfinished files are kept " +
            "with a .part ending. Press Download again with the same link and folder and CloudLink carries on from where it stopped, " +
            "skipping everything that is already complete."),
        ("", "How you know the files are right",
            "Every file is compared with the size and checksum (a fingerprint of the content) that the service reports. " +
            "A file that does not match is downloaded again. Only a complete, checked file gets its real name, " +
            "so anything without the .part ending can be trusted."),
        ("", "When something goes wrong",
            "Connection drops, busy servers and stalls are retried automatically, with longer pauses each time, and CloudLink waits " +
            "if the network goes away. A file marked Failed shows the reason; press Try again and only the failed ones are fetched. " +
            "Skipped means the item has no file content, for example a Google Form."),
        ("", "Safety and privacy",
            "CloudLink only downloads: it never uploads, changes or deletes files in the cloud, and never runs what it downloads. " +
            "Opening a OneDrive link records your account as having opened it, just as opening it in a browser does. " +
            "Files are tagged as coming from the internet, so Windows and Office apply their usual caution when you open them. " +
            "Your sign-ins are stored encrypted for your Windows account in " + AppPaths.Shown + " and nowhere else."),
    ];

    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = "CloudLink " + AppInfo.Display;
        VersionText.ToolTip = VersionText.Text;
        StackPanel? last = null;
        foreach (var (glyph, title, text) in Sections)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
                FontSize = 20,
                Width = 36,
                Margin = new Thickness(0, 2, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            words.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, LineHeight = 20, Margin = new Thickness(0, 4, 0, 0) });
            last = words;
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(words);
            Topics.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 10),
                Child = row,
            });
        }

        // The last card names the data folder. In the Store version that is a long address, so it can be opened from here.
        var open = new Hyperlink(new Run("Open that folder"));
        open.SetResourceReference(TextElement.ForegroundProperty, "ActiveBrush");
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.Root);
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { AppPaths.Root } });
        };
        last!.Children.Add(new TextBlock(open) { Margin = new Thickness(0, 6, 0, 0) });
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Log.FilePath)) Log.Write("Log opened from Help.");
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { ArgumentList = { Log.FilePath } });
    }

    void OpenPrivacy_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://amgcloud.io/apps/cloudlink/privacy/") { UseShellExecute = true });
}
