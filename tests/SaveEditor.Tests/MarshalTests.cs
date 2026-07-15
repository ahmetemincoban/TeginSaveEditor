using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SaveEditor.Core;
using SaveEditor.Core.Formats;
using SaveEditor.Core.Marshal;
using Xunit;

namespace SaveEditor.Tests;

public class MarshalReaderWriterTests
{
    // Marshal.dump("hello") from real (documented) CPython-adjacent CRuby
    // output: I (ivar-wrap) " (string, len 5) "hello", 1 ivar: :E => true
    // (Ruby's standard UTF-8 encoding flag, attached to virtually every
    // String literal since Ruby 1.9).
    private static readonly byte[] HelloStringBytes =
        [0x04, 0x08, 0x49, 0x22, 0x0A, 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x06, 0x3A, 0x06, 0x45, 0x54];

    // Marshal.dump({ a: 1 }): { (hash, 1 pair), :a symbol, Fixnum 1.
    private static readonly byte[] SymbolHashBytes =
        [0x04, 0x08, 0x7B, 0x06, 0x3A, 0x06, 0x61, 0x69, 0x06];

    [Fact]
    public void KnownBytes_StringWithEncodingIvar_ParsesAndRoundTrips()
    {
        var str = (RbString)new MarshalReader(HelloStringBytes).Read()!;
        Assert.Equal("hello", Encoding.UTF8.GetString(str.Bytes));
        Assert.NotNull(str.IVars);
        Assert.Equal("E", str.IVars![0].Key.Name);
        Assert.Equal(true, str.IVars[0].Value);

        byte[] rewritten = new MarshalWriter().Write(str);
        Assert.Equal(HelloStringBytes, rewritten);
    }

    [Fact]
    public void KnownBytes_SymbolKeyedHash_ParsesAndRoundTrips()
    {
        var hash = (RbHash)new MarshalReader(SymbolHashBytes).Read()!;
        Assert.Single(hash.Items);
        Assert.Equal("a", ((RbSymbol)hash.Items[0].Key!).Name);
        Assert.Equal(1L, hash.Items[0].Value);

        byte[] rewritten = new MarshalWriter().Write(hash);
        Assert.Equal(SymbolHashBytes, rewritten);
    }

    [Fact]
    public void Primitives_RoundTrip()
    {
        object?[] values = [null, true, false, 0L, 1L, -1L, 122L, -123L, 123L, -124L, 1_000_000L, -1_000_000L];
        foreach (var v in values)
        {
            byte[] data = new MarshalWriter().Write(v);
            object? back = new MarshalReader(data).Read();
            Assert.Equal(v, back);
        }
    }

    [Fact]
    public void LargeFixnum_PromotesToBignum_AndNormalizesBackOnRead()
    {
        // Outside the 4-byte Fixnum length-prefix range.
        long huge = 5_000_000_000L;
        byte[] data = new MarshalWriter().Write(huge);
        object? back = new MarshalReader(data).Read();
        Assert.Equal(huge, back);
        Assert.IsType<long>(back);
    }

    [Fact]
    public void Bignum_RoundTrips()
    {
        var big = BigInteger.Parse("123456789012345678901234567890");
        byte[] data = new MarshalWriter().Write(big);
        object? back = new MarshalReader(data).Read();
        Assert.Equal(big, back);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(0.0)]
    [InlineData(123456.789)]
    public void Float_RoundTrips(double value)
    {
        byte[] data = new MarshalWriter().Write(value);
        object? back = new MarshalReader(data).Read();
        Assert.Equal(value, back);
    }

    [Fact]
    public void Symbol_RoundTrips_AndLinksRepeatOccurrences()
    {
        var sym = new RbSymbol("gold");
        var arr = new RbArray { Items = [sym, sym, sym] };
        byte[] data = new MarshalWriter().Write(arr);
        var back = (RbArray)new MarshalReader(data).Read()!;
        Assert.Equal(3, back.Items.Count);
        Assert.Same(back.Items[0], back.Items[1]);
        Assert.Same(back.Items[1], back.Items[2]);
        Assert.Equal("gold", ((RbSymbol)back.Items[0]!).Name);
    }

    [Fact]
    public void Array_RoundTrips()
    {
        var arr = new RbArray { Items = [1L, new RbString { Bytes = Encoding.UTF8.GetBytes("two") }, 3.0, null, true] };
        byte[] data = new MarshalWriter().Write(arr);
        var back = (RbArray)new MarshalReader(data).Read()!;
        Assert.Equal(1L, back.Items[0]);
        Assert.Equal("two", Encoding.UTF8.GetString(((RbString)back.Items[1]!).Bytes));
        Assert.Equal(3.0, back.Items[2]);
        Assert.Null(back.Items[3]);
        Assert.Equal(true, back.Items[4]);
    }

    [Fact]
    public void ObjectWithIVars_RoundTrips()
    {
        var obj = new RbObject { ClassName = new RbSymbol("Game_Actor") };
        obj.IVars.Add(new(new RbSymbol("@name"), new RbString { Bytes = Encoding.UTF8.GetBytes("Harold") }));
        obj.IVars.Add(new(new RbSymbol("@hp"), 480L));

        byte[] data = new MarshalWriter().Write(obj);
        var back = (RbObject)new MarshalReader(data).Read()!;
        Assert.Equal("Game_Actor", back.ClassName.Name);
        Assert.Equal("@name", back.IVars[0].Key.Name);
        Assert.Equal("Harold", Encoding.UTF8.GetString(((RbString)back.IVars[0].Value!).Bytes));
        Assert.Equal(480L, back.IVars[1].Value);
    }

    [Fact]
    public void ObjectLink_SharedReference_Preserved()
    {
        var shared = new RbArray { Items = [1L, 2L] };
        var root = new RbArray { Items = [shared, shared] };
        byte[] data = new MarshalWriter().Write(root);
        var back = (RbArray)new MarshalReader(data).Read()!;
        Assert.Same(back.Items[0], back.Items[1]);
    }

    [Fact]
    public void Cycle_Survives()
    {
        var arr = new RbArray();
        arr.Items.Add(arr);
        byte[] data = new MarshalWriter().Write(arr);
        var back = (RbArray)new MarshalReader(data).Read()!;
        Assert.Same(back, back.Items[0]);
    }

    [Fact]
    public void VeryDeepNesting_RaisesSaveFormatException_NotStackOverflow()
    {
        RbArray root = new();
        RbArray current = root;
        for (int i = 1; i < 5000; i++)
        {
            var next = new RbArray();
            current.Items.Add(next);
            current = next;
        }
        Assert.Throws<SaveFormatException>(() => new MarshalWriter().Write(root));
    }

    [Fact]
    public void TruncatedData_ThrowsSaveFormatException()
    {
        byte[] data = [0x04, 0x08, (byte)'[', 0x06]; // Array header claiming 1 element, then EOF
        Assert.Throws<SaveFormatException>(() => new MarshalReader(data).Read());
    }
}

public class MarshalJsonBridgeTests
{
    [Fact]
    public void JsonConversion_RoundTrips_ObjectWithSharedString()
    {
        var name = new RbString { Bytes = Encoding.UTF8.GetBytes("Yoruichi") };
        var obj1 = new RbObject { ClassName = new RbSymbol("Actor") };
        obj1.IVars.Add(new(new RbSymbol("@name"), name));
        var obj2 = new RbObject { ClassName = new RbSymbol("Actor") };
        obj2.IVars.Add(new(new RbSymbol("@name"), name));
        var root = new RbArray { Items = [obj1, obj2] };

        var json = RbJson.ToJson(root);
        object? back = RbJson.FromJson(json);
        byte[] pickled = new MarshalWriter().Write(back);
        var result = (RbArray)new MarshalReader(pickled).Read()!;

        var r1 = (RbObject)result.Items[0]!;
        var r2 = (RbObject)result.Items[1]!;
        Assert.Same(r1.IVars[0].Value, r2.IVars[0].Value);
    }

    [Fact]
    public void JsonConversion_SymbolKeyedHash_UsesPlainObjectShape()
    {
        var hash = new RbHash();
        hash.Items.Add(new(new RbSymbol("gold"), 500L));
        hash.Items.Add(new(new RbSymbol("name"), new RbString { Bytes = Encoding.UTF8.GetBytes("Ali") }));

        var json = RbJson.ToJson(hash)!.AsObject();
        Assert.Equal(500L, json["gold"]!.GetValue<long>());
        Assert.Equal("Ali", json["name"]!.GetValue<string>());

        object? back = RbJson.FromJson(json);
        byte[] data = new MarshalWriter().Write(back);
        var result = (RbHash)new MarshalReader(data).Read()!;
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("gold", ((RbSymbol)result.Items[0].Key!).Name);
    }

    [Fact]
    public void JsonConversion_NonSymbolKeyedHash_UsesTaggedForm()
    {
        var hash = new RbHash();
        hash.Items.Add(new(new RbString { Bytes = Encoding.UTF8.GetBytes("plain-string-key") }, 1L));

        var json = RbJson.ToJson(hash)!.AsObject();
        Assert.Equal("hash", json["rb"]!.GetValue<string>());

        object? back = RbJson.FromJson(json);
        byte[] data = new MarshalWriter().Write(back);
        var result = (RbHash)new MarshalReader(data).Read()!;
        Assert.Equal("plain-string-key", Encoding.UTF8.GetString(((RbString)result.Items[0].Key!).Bytes));
    }

    [Fact]
    public void JsonConversion_LargeIntegerTagged_ForBrowserSafety()
    {
        long value = 9_007_199_254_740_993L; // 2^53 + 1
        var json = RbJson.ToJson(value);
        var tagged = Assert.IsType<JsonObject>(json);
        Assert.Equal("int", tagged["rb"]!.GetValue<string>());

        var reparsed = JsonNode.Parse(json!.ToJsonString());
        object? back = RbJson.FromJson(reparsed);
        Assert.Equal(value, back);
    }

    [Fact]
    public void JsonConversion_Cycle_Survives()
    {
        var arr = new RbArray();
        arr.Items.Add(arr);
        var json = RbJson.ToJson(arr);
        object? back = RbJson.FromJson(json);
        var backArr = (RbArray)back!;
        Assert.Same(backArr, backArr.Items[0]);
    }
}

public class RubyMarshalFormatTests
{
    private readonly FormatDetector _detector = new();

    [Fact]
    public void DetectsBySignatureAndExtension_AndRoundTrips()
    {
        var actor = new RbObject { ClassName = new RbSymbol("Game_Actor") };
        actor.IVars.Add(new(new RbSymbol("@name"), new RbString { Bytes = Encoding.UTF8.GetBytes("Kaiden") }));
        actor.IVars.Add(new(new RbSymbol("@hp"), 350L));
        byte[] data = new MarshalWriter().Write(actor);

        var doc = _detector.Detect(data, "Actors.rvdata2");
        Assert.Equal("marshal", doc.FormatId);

        var ivars = doc.Root!["ivars"]!.AsArray();
        var hpPair = ivars.First(p => p![0]!.GetValue<string>() == "@hp")!;
        Assert.Equal(350L, hpPair[1]!.GetValue<long>());

        hpPair[1] = 999;
        byte[] output = _detector.Encode(doc);
        var doc2 = _detector.Detect(output, "Actors.rvdata2");
        var ivars2 = doc2.Root!["ivars"]!.AsArray();
        var hpPair2 = ivars2.First(p => p![0]!.GetValue<string>() == "@hp")!;
        Assert.Equal(999L, hpPair2[1]!.GetValue<long>());
    }

    [Fact]
    public void WrongExtension_IsNotDetectedAsMarshal()
    {
        var data = new MarshalWriter().Write(1L);
        var doc = _detector.Detect(data, "notes.txt");
        Assert.NotEqual("marshal", doc.FormatId);
    }

    [Fact]
    public void CorruptData_FallsBackToRawView_WithoutThrowing()
    {
        byte[] data = [0x04, 0x08, (byte)'o', 0x00]; // "object" tag, then EOF
        var doc = _detector.Detect(data, "broken.rvdata2");
        Assert.Equal("marshal", doc.FormatId);
        Assert.False(doc.Editable);
        Assert.Equal(data, _detector.Encode(doc));
    }
}
