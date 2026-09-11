using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace CupriFace;

/// <summary>
/// Declarative field validation. A bound field carries rule attributes — <c>required</c>,
/// <c>pattern="regex"</c>, <c>minlength="N"</c>, and the numeric <c>data-min</c>/<c>data-max</c> — and
/// this evaluates them against the field's value, returning the first failing rule's message (or null
/// when valid). A field's own <c>error="…"</c> attribute overrides the default message for that field.
/// Used both for the mid-edit red border (against the edit buffer) and for the inline error text shown
/// once a field has been visited or the form validated.
/// </summary>
internal static class FieldValidation
{
    public static bool HasRules(IElement f) =>
        f.HasAttribute("required") || f.HasAttribute("pattern") || f.HasAttribute("minlength")
        || f.HasAttribute("data-min") || f.HasAttribute("data-max");

    /// <summary>Evaluate a field element's rules against <paramref name="value"/>.</summary>
    public static string? Evaluate(IElement f, string value)
    {
        var custom = f.GetAttribute("error");
        var minLen = int.TryParse(f.GetAttribute("minlength"), out var ml) ? ml : (int?)null;
        var min = double.TryParse(f.GetAttribute("data-min"), out var lo) ? lo : (double?)null;
        var max = double.TryParse(f.GetAttribute("data-max"), out var hi) ? hi : (double?)null;
        return Check(f.HasAttribute("required"), f.GetAttribute("pattern"), minLen, min, max, value, custom);
    }

    /// <summary>The rule check as plain values (so the mid-edit path can call it without an element).</summary>
    public static string? Check(bool required, string? pattern, int? minLen, double? min, double? max, string value, string? custom)
    {
        string? Msg(string dflt) => custom is { Length: > 0 } ? custom : dflt;

        if (required && string.IsNullOrWhiteSpace(value)) return Msg("This field is required.");
        if (value.Length == 0) return null; // remaining rules don't fire on an empty optional field
        if (minLen is { } n && value.Length < n) return Msg($"Must be at least {n} characters.");
        if (pattern is { Length: > 0 } p && !Matches(value, p)) return Msg("Please match the requested format.");
        if (min is { } mn && double.TryParse(value, out var v1) && v1 < mn) return Msg($"Must be at least {Fmt(mn)}.");
        if (max is { } mx && double.TryParse(value, out var v2) && v2 > mx) return Msg($"Must be at most {Fmt(mx)}.");
        return null;
    }

    private static bool Matches(string value, string pattern)
    {
        // HTML `pattern` is implicitly anchored to the whole value; a malformed regex never blocks the user.
        try { return Regex.IsMatch(value, "^(?:" + pattern + ")$"); }
        catch { return true; }
    }

    private static string Fmt(double d) => d == System.Math.Floor(d) ? ((long)d).ToString() : d.ToString();
}
