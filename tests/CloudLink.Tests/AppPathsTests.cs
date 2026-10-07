using CloudLink.Core;

namespace CloudLink.Tests;

/// <summary>The data folder's name and the AppData check. Nothing here changes AppPaths.Root, which other tests use.</summary>
public sealed class AppPathsTests
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string AppData = Path.GetDirectoryName(Local)!;

    [Fact]
    public void A_test_run_has_no_package_and_the_usual_folder()
    {
        Assert.False(AppPackage.IsPackaged);
        Assert.Null(AppPackage.FamilyName);
        Assert.Equal(Local, AppPackage.LocalAppData);
    }

    [Fact]
    public void A_folder_below_local_app_data_is_shown_with_the_variable()
    {
        Assert.Equal(@"%LOCALAPPDATA%\CloudLink", AppPaths.Abbreviate(Path.Combine(Local, "CloudLink")));
        Assert.Equal(@"%LOCALAPPDATA%\Packages\Some.App_abc\LocalCache\Local\CloudLink",
            AppPaths.Abbreviate(Path.Combine(Local, @"Packages\Some.App_abc\LocalCache\Local\CloudLink")));
        Assert.Equal(@"%LOCALAPPDATA%\CloudLink", AppPaths.Abbreviate(Path.Combine(Local.ToUpperInvariant(), "CloudLink")));
    }

    [Fact]
    public void Other_folders_are_shown_as_they_are()
    {
        Assert.Equal(@"D:\Data\CloudLink", AppPaths.Abbreviate(@"D:\Data\CloudLink"));
        // A neighbour whose name merely starts the same way is not below Local AppData.
        Assert.Equal(Local + @"Stuff\CloudLink", AppPaths.Abbreviate(Local + @"Stuff\CloudLink"));
    }

    [Fact]
    public void Folders_under_app_data_are_recognised()
    {
        Assert.True(AppPaths.IsUnderAppData(AppData));
        Assert.True(AppPaths.IsUnderAppData(AppData + @"\"));
        Assert.True(AppPaths.IsUnderAppData(Path.Combine(Local, "Test")));
        Assert.True(AppPaths.IsUnderAppData(Path.Combine(AppData, @"Roaming\Test")));
        Assert.True(AppPaths.IsUnderAppData(Path.Combine(Local, "Test").ToUpperInvariant()));
        Assert.True(AppPaths.IsUnderAppData(@"\\?\" + Path.Combine(Local, "Test")));
    }

    [Fact]
    public void Folders_elsewhere_are_not()
    {
        Assert.False(AppPaths.IsUnderAppData(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(AppPaths.IsUnderAppData(AppData + "Backup"));
        Assert.False(AppPaths.IsUnderAppData(@"D:\AppData\Local\Test"));
        Assert.False(AppPaths.IsUnderAppData(@"\\server\share\AppData\Local"));
        Assert.False(AppPaths.IsUnderAppData(@"C:\"));
    }
}
