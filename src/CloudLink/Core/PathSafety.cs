using System.IO;
using System.Text;

namespace CloudLink.Core;

/// <summary>Turns cloud item names into names Windows will accept.</summary>
public static class PathSafety
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    const int MaxLength = 200;

    public static string SanitizeName(string? name)
    {
        var sb = new StringBuilder(name?.Length ?? 0);
        foreach (char c in name ?? "")
            sb.Append(c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c);

        string s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0) return "_";

        int dot = s.IndexOf('.');
        if (Reserved.Contains(dot < 0 ? s : s[..dot])) s = "_" + s;

        if (s.Length > MaxLength)
        {
            string ext = Path.GetExtension(s);
            if (ext.Length > 20) ext = "";
            s = s[..(MaxLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return s;
    }

    public const string PartSuffix = ".part";

    /// <summary>
    /// Like <see cref="Unique"/>, and also claims "name.part", the temporary name the download uses,
    /// so that no other item in the folder can be given it.
    /// </summary>
    public static string UniqueFile(HashSet<string> taken, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? name : $"{stem} ({i}){ext}";
            string key = candidate.ToUpperInvariant(), partKey = (candidate + PartSuffix).ToUpperInvariant();
            if (taken.Contains(key) || taken.Contains(partKey)) continue;
            taken.Add(key);
            taken.Add(partKey);
            return candidate;
        }
    }

    /// <summary>Returns <paramref name="name"/>, or "name (2).ext" and so on if the folder already has it.</summary>
    public static string Unique(HashSet<string> taken, string name)
    {
        if (taken.Add(name.ToUpperInvariant())) return name;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (taken.Add(candidate.ToUpperInvariant())) return candidate;
        }
    }
}
