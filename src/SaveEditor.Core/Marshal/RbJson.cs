using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaveEditor.Core.Marshal;

/// <summary>
/// Converts an RbModel graph to an editable JSON tree and back. Ruby-specific
/// values use {"rb": kind, ...} tagged objects; shared references and cycles
/// use {"rb":"shared","id":n} / {"rb":"ref","id":n} — the same convention
/// SaveEditor.Core.Pickle.PyJson uses for Python pickle graphs. Symbols are
/// not tracked for sharing: Ruby symbols are always interned by name, so
/// Build() re-interns by name instead of needing link bookkeeping for them.
/// </summary>
public static class RbJson
{
    private const long SafeIntMax = 9_007_199_254_740_991L; // 2^53 - 1

    private sealed class DepthGuard
    {
        private int _depth;
        private const int MaxDepth = 500;
        public void Enter()
        {
            if (++_depth > MaxDepth) throw new SaveFormatException("Veri çok derin iç içe geçmiş.");
        }
        public void Exit() => _depth--;
    }

    public static JsonNode? ToJson(object? root)
    {
        var counts = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        CountRefs(root, counts, new DepthGuard());
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        int nextId = 1;
        return Emit(root, counts, ids, ref nextId, new DepthGuard());
    }

    private static void CountRefs(object? value, Dictionary<object, int> counts, DepthGuard depth)
    {
        if (value is not (double or BigInteger or RbString or RbArray or RbHash or RbObject or RbStruct
            or RbClassOrModule or RbExtended or RbUserClass or RbUserMarshal or RbUserDefined or RbRegexp))
            return;
        if (counts.TryGetValue(value, out int c)) { counts[value] = c + 1; return; }
        counts[value] = 1;

        depth.Enter();
        try
        {
            switch (value)
            {
                case RbString s:
                    if (s.IVars is not null) foreach (var kv in s.IVars) CountRefs(kv.Value, counts, depth);
                    break;
                case RbArray a:
                    foreach (var item in a.Items) CountRefs(item, counts, depth);
                    if (a.IVars is not null) foreach (var kv in a.IVars) CountRefs(kv.Value, counts, depth);
                    break;
                case RbHash h:
                    foreach (var kv in h.Items) { CountRefs(kv.Key, counts, depth); CountRefs(kv.Value, counts, depth); }
                    if (h.HasDefault) CountRefs(h.Default, counts, depth);
                    if (h.IVars is not null) foreach (var kv in h.IVars) CountRefs(kv.Value, counts, depth);
                    break;
                case RbObject o:
                    foreach (var kv in o.IVars) CountRefs(kv.Value, counts, depth);
                    break;
                case RbStruct st:
                    foreach (var kv in st.Members) CountRefs(kv.Value, counts, depth);
                    break;
                case RbExtended ext:
                    CountRefs(ext.Value, counts, depth);
                    break;
                case RbUserClass uc:
                    CountRefs(uc.Wrapped, counts, depth);
                    break;
                case RbUserMarshal um:
                    CountRefs(um.Data, counts, depth);
                    break;
                case RbRegexp re:
                    if (re.IVars is not null) foreach (var kv in re.IVars) CountRefs(kv.Value, counts, depth);
                    break;
                // BigInteger, RbClassOrModule, RbUserDefined: leaves, nothing further to walk.
            }
        }
        finally { depth.Exit(); }
    }

    private static JsonNode? Emit(object? value, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
    {
        switch (value)
        {
            case null: return null;
            case bool b: return b;
            case long l:
                if (l > SafeIntMax || l < -SafeIntMax)
                    return new JsonObject { ["rb"] = "int", ["v"] = l.ToString(CultureInfo.InvariantCulture) };
                return l;
            case RbSymbol sym:
                return new JsonObject { ["rb"] = "symbol", ["name"] = sym.Name };
        }

        if (ids.TryGetValue(value, out int existingId))
            return new JsonObject { ["rb"] = "ref", ["id"] = existingId };

        bool shared = counts.TryGetValue(value, out int count) && count > 1;
        int myId = 0;
        if (shared) { myId = nextId++; ids[value] = myId; }

        depth.Enter();
        JsonNode? node;
        try
        {
            node = value switch
            {
                double d => EmitFloat(d),
                BigInteger big => new JsonObject { ["rb"] = "bignum", ["v"] = big.ToString(CultureInfo.InvariantCulture) },
                RbString s => EmitString(s, counts, ids, ref nextId, depth),
                RbArray a => EmitArray(a, counts, ids, ref nextId, depth),
                RbHash h => EmitHash(h, counts, ids, ref nextId, depth),
                RbObject o => EmitObject(o, counts, ids, ref nextId, depth),
                RbStruct st => EmitStruct(st, counts, ids, ref nextId, depth),
                RbClassOrModule cm => new JsonObject { ["rb"] = cm.IsModule ? "module" : "class", ["name"] = cm.Name },
                RbExtended ext => new JsonObject { ["rb"] = "extended", ["module"] = ext.ModuleName.Name, ["value"] = Emit(ext.Value, counts, ids, ref nextId, depth) },
                RbUserClass uc => new JsonObject { ["rb"] = "userclass", ["class"] = uc.ClassName.Name, ["value"] = Emit(uc.Wrapped, counts, ids, ref nextId, depth) },
                RbUserMarshal um => new JsonObject { ["rb"] = "usermarshal", ["class"] = um.ClassName.Name, ["value"] = Emit(um.Data, counts, ids, ref nextId, depth) },
                RbUserDefined ud => new JsonObject { ["rb"] = "userdefined", ["class"] = ud.ClassName.Name, ["b64"] = Convert.ToBase64String(ud.Data) },
                RbRegexp re => EmitRegexp(re, counts, ids, ref nextId, depth),
                _ => throw new SaveFormatException($"Bilinmeyen Marshal değeri: {value.GetType().Name}"),
            };
        }
        finally { depth.Exit(); }

        if (shared) return new JsonObject { ["rb"] = "shared", ["id"] = myId, ["value"] = node };
        return node;
    }

    private static JsonNode EmitFloat(double d)
    {
        if (double.IsNaN(d)) return new JsonObject { ["rb"] = "float", ["v"] = "nan" };
        if (double.IsPositiveInfinity(d)) return new JsonObject { ["rb"] = "float", ["v"] = "inf" };
        if (double.IsNegativeInfinity(d)) return new JsonObject { ["rb"] = "float", ["v"] = "-inf" };
        if (d == 0.0 && double.IsNegative(d)) return new JsonObject { ["rb"] = "float", ["v"] = "-0.0" };
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return new JsonObject { ["rb"] = "float", ["v"] = d };
        return d;
    }

    private static JsonNode EmitString(RbString s, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
    {
        string? text = TryDecodeUtf8Strict(s.Bytes);
        bool hasIvars = s.IVars is { Count: > 0 };
        if (text is not null && !hasIvars) return text;

        var obj = new JsonObject { ["rb"] = "str" };
        if (text is not null) obj["v"] = text;
        else obj["b64"] = Convert.ToBase64String(s.Bytes);
        if (hasIvars) obj["ivars"] = EmitPairs(s.IVars!, counts, ids, ref nextId, depth);
        return obj;
    }

    private static JsonNode EmitArray(RbArray a, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
    {
        var items = new JsonArray();
        foreach (var item in a.Items) items.Add(Emit(item, counts, ids, ref nextId, depth));
        if (a.IVars is null) return items;
        return new JsonObject { ["rb"] = "array", ["items"] = items, ["ivars"] = EmitPairs(a.IVars, counts, ids, ref nextId, depth) };
    }

    private static JsonNode EmitHash(RbHash h, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
    {
        bool plainKeys = !h.HasDefault && h.IVars is null && h.Items.All(kv => kv.Key is RbSymbol rs && rs.Name != "rb");
        bool uniqueKeys = plainKeys && h.Items.Select(kv => ((RbSymbol)kv.Key!).Name).Distinct().Count() == h.Items.Count;
        if (plainKeys && uniqueKeys)
        {
            var obj = new JsonObject();
            foreach (var kv in h.Items) obj[((RbSymbol)kv.Key!).Name] = Emit(kv.Value, counts, ids, ref nextId, depth);
            return obj;
        }

        var items = new JsonArray();
        foreach (var kv in h.Items)
            items.Add(new JsonArray { Emit(kv.Key, counts, ids, ref nextId, depth), Emit(kv.Value, counts, ids, ref nextId, depth) });
        var result = new JsonObject { ["rb"] = "hash", ["items"] = items };
        if (h.HasDefault) result["default"] = Emit(h.Default, counts, ids, ref nextId, depth);
        if (h.IVars is not null) result["ivars"] = EmitPairs(h.IVars, counts, ids, ref nextId, depth);
        return result;
    }

    private static JsonNode EmitObject(RbObject o, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
        => new JsonObject { ["rb"] = "object", ["class"] = o.ClassName.Name, ["ivars"] = EmitPairs(o.IVars, counts, ids, ref nextId, depth) };

    private static JsonNode EmitStruct(RbStruct st, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
        => new JsonObject { ["rb"] = "struct", ["class"] = st.ClassName.Name, ["members"] = EmitPairs(st.Members, counts, ids, ref nextId, depth) };

    private static JsonNode EmitRegexp(RbRegexp re, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
    {
        string? text = TryDecodeUtf8Strict(re.Pattern);
        var obj = new JsonObject { ["rb"] = "regexp", ["options"] = re.Options };
        if (text is not null) obj["pattern"] = text;
        else obj["b64"] = Convert.ToBase64String(re.Pattern);
        if (re.IVars is { Count: > 0 }) obj["ivars"] = EmitPairs(re.IVars, counts, ids, ref nextId, depth);
        return obj;
    }

    private static JsonArray EmitPairs(List<KeyValuePair<RbSymbol, object?>> pairs, Dictionary<object, int> counts, Dictionary<object, int> ids, ref int nextId, DepthGuard depth)
    {
        var arr = new JsonArray();
        // JsonValue.Create(string) explicitly, not JsonArray's generic Add<T>:
        // the generic overload resolves through a reflection-based JsonTypeInfo
        // lookup that throws under JsonSerializerIsReflectionEnabledByDefault=false
        // (confirmed to actually happen with .NET 10's file-based app runner).
        foreach (var kv in pairs) arr.Add(new JsonArray { JsonValue.Create(kv.Key.Name), Emit(kv.Value, counts, ids, ref nextId, depth) });
        return arr;
    }

    private static string? TryDecodeUtf8Strict(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static object? FromJson(JsonNode? node)
    {
        var byId = new Dictionary<int, object>();
        var symbols = new Dictionary<string, RbSymbol>();
        return Build(node, byId, symbols, new DepthGuard());
    }

    private static RbSymbol Intern(string name, Dictionary<string, RbSymbol> symbols)
    {
        if (!symbols.TryGetValue(name, out var sym)) { sym = new RbSymbol(name); symbols[name] = sym; }
        return sym;
    }

    private static object? Build(JsonNode? node, Dictionary<int, object> byId, Dictionary<string, RbSymbol> symbols, DepthGuard depth)
    {
        depth.Enter();
        try
        {
            switch (node)
            {
                case null: return null;
                case JsonValue v:
                {
                    if (v.TryGetValue(out bool b)) return b;
                    if (v.TryGetValue(out long l)) return l;
                    if (v.TryGetValue(out int i)) return (long)i;
                    if (v.TryGetValue(out double d)) return d;
                    if (v.TryGetValue(out float f)) return (double)f;
                    if (v.TryGetValue(out string? s)) return new RbString { Bytes = Encoding.UTF8.GetBytes(s!) };
                    if (v.TryGetValue(out JsonElement el))
                    {
                        string raw = el.GetRawText();
                        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double dd)) return dd;
                    }
                    throw new SaveFormatException($"Değer çözümlenemedi: {v.ToJsonString()}");
                }
                case JsonArray arr:
                {
                    var list = new RbArray();
                    foreach (var item in arr) list.Items.Add(Build(item, byId, symbols, depth));
                    return list;
                }
                case JsonObject obj when obj["rb"] is JsonValue tagValue && tagValue.TryGetValue(out string? tag):
                    return BuildTagged(obj, tag!, null, byId, symbols, depth);
                case JsonObject obj:
                {
                    var hash = new RbHash();
                    foreach (var (key, value) in obj)
                        hash.Items.Add(new(Intern(key, symbols), Build(value, byId, symbols, depth)));
                    return hash;
                }
                default:
                    throw new SaveFormatException("Geçersiz düğüm.");
            }
        }
        finally { depth.Exit(); }
    }

    private static object? BuildTagged(JsonObject obj, string tag, int? sharedId, Dictionary<int, object> byId, Dictionary<string, RbSymbol> symbols, DepthGuard depth)
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
                if (valueNode is JsonArray arr)
                {
                    var list = new RbArray();
                    byId[id] = list;
                    foreach (var item in arr) list.Items.Add(Build(item, byId, symbols, depth));
                    return list;
                }
                if (valueNode is JsonObject inner && inner["rb"] is JsonValue tv && tv.TryGetValue(out string? innerTag))
                    return BuildTagged(inner, innerTag!, id, byId, symbols, depth);
                if (valueNode is JsonObject plainObj)
                {
                    var hash = new RbHash();
                    byId[id] = hash;
                    foreach (var (key, value) in plainObj)
                        hash.Items.Add(new(Intern(key, symbols), Build(value, byId, symbols, depth)));
                    return hash;
                }
                object? plain = Build(valueNode, byId, symbols, depth);
                if (plain is not null) byId[id] = plain;
                return plain;
            }
            case "symbol":
                return Intern(obj["name"]!.GetValue<string>(), symbols);
            case "int":
                return long.Parse(obj["v"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            case "bignum":
            {
                var big = BigInteger.Parse(obj["v"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                if (sharedId is int bid) byId[bid] = big;
                return big;
            }
            case "float":
            {
                var v = obj["v"]!;
                double d = v is JsonValue jv && jv.TryGetValue(out string? special)
                    ? special switch
                    {
                        "nan" => double.NaN,
                        "inf" => double.PositiveInfinity,
                        "-inf" => double.NegativeInfinity,
                        "-0.0" => -0.0,
                        _ => double.Parse(special!, CultureInfo.InvariantCulture),
                    }
                    : v.GetValue<double>();
                if (sharedId is int fid) byId[fid] = d;
                return d;
            }
            case "str":
            {
                var s = new RbString { Bytes = obj["v"] is JsonNode vNode ? Encoding.UTF8.GetBytes(vNode.GetValue<string>()) : Convert.FromBase64String(obj["b64"]!.GetValue<string>()) };
                if (sharedId is int sid) byId[sid] = s;
                if (obj["ivars"] is JsonArray ivarsArr) s.IVars = BuildPairs(ivarsArr, byId, symbols, depth);
                return s;
            }
            case "array":
            {
                var a = new RbArray();
                if (sharedId is int aid) byId[aid] = a;
                foreach (var item in (JsonArray)obj["items"]!) a.Items.Add(Build(item, byId, symbols, depth));
                if (obj["ivars"] is JsonArray ivarsArr) a.IVars = BuildPairs(ivarsArr, byId, symbols, depth);
                return a;
            }
            case "hash":
            {
                var h = new RbHash();
                if (sharedId is int hid) byId[hid] = h;
                foreach (var pair in (JsonArray)obj["items"]!)
                {
                    var kv = (JsonArray)pair!;
                    h.Items.Add(new(Build(kv[0], byId, symbols, depth), Build(kv[1], byId, symbols, depth)));
                }
                if (obj.ContainsKey("default")) { h.HasDefault = true; h.Default = Build(obj["default"], byId, symbols, depth); }
                if (obj["ivars"] is JsonArray ivarsArr) h.IVars = BuildPairs(ivarsArr, byId, symbols, depth);
                return h;
            }
            case "object":
            {
                var o = new RbObject { ClassName = Intern(obj["class"]!.GetValue<string>(), symbols) };
                if (sharedId is int oid) byId[oid] = o;
                o.IVars = BuildPairs((JsonArray)obj["ivars"]!, byId, symbols, depth);
                return o;
            }
            case "struct":
            {
                var st = new RbStruct { ClassName = Intern(obj["class"]!.GetValue<string>(), symbols) };
                if (sharedId is int stid) byId[stid] = st;
                st.Members = BuildPairs((JsonArray)obj["members"]!, byId, symbols, depth);
                return st;
            }
            case "class":
            case "module":
            {
                var cm = new RbClassOrModule { Name = obj["name"]!.GetValue<string>(), IsModule = tag == "module" };
                if (sharedId is int cmid) byId[cmid] = cm;
                return cm;
            }
            case "extended":
            {
                var ext = new RbExtended { ModuleName = Intern(obj["module"]!.GetValue<string>(), symbols) };
                if (sharedId is int eid) byId[eid] = ext;
                ext.Value = Build(obj["value"], byId, symbols, depth);
                return ext;
            }
            case "userclass":
            {
                var uc = new RbUserClass { ClassName = Intern(obj["class"]!.GetValue<string>(), symbols) };
                if (sharedId is int uid) byId[uid] = uc;
                uc.Wrapped = Build(obj["value"], byId, symbols, depth);
                return uc;
            }
            case "usermarshal":
            {
                var um = new RbUserMarshal { ClassName = Intern(obj["class"]!.GetValue<string>(), symbols) };
                if (sharedId is int umid) byId[umid] = um;
                um.Data = Build(obj["value"], byId, symbols, depth);
                return um;
            }
            case "userdefined":
            {
                var ud = new RbUserDefined
                {
                    ClassName = Intern(obj["class"]!.GetValue<string>(), symbols),
                    Data = Convert.FromBase64String(obj["b64"]!.GetValue<string>()),
                };
                if (sharedId is int udid) byId[udid] = ud;
                return ud;
            }
            case "regexp":
            {
                var re = new RbRegexp
                {
                    Pattern = obj["pattern"] is JsonNode pNode ? Encoding.UTF8.GetBytes(pNode.GetValue<string>()) : Convert.FromBase64String(obj["b64"]!.GetValue<string>()),
                    Options = (byte)obj["options"]!.GetValue<int>(),
                };
                if (sharedId is int reid) byId[reid] = re;
                if (obj["ivars"] is JsonArray ivarsArr) re.IVars = BuildPairs(ivarsArr, byId, symbols, depth);
                return re;
            }
            default:
                throw new SaveFormatException($"Bilinmeyen rb etiketi: {tag}");
        }
    }

    private static List<KeyValuePair<RbSymbol, object?>> BuildPairs(JsonArray arr, Dictionary<int, object> byId, Dictionary<string, RbSymbol> symbols, DepthGuard depth)
    {
        var list = new List<KeyValuePair<RbSymbol, object?>>();
        foreach (var pair in arr)
        {
            var kv = (JsonArray)pair!;
            list.Add(new(Intern(kv[0]!.GetValue<string>(), symbols), Build(kv[1], byId, symbols, depth)));
        }
        return list;
    }
}
