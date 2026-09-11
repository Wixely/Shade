namespace CupriFace.Resources;

/// <summary>
/// Safety limits for a <see cref="CupriSource.Url"/> fetch. Defaults are deliberately strict —
/// loosening any of them is an explicit, visible opt-in.
/// </summary>
public sealed class CupriSourceOptions
{
    /// <summary>Reject non-<c>https</c> URLs (guards against MITM tampering). Default <c>true</c>.</summary>
    public bool RequireHttps { get; init; } = true;

    /// <summary>Abort if the response exceeds this many bytes (guards against resource exhaustion). Default 8 MiB.</summary>
    public long MaxBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Give up if the fetch takes longer than this. Default 10s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Follow HTTP redirects. Every target is still checked against <see cref="RequireHttps"/>
    /// and <see cref="AllowedHosts"/>. Default <c>false</c>.</summary>
    public bool FollowRedirects { get; init; }

    /// <summary>If set, the URL's host must be in this allow-list (guards against SSRF / drift). Default: any host.</summary>
    public IReadOnlyCollection<string>? AllowedHosts { get; init; }

    /// <summary>The strict defaults.</summary>
    public static CupriSourceOptions Default { get; } = new();
}
