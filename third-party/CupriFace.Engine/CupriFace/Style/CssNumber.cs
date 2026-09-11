using System.Globalization;

namespace CupriFace.Style;

/// <summary>CSS numbers always use a dot decimal separator, independent of the user's locale.</summary>
internal static class CssNumber
{
    public static bool TryParse(string? text, out float value)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value))
            return true;

        value = 0;
        return false;
    }
}
