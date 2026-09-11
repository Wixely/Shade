using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shade;

public sealed record MonitorCandidate(string Source, string Connection, string Label,
    string? HardwareKey, int X, int Y, int Width, int Height);

public static class MonitorIdentity
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Original parser for the VESA EDID base block. Timing, size and layout are deliberately excluded.
    public static string? HardwareKey(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !edid[..8].SequenceEqual(new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }) || edid[18] != 1)
            return null;
        var checksum = 0;
        for (var i = 0; i < 128; i++) checksum += edid[i];
        if ((checksum & 255) != 0) return null;
        var manufacturer = BinaryPrimitives.ReadUInt16BigEndian(edid[8..]);
        if (Enumerable.Range(0, 3).Any(i => ((manufacturer >> (i * 5)) & 31) is < 1 or > 26)) return null;
        var product = BinaryPrimitives.ReadUInt16LittleEndian(edid[10..]);
        var number = BinaryPrimitives.ReadUInt32LittleEndian(edid[12..]);
        string? serial = null;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 || edid[offset + 3] != 255 || edid[offset + 4] != 0) continue;
            var text = Encoding.ASCII.GetString(edid.Slice(offset + 5, 13)).Trim('\0', '\n', '\r', ' ').ToUpperInvariant();
            if (text.All(c => c is >= ' ' and <= '~') && text.Length >= 3 &&
                text.Any(c => c is not ('0' or ' ')) && text is not ("UNKNOWN" or "DEFAULT" or "SERIAL NUMBER" or "N/A")) serial = text;
        }
        // Prefer the serial descriptor; some displays use a fixed placeholder in the numeric field.
        serial ??= number is not (0 or uint.MaxValue) ? number.ToString("X8") : null;
        return serial is null ? null : Hash($"edid-v1|{manufacturer:X4}|{product:X4}|{serial}");
    }

    public static IReadOnlyList<Display> Resolve(IReadOnlyList<MonitorCandidate> candidates, ISet<string> ambiguousHardware)
    {
        foreach (var group in candidates.Where(c => c.HardwareKey is not null).GroupBy(c => c.HardwareKey!))
            if (group.Select(c => c.Connection).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                ambiguousHardware.Add(group.Key);

        List<Display> displays = [];
        foreach (var group in candidates.GroupBy(c => c.Source, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToArray();
            var keys = members.Select(c => c.HardwareKey is { } key && !ambiguousHardware.Contains(key)
                ? "monitor-v1-" + key : "connection-v1-" + Hash(c.Connection.ToUpperInvariant())).Order(StringComparer.Ordinal).ToArray();
            var remember = members.All(c => c.HardwareKey is { } key && !ambiguousHardware.Contains(key));
            var first = members[0];
            var id = keys.Length == 1 ? keys[0] : "clone-v1-" + Hash(string.Join('|', keys));
            displays.Add(new(id, string.Join(" + ", members.Select(c => c.Label)) + (members.Length > 1 ? " (mirrored)" : ""),
                first.X, first.Y, first.Width, first.Height, remember,
                remember ? "Hardware identity; settings follow this display" : "Missing or duplicate serial; connection identity, automatic recall disabled"));
        }
        if (displays.Select(d => d.Id).Distinct().Count() != displays.Count)
            throw new InvalidOperationException("Display identities could not be distinguished.");
        return displays.AsReadOnly();
    }
}
