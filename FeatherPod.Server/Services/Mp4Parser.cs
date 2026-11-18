using System.Buffers.Binary;

namespace FeatherPod.Server.Services;

/// <summary>
/// Reads a media creation date, falling back to embedded C2PA provenance when the movie header has no date.
/// </summary>
internal static class Mp4Parser
{
    private static readonly DateTime Mp4Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] C2paUuid = Convert.FromHexString("D8FEC3D61B0E483C92975828877EC481");
    private const int MaxProvenanceBytes = 4 * 1024 * 1024;
    private const int MaxBoxes = 100_000;

    public static DateTime? GetCreationTime(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            byte[]? provenance = null;
            var provenanceCount = 0;
            var remainingBoxes = MaxBoxes;
            Span<byte> uuid = stackalloc byte[16];
            Span<byte> purpose = stackalloc byte[11];
            while (stream.Position < stream.Length && --remainingBoxes > 0)
            {
                var box = Mp4Box.Read(stream, stream.Length);
                if (box.Type == "moov")
                {
                    DateTime? creationTime;
                    try
                    {
                        creationTime = ReadMovieCreationTime(stream, box.End, ref remainingBoxes);
                    }
                    catch (InvalidDataException)
                    {
                        // The outer box is bounded, so its malformed children need not hide other date metadata.
                        creationTime = null;
                    }

                    if (creationTime.HasValue)
                    {

                        return creationTime;
                    }
                }
                else if (box.Type == "uuid" && box.End - stream.Position >= C2paUuid.Length)
                {
                    stream.ReadExactly(uuid);
                    if (uuid.SequenceEqual(C2paUuid))
                    {
                        var contentStart = stream.Position;
                        var length = box.End - contentStart;
                        if (length >= purpose.Length)
                        {
                            stream.ReadExactly(purpose);
                            if (BinaryPrimitives.ReadUInt32BigEndian(purpose) == 0 && purpose.Slice(4, 6).SequenceEqual("merkle"u8) && purpose[10] == 0)
                            {
                                stream.Position = box.End;

                                continue;
                            }

                            stream.Position = contentStart;
                        }

                        provenanceCount++;
                        if (length <= MaxProvenanceBytes && provenanceCount == 1)
                        {
                            provenance = new byte[(int)length];
                            stream.ReadExactly(provenance);
                        }
                    }
                }

                stream.Position = box.End;
            }

            return remainingBoxes > 0 && provenanceCount == 1 && provenance != null ? C2paCreationDate.Read(provenance) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            // Unreadable or malformed metadata leaves the caller's upload-date fallback in charge.

            return null;
        }
    }

    private static DateTime? ReadMovieCreationTime(Stream stream, long end, ref int remainingBoxes)
    {
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < end && --remainingBoxes > 0)
        {
            var box = Mp4Box.Read(stream, end);
            if (box.Type == "mvhd" && box.End - stream.Position >= 8)
            {
                stream.ReadExactly(header[..4]);
                var version = header[0];
                if ((version is 0 or 1) && header[1] == 0 && header[2] == 0 && header[3] == 0)
                {
                    var requiredPayloadLength = version == 1 ? 112 : 100;
                    if (box.End - box.ContentStart < requiredPayloadLength)
                    {
                        stream.Position = box.End;

                        continue;
                    }

                    var width = version == 1 ? 8 : 4;
                    stream.ReadExactly(header[..width]);
                    var seconds = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(header) : BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (seconds > 0 && seconds <= (ulong)((DateTime.MaxValue.Ticks - Mp4Epoch.Ticks) / TimeSpan.TicksPerSecond))
                    {

                        return Mp4Epoch.AddTicks((long)seconds * TimeSpan.TicksPerSecond);
                    }
                }
            }

            stream.Position = box.End;
        }

        return null;
    }
}
