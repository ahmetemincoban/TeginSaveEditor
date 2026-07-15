using System.Text;
using System.Text.Json.Nodes;
using SaveEditor.Core;
using SaveEditor.Core.Formats;
using Xunit;

namespace SaveEditor.Tests;

public class NbtTests
{
    private readonly FormatDetector _detector = new();

    /// <summary>Builds a small but representative NBT compound covering
    /// every tag type: byte/short/int/long/float/double, byte/int/long
    /// arrays, string, a nested compound, and lists of int and of
    /// compound (the common "Inventory"-style shape).</summary>
    private static byte[] BuildSampleNbt(out JsonObject expectedRoot)
    {
        var doc = new SaveDocument
        {
            FormatId = "nbt",
            FormatName = "",
            FileName = "level.dat",
            Root = JsonNode.Parse($$"""
            {
              "aByte": { "nbt": "byte", "v": -5 },
              "aShort": { "nbt": "short", "v": 1000 },
              "anInt": 424242,
              "aLong": { "nbt": "long", "v": {{long.MaxValue}} },
              "aFloat": { "nbt": "float", "v": 1.5 },
              "aDouble": { "nbt": "double", "v": 2.5 },
              "aString": "Steve",
              "byteArray": { "nbt": "bytearray", "b64": "AQIDBA==" },
              "intArray": { "nbt": "intarray", "items": [1, 2, 3] },
              "longArray": { "nbt": "longarray", "items": [10, 20, {{long.MaxValue}}] },
              "nested": { "inner": 7 },
              "ints": { "nbt": "list", "type": "int", "items": [1, 2, 3] },
              "items": {
                "nbt": "list",
                "type": "compound",
                "items": [
                  { "id": "sword", "count": 1 },
                  { "id": "shield", "count": 1 }
                ]
              }
            }
            """)!.AsObject(),
        };
        expectedRoot = (JsonObject)doc.Root;
        var format = new NbtFormat();
        return format.Write(doc);
    }

    [Fact]
    public void Nbt_DetectsAndRoundTrips_AllTagTypes()
    {
        byte[] data = BuildSampleNbt(out _);
        var doc = _detector.Detect(data, "level.dat");
        Assert.Equal("nbt", doc.FormatId);

        var root = doc.Root!.AsObject();
        Assert.Equal(-5, root["aByte"]!["v"]!.GetValue<int>());
        Assert.Equal(1000, root["aShort"]!["v"]!.GetValue<int>());
        Assert.Equal(424242, root["anInt"]!.GetValue<int>());
        Assert.Equal(long.MaxValue.ToString(), root["aLong"]!["v"]!.GetValue<string>());
        Assert.Equal(1.5f, root["aFloat"]!["v"]!.GetValue<float>());
        Assert.Equal(2.5, root["aDouble"]!["v"]!.GetValue<double>());
        Assert.Equal("Steve", root["aString"]!.GetValue<string>());
        Assert.Equal("AQIDBA==", root["byteArray"]!["b64"]!.GetValue<string>());
        Assert.Equal(3, root["intArray"]!["items"]!.AsArray().Count);
        Assert.Equal(7, root["nested"]!["inner"]!.GetValue<int>());
        Assert.Equal(3, root["ints"]!["items"]!.AsArray().Count);
        Assert.Equal("sword", root["items"]!["items"]![0]!["id"]!.GetValue<string>());

        byte[] output = _detector.Encode(doc);
        Assert.Equal(data, output);
    }

    [Fact]
    public void Nbt_EditValue_RoundTrips()
    {
        byte[] data = BuildSampleNbt(out _);
        var doc = _detector.Detect(data, "level.dat");
        doc.Root!["anInt"] = 999;
        doc.Root["aString"] = "Alex";

        byte[] output = _detector.Encode(doc);
        var doc2 = _detector.Detect(output, "level.dat");
        Assert.Equal(999, doc2.Root!["anInt"]!.GetValue<int>());
        Assert.Equal("Alex", doc2.Root["aString"]!.GetValue<string>());
    }

    [Fact]
    public void Nbt_GzipWrapped_DetectsAndRoundTrips()
    {
        byte[] inner = BuildSampleNbt(out _);
        byte[] data = new GZipWrapper().Wrap(inner);

        var doc = _detector.Detect(data, "level.dat");
        Assert.Equal("nbt", doc.FormatId);
        Assert.Contains("gzip", doc.Wrappers);

        doc.Root!["anInt"] = 111;
        byte[] output = _detector.Encode(doc);
        var doc2 = _detector.Detect(output, "level.dat");
        Assert.Equal(111, doc2.Root!["anInt"]!.GetValue<int>());
    }

    [Fact]
    public void Nbt_NegativeZeroAndSpecialFloats_RoundTrip()
    {
        var doc = new SaveDocument
        {
            FormatId = "nbt",
            FormatName = "",
            FileName = "x.dat",
            Root = JsonNode.Parse("""
            {
              "nan": { "nbt": "float", "v": "NaN" },
              "negZero": { "nbt": "double", "v": "-0.0" },
              "inf": { "nbt": "double", "v": "Infinity" }
            }
            """)!.AsObject(),
        };
        byte[] data = new NbtFormat().Write(doc);
        var doc2 = _detector.Detect(data, "x.dat");
        Assert.Equal("NaN", doc2.Root!["nan"]!["v"]!.GetValue<string>());
        Assert.Equal("-0.0", doc2.Root["negZero"]!["v"]!.GetValue<string>());
        Assert.Equal("Infinity", doc2.Root["inf"]!["v"]!.GetValue<string>());
    }

    [Fact]
    public void Nbt_WrongExtension_IsNotDetectedAsNbt()
    {
        byte[] data = BuildSampleNbt(out _);
        var doc = _detector.Detect(data, "level.bin");
        Assert.NotEqual("nbt", doc.FormatId);
    }

    [Fact]
    public void Nbt_CorruptData_FallsBackToRawView_WithoutThrowing()
    {
        byte[] data = [10, 0, 0, 3, 0, 1, (byte)'x']; // TAG_Compound, empty name, TAG_Int field header then EOF
        var doc = _detector.Detect(data, "broken.dat");
        Assert.Equal("nbt", doc.FormatId);
        Assert.False(doc.Editable);
        Assert.Equal(data, _detector.Encode(doc));
    }

    [Fact]
    public void Nbt_VeryDeepNesting_RaisesSaveFormatException_NotStackOverflow()
    {
        var deep = new JsonObject();
        var current = deep;
        for (int i = 1; i < 2000; i++)
        {
            var next = new JsonObject();
            current["child"] = next;
            current = next;
        }
        var doc = new SaveDocument { FormatId = "nbt", FormatName = "", FileName = "x.dat", Root = deep };
        Assert.Throws<SaveFormatException>(() => new NbtFormat().Write(doc));
    }
}
