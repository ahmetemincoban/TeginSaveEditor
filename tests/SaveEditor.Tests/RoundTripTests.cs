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
    public void PlainText_FallsBack()
    {
        var doc = _detector.Detect(Encoding.UTF8.GetBytes("merhaba dünya\nsatır 2"), "notes.txt");
        Assert.Equal("text", doc.FormatId);
        byte[] output = _detector.Encode(doc);
        Assert.Equal("merhaba dünya\nsatır 2", Encoding.UTF8.GetString(output));
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
}

public class PickleTests
{
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
}
