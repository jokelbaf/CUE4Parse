using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace CUE4Parse.UE4.Assets.Exports.Texture;

public class UOodleTextureStorageProviderFactory : UTextureAllMipDataProviderFactory
{
    private const int HeaderPrefixSize = 20;
    private const int ModeCount = 10;
    private const int MinimumSerializedSize = HeaderPrefixSize + 11 * sizeof(int) + ModeCount * sizeof(int);

    public int SizeX { get; private set; }
    public int SizeY { get; private set; }
    public uint OodleFlags { get; private set; }
    public int[] ModeCounts { get; private set; } = [];

    [JsonIgnore]
    public byte[] Data { get; private set; } = [];

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        base.Deserialize(Ar, validPos);

        if (Ar.Game != GAME_WutheringWaves || validPos - Ar.Position < MinimumSerializedSize)
        {
            Ar.Position = validPos;
            return;
        }

        Ar.Position += HeaderPrefixSize;
        SizeX = Ar.Read<int>();
        SizeY = Ar.Read<int>();
        Ar.Position += 2 * sizeof(int);
        _ = Ar.Read<int>();
        OodleFlags = Ar.Read<uint>();
        ModeCounts = Ar.ReadArray<int>(ModeCount);
        Data = new FByteBulkData(Ar).Data;
        Ar.Position = validPos;
    }

    public bool TryCreatePlatformData(out FTexturePlatformData platformData)
    {
        platformData = new FTexturePlatformData();
        if (SizeX <= 0 || SizeY <= 0 || Data.Length == 0 || ModeCounts.Length != ModeCount)
            return false;

        platformData = new FTexturePlatformData(SizeX, SizeY, EPixelFormat.PF_BC7, new Lazy<byte[]?>(Decode));
        return true;
    }

    private byte[] Decode()
    {
        var expectedSize = ((SizeX + 3) / 4) * ((SizeY + 3) / 4) * 16;
        var decoded = BC7PrepDecoder.Decode(Data, ModeCounts, OodleFlags);
        if (decoded is null || decoded.Length != expectedSize)
            throw new ParserException("Failed to decode Oodle BC7Prep texture data");

        return decoded;
    }
}
