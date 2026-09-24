using System;
using System.Buffers.Binary;
using System.IO;

namespace AetherFrame.Services.Packages;

/// <summary>
/// Reads a ZIP file's "end of central directory" record — 22 bytes near the end of the file —
/// before the archive is opened. <see cref="System.IO.Compression.ZipArchive"/> loads the whole
/// central directory into memory up front, so a hostile file declaring millions of entries must be
/// refused from this record first. Also refuses ZIP64 and multi-disk archives, which a package
/// (a few dozen entries, well under 4 GB, one file) never needs.
/// </summary>
internal static class ZipPreflight
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const int EndRecordSize = 22;
    private const int MaxCommentLength = ushort.MaxValue;

    /// <summary>The declared entry count, or null (with <paramref name="error"/>) when the file isn't a plain ZIP.</summary>
    internal static int? ReadEntryCount(Stream stream, out string? error) => Read(stream, out error)?.EntryCount;

    /// <summary>
    /// The declared entry count and central directory size, or null (with <paramref name="error"/>)
    /// when the file isn't a plain ZIP.
    /// </summary>
    internal static (int EntryCount, long DirectoryBytes)? Read(Stream stream, out string? error)
    {
        error = null;
        var length = stream.Length;
        if (length < EndRecordSize)
        {
            error = "file too short to be a ZIP archive";
            return null;
        }

        // The record sits at the very end, followed only by an optional comment of up to 64 KiB.
        var tailLength = (int)Math.Min(length, EndRecordSize + MaxCommentLength);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        for (var i = tailLength - EndRecordSize; i >= 0; i--)
        {
            var record = tail.AsSpan(i);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            // A real end record's comment runs exactly to the end of the file; a stray signature
            // inside a comment doesn't.
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
            if (i + EndRecordSize + commentLength != tailLength)
            {
                continue;
            }

            var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);

            if (diskNumber != 0 || directoryDisk != 0 || entriesOnDisk != totalEntries)
            {
                error = "multi-part archive";
                return null;
            }

            if (totalEntries == ushort.MaxValue || HasZip64Locator(tail, i))
            {
                error = "ZIP64 archive";
                return null;
            }

            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if ((long)directoryOffset + directorySize > length - tailLength + i)
            {
                error = "central directory outside the file";
                return null;
            }

            return (totalEntries, directorySize);
        }

        error = "no ZIP end record";
        return null;
    }

    private static bool HasZip64Locator(byte[] tail, int endRecordOffset)
    {
        const int locatorSize = 20;
        return endRecordOffset >= locatorSize
            && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(endRecordOffset - locatorSize)) == Zip64LocatorSignature;
    }
}
