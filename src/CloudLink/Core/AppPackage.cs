using System.Runtime.InteropServices;

namespace CloudLink.Core;

/// <summary>What changes when CloudLink runs from an installed package, as the Microsoft Store version does.</summary>
public static class AppPackage
{
    const int ErrorInsufficientBuffer = 122;   // a package exists; the plain exe gets 15700, APPMODEL_ERROR_NO_PACKAGE
    // KF_FLAG_RETURN_FILTER_REDIRECTION_TARGET | KF_FLAG_DONT_VERIFY: the real folder, whether or not it exists yet.
    const uint RedirectionTarget = 0x00040000 | 0x00004000;
    static readonly Guid LocalAppDataId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");   // FOLDERID_LocalAppData

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetCurrentPackageFamilyName(ref uint length, char[]? name);

    [DllImport("shell32.dll", ExactSpelling = true)]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The package family name, of the form Name_publisherid; null for the plain exe.</summary>
    public static string? FamilyName { get; } = ReadFamilyName();

    public static bool IsPackaged => FamilyName is not null;

    /// <summary>
    /// %LOCALAPPDATA% as other programs see it. Windows keeps what a packaged app writes there in a private
    /// folder that only the app sees under the usual name; this is that folder's real path. Using it keeps
    /// Notepad and File Explorer able to open CloudLink's files, and keeps the Store version out of the folder
    /// of a plain CloudLink.exe on the same PC.
    /// </summary>
    public static string LocalAppData { get; } = ReadLocalAppData();

    static string? ReadFamilyName()
    {
        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer) return null;
        var buffer = new char[length];
        return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    static string ReadLocalAppData()
    {
        string usual = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!IsPackaged) return usual;
        int hr = SHGetKnownFolderPath(in LocalAppDataId, RedirectionTarget, IntPtr.Zero, out IntPtr path);
        try { return hr == 0 && Marshal.PtrToStringUni(path) is { Length: > 0 } real ? real : usual; }
        finally { Marshal.FreeCoTaskMem(path); }
    }
}
