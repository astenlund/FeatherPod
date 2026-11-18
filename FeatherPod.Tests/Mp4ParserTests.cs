using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Text;
using FeatherPod.Server.Services;
using FeatherPod.Tests.Helpers;

namespace FeatherPod.Tests;

public class Mp4ParserTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"FeatherPod-date-{Guid.NewGuid():N}.m4a");
    private static readonly DateTime Mp4Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GetCreationTime_ZeroMovieTimestampIsMissing(byte version)
    {
        // Arrange
        File.WriteAllBytes(_path, MovieHeader(version, 0));

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, 2026)]
    [InlineData(1, 2026)]
    [InlineData(1, 2050)]
    public void GetCreationTime_PreservesNonzeroMovieTimestamp(byte version, int year)
    {
        // Arrange
        var expected = new DateTime(year, 11, 12, 8, 9, 22, DateTimeKind.Utc);
        File.WriteAllBytes(_path, MovieHeader(version, (ulong)(expected - Mp4Epoch).TotalSeconds));

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Equal(expected, result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
    }

    [Fact]
    public void GetCreationTime_C2paUsesLinkedCreationSignatureNotTranscodeOrCertificateDate()
    {
        // Arrange
        File.WriteAllBytes(_path, [.. Provenance(), .. MovieHeader(0, 0)]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
    }

    [Fact]
    public void GetCreationTime_MovieDateTakesPrecedenceOverProvenance()
    {
        // Arrange
        var expected = new DateTime(2026, 11, 12, 8, 9, 22, DateTimeKind.Utc);
        File.WriteAllBytes(_path, [.. Provenance(), .. MovieHeader(0, (ulong)(expected - Mp4Epoch).TotalSeconds)]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetCreationTime_ProvenanceAfterMovieIsRead()
    {
        // Arrange
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. Provenance()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
    }

    [Fact]
    public void GetCreationTime_BrokenParentReferenceFallsBack()
    {
        // Arrange
        var provenance = Provenance();
        const string reference = "self#jumbf=/c2pa/urn:c2pa:b368967f-3a1c-5a85-5a94-40f06b21082c";
        var offset = ParentReferenceOffset(provenance, reference);
        Assert.True(offset >= 0);
        provenance[offset + reference.Length - 1] = (byte)'x';
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. new ProvenanceTestFixture(provenance).Encode()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_TimestampForAnotherSignatureFallsBack()
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        fixture.KeepCreationOnly();
        var signature = fixture.Creation.Child("c2pa.signature").Cbor;
        var reader = new CborReader(signature.Payload);
        var tag = reader.ReadTag();
        var cose = ProvenanceTestFixture.DecodeArray(reader.ReadEncodedValue().ToArray());
        var signatureBytes = new CborReader(cose[3]).ReadByteString();
        signatureBytes[0] ^= 1;
        var changedSignature = new CborWriter();
        changedSignature.WriteByteString(signatureBytes);
        cose[3] = changedSignature.Encode();
        var output = new CborWriter();
        output.WriteTag(tag);
        output.WriteEncodedValue(ProvenanceTestFixture.EncodeArray(cose));
        signature.Payload = output.Encode();
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_ChangedParentManifestHashFallsBack()
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        fixture.Creation.Children.Add(new ProvenanceTestFixture.Box("free") { Payload = [1] });
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode(refreshHashes: false)]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_DuplicateProvenanceIsAmbiguous()
    {
        // Arrange
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. Provenance(), .. Provenance()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(1000)]
    public void GetCreationTime_TruncatedProvenanceFallsBack(int length)
    {
        // Arrange
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. Provenance().Take(length)]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_ExtendedMovieBoxUsesBigEndianSize()
    {
        // Arrange
        var expected = new DateTime(2026, 11, 12, 8, 9, 22, DateTimeKind.Utc);
        var ordinary = MovieHeader(0, (ulong)(expected - Mp4Epoch).TotalSeconds);
        var extended = new byte[ordinary.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(extended, 1);
        "moov"u8.CopyTo(extended.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), (ulong)extended.Length);
        ordinary.AsSpan(8).CopyTo(extended.AsSpan(16));
        File.WriteAllBytes(_path, extended);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void GetCreationTime_RebuiltFixtureAndPermittedPaddingPreserveCreationDate(int padding)
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode(padding: padding)]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("c2pa.opened")]
    [InlineData("c2pa.transcoded")]
    public void GetCreationTime_TerminalManifestWithoutCreationEvidenceFallsBack(string action)
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        fixture.KeepCreationOnly();
        SetFirstAction(fixture.Creation, action);
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_OpenedManifestWithoutParentRelationshipFallsBack()
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        var ingredient = fixture.Active.Child("c2pa.assertions").Child("c2pa.ingredient.v3").Cbor;
        var map = ProvenanceTestFixture.DecodeMap(ingredient.Payload);
        map["relationship"] = ProvenanceTestFixture.Text("componentOf");
        ingredient.Payload = ProvenanceTestFixture.EncodeMap(map);
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_CreationActionWithParentIsAmbiguous()
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        SetFirstAction(fixture.Active, "c2pa.created");
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_IncorrectAssertionHashFallsBack()
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        var claim = fixture.Active.Child("c2pa.claim.v2").Cbor;
        var map = ProvenanceTestFixture.DecodeMap(claim.Payload);
        var references = ProvenanceTestFixture.DecodeArray(map["created_assertions"]);
        var ingredient = ProvenanceTestFixture.DecodeMap(references[0]);
        Assert.EndsWith("/c2pa.ingredient.v3", new CborReader(ingredient["url"]).ReadTextString());
        ingredient["hash"][^1] ^= 1;
        references[0] = ProvenanceTestFixture.EncodeMap(ingredient);
        map["created_assertions"] = ProvenanceTestFixture.EncodeArray(references);
        claim.Payload = ProvenanceTestFixture.EncodeMap(map);
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode(refreshHashes: false)]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void GetCreationTime_ShortMovieHeaderDoesNotSupplyDate(byte version, bool hasProvenance)
    {
        // Arrange
        var fullHeader = MovieHeader(version, 1);
        var shortPayload = fullHeader.AsSpan(16, version == 1 ? 12 : 8).ToArray();
        var movie = Box("moov", Box("mvhd", shortPayload));
        File.WriteAllBytes(_path, hasProvenance ? [.. movie, .. Provenance()] : movie);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        if (hasProvenance)
        {
            Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetCreationTime_MalformedChildrenInBoundedMovieLeaveProvenanceAvailable(bool movieFirst)
    {
        // Arrange
        var movie = Box("moov", new byte[7]);
        File.WriteAllBytes(_path, movieFirst ? [.. movie, .. Provenance()] : [.. Provenance(), .. movie]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
    }

    [Fact]
    public void TryGetPublishedDateFromFile_TagReaderFailureStillTriesContainerDate()
    {
        // Arrange
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. Provenance()]);
        Assert.Throws<TagLib.CorruptFileException>(() =>
        {
            using var file = TagLib.File.Create(_path);
        });

        // Act
        var found = EpisodeService.TryGetPublishedDateFromFile(_path, out var result);

        // Assert
        Assert.True(found);
        Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GetCreationTime_ZeroBoxSizeUsesFileEndAndRespectsParentBounds(bool trailingBox, bool provenance)
    {
        // Arrange
        var movie = MovieHeader(0, 512);
        BinaryPrimitives.WriteUInt32BigEndian(movie.AsSpan(8), 0);
        File.WriteAllBytes(_path, [.. provenance ? Provenance() : [], .. movie, .. trailingBox ? Box("free", new byte[8]) : []]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        if (provenance)
        {
            Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
        }
        else if (trailingBox)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Equal(Mp4Epoch.AddSeconds(512), result);
        }
    }

    [Theory]
    [InlineData("c2pa.claim.v2", "c2pa.claim")]
    [InlineData("c2pa.signature", "another.signature")]
    [InlineData("c2pa.assertions", "another.assertions")]
    public void GetCreationTime_RequiredManifestRoleMustBeUnique(string role, string alternateLabel)
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        fixture.KeepCreationOnly();
        var duplicate = ProvenanceTestFixture.Box.Read(fixture.Creation.Child(role).Encode());
        duplicate.RenameLabel(alternateLabel);
        fixture.Creation.Children.Add(duplicate);
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void GetCreationTime_ReservedMovieFlagsLeaveOtherDateSourcesAvailable(byte version, bool provenance)
    {
        // Arrange
        var movie = MovieHeader(version, (ulong)(new DateTime(2024, 5, 31, 11, 33, 20, DateTimeKind.Utc) - Mp4Epoch).TotalSeconds);
        movie[19] = 1;
        File.WriteAllBytes(_path, [.. movie, .. provenance ? Provenance() : []]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        if (provenance)
        {
            Assert.Equal(new DateTime(2026, 11, 17, 17, 9, 37, DateTimeKind.Utc), result);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Fact]
    public void GetCreationTime_OpeningActionMustLinkToSelectedParent()
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        var assertions = fixture.Active.Child("c2pa.assertions");
        var otherIngredient = ProvenanceTestFixture.Box.Read(assertions.Child("c2pa.ingredient.v3").Encode());
        otherIngredient.RenameLabel("c2pa.ingredient.v3__2");
        var otherData = ProvenanceTestFixture.DecodeMap(otherIngredient.Cbor.Payload);
        otherData["relationship"] = ProvenanceTestFixture.Text("componentOf");
        otherIngredient.Cbor.Payload = ProvenanceTestFixture.EncodeMap(otherData);
        assertions.Children.Add(otherIngredient);
        var claim = fixture.Active.Child("c2pa.claim.v2").Cbor;
        var claimData = ProvenanceTestFixture.DecodeMap(claim.Payload);
        var claimReferences = ProvenanceTestFixture.DecodeArray(claimData["created_assertions"]);
        var additionalReference = ProvenanceTestFixture.DecodeMap(claimReferences[^1]);
        additionalReference["url"] = ProvenanceTestFixture.Text("self#jumbf=c2pa.assertions/c2pa.ingredient.v3__2");
        claimReferences.Add(ProvenanceTestFixture.EncodeMap(additionalReference));
        claimData["created_assertions"] = ProvenanceTestFixture.EncodeArray(claimReferences);
        claim.Payload = ProvenanceTestFixture.EncodeMap(claimData);
        var assertion = fixture.Active.Child("c2pa.assertions").Child("c2pa.actions.v2").Cbor;
        var map = ProvenanceTestFixture.DecodeMap(assertion.Payload);
        var actions = ProvenanceTestFixture.DecodeArray(map["actions"]);
        var opening = ProvenanceTestFixture.DecodeMap(actions[0]);
        var parameters = ProvenanceTestFixture.DecodeMap(opening["parameters"]);
        var ingredients = ProvenanceTestFixture.DecodeArray(parameters["ingredients"]);
        var reference = ProvenanceTestFixture.DecodeMap(ingredients[0]);
        reference["url"] = ProvenanceTestFixture.Text("self#jumbf=c2pa.assertions/c2pa.ingredient.v3__2");
        ingredients[0] = ProvenanceTestFixture.EncodeMap(reference);
        parameters["ingredients"] = ProvenanceTestFixture.EncodeArray(ingredients);
        opening["parameters"] = ProvenanceTestFixture.EncodeMap(parameters);
        actions[0] = ProvenanceTestFixture.EncodeMap(opening);
        map["actions"] = ProvenanceTestFixture.EncodeArray(actions);
        assertion.Payload = ProvenanceTestFixture.EncodeMap(map);
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode()]);

        // Act
        var result = Mp4Parser.GetCreationTime(_path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetCreationTime_ExcessivePathSegmentsHaveBoundedAllocations()
    {
        // Arrange
        var fixture = new ProvenanceTestFixture(Provenance());
        fixture.KeepCreationOnly();
        var claim = fixture.Creation.Child("c2pa.claim.v2").Cbor;
        var fields = ProvenanceTestFixture.DecodeMap(claim.Payload);
        fields["signature"] = ProvenanceTestFixture.Text("self#jumbf=" + new string('/', 100000));
        claim.Payload = ProvenanceTestFixture.EncodeMap(fields);
        File.WriteAllBytes(_path, [.. MovieHeader(0, 0), .. fixture.Encode(refreshHashes: false)]);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = Mp4Parser.GetCreationTime(_path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Null(result);
        Assert.True(allocated < 2 * 1024 * 1024, $"Allocated {allocated} bytes for a rejected path.");
    }

    private static void SetFirstAction(ProvenanceTestFixture.Box manifest, string name)
    {
        var leaf = manifest.Child("c2pa.assertions").Child("c2pa.actions.v2").Cbor;
        var map = ProvenanceTestFixture.DecodeMap(leaf.Payload);
        var actions = ProvenanceTestFixture.DecodeArray(map["actions"]);
        var action = ProvenanceTestFixture.DecodeMap(actions[0]);
        action["action"] = ProvenanceTestFixture.Text(name);
        actions[0] = ProvenanceTestFixture.EncodeMap(action);
        map["actions"] = ProvenanceTestFixture.EncodeArray(actions);
        leaf.Payload = ProvenanceTestFixture.EncodeMap(map);
    }

    private static int ParentReferenceOffset(byte[] provenance, string reference)
    {
        var assertionStart = provenance.AsSpan().LastIndexOf("c2pa.ingredient.v3"u8);
        Assert.True(assertionStart >= 0);
        var writer = new CborWriter();
        writer.WriteTextString(reference);
        var relativeOffset = provenance.AsSpan(assertionStart).IndexOf(writer.Encode());
        Assert.True(relativeOffset >= 0);

        return assertionStart + relativeOffset + writer.Encode().Length - Encoding.ASCII.GetByteCount(reference);
    }

    private static byte[] Provenance() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "notebooklm-provenance.box"));

    private static byte[] MovieHeader(byte version, ulong creation)
    {
        var payload = new byte[version == 1 ? 112 : 100];
        payload[0] = version;
        if (version == 1)
        {
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(4), creation);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)creation);
        }

        return Box("moov", Box("mvhd", payload));
    }

    private static byte[] Box(string type, byte[] content)
    {
        var bytes = new byte[content.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        content.CopyTo(bytes, 8);

        return bytes;
    }

    public void Dispose()
    {
        File.Delete(_path);
    }
}
