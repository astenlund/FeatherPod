using System.Text;

namespace FeatherPod.Services;

/// <summary>
/// Simple MP4/M4A parser to extract creation time from the movie header box (mvhd).
/// This reads the "Media created" timestamp that Windows displays in file properties.
/// </summary>
internal static class Mp4Parser
{
    // MP4 epoch starts at January 1, 1904 UTC
    private static readonly DateTime Mp4Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Extracts the creation time from an MP4/M4A file's movie header box (mvhd).
    /// </summary>
    /// <param name="filePath">Path to the MP4/M4A file</param>
    /// <returns>Creation time in UTC, or null if not found or parse error</returns>
    public static DateTime? GetCreationTime(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // Search for the moov box, then mvhd inside it
            while (fs.Position < fs.Length)
            {
                if (fs.Length - fs.Position < 8)
                    break;

                var boxSize = ReadUInt32BigEndian(reader);
                var boxType = Encoding.ASCII.GetString(reader.ReadBytes(4));

                if (boxSize == 1)
                {
                    // Extended size (64-bit)
                    boxSize = (uint)reader.ReadInt64();
                }

                if (boxType == "moov")
                {
                    // Found movie box - search inside it for mvhd
                    var moovEndPosition = fs.Position - 8 + boxSize;

                    while (fs.Position < moovEndPosition && fs.Position < fs.Length)
                    {
                        if (moovEndPosition - fs.Position < 8)
                            break;

                        var innerBoxSize = ReadUInt32BigEndian(reader);
                        var innerBoxType = Encoding.ASCII.GetString(reader.ReadBytes(4));

                        if (innerBoxType == "mvhd")
                        {
                            // Found movie header box - parse the creation time
                            var version = reader.ReadByte();
                            reader.ReadBytes(3); // flags (skip)

                            uint creationTime;
                            if (version == 1)
                            {
                                // Version 1 uses 64-bit timestamps
                                var creationTime64 = ReadUInt64BigEndian(reader);
                                creationTime = (uint)creationTime64; // Truncate to 32-bit (good until 2040)
                            }
                            else
                            {
                                // Version 0 uses 32-bit timestamps
                                creationTime = ReadUInt32BigEndian(reader);
                            }

                            // Convert from MP4 epoch (1904) to .NET DateTime
                            return Mp4Epoch.AddSeconds(creationTime);
                        }

                        // Skip this inner box and move to next
                        if (innerBoxSize > 8 && innerBoxSize < int.MaxValue)
                        {
                            var skipAmount = (long)innerBoxSize - 8;
                            if (fs.Position + skipAmount <= moovEndPosition)
                            {
                                fs.Seek(skipAmount, SeekOrigin.Current);
                            }
                            else
                            {
                                break;
                            }
                        }
                        else if (innerBoxSize <= 8)
                        {
                            break;
                        }
                    }

                    // mvhd not found in moov, but don't continue searching root level
                    return null;
                }

                // Skip this box and move to the next
                if (boxSize > 8 && boxSize < int.MaxValue)
                {
                    var skipAmount = (long)boxSize - 8;
                    if (fs.Position + skipAmount <= fs.Length)
                    {
                        fs.Seek(skipAmount, SeekOrigin.Current);
                    }
                    else
                    {
                        break;
                    }
                }
                else if (boxSize <= 8)
                {
                    break; // Invalid box size
                }
            }

            return null;
        }
        catch
        {
            // Return null on any parse error
            return null;
        }
    }

    private static uint ReadUInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static ulong ReadUInt64BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt64(bytes, 0);
    }
}
