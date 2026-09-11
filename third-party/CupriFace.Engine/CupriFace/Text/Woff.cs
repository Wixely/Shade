using System.Buffers.Binary;
using System.IO.Compression;

namespace CupriFace.Text;

/// <summary>
/// Unwraps a WOFF 1 file to the SFNT (TTF/OTF) bytes Skia reads. WOFF 1 is the same tables as the
/// font inside, each optionally zlib-compressed, behind a 44-byte header and a 20-byte-per-table
/// directory — a repackaging, not a format, which is why this is a page and not a library.
///
/// <para>WOFF 2 is a different matter: Brotli plus a transform of the <c>glyf</c>/<c>loca</c> tables
/// that has to be undone, so a decoder rather than a decompression call. It is recognised and
/// refused with a message naming the fact, which beats "not a readable font" by a long way.</para>
/// </summary>
internal static class Woff
{
    private const uint Woff1Signature = 0x774F4646; // 'wOFF'
    private const uint Woff2Signature = 0x774F4632; // 'wOF2'

    public static bool IsWoff1(ReadOnlySpan<byte> data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Woff1Signature;
    public static bool IsWoff2(ReadOnlySpan<byte> data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Woff2Signature;

    /// <summary>The SFNT payload of a WOFF 1 file.</summary>
    public static byte[] ToSfnt(ReadOnlySpan<byte> woff)
    {
        if (woff.Length < 44 || !IsWoff1(woff)) throw new ArgumentException("Not a WOFF 1 file.", nameof(woff));

        var flavor = BinaryPrimitives.ReadUInt32BigEndian(woff[4..]);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(woff[12..]);
        var totalSfntSize = BinaryPrimitives.ReadUInt32BigEndian(woff[16..]);
        if (numTables == 0 || 44 + numTables * 20 > woff.Length) throw new ArgumentException("WOFF 1 table directory is truncated.", nameof(woff));

        // SFNT offset table + table records, then the tables 4-byte aligned in directory order.
        var recordsEnd = 12 + numTables * 16;
        var output = new byte[Math.Max((int)totalSfntSize, recordsEnd)];
        var head = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(head, flavor);
        BinaryPrimitives.WriteUInt16BigEndian(head[4..], numTables);
        var entrySelector = (ushort)Math.Floor(Math.Log2(numTables));
        var searchRange = (ushort)((1 << entrySelector) * 16);
        BinaryPrimitives.WriteUInt16BigEndian(head[6..], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(head[8..], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(head[10..], (ushort)(numTables * 16 - searchRange));

        var write = recordsEnd;
        var ms = new MemoryStream(output.Length);
        ms.Write(output, 0, recordsEnd);
        for (var t = 0; t < numTables; t++)
        {
            var entry = woff[(44 + t * 20)..];
            var tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var compLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var origLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);
            if (offset < 0 || compLength < 0 || offset + compLength > woff.Length)
                throw new ArgumentException($"WOFF 1 table {Tag(tag)} lies outside the file.", nameof(woff));

            byte[] table;
            if (compLength < origLength)
            {
                using var src = new MemoryStream(woff.Slice(offset, compLength).ToArray());
                using var z = new ZLibStream(src, CompressionMode.Decompress);
                table = new byte[origLength];
                var read = 0;
                while (read < origLength)
                {
                    var n = z.Read(table, read, origLength - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != origLength) throw new ArgumentException($"WOFF 1 table {Tag(tag)} did not inflate to its declared size.", nameof(woff));
            }
            else
            {
                table = woff.Slice(offset, origLength).ToArray();
            }

            var record = output.AsSpan(12 + t * 16);
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], checksum);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)write);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)origLength);

            ms.Position = write;
            ms.Write(table, 0, table.Length);
            write += origLength;
            var pad = (4 - (write & 3)) & 3;
            for (var p = 0; p < pad; p++) ms.WriteByte(0);
            write += pad;
        }
        // The records were written into `output` after the stream took its copy; patch them in.
        var result = ms.ToArray();
        output.AsSpan(0, recordsEnd).CopyTo(result);
        return result;
    }

    private static string Tag(uint tag) => new(new[] { (char)(tag >> 24), (char)(tag >> 16 & 0xFF), (char)(tag >> 8 & 0xFF), (char)(tag & 0xFF) });
}
