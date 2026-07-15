using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaveEditor.Core.Pickle;

// In-memory model of an unpickled Python object graph. Kept as CLR types where
// possible (null, bool, long, double, string, byte[], BigInteger) plus the
// container/object nodes below.

public sealed class PyTuple { public List<object?> Items = []; }
public sealed class PyList { public List<object?> Items = []; }
public sealed class PyDict { public List<KeyValuePair<object?, object?>> Items = []; }
public sealed class PySet { public List<object?> Items = []; public bool Frozen; }
public sealed class PyGlobal { public required string Module; public required string Name; }
public sealed class PyPersId { public object? Value; }

public sealed class PyObject
{
    /// <summary>"reduce" (callable+args), "newobj" (cls+args) or "newobj_ex" (cls+args+kwargs).</summary>
    public required string Kind;
    public object? Callable;
    public PyTuple Args = new();
    public object? KwArgs;
    public bool HasState;
    public object? State;

    /// <summary>APPEND(S) applied to the object (list-like classes, e.g. Ren'Py RevertableList).</summary>
    public PyList? Appends;

    /// <summary>SETITEM(S) applied to the object (dict-like classes, e.g. OrderedDict, RevertableDict).</summary>
    public PyDict? SetItems;
}

/// <summary>
/// Converts a Python object graph to an editable JSON tree and back.
/// Python-specific values use {"py": kind, ...} tagged objects; shared
/// references and cycles use {"py":"shared","id":n} / {"py":"ref","id":n}.
/// </summary>
public static class PyJson
{
    public static JsonNode? ToJson(object? root)
    {
        var counts = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        CountRefs(root, counts);
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        int nextId = 1;
        return Emit(root, counts, ids, ref nextId);
    }

    private static void CountRefs(object? value, Dictionary<object, int> counts)
    {
        if (value is not (PyTuple or PyList or PyDict or PySet or PyObject or PyPersId)) return;
        if (counts.TryGetValue(value, out int c))
        {
            counts[value] = c + 1;
            return;
        }
        counts[value] = 1;
        switch (value)
        {
            case PyTuple t: foreach (var i in t.Items) CountRefs(i, counts); break;
            case PyList l: foreach (var i in l.Items) CountRefs(i, counts); break;
            case PySet s: foreach (var i in s.Items) CountRefs(i, counts); break;
            case PyDict d:
                foreach (var kv in d.Items) { CountRefs(kv.Key, counts); CountRefs(kv.Value, counts); }
                break;
            case PyObject o:
                CountRefs(o.Callable, counts);
                foreach (var i in o.Args.Items) CountRefs(i, counts);
                CountRefs(o.KwArgs, counts);
                if (o.HasState) CountRefs(o.State, counts);
                if (o.Appends is not null) foreach (var i in o.Appends.Items) CountRefs(i, counts);
                if (o.SetItems is not null)
                    foreach (var kv in o.SetItems.Items) { CountRefs(kv.Key, counts); CountRefs(kv.Value, counts); }
                break;
            case PyPersId p: CountRefs(p.Value, counts); break;
        }
    }

    private static JsonNode? Emit(object? value, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId)
    {
        switch (value)
        {
            case null: return null;
            case bool b: return b;
            case long l: return l;
            case BigInteger big:
                return new JsonObject { ["py"] = "long", ["v"] = big.ToString(CultureInfo.InvariantCulture) };
            case double d:
                if (double.IsNaN(d)) return new JsonObject { ["py"] = "float", ["v"] = "nan" };
                if (double.IsPositiveInfinity(d)) return new JsonObject { ["py"] = "float", ["v"] = "inf" };
                if (double.IsNegativeInfinity(d)) return new JsonObject { ["py"] = "float", ["v"] = "-inf" };
                // JSON/JS cannot carry the sign of -0.0; keep it as a string.
                if (d == 0.0 && double.IsNegative(d))
                    return new JsonObject { ["py"] = "float", ["v"] = "-0.0" };
                // Keep integral floats tagged so 1.0 doesn't silently become int 1.
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                    return new JsonObject { ["py"] = "float", ["v"] = d };
                return d;
            case string s: return s;
            case byte[] bytes:
                return new JsonObject { ["py"] = "bytes", ["b64"] = Convert.ToBase64String(bytes) };
        }

        if (ids.TryGetValue(value, out int existingId))
        {
            return new JsonObject { ["py"] = "ref", ["id"] = existingId };
        }

        bool shared = counts.TryGetValue(value, out int count) && count > 1;
        int myId = 0;
        if (shared)
        {
            myId = nextId++;
            ids[value] = myId;
        }

        JsonNode? node;
        switch (value)
        {
            case PyList list:
            {
                var arr = new JsonArray();
                foreach (var item in list.Items) arr.Add(Emit(item, counts, ids, ref nextId));
                node = arr;
                break;
            }
            case PyTuple tuple:
            {
                var arr = new JsonArray();
                foreach (var item in tuple.Items) arr.Add(Emit(item, counts, ids, ref nextId));
                node = new JsonObject { ["py"] = "tuple", ["items"] = arr };
                break;
            }
            case PySet set:
            {
                var arr = new JsonArray();
                foreach (var item in set.Items) arr.Add(Emit(item, counts, ids, ref nextId));
                node = new JsonObject { ["py"] = set.Frozen ? "frozenset" : "set", ["items"] = arr };
                break;
            }
            case PyDict dict:
                node = EmitDict(dict, counts, ids, ref nextId);
                break;
            case PyGlobal g:
                node = new JsonObject { ["py"] = "global", ["module"] = g.Module, ["name"] = g.Name };
                break;
            case PyObject o:
            {
                var obj = new JsonObject
                {
                    ["py"] = o.Kind,
                    ["callable"] = Emit(o.Callable, counts, ids, ref nextId),
                };
                var args = new JsonArray();
                foreach (var a in o.Args.Items) args.Add(Emit(a, counts, ids, ref nextId));
                obj["args"] = args;
                if (o.Kind == "newobj_ex") obj["kwargs"] = Emit(o.KwArgs, counts, ids, ref nextId);
                if (o.Appends is not null)
                {
                    var appends = new JsonArray();
                    foreach (var i in o.Appends.Items) appends.Add(Emit(i, counts, ids, ref nextId));
                    obj["appends"] = appends;
                }
                if (o.SetItems is not null) obj["setitems"] = EmitDict(o.SetItems, counts, ids, ref nextId);
                if (o.HasState) obj["state"] = Emit(o.State, counts, ids, ref nextId);
                node = obj;
                break;
            }
            case PyPersId p:
                node = new JsonObject { ["py"] = "persid", ["value"] = Emit(p.Value, counts, ids, ref nextId) };
                break;
            default:
                throw new SaveFormatException($"Bilinmeyen pickle değeri: {value.GetType().Name}");
        }

        if (shared)
        {
            return new JsonObject { ["py"] = "shared", ["id"] = myId, ["value"] = node };
        }
        return node;
    }

    private static JsonNode EmitDict(PyDict dict, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId)
    {
        bool plainKeys = dict.Items.All(kv => kv.Key is string k && k != "py");
        bool uniqueKeys = plainKeys && dict.Items.Select(kv => (string)kv.Key!).Distinct().Count() == dict.Items.Count;
        if (plainKeys && uniqueKeys)
        {
            var obj = new JsonObject();
            foreach (var kv in dict.Items)
                obj[(string)kv.Key!] = Emit(kv.Value, counts, ids, ref nextId);
            return obj;
        }
        var items = new JsonArray();
        foreach (var kv in dict.Items)
        {
            items.Add(new JsonArray
            {
                Emit(kv.Key, counts, ids, ref nextId),
                Emit(kv.Value, counts, ids, ref nextId),
            });
        }
        return new JsonObject { ["py"] = "dict", ["items"] = items };
    }

    public static object? FromJson(JsonNode? node)
    {
        var byId = new Dictionary<int, object>();
        return Build(node, byId);
    }

    private static object? Build(JsonNode? node, Dictionary<int, object> byId)
    {
        switch (node)
        {
            case null: return null;
            case JsonValue v:
            {
                if (v.TryGetValue(out bool b)) return b;
                if (v.TryGetValue(out long l)) return l;
                if (v.TryGetValue(out int i)) return (long)i;
                if (v.TryGetValue(out uint ui)) return (long)ui;
                if (v.TryGetValue(out short sh)) return (long)sh;
                if (v.TryGetValue(out byte by)) return (long)by;
                if (v.TryGetValue(out ulong ul)) return ul <= long.MaxValue ? (long)ul : new BigInteger(ul);
                if (v.TryGetValue(out double d)) return d;
                if (v.TryGetValue(out float f)) return (double)f;
                if (v.TryGetValue(out decimal m)) return (double)m;
                if (v.TryGetValue(out string? s)) return s!;
                // Big integers and high-precision decimals: fall back to raw JSON text.
                if (v.TryGetValue(out JsonElement el))
                {
                    string raw = el.GetRawText();
                    if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var big)) return big;
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double dd)) return dd;
                }
                throw new SaveFormatException($"Değer çözümlenemedi: {v.ToJsonString()}");
            }
            case JsonArray arr:
            {
                var list = new PyList();
                foreach (var item in arr) list.Items.Add(Build(item, byId));
                return list;
            }
            case JsonObject obj when obj["py"] is JsonValue tagValue && tagValue.TryGetValue(out string? tag):
                return BuildTagged(obj, tag!, byId);
            case JsonObject obj:
            {
                var dict = new PyDict();
                foreach (var (key, value) in obj)
                    dict.Items.Add(new(key, Build(value, byId)));
                return dict;
            }
            default:
                throw new SaveFormatException("Geçersiz düğüm.");
        }
    }

    private static object? BuildTagged(JsonObject obj, string tag, Dictionary<int, object> byId)
    {
        switch (tag)
        {
            case "ref":
            {
                int id = obj["id"]!.GetValue<int>();
                if (!byId.TryGetValue(id, out object? target))
                    throw new SaveFormatException($"Referans hedefi bulunamadı: id={id}. 'shared' düğümü referanstan önce gelmelidir.");
                return target;
            }
            case "shared":
            {
                int id = obj["id"]!.GetValue<int>();
                var valueNode = obj["value"];
                // Create the container first and register it so cycles resolve.
                switch (valueNode)
                {
                    case JsonArray arr:
                    {
                        var list = new PyList();
                        byId[id] = list;
                        foreach (var item in arr) list.Items.Add(Build(item, byId));
                        return list;
                    }
                    case JsonObject inner when inner["py"] is JsonValue tv && tv.TryGetValue(out string? innerTag):
                    {
                        object? result = BuildTaggedShared(inner, innerTag!, id, byId);
                        return result;
                    }
                    case JsonObject innerObj:
                    {
                        var dict = new PyDict();
                        byId[id] = dict;
                        foreach (var (key, value) in innerObj)
                            dict.Items.Add(new(key, Build(value, byId)));
                        return dict;
                    }
                    default:
                    {
                        object? plain = Build(valueNode, byId);
                        if (plain is not null) byId[id] = plain;
                        return plain;
                    }
                }
            }
            case "tuple":
            {
                var tuple = new PyTuple();
                foreach (var item in (JsonArray)obj["items"]!) tuple.Items.Add(Build(item, byId));
                return tuple;
            }
            case "set":
            case "frozenset":
            {
                var set = new PySet { Frozen = tag == "frozenset" };
                foreach (var item in (JsonArray)obj["items"]!) set.Items.Add(Build(item, byId));
                return set;
            }
            case "dict":
            {
                var dict = new PyDict();
                foreach (var pair in (JsonArray)obj["items"]!)
                {
                    var kv = (JsonArray)pair!;
                    dict.Items.Add(new(Build(kv[0], byId), Build(kv[1], byId)));
                }
                return dict;
            }
            case "bytes":
                return Convert.FromBase64String(obj["b64"]!.GetValue<string>());
            case "long":
                return BigInteger.Parse(obj["v"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            case "float":
            {
                var v = obj["v"]!;
                if (v is JsonValue jv && jv.TryGetValue(out string? special))
                {
                    return special switch
                    {
                        "nan" => double.NaN,
                        "inf" => double.PositiveInfinity,
                        "-inf" => double.NegativeInfinity,
                        _ => double.Parse(special!, CultureInfo.InvariantCulture),
                    };
                }
                return v.GetValue<double>();
            }
            case "global":
                return new PyGlobal { Module = obj["module"]!.GetValue<string>(), Name = obj["name"]!.GetValue<string>() };
            case "persid":
                return new PyPersId { Value = Build(obj["value"], byId) };
            case "reduce":
            case "newobj":
            case "newobj_ex":
                return BuildObject(obj, tag, null, byId);
            default:
                throw new SaveFormatException($"Bilinmeyen py etiketi: {tag}");
        }
    }

    private static object? BuildTaggedShared(JsonObject inner, string innerTag, int id, Dictionary<int, object> byId)
    {
        switch (innerTag)
        {
            case "dict":
            {
                var dict = new PyDict();
                byId[id] = dict;
                foreach (var pair in (JsonArray)inner["items"]!)
                {
                    var kv = (JsonArray)pair!;
                    dict.Items.Add(new(Build(kv[0], byId), Build(kv[1], byId)));
                }
                return dict;
            }
            case "set":
            case "frozenset":
            {
                var set = new PySet { Frozen = innerTag == "frozenset" };
                byId[id] = set;
                foreach (var item in (JsonArray)inner["items"]!) set.Items.Add(Build(item, byId));
                return set;
            }
            case "tuple":
            {
                var tuple = new PyTuple();
                byId[id] = tuple;
                foreach (var item in (JsonArray)inner["items"]!) tuple.Items.Add(Build(item, byId));
                return tuple;
            }
            case "reduce":
            case "newobj":
            case "newobj_ex":
                return BuildObject(inner, innerTag, id, byId);
            default:
            {
                object? result = BuildTagged(inner, innerTag, byId);
                if (result is not null) byId[id] = result;
                return result;
            }
        }
    }

    private static PyObject BuildObject(JsonObject obj, string kind, int? sharedId, Dictionary<int, object> byId)
    {
        var py = new PyObject { Kind = kind };
        if (sharedId is int id) byId[id] = py;
        py.Callable = Build(obj["callable"], byId);
        var args = new PyTuple();
        foreach (var a in (JsonArray)obj["args"]!) args.Items.Add(Build(a, byId));
        py.Args = args;
        if (kind == "newobj_ex") py.KwArgs = Build(obj["kwargs"], byId);
        if (obj.ContainsKey("appends"))
        {
            py.Appends = Build(obj["appends"], byId) as PyList
                ?? throw new SaveFormatException("'appends' bir dizi olmalı.");
        }
        if (obj.ContainsKey("setitems"))
        {
            py.SetItems = Build(obj["setitems"], byId) as PyDict
                ?? throw new SaveFormatException("'setitems' bir nesne olmalı.");
        }
        if (obj.ContainsKey("state"))
        {
            py.HasState = true;
            py.State = Build(obj["state"], byId);
        }
        return py;
    }
}
