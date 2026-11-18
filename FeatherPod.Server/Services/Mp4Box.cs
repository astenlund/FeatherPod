using System.Buffers.Binary;
using System.Text;

namespace FeatherPod.Server.Services;

internal readonly record struct Mp4Box(string Type, long ContentStart, long End)
{
    public static Mp4Box Read(Stream stream, long parentEnd)
    {
        var start = stream.Position;
        Span<byte> header = stackalloc byte[8];
        if (parentEnd - start < header.Length)
        {
            throw new InvalidDataException("Truncated MP4 box header.");
        }

        stream.ReadExactly(header);
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = Encoding.ASCII.GetString(header[4..]);
        if (size == 1)
        {
            if (parentEnd - stream.Position < header.Length)
            {
                throw new InvalidDataException("Truncated extended MP4 box header.");
            }

            stream.ReadExactly(header);
            size = BinaryPrimitives.ReadUInt64BigEndian(header);
        }
        else if (size == 0)
        {
            size = (ulong)(stream.Length - start);
        }

        if (size < (ulong)(stream.Position - start) || size > (ulong)(parentEnd - start))
        {
            throw new InvalidDataException("MP4 box extends outside its parent.");
        }

        return new Mp4Box(type, stream.Position, start + (long)size);
    }
}
