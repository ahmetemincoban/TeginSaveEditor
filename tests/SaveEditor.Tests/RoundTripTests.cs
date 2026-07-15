using System.Text;
using System.Text.Json.Nodes;
using SaveEditor.Core;
using SaveEditor.Core.Formats;
using SaveEditor.Core.Pickle;
using Xunit;

namespace SaveEditor.Tests;

public class LzStringTests
{
    [Theory]
    [InlineData("hello world")]
    [InlineData("{\"gold\":1234,\"name\":\"Kahraman\",\"items\":[1,2,3]}")]
    [InlineData("")]
    [InlineData("ğüşiöçĞÜŞİÖÇ 日本語 🎮")]
    public void CompressDecompress_RoundTrips(string input)
    {
        string compressed = LzString.CompressToBase64(input);
        Assert.Equal(input, LzString.DecompressFromBase64(compressed));
    }

    [Fact]
    public void LongPayload_RoundTrips()
    {
        string payload = new string('x', 10000) + "{\"a\":1}" + string.Concat(Enumerable.Range(0, 500).Select(i => $"k{i}=v{i};"));
        Assert.Equal(payload, LzString.DecompressFromBase64(LzString.CompressToBase64(payload)));
    }
}

public class DetectorTests
{
    private readonly FormatDetector _detector = new();

    [Fact]
    public void PlainJson_DetectsAndRoundTrips()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"gold\":500,\"party\":[\"Ayşe\",\"Ali\"]}");
        var doc = _detector.Detect(data, "save1.save");
        Assert.Equal("json", doc.FormatId);
        doc.Root!["gold"] = 9999;
        byte[] output = _detector.Encode(doc);
        var doc2 = _detector.Detect(output, "save1.save");
        Assert.Equal(9999, doc2.Root!["gold"]!.GetValue<int>());
    }

    [Fact]
    public void CompactJson_StaysCompact_AfterEdit()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"gold\":500,\"party\":[\"Ayşe\",\"Ali\"]}");
        var doc = _detector.Detect(data, "save1.save");
        doc.Root!["gold"] = 9999;
        byte[] output = _detector.Encode(doc);
        Assert.DoesNotContain((byte)'\n', output);
    }

    [Fact]
    public void IndentedJson_StaysIndented_AfterEdit()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\n  \"gold\": 500,\n  \"party\": [\n    \"Ayşe\",\n    \"Ali\"\n  ]\n}");
        var doc = _detector.Detect(data, "save1.save");
        doc.Root!["gold"] = 9999;
        byte[] output = _detector.Encode(doc);
        string outText = Encoding.UTF8.GetString(output);
        Assert.Contains('\n', outText);
        Assert.Contains("  \"gold\"", outText);

        var doc2 = _detector.Detect(output, "save1.save");
        Assert.Equal(9999, doc2.Root!["gold"]!.GetValue<int>());
    }

    [Fact]
    public void RpgMakerMv_LzString_RoundTrips()
    {
        string json = "{\"system\":{\"versionId\":42},\"actors\":{\"@a\":[null,{\"_hp\":100,\"_name\":\"Harold\"}]},\"gold\":{\"@c\":\"Game_Gold\",\"_value\":777}}";
        byte[] data = Encoding.ASCII.GetBytes(LzString.CompressToBase64(json));

        var doc = _detector.Detect(data, "file1.rpgsave");
        Assert.Equal("json", doc.FormatId);
        Assert.Contains("lzstring-base64", doc.Wrappers);
        Assert.Equal(777, doc.Root!["gold"]!["_value"]!.GetValue<int>());

        doc.Root["gold"]!["_value"] = 123456;
        byte[] output = _detector.Encode(doc);
        var doc2 = _detector.Detect(output, "file1.rpgsave");
        Assert.Equal(123456, doc2.Root!["gold"]!["_value"]!.GetValue<int>());
    }

    [Fact]
    public void RpgMakerMz_Zlib_RoundTrips()
    {
        string json = "{\"gold\":{\"_value\":50}}";
        byte[] data = new ZlibWrapper().Wrap(Encoding.UTF8.GetBytes(json));

        var doc = _detector.Detect(data, "file1.rmmzsave");
        Assert.Equal("json", doc.FormatId);
        Assert.Contains("zlib", doc.Wrappers);

        doc.Root!["gold"]!["_value"] = 42;
        var doc2 = _detector.Detect(_detector.Encode(doc), "file1.rmmzsave");
        Assert.Equal(42, doc2.Root!["gold"]!["_value"]!.GetValue<int>());
    }

    [Fact]
    public void GzipJson_RoundTrips()
    {
        byte[] data = new GZipWrapper().Wrap(Encoding.UTF8.GetBytes("{\"hp\":10}"));
        var doc = _detector.Detect(data, "save.dat");
        Assert.Equal("json", doc.FormatId);
        Assert.Contains("gzip", doc.Wrappers);
        var doc2 = _detector.Detect(_detector.Encode(doc), "save.dat");
        Assert.Equal(10, doc2.Root!["hp"]!.GetValue<int>());
    }

    [Fact]
    public void Base64WrappedJson_RoundTrips()
    {
        byte[] data = Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"lvl\":3}")));
        var doc = _detector.Detect(data, "save.txt");
        Assert.Equal("json", doc.FormatId);
        Assert.Contains("base64", doc.Wrappers);
    }

    [Fact]
    public void LineWrappedBase64Json_DetectsAndRoundTrips()
    {
        string json = "{\"party\":[\"Ayşe\",\"Ali\",\"Deniz\"],\"gold\":250}";
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        // Classic RFC 2045 76-column wrapping.
        var sb = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 76)
        {
            sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append('\n');
        }
        byte[] data = Encoding.ASCII.GetBytes(sb.ToString());

        var doc = _detector.Detect(data, "save.txt");
        Assert.Equal("json", doc.FormatId);
        Assert.Contains("base64", doc.Wrappers);
        Assert.Equal(250, doc.Root!["gold"]!.GetValue<int>());

        doc.Root["gold"] = 999;
        var doc2 = _detector.Detect(_detector.Encode(doc), "save.txt");
        Assert.Equal(999, doc2.Root!["gold"]!.GetValue<int>());
    }

    [Fact]
    public void PlainText_FallsBack()
    {
        var doc = _detector.Detect(Encoding.UTF8.GetBytes("merhaba dünya\nsatır 2"), "notes.txt");
        Assert.Equal("text", doc.FormatId);
        byte[] output = _detector.Encode(doc);
        Assert.Equal("merhaba dünya\nsatır 2", Encoding.UTF8.GetString(output));
    }

    [Fact]
    public void Utf16LeXml_RoundTrips_ByteIdentical_WhenUnedited()
    {
        string xml = "<save><gold>250</gold><name>Ayşe</name></save>";
        byte[] data = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();

        var doc = _detector.Detect(data, "save.xml");
        Assert.Equal("xml", doc.FormatId);
        Assert.Equal(xml, doc.Root!.GetValue<string>());

        // Unedited: writing it back must reproduce the exact UTF-16LE + BOM bytes.
        Assert.Equal(data, _detector.Encode(doc));
    }

    [Fact]
    public void Utf16BeText_RoundTrips_SameEncoding()
    {
        string text = "seviye=5\ncan=100";
        byte[] data = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();

        var doc = _detector.Detect(data, "notes.txt");
        Assert.Equal("text", doc.FormatId);
        Assert.Equal(text, doc.Root!.GetValue<string>());
        Assert.Equal(data, _detector.Encode(doc));
    }

    [Fact]
    public void UnknownBinary_FallsBackToBase64View()
    {
        byte[] data = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00, 0x99];
        var doc = _detector.Detect(data, "save.bin");
        Assert.Equal("binary", doc.FormatId);
        Assert.Equal(data, _detector.Encode(doc));
    }

    [Fact]
    public void Ini_RoundTrips()
    {
        byte[] data = Encoding.UTF8.GetBytes("[player]\ngold=100\nname=Ali\n\n[settings]\nvolume=0.8\n");
        var doc = _detector.Detect(data, "config.ini");
        Assert.Equal("ini", doc.FormatId);
        Assert.Equal("100", doc.Root!["player"]!["gold"]!.GetValue<string>());
        doc.Root["player"]!["gold"] = "5000";
        var doc2 = _detector.Detect(_detector.Encode(doc), "config.ini");
        Assert.Equal("5000", doc2.Root!["player"]!["gold"]!.GetValue<string>());
    }

    [Fact]
    public void Ini_DuplicateKeyInSection_FallsBackToPlainText_NoDataLoss()
    {
        byte[] data = Encoding.UTF8.GetBytes("[player]\ngold=100\ngold=200\n");
        var doc = _detector.Detect(data, "config.ini");
        Assert.Equal("ini", doc.FormatId);
        Assert.Contains(doc.Warnings, w => w.Contains("Yinelenen"));
        Assert.Equal(data, _detector.Encode(doc));
    }

    [Fact]
    public void Ini_DuplicateSectionName_FallsBackToPlainText_NoDataLoss()
    {
        byte[] data = Encoding.UTF8.GetBytes("[player]\ngold=100\n\n[player]\nname=Ali\n");
        var doc = _detector.Detect(data, "config.ini");
        Assert.Equal("ini", doc.FormatId);
        Assert.Contains(doc.Warnings, w => w.Contains("Yinelenen"));
        Assert.Equal(data, _detector.Encode(doc));
    }
}

public class Es3Tests
{
    private readonly FormatDetector _detector = new();

    [Fact]
    public void PlainEs3_RoundTrips()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"coins\":{\"__type\":\"int\",\"value\":250}}");
        var doc = _detector.Detect(data, "SaveFile.es3");
        Assert.Equal("es3", doc.FormatId);
        doc.Root!["coins"]!["value"] = 9000;
        var doc2 = _detector.Detect(_detector.Encode(doc), "SaveFile.es3");
        Assert.Equal(9000, doc2.Root!["coins"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void EncryptedEs3_DefaultPassword_RoundTrips()
    {
        // Build an encrypted file the way ES3 does, then verify we can open and re-save it.
        var format = new Es3Format();
        byte[] plain = Encoding.UTF8.GetBytes("{\"coins\":{\"value\":1}}");
        var doc0 = new SaveDocument { FormatId = "es3", FormatName = "", FileName = "s.es3", Root = JsonNode.Parse("{\"coins\":{\"value\":1}}") };
        doc0.State["encrypted"] = true;
        doc0.State["gzip"] = false;
        doc0.State["password"] = "password";
        byte[] encrypted = format.Write(doc0);
        Assert.False(Encoding.UTF8.GetString(encrypted).Contains("coins"));

        var doc = _detector.Detect(encrypted, "s.es3");
        Assert.Equal("es3", doc.FormatId);
        Assert.Equal(1, doc.Root!["coins"]!["value"]!.GetValue<int>());

        doc.Root["coins"]!["value"] = 777;
        var doc2 = _detector.Detect(_detector.Encode(doc), "s.es3");
        Assert.Equal(777, doc2.Root!["coins"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void UnsupportedBinaryEs3_ErrorMentionsBothBinaryAndPassword()
    {
        // Not JSON, not gzip, and random bytes essentially never decrypt to
        // valid JSON under any candidate password: this is ES3's actual
        // (unsupported) binary serializer, not a wrong-password case.
        byte[] data = new byte[64];
        new Random(42).NextBytes(data);

        var ex = Assert.Throws<SaveFormatException>(() => _detector.Detect(data, "SaveFile.es3"));
        Assert.Contains("ikili", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("şifre", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class PickleTests
{
    [Fact]
    public void InvalidBinget_ReportsMemoErrorWithPosition()
    {
        // PROTO 2, BINGET id=0 (never PUT), STOP.
        byte[] data = [0x80, 0x02, (byte)'h', 0x00, (byte)'.'];
        var ex = Assert.Throws<SaveFormatException>(() => new PickleReader(data).Read());
        Assert.Contains("memo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opcode", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("konum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Primitives_RoundTrip()
    {
        var dict = new PyDict();
        dict.Items.Add(new("gold", 1234L));
        dict.Items.Add(new("name", "Ayşe"));
        dict.Items.Add(new("hp", 99.5));
        dict.Items.Add(new("alive", true));
        dict.Items.Add(new("nothing", null));

        byte[] pickled = new PickleWriter().Write(dict);
        var result = (PyDict)new PickleReader(pickled).Read()!;
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(1234L, result.Items[0].Value);
        Assert.Equal("Ayşe", result.Items[1].Value);
        Assert.Equal(99.5, result.Items[2].Value);
        Assert.Equal(true, result.Items[3].Value);
        Assert.Null(result.Items[4].Value);
    }

    [Fact]
    public void SharedReference_Preserved()
    {
        var shared = new PyList { Items = [1L, 2L] };
        var root = new PyList { Items = [shared, shared] };

        byte[] pickled = new PickleWriter().Write(root);
        var result = (PyList)new PickleReader(pickled).Read()!;
        Assert.Same(result.Items[0], result.Items[1]);
    }

    [Fact]
    public void Cycle_Survives()
    {
        var list = new PyList();
        list.Items.Add(list);
        byte[] pickled = new PickleWriter().Write(list);
        var result = (PyList)new PickleReader(pickled).Read()!;
        Assert.Same(result, result.Items[0]);
    }

    [Fact]
    public void ObjectWithState_RoundTrips()
    {
        var obj = new PyObject
        {
            Kind = "newobj",
            Callable = new PyGlobal { Module = "renpy.python", Name = "RevertableDict" },
            HasState = true,
        };
        var stateDict = new PyDict();
        stateDict.Items.Add(new("points", 42L));
        obj.State = stateDict;

        byte[] pickled = new PickleWriter().Write(obj);
        var result = (PyObject)new PickleReader(pickled).Read()!;
        Assert.Equal("newobj", result.Kind);
        Assert.Equal("RevertableDict", ((PyGlobal)result.Callable!).Name);
        Assert.Equal(42L, ((PyDict)result.State!).Items[0].Value);
    }

    [Fact]
    public void JsonConversion_RoundTrips()
    {
        var tuple = new PyTuple { Items = [1L, "iki", 3.5] };
        var dict = new PyDict();
        dict.Items.Add(new("pos", tuple));
        dict.Items.Add(new("flags", new PySet { Items = ["a", "b"] }));
        dict.Items.Add(new("big", System.Numerics.BigInteger.Parse("123456789012345678901234567890")));

        var json = PyJson.ToJson(dict);
        object? back = PyJson.FromJson(json);
        byte[] pickled = new PickleWriter().Write(back);
        var result = (PyDict)new PickleReader(pickled).Read()!;
        Assert.Equal("pos", result.Items[0].Key);
        Assert.Equal(3, ((PyTuple)result.Items[0].Value!).Items.Count);
        Assert.IsType<PySet>(result.Items[1].Value);
    }

    [Fact]
    public void ObjectWithSetItemsAndAppends_RoundTrips()
    {
        // OrderedDict / Ren'Py RevertableDict pattern: REDUCE + SETITEMS,
        // RevertableList pattern: REDUCE + APPENDS.
        var dictObj = new PyObject
        {
            Kind = "reduce",
            Callable = new PyGlobal { Module = "collections", Name = "OrderedDict" },
            SetItems = new PyDict(),
        };
        dictObj.SetItems.Items.Add(new("hp", 55L));
        var listObj = new PyObject
        {
            Kind = "newobj",
            Callable = new PyGlobal { Module = "renpy.revertable", Name = "RevertableList" },
            Appends = new PyList { Items = ["kılıç", "kalkan"] },
        };
        var root = new PyList { Items = [dictObj, listObj] };

        var json = PyJson.ToJson(root);
        object? back = PyJson.FromJson(json);
        byte[] pickled = new PickleWriter().Write(back);
        var result = (PyList)new PickleReader(pickled).Read()!;
        var d = (PyObject)result.Items[0]!;
        Assert.Equal(55L, d.SetItems!.Items[0].Value);
        var l = (PyObject)result.Items[1]!;
        Assert.Equal("kalkan", l.Appends!.Items[1]);
    }

    [Fact]
    public void RealCPythonProtocol4_SetAndFrozenset_Parse()
    {
        // pickle.dumps({'kume': {1, 2}, 'donmus': frozenset({'a'}), 'liste': [True]}, protocol=4)
        // Regression: ADDITEMS is 0x90 and FROZENSET is 0x91 (they were swapped once).
        byte[] data = Convert.FromHexString(
            "80049530000000000000007d94288c046b756d65948f94284b014b02908c06646f6e6d757394288c01619491948c056c69737465945d948861752e");
        var result = (PyDict)new PickleReader(data).Read()!;
        Assert.Equal(3, result.Items.Count);
        var kume = Assert.IsType<PySet>(result.Items[0].Value);
        Assert.False(kume.Frozen);
        Assert.Equal([1L, 2L], kume.Items);
        var donmus = Assert.IsType<PySet>(result.Items[1].Value);
        Assert.True(donmus.Frozen);
        Assert.Equal(["a"], donmus.Items);

        // And our writer's set opcodes must be readable again.
        byte[] rewritten = new PickleWriter().Write(result, 4);
        var again = (PyDict)new PickleReader(rewritten).Read()!;
        Assert.Equal(2, ((PySet)again.Items[0].Value!).Items.Count);
    }

    [Fact]
    public void IntegralFloat_StaysFloat()
    {
        var dict = new PyDict();
        dict.Items.Add(new("ratio", 1.0));
        var json = PyJson.ToJson(dict);
        object? back = PyJson.FromJson(json);
        Assert.Equal(1.0, ((PyDict)back!).Items[0].Value);
        Assert.IsType<double>(((PyDict)back).Items[0].Value);
    }

    [Fact]
    public void LargeLong_TaggedInJson_SoBrowserRoundTripPreservesPrecision()
    {
        // 2^53 + 1: the smallest positive integer a JS Number cannot represent exactly.
        long value = 9_007_199_254_740_993L;
        var dict = new PyDict();
        dict.Items.Add(new("id", value));

        byte[] pickled = new PickleWriter().Write(dict);
        var jsonNode = PyJson.ToJson(PyJson.FromJson(PyJson.ToJson(new PickleReader(pickled).Read())));

        // The value must be carried as a tagged string, not a bare JSON number,
        // so a JS JSON.parse/stringify round trip cannot mangle it.
        var tagged = Assert.IsType<System.Text.Json.Nodes.JsonObject>(jsonNode!["id"]);
        Assert.Equal("long", tagged["py"]!.GetValue<string>());
        Assert.Equal(value.ToString(), tagged["v"]!.GetValue<string>());

        // Simulate the browser hop: serialize to JSON text and parse it back.
        var reparsed = System.Text.Json.Nodes.JsonNode.Parse(jsonNode.ToJsonString());
        object? back = PyJson.FromJson(reparsed);
        byte[] rewritten = new PickleWriter().Write(back);
        Assert.Equal(pickled, rewritten);
    }

    /// <summary>Hand-assembles a pickle stream for depth nested single-element
    /// lists ([[[...[]...]]]) without going through PickleWriter (which now
    /// refuses to emit graphs this deep), so PickleReader's iterative parser
    /// can be exercised on genuinely deep input.</summary>
    private static byte[] BuildDeepNestedListPickle(int depth)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x80); ms.WriteByte(2); // PROTO 2
        ms.WriteByte((byte)']');             // EMPTY_LIST (innermost)
        WriteLongBinPut(ms, 0);
        for (int i = 1; i < depth; i++)
        {
            ms.WriteByte((byte)']');         // EMPTY_LIST (new outer)
            WriteLongBinGet(ms, i - 1);       // push the previous list
            ms.WriteByte((byte)'a');          // APPEND
            WriteLongBinPut(ms, i);
        }
        ms.WriteByte((byte)'.');             // STOP
        return ms.ToArray();

        static void WriteLongBinPut(MemoryStream s, int id)
        {
            s.WriteByte((byte)'r');
            s.Write(BitConverter.GetBytes((uint)id));
        }
        static void WriteLongBinGet(MemoryStream s, int id)
        {
            s.WriteByte((byte)'j');
            s.Write(BitConverter.GetBytes((uint)id));
        }
    }

    [Fact]
    public void VeryDeepNesting_RaisesSaveFormatException_InsteadOfCrashingTheProcess()
    {
        const int depth = 100_000;

        PyList deep = new();
        PyList current = deep;
        for (int i = 1; i < depth; i++)
        {
            var next = new PyList();
            current.Items.Add(next);
            current = next;
        }

        Assert.Throws<SaveFormatException>(() => PyJson.ToJson(deep));
        Assert.Throws<SaveFormatException>(() => new PickleWriter().Write(deep));

        // PickleReader is stack-based/iterative and isn't expected to guard
        // depth itself; it must still parse deep input without crashing. The
        // resulting deep graph then hits the same PyJson guard as above.
        byte[] deepPickle = BuildDeepNestedListPickle(depth);
        object? parsed = new PickleReader(deepPickle).Read();
        Assert.IsType<PyList>(parsed);
        Assert.Throws<SaveFormatException>(() => PyJson.ToJson(parsed));
    }

    [Fact]
    public void VeryDeepJson_RaisesSaveFormatException_OnFromJson()
    {
        // Well past our own MaxDepth (2000) but comfortably short of the depth
        // where System.Text.Json's own JsonNode plumbing (parent-chain Options
        // lookup) would itself overflow the stack while building the tree.
        var deep = new System.Text.Json.Nodes.JsonArray();
        var current = deep;
        for (int i = 1; i < 3000; i++)
        {
            var next = new System.Text.Json.Nodes.JsonArray();
            current.Add(next);
            current = next;
        }
        Assert.Throws<SaveFormatException>(() => PyJson.FromJson(deep));
    }
}

public class GvasTests
{
    private readonly FormatDetector _detector = new();

    private static byte[] BuildSampleGvas()
    {
        // Minimal synthetic UE4 save: header + IntProperty + StrProperty + None
        var doc = new SaveDocument
        {
            FormatId = "gvas",
            FormatName = "",
            FileName = "x.sav",
            Root = JsonNode.Parse("""
            {
              "header": {
                "saveGameVersion": 2,
                "packageVersionUE4": 522,
                "engine": { "major": 4, "minor": 27, "patch": 2, "changelist": 0, "branch": "++UE4+Release-4.27" },
                "customVersionFormat": 3,
                "customVersions": [],
                "saveGameClassName": "/Script/MyGame.MySaveGame"
              },
              "properties": [
                { "name": "Gold", "type": "IntProperty", "value": 250 },
                { "name": "PlayerName", "type": "StrProperty", "value": "Hero" },
                { "name": "Health", "type": "FloatProperty", "value": 73.5 },
                { "name": "Hardcore", "type": "BoolProperty", "value": true },
                { "name": "Levels", "type": "ArrayProperty", "arrayType": "IntProperty", "value": [1, 2, 3] }
              ]
            }
            """)!.AsObject(),
        };
        return new GvasFormat().Write(doc);
    }

    [Fact]
    public void Gvas_DetectsAndRoundTrips()
    {
        byte[] data = BuildSampleGvas();
        var doc = _detector.Detect(data, "Slot1.sav");
        Assert.Equal("gvas", doc.FormatId);

        var props = doc.Root!["properties"]!.AsArray();
        var gold = props.First(p => p!["name"]!.GetValue<string>() == "Gold")!;
        Assert.Equal(250, gold["value"]!.GetValue<int>());

        gold["value"] = 99999;
        byte[] output = _detector.Encode(doc);
        var doc2 = _detector.Detect(output, "Slot1.sav");
        var gold2 = doc2.Root!["properties"]!.AsArray().First(p => p!["name"]!.GetValue<string>() == "Gold")!;
        Assert.Equal(99999, gold2["value"]!.GetValue<int>());
    }

    [Fact]
    public void Gvas_ByteIdentical_WhenUnedited()
    {
        byte[] data = BuildSampleGvas();
        var doc = _detector.Detect(data, "Slot1.sav");
        Assert.Equal(data, _detector.Encode(doc));
    }

    [Fact]
    public void Gvas_LargeInt64_SurvivesJsonRoundTrip_ByteIdentical()
    {
        var doc = new SaveDocument
        {
            FormatId = "gvas",
            FormatName = "",
            FileName = "x.sav",
            Root = JsonNode.Parse($$"""
            {
              "header": {
                "saveGameVersion": 2,
                "packageVersionUE4": 522,
                "engine": { "major": 4, "minor": 27, "patch": 2, "changelist": 0, "branch": "" },
                "customVersionFormat": 3,
                "customVersions": [],
                "saveGameClassName": "/Script/MyGame.MySaveGame"
              },
              "properties": [
                { "name": "Seed", "type": "Int64Property", "value": {{long.MaxValue}} },
                { "name": "Mask", "type": "UInt64Property", "value": {{ulong.MaxValue}} },
                { "name": "Small", "type": "Int64Property", "value": 42 }
              ]
            }
            """)!.AsObject(),
        };
        byte[] data = new GvasFormat().Write(doc);

        var doc2 = _detector.Detect(data, "x.sav");
        var props = doc2.Root!["properties"]!.AsArray();
        var seed = props.First(p => p!["name"]!.GetValue<string>() == "Seed")!;
        var mask = props.First(p => p!["name"]!.GetValue<string>() == "Mask")!;
        var small = props.First(p => p!["name"]!.GetValue<string>() == "Small")!;

        // Values that would lose precision as a bare JS Number must be tagged strings.
        Assert.Equal(long.MaxValue.ToString(), seed["value"]!.GetValue<string>());
        Assert.Equal(ulong.MaxValue.ToString(), mask["value"]!.GetValue<string>());
        // Small values stay plain numbers (no unnecessary tagging).
        Assert.Equal(42L, small["value"]!.GetValue<long>());

        // Simulate the browser hop: serialize to JSON text and parse it back.
        var reparsed = JsonNode.Parse(doc2.Root.ToJsonString())!.AsObject();
        var doc3 = new SaveDocument { FormatId = "gvas", FormatName = "", FileName = "x.sav", Root = reparsed };
        byte[] output = new GvasFormat().Write(doc3);
        Assert.Equal(data, output);
    }

    private static void WriteFString(BinaryWriter w, string s)
    {
        if (s.Length == 0) { w.Write(0); return; }
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        w.Write(bytes.Length + 1);
        w.Write(bytes);
        w.Write((byte)0);
    }

    /// <summary>
    /// A StructProperty whose declared size doesn't match what its (generic,
    /// property-list) body actually contains: the body is a bare "None"
    /// terminator (an empty struct) followed by 10 bytes of padding that are
    /// still inside the declared size span. Parsing the property list itself
    /// never throws - it just stops right after "None" - so this only shows
    /// up as a byte-count mismatch, which is exactly what T2 guards against.
    /// </summary>
    private static byte[] BuildGvasWithMisalignedStruct()
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("GVAS"u8);
        w.Write(2);           // saveGameVersion
        w.Write(0);           // packageVersionUE4
        w.Write((ushort)4); w.Write((ushort)27); w.Write((ushort)2); w.Write((uint)0); // engine
        WriteFString(w, "");  // engine.branch
        w.Write(3);           // customVersionFormat
        w.Write(0);           // customVersions count
        WriteFString(w, "");  // saveGameClassName

        WriteFString(w, "Broken");            // property name
        WriteFString(w, "StructProperty");    // property type
        byte[] noneBytes;
        using (var nms = new MemoryStream())
        {
            WriteFString(new BinaryWriter(nms), "None");
            noneBytes = nms.ToArray();
        }
        int padding = 10;
        w.Write(noneBytes.Length + padding);  // declared size (too large for the body)
        w.Write(0);                           // arrayIndex
        WriteFString(w, "MyStruct");           // structType (unknown -> generic property list)
        w.Write(new byte[16]);                 // structGuid
        w.Write((byte)0);                      // guid flag
        w.Write(noneBytes);                    // empty property list ("None")
        w.Write(new byte[padding]);            // padding still inside the declared size

        WriteFString(w, "None"); // terminate the top-level property list
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Gvas_MisalignedStruct_FallsBackToRaw_WithoutThrowing()
    {
        byte[] data = BuildGvasWithMisalignedStruct();
        var doc = _detector.Detect(data, "broken.sav");
        Assert.Equal("gvas", doc.FormatId);
        Assert.Contains(doc.Warnings, w => w.Contains("ham"));

        var prop = doc.Root!["properties"]!.AsArray().Single()!;
        Assert.NotNull(prop["value"]!["__raw"]);

        byte[] output = _detector.Encode(doc);
        Assert.Equal(data, output);
    }
}

public class RenpyTests
{
    private readonly FormatDetector _detector = new();

    [Fact]
    public void RenpySave_RoundTrips()
    {
        // Build a minimal Ren'Py-style save: zip with pickled log + json metadata.
        var store = new PyDict();
        store.Items.Add(new("store.gold", 100L));
        store.Items.Add(new("store.name", "Deniz"));
        var log = new PyDict();
        log.Items.Add(new("log", store));
        byte[] pickled = new PickleWriter().Write(log);

        byte[] zipData;
        using (var ms = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry("log");
                using (var s = entry.Open()) s.Write(pickled);
                var json = zip.CreateEntry("json");
                using (var s = json.Open()) s.Write(Encoding.UTF8.GetBytes("{\"_save_name\": \"\"}"));
            }
            zipData = ms.ToArray();
        }

        var doc = _detector.Detect(zipData, "1-1-LT1.save");
        Assert.Equal("renpy", doc.FormatId);
        Assert.Equal(100L, doc.Root!["log"]!["store.gold"]!.GetValue<long>());

        doc.Root["log"]!["store.gold"] = 424242;
        byte[] output = _detector.Encode(doc);

        var doc2 = _detector.Detect(output, "1-1-LT1.save");
        Assert.Equal(424242L, doc2.Root!["log"]!["store.gold"]!.GetValue<long>());
    }
}

public class SqliteTests
{
    private readonly FormatDetector _detector = new();

    [Fact]
    public void Sqlite_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), "se-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE player (id INTEGER PRIMARY KEY, name TEXT, gold INTEGER); " +
                                  "INSERT INTO player VALUES (1, 'Ali', 50), (2, 'Ayşe', 75);";
                cmd.ExecuteNonQuery();
            }
            byte[] data = File.ReadAllBytes(path);

            var doc = _detector.Detect(data, "game.db");
            Assert.Equal("sqlite", doc.FormatId);
            var rows = doc.Root!["tables"]!["player"]!["rows"]!.AsArray();
            Assert.Equal(2, rows.Count);
            Assert.Equal(50L, rows[0]![2]!.GetValue<long>());

            rows[0]![2] = 12345;
            byte[] output = _detector.Encode(doc);
            var doc2 = _detector.Detect(output, "game.db");
            Assert.Equal(12345L, doc2.Root!["tables"]!["player"]!["rows"]!.AsArray()[0]![2]!.GetValue<long>());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Sqlite_ValidHeaderCorruptBody_ThrowsSaveFormatException()
    {
        // CanRead only looks at the 16-byte "SQLite format 3\0" header; a file
        // that has the right header but a corrupt/truncated body (found by
        // fuzzing) used to leak a raw Microsoft.Data.Sqlite.SqliteException.
        byte[] data = [.. Encoding.UTF8.GetBytes("SQLite format 3\0"), .. new byte[64]];
        Assert.Throws<SaveFormatException>(() => _detector.Detect(data, "broken.db"));
    }
}
