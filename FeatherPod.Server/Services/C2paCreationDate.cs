using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;

namespace FeatherPod.Server.Services;

/// <summary>
/// Extracts a date from embedded provenance, not a trust verdict. Follows a single parent chain back to
/// creation and reads the RFC 3161 genTime associated with its claim signature. Certificate dates are never used.
/// Unsupported, ambiguous or malformed provenance leaves publication-date selection to the caller.
/// </summary>
internal static class C2paCreationDate
{
    private const int MaxEntries = 256;
    private const string CreatedAction = "c2pa.created";
    private const string OpenedAction = "c2pa.opened";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] JumbfUuidSuffix = Convert.FromHexString("00110010800000AA00389B71");

    public static DateTime? Read(byte[] content)
    {
        try
        {
            content = content.ToArray();
            // ContentProvenanceBox: version/flags, purpose string, merkle offset, then JUMBF.
            if (content.Length < 21 || BinaryPrimitives.ReadUInt32BigEndian(content) != 0 || (!content.AsSpan(4, 8).SequenceEqual("manifest"u8) || content[12] != 0))
            {

                return null;
            }

            using var stream = new MemoryStream(content, 0, content.Length, false, true);
            stream.Position = 21;
            var budget = 4096;
            var store = ReadNode(stream, stream.Length, 0, ref budget);
            // ContentProvenanceBox permits unused padding after the manifest store.
            if (store.Kind != "c2pa" || store.Label != "c2pa")
            {

                return null;
            }

            var manifestNodes = store.Children.Where(x => IsManifest(x.Kind)).ToList();
            if (manifestNodes.Count is 0 or > MaxEntries)
            {

                return null;
            }

            var manifests = manifestNodes.ToDictionary(x => x.Label, StringComparer.Ordinal);
            var current = manifestNodes[^1];
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(current.Label))
            {
                if (!IsStandardManifest(current.Kind))
                {

                    return null;
                }

                var claim = Map(Child(current, "c2pa.claim.v2", "c2cl").Data);
                var signatureNode = ClaimSignature(current, claim);
                var assertions = Child(current, "c2pa.assertions", "c2as");
                var referenceLists = new List<List<ReadOnlyMemory<byte>>> { Array(claim["created_assertions"]) };
                if (claim.TryGetValue("gathered_assertions", out var gathered))
                {
                    referenceLists.Add(Array(gathered));
                }

                if (referenceLists.Any(x => x.Count == 0))
                {

                    return null;
                }

                var algorithm = claim.TryGetValue("alg", out var algorithmBytes) ? Text(algorithmBytes) : null;
                var assertionLists = referenceLists
                    .Select(list => list.Select(x => ResolveAssertion(assertions, Map(x), algorithm, current.Label)).ToList())
                    .ToList();
                var referenced = assertionLists.SelectMany(x => x).ToList();
                if (referenced.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count() != referenced.Count)
                {

                    return null;
                }

                var originAction = ReadOrigin(referenced);
                var origin = Text(originAction["action"]);
                var parents = referenced.Where(x => IsInstanceLabel(x.Label, "c2pa.ingredient.v3"))
                    .Where(x => Text(Map(CborData(x))["relationship"]) == "parentOf")
                    .ToList();
                if (origin == CreatedAction)
                {
                    if (parents.Count != 0)
                    {

                        return null;
                    }

                    return ReadSignatureTimestamp(signatureNode.Data);
                }

                if (parents.Count != 1)
                {

                    return null;
                }

                var openingReferences = Array(Map(originAction["parameters"])["ingredients"]);
                if (openingReferences.Count != 1 || ResolveAssertion(assertions, Map(openingReferences[0]), algorithm, current.Label) != parents[0])
                {

                    return null;
                }

                var parentIngredient = Map(parents[0].Data);
                var parentReference = Map(parentIngredient["activeManifest"]);
                var parentPath = LocalPath(parentReference["url"]);
                if (!parentPath.Absolute || parentPath.Segments.Length != 2 || parentPath.Segments[0] != "c2pa" || !manifests.TryGetValue(parentPath.Segments[1], out var parent))
                {

                    return null;
                }

                CheckHash(parentReference, parent, algorithm);
                current = parent;
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or KeyNotFoundException or CborContentException or AsnContentException or CryptographicException or OverflowException)
        {
            // Metadata is optional; an unsupported or corrupt structure must not reject the upload.

            return null;
        }
    }

    private static Dictionary<string, ReadOnlyMemory<byte>> ReadOrigin(List<Node> assertions)
    {
        var actions = assertions.Where(x => IsInstanceLabel(x.Label, "c2pa.actions.v2"))
            .SelectMany(x => Array(Map(CborData(x))["actions"]))
            .Select(Map)
            .ToList();
        if (actions.Count == 0 || Text(actions[0]["action"]) is not (CreatedAction or OpenedAction)
            || actions.Skip(1).Any(x => Text(x["action"]) is CreatedAction or OpenedAction))
        {
            throw new InvalidDataException("Missing or ambiguous creation history.");
        }

        return actions[0];
    }

    private static bool IsStandardManifest(string kind) => kind is "c2ma" or "c2md";

    private static bool IsManifest(string kind) => IsStandardManifest(kind) || kind is "c2cm" or "c2um";

    private static bool IsInstanceLabel(string label, string name)
    {
        if (label == name)
        {

            return true;
        }

        var prefix = name + "__";
        if (!label.StartsWith(prefix, StringComparison.Ordinal) || label.Length == prefix.Length || label[prefix.Length] is < '1' or > '9')
        {

            return false;
        }

        return label.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static Node ClaimSignature(Node manifest, Dictionary<string, ReadOnlyMemory<byte>> claim)
    {
        var reference = LocalPath(claim["signature"]);
        string[] expected = reference.Absolute ? ["c2pa", manifest.Label, "c2pa.signature"] : ["c2pa.signature"];
        if (!reference.Segments.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Claim signature does not refer to its own manifest.");
        }

        var signature = Child(manifest, "c2pa.signature", "c2cs");
        if (ReadSignature(signature.Data) == null)
        {
            throw new InvalidDataException("Malformed claim signature.");
        }

        return signature;
    }

    private static Node ResolveAssertion(Node assertions, Dictionary<string, ReadOnlyMemory<byte>> reference, string? algorithm, string manifestLabel)
    {
        var path = LocalPath(reference["url"]);
        var prefixMatches = path.Absolute
            ? path.Segments.Length == 4 && path.Segments[0] == "c2pa" && path.Segments[1] == manifestLabel && path.Segments[2] == "c2pa.assertions"
            : path.Segments.Length == 2 && path.Segments[0] == "c2pa.assertions";
        if (!prefixMatches)
        {
            throw new InvalidDataException("Unsupported assertion reference.");
        }

        var label = path.Segments[^1];
        var assertion = assertions.Children.Single(x => x.Label == label);
        CheckHash(reference, assertion, algorithm);

        return assertion;
    }

    private static LocalReference LocalPath(ReadOnlyMemory<byte> encoded)
    {
        const string prefix = "self#jumbf=";
        var uri = Text(encoded);
        if (!uri.StartsWith(prefix, StringComparison.Ordinal) || uri.Contains("..", StringComparison.Ordinal)
            || uri.Contains('%') || uri.Contains('?') || uri.Contains(';'))
        {
            throw new InvalidDataException("Unsupported local provenance reference.");
        }

        var path = uri[prefix.Length..];
        var absolute = path.StartsWith('/');
        if (path.Count(x => x == '/') > (absolute ? 4 : 3))
        {
            throw new InvalidDataException("Unsupported provenance reference depth.");
        }

        var segments = (absolute ? path[1..] : path).Split('/', 5);
        if (segments.Length > 4 || segments.Any(x => x.Length == 0))
        {
            throw new InvalidDataException("Unsupported provenance reference depth.");
        }

        return new LocalReference(absolute, segments);
    }

    private static ReadOnlyMemory<byte> CborData(Node node)
    {
        if (node.Kind != "cbor")
        {
            throw new InvalidDataException("Unsupported encoding for provenance history.");
        }

        return node.Data;
    }

    private static void CheckHash(Dictionary<string, ReadOnlyMemory<byte>> reference, Node target, string? defaultAlgorithm)
    {
        var algorithm = reference.TryGetValue("alg", out var value) ? Text(value) : defaultAlgorithm;
        if (algorithm is not ("sha256" or "sha384" or "sha512"))
        {
            throw new InvalidDataException("Unsupported provenance hash algorithm.");
        }

        var digest = target.GetDigest(algorithm);
        if (!CryptographicOperations.FixedTimeEquals(digest, Bytes(reference["hash"])))
        {
            throw new InvalidDataException("Provenance reference does not match its target.");
        }
    }

    private static Node Child(Node parent, string label, string kind)
    {
        var child = parent.Children.Single(x => x.Kind == kind);
        if (child.Label != label)
        {
            throw new InvalidDataException("Unexpected label for a required JUMBF role.");
        }

        return child;
    }

    private static Node ReadNode(MemoryStream stream, long end, int depth, ref int budget)
    {
        if (depth > 8 || --budget <= 0)
        {
            throw new InvalidDataException("Provenance nesting limit exceeded.");
        }

        var box = Mp4Box.Read(stream, end);
        if (box.Type != "jumb")
        {
            throw new InvalidDataException("Expected JUMBF superbox.");
        }

        var description = Mp4Box.Read(stream, box.End);
        var descriptionBytes = new byte[checked((int)(description.End - stream.Position))];
        stream.ReadExactly(descriptionBytes);
        if (description.Type != "jumd" || descriptionBytes.Length < 18 || descriptionBytes[16] is not (3 or 19))
        {
            throw new InvalidDataException("Unsupported JUMBF description.");
        }

        var labelEnd = System.Array.IndexOf(descriptionBytes, (byte)0, 17);
        if (labelEnd < 17 || (descriptionBytes[16] == 3 && labelEnd != descriptionBytes.Length - 1))
        {
            throw new InvalidDataException("Invalid JUMBF label.");
        }

        if (descriptionBytes[16] == 19)
        {
            var privateField = descriptionBytes.AsSpan(labelEnd + 1);
            if (privateField.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(privateField) != privateField.Length)
            {
                throw new InvalidDataException("Invalid JUMBF private field.");
            }

            if (privateField.Slice(4, 4).SequenceEqual("c2sh"u8) && privateField.Length is not (24 or 40))
            {
                throw new InvalidDataException("Invalid C2PA salt length.");
            }
        }

        var kind = descriptionBytes.AsSpan(4, 12).SequenceEqual(JumbfUuidSuffix) ? Encoding.ASCII.GetString(descriptionBytes, 0, 4) : Convert.ToHexString(descriptionBytes.AsSpan(0, 16));
        var label = StrictUtf8.GetString(descriptionBytes, 17, labelEnd - 17);
        var encodedContent = stream.GetBuffer().AsMemory((int)box.ContentStart, (int)(box.End - box.ContentStart));
        var isContainer = IsStandardManifest(kind) || kind is "c2pa" or "c2as";
        var isCborLeaf = kind is "c2cl" or "c2cs" or "cbor";
        if (!isContainer && !isCborLeaf)
        {
            // Extension content belongs to its declared type, not to the C2PA schema understood here.
            stream.Position = box.End;

            return new Node(kind, label, [], default, encodedContent);
        }

        var children = new List<Node>();
        ReadOnlyMemory<byte> data = default;
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var hasData = false;
        while (stream.Position < box.End)
        {
            var start = stream.Position;
            var child = Mp4Box.Read(stream, box.End);
            if (isContainer && child.Type == "jumb")
            {
                stream.Position = start;
                var nested = ReadNode(stream, box.End, depth + 1, ref budget);
                if (!labels.Add(nested.Label))
                {
                    throw new InvalidDataException("Duplicate provenance label.");
                }

                children.Add(nested);
            }
            else if (isCborLeaf && child.Type == "cbor")
            {
                if (hasData)
                {
                    throw new InvalidDataException("Duplicate CBOR content.");
                }

                var bytes = new byte[checked((int)(child.End - stream.Position))];
                stream.ReadExactly(bytes);
                data = bytes;
                hasData = true;
            }
            else if (child.Type != "free")
            {
                throw new InvalidDataException("Unexpected content in a recognized provenance box.");
            }
            if (--budget <= 0)
            {
                throw new InvalidDataException("Provenance box limit exceeded.");
            }

            stream.Position = child.End;
        }

        if (isCborLeaf && !hasData)
        {
            throw new InvalidDataException("Missing CBOR content.");
        }

        return new Node(kind, label, children, data, encodedContent);
    }

    private static Dictionary<string, ReadOnlyMemory<byte>> Map(ReadOnlyMemory<byte> bytes)
    {
        var reader = new CborReader(bytes);
        reader.ReadStartMap();
        var map = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var count = 0;
        while (reader.PeekState() != CborReaderState.EndMap && ++count <= MaxEntries)
        {
            var key = reader.ReadTextString();
            map.Add(key, reader.ReadEncodedValue());
        }

        reader.ReadEndMap();
        RequireEnd(reader);

        return map;
    }

    private static List<ReadOnlyMemory<byte>> Array(ReadOnlyMemory<byte> bytes)
    {
        var reader = new CborReader(bytes);
        reader.ReadStartArray();
        var values = new List<ReadOnlyMemory<byte>>();
        while (reader.PeekState() != CborReaderState.EndArray && values.Count < MaxEntries)
        {
            values.Add(reader.ReadEncodedValue());
        }

        reader.ReadEndArray();
        RequireEnd(reader);

        return values;
    }

    private static string Text(ReadOnlyMemory<byte> bytes)
    {
        var reader = new CborReader(bytes);
        var value = reader.ReadTextString();
        RequireEnd(reader);

        return value;
    }

    private static byte[] Bytes(ReadOnlyMemory<byte> bytes)
    {
        var reader = new CborReader(bytes);
        var value = reader.ReadByteString();
        RequireEnd(reader);

        return value;
    }

    private static void RequireEnd(CborReader reader)
    {
        if (reader.BytesRemaining != 0)
        {
            throw new InvalidDataException("Trailing CBOR data.");
        }
    }

    private static List<ReadOnlyMemory<byte>>? ReadSignature(ReadOnlyMemory<byte> bytes)
    {
        var reader = new CborReader(bytes);
        if (reader.ReadTag() != (CborTag)18)
        {

            return null;
        }

        var cose = Array(reader.ReadEncodedValue());
        RequireEnd(reader);
        if (cose.Count != 4 || Bytes(cose[3]).Length == 0)
        {

            return null;
        }

        var payload = new CborReader(cose[2]);
        payload.ReadNull();
        RequireEnd(payload);
        HeaderMap(Bytes(cose[0]));
        HeaderMap(cose[1]);

        return cose;
    }

    private static DateTime? ReadSignatureTimestamp(ReadOnlyMemory<byte> bytes)
    {
        var cose = ReadSignature(bytes);
        if (cose == null)
        {

            return null;
        }

        var headers = HeaderMap(cose[1]);
        if (!headers.TryGetValue(new HeaderLabel(CborReaderState.TextString, Name: "sigTst2"), out var container))
        {

            return null;
        }

        var tokens = Array(Map(container)["tstTokens"]);
        if (tokens.Count != 1)
        {

            return null;
        }

        // C2PA v2 timestamps hash the COSE CounterSignature structure, including the encoded signature bstr.
        var writer = new CborWriter();
        writer.WriteStartArray(4);
        writer.WriteTextString("CounterSignature");
        writer.WriteByteString(Bytes(cose[0]));
        writer.WriteByteString([]);
        writer.WriteByteString(cose[3].Span);
        writer.WriteEndArray();

        return ReadTimestampToken(Bytes(Map(tokens[0])["val"]), writer.Encode());
    }

    private static Dictionary<HeaderLabel, ReadOnlyMemory<byte>> HeaderMap(ReadOnlyMemory<byte> bytes)
    {
        var reader = new CborReader(bytes);
        reader.ReadStartMap();
        var headers = new Dictionary<HeaderLabel, ReadOnlyMemory<byte>>();
        while (reader.PeekState() != CborReaderState.EndMap && headers.Count < MaxEntries)
        {
            var label = ReadHeaderLabel(reader);
            headers.Add(label, reader.ReadEncodedValue());
        }

        reader.ReadEndMap();
        RequireEnd(reader);

        return headers;
    }

    private static HeaderLabel ReadHeaderLabel(CborReader reader) => reader.PeekState() switch
    {
        CborReaderState.TextString or CborReaderState.StartIndefiniteLengthTextString => new HeaderLabel(CborReaderState.TextString, Name: reader.ReadTextString()),
        CborReaderState.UnsignedInteger => new HeaderLabel(CborReaderState.UnsignedInteger, reader.ReadUInt64()),
        CborReaderState.NegativeInteger => new HeaderLabel(CborReaderState.NegativeInteger, reader.ReadCborNegativeIntegerRepresentation()),
        _ => throw new InvalidDataException("Invalid COSE header label.")
    };

    private static DateTime? ReadTimestampToken(byte[] token, byte[] counterSignature)
    {
        // Navigate only the CMS envelope and TSTInfo fields needed for the date.
        // Certificates, signer algorithms and signatures are not authenticated or profiled.
        var reader = new AsnReader(token, AsnEncodingRules.DER);
        var content = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        if (content.ReadObjectIdentifier() != "1.2.840.113549.1.7.2")
        {

            return null;
        }

        var envelope = content.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        content.ThrowIfNotEmpty();
        var signedData = envelope.ReadSequence();
        envelope.ThrowIfNotEmpty();
        signedData.ReadInteger();
        signedData.ReadSetOf();
        var encapsulated = signedData.ReadSequence();
        if (encapsulated.ReadObjectIdentifier() != "1.2.840.113549.1.9.16.1.4")
        {

            return null;
        }

        var payload = encapsulated.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        encapsulated.ThrowIfNotEmpty();
        var timestampReader = new AsnReader(payload.ReadOctetString(), AsnEncodingRules.DER);
        payload.ThrowIfNotEmpty();
        var info = timestampReader.ReadSequence();
        timestampReader.ThrowIfNotEmpty();
        if (info.ReadInteger() != 1)
        {

            return null;
        }

        info.ReadObjectIdentifier(); // Policy.
        var imprint = info.ReadSequence();
        var algorithm = imprint.ReadSequence();
        var digestAlgorithm = algorithm.ReadObjectIdentifier();
        var digest = imprint.ReadOctetString();
        imprint.ThrowIfNotEmpty();
        if (!CryptographicOperations.FixedTimeEquals(digest, ComputeDigest(counterSignature, digestAlgorithm)))
        {

            return null;
        }

        info.ReadInteger(); // Serial number.
        var timestamp = info.ReadGeneralizedTime();

        return timestamp > DateTimeOffset.UnixEpoch && timestamp <= DateTimeOffset.UtcNow ? timestamp.UtcDateTime : null;
    }

    private static byte[] ComputeDigest(ReadOnlySpan<byte> content, string? algorithm) => algorithm switch
    {
        "sha256" or "2.16.840.1.101.3.4.2.1" => SHA256.HashData(content),
        "sha384" or "2.16.840.1.101.3.4.2.2" => SHA384.HashData(content),
        "sha512" or "2.16.840.1.101.3.4.2.3" => SHA512.HashData(content),
        _ => throw new InvalidDataException("Unsupported metadata digest algorithm.")
    };

    private sealed class Node(string kind, string label, List<Node> children, ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> encodedContent)
    {
        // Nodes belong to one read's private byte snapshot. Cache at most one digest per accepted algorithm,
        // discard everything with that read, and compare every supplied reference even on cache hits.
        private Dictionary<string, byte[]>? _digests;

        public string Kind { get; } = kind;

        public string Label { get; } = label;

        public List<Node> Children { get; } = children;

        public ReadOnlyMemory<byte> Data { get; } = data;

        public ReadOnlySpan<byte> GetDigest(string algorithm)
        {
            _digests ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (!_digests.TryGetValue(algorithm, out var digest))
            {
                digest = ComputeDigest(encodedContent.Span, algorithm);
                _digests.Add(algorithm, digest);
            }

            return digest;
        }
    }


    private readonly record struct LocalReference(bool Absolute, string[] Segments);

    private readonly record struct HeaderLabel(CborReaderState State, ulong Number = 0, string? Name = null);
}
