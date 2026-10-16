// SemVer 2.0.0 parse / compare helper (F:\tool common rule 4-2). .NET 6+ / C# 10+, no packages.
//
//   var latest = SemVer.TagToVersion(release.TagName);   // "v1.2.3" -> "1.2.3", invalid -> null
//   if (latest != null && SemVer.IsNewer(latest, CurrentVersion)) ShowUpdateDialog(latest);
//
// Do NOT use System.Version for update checks: it does not understand prerelease tags
// and orders "1.0.0-rc.1" incorrectly.
//
// Notes on the regex: .NET "\d" matches all Unicode digits and "$" matches before a trailing
// "\n", so this port uses [0-9] and \z to keep exactly the SemVer 2.0.0 grammar.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KuroMon.Services;

public sealed class SemVer : IComparable<SemVer>
{
    public static readonly Regex Pattern = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)" +
        @"(?:-((?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
        @"(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?\z",
        RegexOptions.CultureInvariant);

    // Numeric parts are kept as canonical digit strings so very large numbers compare exactly.
    public string Major { get; }
    public string Minor { get; }
    public string Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public IReadOnlyList<string> Build { get; }

    private SemVer(string major, string minor, string patch, string[] pre, string[] build)
    {
        Major = major; Minor = minor; Patch = patch; Prerelease = pre; Build = build;
    }

    /// <summary>Parse a version string without the "v" prefix. Returns null when invalid.</summary>
    public static SemVer? Parse(string? text)
    {
        if (text is null) return null;
        var m = Pattern.Match(text);
        if (!m.Success) return null;
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : Array.Empty<string>();
        var build = m.Groups[5].Success ? m.Groups[5].Value.Split('.') : Array.Empty<string>();
        return new SemVer(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, pre, build);
    }

    public static bool IsValid(string? text) => Parse(text) is not null;

    /// <summary>"v1.2.3" -> "1.2.3". Only one lowercase "v" is stripped. Invalid -> null (ignore that tag).</summary>
    public static string? TagToVersion(string? tag)
    {
        if (tag is null) return null;
        var s = tag.StartsWith('v') ? tag.Substring(1) : tag;
        return IsValid(s) ? s : null;
    }

    /// <summary>-1 / 0 / 1. Throws ArgumentException for invalid input. Build metadata is ignored.</summary>
    public static int Compare(string a, string b)
    {
        var x = Parse(a) ?? throw new ArgumentException($"invalid SemVer: {a}", nameof(a));
        var y = Parse(b) ?? throw new ArgumentException($"invalid SemVer: {b}", nameof(b));
        return x.CompareTo(y);
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        int c = CompareNumber(Major, other.Major);
        if (c != 0) return c;
        c = CompareNumber(Minor, other.Minor);
        if (c != 0) return c;
        c = CompareNumber(Patch, other.Patch);
        if (c != 0) return c;

        bool xr = Prerelease.Count == 0, yr = other.Prerelease.Count == 0;
        if (xr && yr) return 0;
        if (xr) return 1;   // release > prerelease
        if (yr) return -1;

        int n = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (int i = 0; i < n; i++)
        {
            c = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
            if (c != 0) return c;
        }
        return Math.Sign(Prerelease.Count - other.Prerelease.Count);
    }

    private static bool IsNumeric(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s) if (ch < '0' || ch > '9') return false;
        return true;
    }

    private static int CompareNumber(string a, string b)
    {
        if (a.Length != b.Length) return a.Length > b.Length ? 1 : -1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool an = IsNumeric(a), bn = IsNumeric(b);
        if (an && bn) return CompareNumber(a, b);
        if (an) return -1;  // numeric identifiers are always lower
        if (bn) return 1;
        return Math.Sign(string.CompareOrdinal(a, b));  // ASCII order
    }

    public override string ToString()
    {
        var s = $"{Major}.{Minor}.{Patch}";
        if (Prerelease.Count > 0) s += "-" + string.Join('.', Prerelease);
        if (Build.Count > 0) s += "+" + string.Join('.', Build);
        return s;
    }
}
