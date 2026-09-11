namespace CupriFace.Resources;

/// <summary>
/// Where a <see cref="CupriSource"/> gets its bytes, and therefore how much you should trust
/// them. Surfaced so a host can reason about (or gate) untrusted UI.
/// </summary>
public enum ResourceTrust
{
    /// <summary>Compiled into an assembly manifest — no IO, no network, tamper-resistant. Preferred.</summary>
    Embedded,
    /// <summary>Read from a local file at runtime. As trustworthy as the path you hand it.</summary>
    LocalFile,
    /// <summary>Fetched over the network at runtime. Treat as untrusted (see <see cref="CupriSource.Url"/>).</summary>
    Remote,
}

/// <summary>Thrown when a resource can't be loaded, or a <see cref="CupriSource"/> security guard trips.</summary>
public sealed class CupriResourceException : Exception
{
    public CupriResourceException(string message, Exception? inner = null) : base(message, inner) { }
}
