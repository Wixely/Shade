using System.Net;
using System.Reflection;
using System.Text;

namespace CupriFace.Resources;

/// <summary>
/// A source of resource data (markup, styles, and — later — images/fonts/media) for a
/// <see cref="CupriApp"/>. One abstraction over three origins, each carrying its
/// <see cref="ResourceTrust"/> so callers and hosts can reason about untrusted UI:
/// <list type="bullet">
/// <item><see cref="Embedded(Assembly,string)"/> — compiled into the binary (preferred).</item>
/// <item><see cref="File(string)"/> — read from disk at runtime.</item>
/// <item><see cref="Url(Uri,CupriSourceOptions?)"/> — fetched over the network at runtime.</item>
/// </list>
/// CupriFace runs no JavaScript, so even untrusted markup cannot execute code — but it can still
/// drive data bindings, request sub-resources (future <c>url()</c>), and exhaust memory, so the
/// non-embedded origins expose their risk through <see cref="Trust"/> and, for URLs, strict
/// <see cref="CupriSourceOptions"/> defaults.
/// </summary>
public sealed class CupriSource
{
    private readonly Func<byte[]> _read;
    private readonly Func<CancellationToken, Task<byte[]>> _readAsync;

    /// <summary>Where these bytes come from — and how much to trust them.</summary>
    public ResourceTrust Trust { get; }

    /// <summary>A short, non-secret description of the origin (for diagnostics/error messages).</summary>
    public string Origin { get; }

    private CupriSource(ResourceTrust trust, string origin,
        Func<byte[]> read, Func<CancellationToken, Task<byte[]>> readAsync)
    {
        Trust = trust;
        Origin = origin;
        _read = read;
        _readAsync = readAsync;
    }

    // ---- factories ---------------------------------------------------------

    /// <summary>
    /// A resource embedded in <paramref name="assembly"/>'s manifest under <paramref name="logicalName"/>
    /// (e.g. <c>"Assets/ShowcaseApp.html"</c>). <b>Preferred:</b> no IO, no network, tamper-resistant,
    /// and resolves identically on desktop and WASM. Trust = <see cref="ResourceTrust.Embedded"/>.
    /// </summary>
    public static CupriSource Embedded(Assembly assembly, string logicalName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(logicalName);
        byte[] Load()
        {
            using var stream = assembly.GetManifestResourceStream(logicalName)
                ?? throw new CupriResourceException(
                    $"Embedded resource '{logicalName}' not found in '{assembly.GetName().Name}'. " +
                    $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        return new CupriSource(ResourceTrust.Embedded, $"embedded:{logicalName}",
            Load, _ => Task.FromResult(Load()));
    }

    /// <summary>As <see cref="Embedded(Assembly,string)"/>, using the assembly that defines <typeparamref name="TAnchor"/>.</summary>
    public static CupriSource Embedded<TAnchor>(string logicalName) =>
        Embedded(typeof(TAnchor).Assembly, logicalName);

    /// <summary>
    /// A resource read from a local file at runtime. Trust = <see cref="ResourceTrust.LocalFile"/>.
    /// <b>Security:</b> reads whatever is at <paramref name="path"/> when loaded — if the path is
    /// derived from untrusted input, validate it first (path traversal, TOCTOU).
    /// </summary>
    public static CupriSource File(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        byte[] Load()
        {
            try { return System.IO.File.ReadAllBytes(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new CupriResourceException($"Could not read file '{full}': {e.Message}", e);
            }
        }
        return new CupriSource(ResourceTrust.LocalFile, $"file:{full}",
            Load, ct => System.IO.File.ReadAllBytesAsync(full, ct));
    }

    /// <summary>
    /// A resource fetched over the network at runtime. Trust = <see cref="ResourceTrust.Remote"/> —
    /// the <b>most dangerous</b> origin: the content is remote-controlled UI. <paramref name="options"/>
    /// defaults are strict (<c>https</c>-only, size-capped, timed out, no redirects); loosening any of
    /// them is an explicit opt-in. Prefer <see cref="Embedded(Assembly,string)"/> for anything you ship.
    /// </summary>
    public static CupriSource Url(Uri uri, CupriSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var opt = options ?? CupriSourceOptions.Default;
        ValidateUrl(uri, opt);

        return new CupriSource(ResourceTrust.Remote, $"url:{uri}",
            () => FetchAsync(uri, opt, CancellationToken.None).GetAwaiter().GetResult(),
            ct => FetchAsync(uri, opt, ct));
    }

    /// <summary>An in-memory literal (e.g. a hand-written string). Trust = <see cref="ResourceTrust.Embedded"/>.</summary>
    public static CupriSource Text(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        return new CupriSource(ResourceTrust.Embedded, "text:inline",
            () => bytes, _ => Task.FromResult(bytes));
    }

    // ---- reads -------------------------------------------------------------

    /// <summary>Read the resource as UTF-8 text. Blocks for a <see cref="Url"/> source.</summary>
    public string ReadText() => Decode(_read());

    /// <summary>Read the resource as UTF-8 text asynchronously.</summary>
    public async Task<string> ReadTextAsync(CancellationToken ct = default) => Decode(await _readAsync(ct));

    /// <summary>Read the raw bytes (for images/fonts/media). Blocks for a <see cref="Url"/> source.</summary>
    public byte[] ReadBytes() => _read();

    /// <summary>Read the raw bytes asynchronously.</summary>
    public Task<byte[]> ReadBytesAsync(CancellationToken ct = default) => _readAsync(ct);

    // Strip a UTF-8 BOM if present (files saved from an editor often carry one).
    private static string Decode(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

    // ---- url fetch (shared, redirect-safe) --------------------------------

    // Redirects are followed manually so every destination is checked against the same scheme and
    // host policy as the original URL. HttpClient's automatic redirect handler would validate only
    // the first URI, allowing an approved origin to bounce a request into a blocked/private host.
    private static readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private const int MaxRedirects = 10;

    private static void ValidateUrl(Uri uri, CupriSourceOptions opt)
    {
        if (!uri.IsAbsoluteUri || uri.Host.Length == 0
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new CupriResourceException($"Refusing to load '{uri}': only absolute http/https URLs are supported.");
        if (opt.RequireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new CupriResourceException($"Refusing to load '{uri}': only https is allowed (set CupriSourceOptions.RequireHttps=false to override).");
        if (opt.AllowedHosts is { } hosts && !hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new CupriResourceException($"Refusing to load '{uri}': host '{uri.Host}' is not in AllowedHosts.");
    }

    private static async Task<byte[]> FetchAsync(Uri uri, CupriSourceOptions opt, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(opt.Timeout);
        var ct = cts.Token;
        var current = uri;
        var redirects = 0;
        try
        {
            while (true)
            {
                using var resp = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode is >= 300 and < 400)
                {
                    if (!opt.FollowRedirects)
                        throw new CupriResourceException($"Refusing to load '{current}': server redirected and FollowRedirects=false.");
                    if (resp.Headers.Location is not { } location)
                        throw new CupriResourceException($"Refusing to load '{current}': redirect response has no Location header.");
                    if (redirects++ >= MaxRedirects)
                        throw new CupriResourceException($"Refusing to load '{current}': more than {MaxRedirects} redirects.");

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    ValidateUrl(next, opt); // validate BEFORE making a request to the redirect target
                    current = next;
                    continue;
                }

                resp.EnsureSuccessStatusCode();

                if (resp.Content.Headers.ContentLength is long declared && declared > opt.MaxBytes)
                    throw new CupriResourceException($"Refusing to load '{current}': {declared} bytes exceeds MaxBytes={opt.MaxBytes}.");

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    if (ms.Length + read > opt.MaxBytes)
                        throw new CupriResourceException($"Refusing to load '{current}': response exceeds MaxBytes={opt.MaxBytes}.");
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            throw new CupriResourceException($"Timed out loading '{uri}' after {opt.Timeout.TotalSeconds:0.#}s.");
        }
        catch (HttpRequestException e)
        {
            throw new CupriResourceException($"Could not load '{uri}': {e.Message}", e);
        }
    }
}
