using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// Rebuilds the recorded, audio-free fixture so structural mutations retain valid local hashed links.
/// Does not recreate cryptographic signatures: production date extraction does not authenticate them.
/// </summary>
internal sealed class ProvenanceTestFixture
{
    private readonly byte[] _prefix;
    private readonly Box _store;

    public ProvenanceTestFixture(byte[] bytes)
    {
        _prefix = bytes[8..45];
        _store = Box.Read(bytes.AsSpan(45));
    }

    public Box Creation => _store.Children[1];

    public Box Active => _store.Children[^1];

    public void KeepCreationOnly()
    {
        _store.Children.RemoveRange(2, _store.Children.Count - 2);
    }

    public byte[] Encode(bool refreshHashes = true, int padding = 0)
    {
        if (refreshHashes)
        {
            // The fixture has two manifests. Repeated bottom-up passes also refresh enclosing claim links.
            for (var pass = 0; pass < 4; pass++)
            {
                foreach (var manifest in _store.Children.Skip(1))
                {
                    foreach (var leaf in manifest.Leaves().Where(x => x.Type == "cbor"))
                    {
                        leaf.Payload = Refresh(leaf.Payload, manifest);
                    }
                }
            }
        }

        return Pack("uuid", [.. _prefix, .. _store.Encode(), .. new byte[padding]]);
    }

    private byte[] Refresh(byte[] encoded, Box manifest)
    {
        var reader = new CborReader(encoded);
        if (reader.PeekState() == CborReaderState.StartArray)
        {

            return EncodeArray(DecodeArray(encoded).Select(x => Refresh(x, manifest)));
        }

        if (reader.PeekState() != CborReaderState.StartMap)
        {

            return encoded;
        }

        var map = DecodeMap(encoded);
        foreach (var key in map.Keys.ToArray())
        {
            map[key] = Refresh(map[key], manifest);
        }

        if (map.TryGetValue("url", out var urlBytes) && map.ContainsKey("hash"))
        {
            var url = new CborReader(urlBytes).ReadTextString();
            Box? target = null;
            if (url.StartsWith("self#jumbf=/c2pa/", StringComparison.Ordinal))
            {
                target = Find(_store, url["self#jumbf=/c2pa/".Length..]);
            }
            else if (url.StartsWith("self#jumbf=", StringComparison.Ordinal))
            {
                target = Find(manifest, url["self#jumbf=".Length..]);
            }

            if (target != null)
            {
                var writer = new CborWriter();
                writer.WriteByteString(SHA256.HashData(target.Content()));
                map["hash"] = writer.Encode();
            }
        }

        return EncodeMap(map);
    }

    private static Box? Find(Box root, string path)
    {
        Box? current = root;
        foreach (var label in path.Split('/'))
        {
            current = current?.Children.SingleOrDefault(x => x.Type == "jumb" && x.Label == label);
        }

        return current;
    }

    public static Dictionary<string, byte[]> DecodeMap(byte[] encoded)
    {
        var reader = new CborReader(encoded);
        reader.ReadStartMap();
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            values.Add(reader.ReadTextString(), reader.ReadEncodedValue().ToArray());
        }

        reader.ReadEndMap();
        Assert.Equal(0, reader.BytesRemaining);

        return values;
    }

    public static byte[] EncodeMap(Dictionary<string, byte[]> values)
    {
        var writer = new CborWriter();
        writer.WriteStartMap(values.Count);
        foreach (var (key, value) in values)
        {
            writer.WriteTextString(key);
            writer.WriteEncodedValue(value);
        }

        writer.WriteEndMap();

        return writer.Encode();
    }

    public static List<byte[]> DecodeArray(byte[] encoded)
    {
        var reader = new CborReader(encoded);
        reader.ReadStartArray();
        var values = new List<byte[]>();
        while (reader.PeekState() != CborReaderState.EndArray)
        {
            values.Add(reader.ReadEncodedValue().ToArray());
        }

        reader.ReadEndArray();
        Assert.Equal(0, reader.BytesRemaining);

        return values;
    }

    public static byte[] EncodeArray(IEnumerable<byte[]> values)
    {
        var array = values.ToList();
        var writer = new CborWriter();
        writer.WriteStartArray(array.Count);
        foreach (var value in array)
        {
            writer.WriteEncodedValue(value);
        }

        writer.WriteEndArray();

        return writer.Encode();
    }

    public static byte[] Text(string value)
    {
        var writer = new CborWriter();
        writer.WriteTextString(value);

        return writer.Encode();
    }

    private static byte[] Pack(string type, byte[] content)
    {
        var bytes = new byte[content.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        content.CopyTo(bytes, 8);

        return bytes;
    }

    internal sealed class Box(string type)
    {
        public string Type { get; } = type;

        public List<Box> Children { get; } = [];

        public byte[] Payload { get; set; } = [];

        public string Label => Encoding.UTF8.GetString(Children[0].Payload, 17, System.Array.IndexOf(Children[0].Payload, (byte)0, 17) - 17);

        public Box Child(string label) => Children.Single(x => x.Type == "jumb" && x.Label == label);

        public Box Cbor => Children.Single(x => x.Type == "cbor");

        public void RenameLabel(string label)
        {
            var description = Children[0].Payload;
            var terminator = System.Array.IndexOf(description, (byte)0, 17);
            Children[0].Payload = [.. description.AsSpan(0, 17), .. Encoding.UTF8.GetBytes(label), 0, .. description.AsSpan(terminator + 1)];
        }

        public static Box Read(ReadOnlySpan<byte> bytes)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes));
            var box = new Box(Encoding.ASCII.GetString(bytes[4..8]));
            if (box.Type == "jumb")
            {
                var offset = 8;
                while (offset < size)
                {
                    box.Children.Add(Read(bytes[offset..size]));
                    offset += checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]));
                }
            }
            else
            {
                box.Payload = bytes[8..size].ToArray();
            }

            return box;
        }

        public byte[] Content() => Type == "jumb" ? Children.SelectMany(x => x.Encode()).ToArray() : Payload;

        public byte[] Encode() => Pack(Type, Content());

        public IEnumerable<Box> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(x => x.Leaves());
    }
}
