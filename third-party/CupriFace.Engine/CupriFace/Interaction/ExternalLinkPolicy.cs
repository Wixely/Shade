namespace CupriFace.Interaction;

/// <summary>
/// The schemes a host may hand to an OS or browser as an external link. Keeping this policy in the
/// engine gives every host the same boundary and prevents remote markup from invoking executable,
/// local-file, intent, or arbitrary custom-protocol handlers.
/// </summary>
public static class ExternalLinkPolicy
{
    /// <summary>Whether <paramref name="href"/> is an absolute, explicitly supported external URI.</summary>
    public static bool IsAllowed(string? href)
    {
        if (string.IsNullOrEmpty(href) || href != href.Trim()) return false;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return false;

        return uri.Scheme switch
        {
            "http" or "https" => uri.Host.Length > 0,
            "mailto" or "tel" => true,
            _ => false,
        };
    }
}
