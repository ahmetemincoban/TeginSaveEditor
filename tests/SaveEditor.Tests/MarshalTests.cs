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
        // Outside the signed int32 range, so real Ruby dumps it as Bignum.
        long huge = 5_000_000_000L;
        byte[] data = new MarshalWriter().Write(huge);
        object? back = new MarshalReader(data).Read();
        Assert.Equal(huge, back);
        Assert.IsType<long>(back);
    }

    [Fact]
    public void FixnumBeyondInt32Range_PromotesToBignum_MatchingRealRubyBytes()
    {
        // Marshal.dump(3_000_000_000) in real CPython-adjacent CRuby:
        // 3e9 fits in 4 little-endian bytes but exceeds int32.MaxValue, so
        // Ruby dumps it as Bignum, not Fixnum (marshal.c: RSHIFT(v,31)==0||-1).
        // A 32-bit-`long` Ruby build (still true of Windows Ruby) would read
        // a naive 4-byte Fixnum encoding of this value back as negative.
        byte[] expected = [0x04, 0x08, 0x6C, 0x2B, 0x07, 0x00, 0x5E, 0xD0, 0xB2];
        byte[] actual = new MarshalWriter().Write(3_000_000_000L);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Int32BoundaryValues_StayFixnum_OneBeyondPromotesToBignum()
    {
        Assert.Equal((byte)'i', new MarshalWriter().Write((long)int.MaxValue)[2]);
        Assert.Equal((byte)'i', new MarshalWriter().Write((long)int.MinValue)[2]);
        Assert.Equal((byte)'l', new MarshalWriter().Write((long)int.MaxValue + 1)[2]);
        Assert.Equal((byte)'l', new MarshalWriter().Write((long)int.MinValue - 1)[2]);
    }

    [Fact]
    public void FixnumBeyondInt32Range_ReadsBackAndRoundTripsThroughJson()
    {
        byte[] realRubyBytes = [0x04, 0x08, 0x6C, 0x2B, 0x07, 0x00, 0x5E, 0xD0, 0xB2];
        object? value = new MarshalReader(realRubyBytes).Read();
        Assert.Equal(3_000_000_000L, value);

        var json = RbJson.ToJson(value);
        object? back = RbJson.FromJson(json);
        byte[] rewritten = new MarshalWriter().Write(back);
        Assert.Equal(realRubyBytes, rewritten);
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

    // Real Marshal.dump([a, a]) output for each wrapper kind, where `a` is
    // shared (referenced twice via the same array). The trailing '@' opcode
    // only resolves to the correct object if the link table numbering
    // matches real Ruby's: 'C' and 'e' share one slot between the wrapper
    // and its wrapped content (it's the same underlying Ruby object); 'U'
    // gets two slots (marshal_dump returns a genuinely separate object).
    // Before the V2 fix, the reader over-registered 'C'/'e' content into an
    // extra slot, so this '@' resolved to the wrong object.

    // class MyArr < Array; end; a = MyArr.new; a << 1 << 2; Marshal.dump([a, a])
    private static readonly byte[] UserClassSharedTwiceBytes =
        [0x04, 0x08, 0x5b, 0x07, 0x43, 0x3a, 0x0a, 0x4d, 0x79, 0x41, 0x72, 0x72, 0x5b, 0x07, 0x69, 0x06, 0x69, 0x07, 0x40, 0x06];

    // class MyU; def initialize(v); @v=v; end; def marshal_dump; @v; end; def marshal_load(v); @v=v; end; end
    // u = MyU.new("hello"); Marshal.dump([u, u])
    private static readonly byte[] UserMarshalSharedTwiceBytes =
        [0x04, 0x08, 0x5b, 0x07, 0x55, 0x3a, 0x08, 0x4d, 0x79, 0x55, 0x49, 0x22, 0x0a, 0x68, 0x65, 0x6c, 0x6c, 0x6f, 0x06, 0x3a, 0x06, 0x45, 0x54, 0x40, 0x06];

    // module Ext; def foo; end; end; obj = Object.new; obj.extend(Ext); Marshal.dump([obj, obj])
    private static readonly byte[] ExtendedSharedTwiceBytes =
        [0x04, 0x08, 0x5b, 0x07, 0x65, 0x3a, 0x08, 0x45, 0x78, 0x74, 0x6f, 0x3a, 0x0b, 0x4f, 0x62, 0x6a, 0x65, 0x63, 0x74, 0x00, 0x40, 0x06];

    [Fact]
    public void KnownBytes_UserClassSharedTwice_ResolvesSameObject()
    {
        var arr = (RbArray)new MarshalReader(UserClassSharedTwiceBytes).Read()!;
        Assert.Same(arr.Items[0], arr.Items[1]);
        Assert.IsType<RbUserClass>(arr.Items[0]);
    }

    [Fact]
    public void KnownBytes_UserMarshalSharedTwice_ResolvesSameObject()
    {
        var arr = (RbArray)new MarshalReader(UserMarshalSharedTwiceBytes).Read()!;
        Assert.Same(arr.Items[0], arr.Items[1]);
        Assert.IsType<RbUserMarshal>(arr.Items[0]);
    }

    [Fact]
    public void KnownBytes_ExtendedSharedTwice_ResolvesSameObject()
    {
        var arr = (RbArray)new MarshalReader(ExtendedSharedTwiceBytes).Read()!;
        Assert.Same(arr.Items[0], arr.Items[1]);
        Assert.IsType<RbExtended>(arr.Items[0]);
    }

    [Theory]
    [InlineData("KnownBytes_UserClassSharedTwice_ResolvesSameObject")]
    [InlineData("KnownBytes_UserMarshalSharedTwice_ResolvesSameObject")]
    [InlineData("KnownBytes_ExtendedSharedTwice_ResolvesSameObject")]
    public void KnownBytes_AlsoRoundTripThroughOwnWriter(string caseName)
    {
        byte[] source = caseName switch
        {
            "KnownBytes_UserClassSharedTwice_ResolvesSameObject" => UserClassSharedTwiceBytes,
            "KnownBytes_UserMarshalSharedTwice_ResolvesSameObject" => UserMarshalSharedTwiceBytes,
            _ => ExtendedSharedTwiceBytes,
        };
        object? model = new MarshalReader(source).Read();
        byte[] rewritten = new MarshalWriter().Write(model);
        var reread = (RbArray)new MarshalReader(rewritten).Read()!;
        Assert.Same(reread.Items[0], reread.Items[1]);
    }

    public static IEnumerable<object[]> WriterModelFactories()
    {
        yield return
        [
            "usermarshal",
            () =>
            {
                var um = new RbUserMarshal { ClassName = new RbSymbol("MyU"), Data = new RbArray { Items = [9L, 9L] } };
                return (object)new RbArray { Items = [um, um] };
            },
        ];
        yield return
        [
            "extended",
            () =>
            {
                var ext = new RbExtended { ModuleName = new RbSymbol("Ext"), Value = new RbObject { ClassName = new RbSymbol("Object") } };
                return (object)new RbArray { Items = [ext, ext] };
            },
        ];
        yield return
        [
            "userclass",
            () =>
            {
                var uc = new RbUserClass { ClassName = new RbSymbol("MyArr"), Wrapped = new RbArray { Items = [1L, 2L] } };
                return (object)new RbArray { Items = [uc, uc] };
            },
        ];
    }

    [SkippableTheory]
    [MemberData(nameof(WriterModelFactories))]
    public void Writer_SharedWrappedValue_LoadsCorrectlyInRealRuby(string _, Func<object> buildModel)
    {
        string? rubyExe = FindRuby();
        Skip.If(rubyExe is null, "ruby yerelde bulunamadı");

        byte[] data = new MarshalWriter().Write(buildModel());
        string tmp = Path.Combine(Path.GetTempPath(), "se-marshal-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tmp, data);
        try
        {
            string script = Path.Combine(AppContext.BaseDirectory, "Scripts", "marshal_link_check.rb");
            Skip.If(!File.Exists(script), "marshal_link_check.rb çıktı dizinine kopyalanmamış");

            var psi = new System.Diagnostics.ProcessStartInfo(rubyExe, $"\"{script}\" \"{tmp}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, $"ruby marshal_link_check.rb reported failure:\n{stdout}\n{stderr}");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static string? FindRuby()
    {
        // Bare "ruby" first (the normal case once PATH is refreshed); RubyInstaller's
        // default install locations as a fallback, since a PATH change from an
        // installer doesn't propagate to a shell that was already running.
        var candidates = new List<string> { "ruby" };
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(@"C:\", "Ruby*-x64").OrderDescending())
                candidates.Add(Path.Combine(dir, "bin", "ruby.exe"));
        }
        catch
        {
            // best-effort probing only
        }
        foreach (string candidate in candidates)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit();
                if (proc.ExitCode == 0) return candidate;
            }
            catch
            {
                // try the next candidate
            }
        }
        return null;
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
