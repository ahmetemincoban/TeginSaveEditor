using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SaveEditor.Core.Pickle;

/// <summary>
/// Python pickle serializer for a PyModel graph. Emits protocol 2 opcodes
/// (protocol 3+/4+ only when bytes or set literals require them), matching
/// what Ren'Py and other Python games expect.
/// </summary>
public sealed class PickleWriter
{
    // See PyJson.MaxDepth: kept well below where a real stack overflow occurs.
    private const int MaxDepth = 500;

    private readonly MemoryStream _out = new();
    private readonly Dictionary<object, int> _memo = new(ReferenceEqualityComparer.Instance);
    private int _minProtocol = 2;
    private int _depth;

    public byte[] Write(object? root, int requestedProtocol = 2)
    {
        // Body is buffered first because bytes/sets can raise the protocol.
        Save(root);
        _out.WriteByte((byte)'.'); // STOP

        int protocol = Math.Max(requestedProtocol, _minProtocol);
        using var result = new MemoryStream();
        result.WriteByte(0x80); // PROTO
        result.WriteByte((byte)protocol);
        _out.WriteTo(result);
        return result.ToArray();
    }

    private void Save(object? value)
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw new SaveFormatException("Veri çok derin iç içe geçmiş.");
        }
        try
        {
            SaveCore(value);
        }
        finally
        {
            _depth--;
        }
    }

    private void SaveCore(object? value)
    {
        switch (value)
        {
            case null: Emit((byte)'N'); return;
            case bool b: Emit(b ? (byte)0x88 : (byte)0x89); return;
            case long l: SaveInt(l); return;
            case int i: SaveInt(i); return;
            case BigInteger big: SaveBigInt(big); return;
            case double d:
                Emit((byte)'G');
                Span<byte> buf = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(buf, d);
                _out.Write(buf);
                return;
        }

        if (_memo.TryGetValue(value, out int memoId))
        {
            EmitGet(memoId);
            return;
        }

        switch (value)
        {
            case string s: SaveString(s); return;
            case byte[] bytes: SaveBytes(bytes); return;
            case PyTuple tuple: SaveTuple(tuple); return;
            case PyList list: SaveList(list); return;
            case PyDict dict: SaveDict(dict); return;
            case PySet set: SaveSet(set); return;
            case PyGlobal global: SaveGlobal(global); return;
            case PyObject obj: SaveObject(obj); return;
            case PyPersId pid:
                Save(pid.Value);
                Emit((byte)'Q'); // BINPERSID
                return;
            default:
                throw new SaveFormatException($"Pickle'a yazılamayan tip: {value.GetType().Name}");
        }
    }

    private void SaveInt(long l)
    {
        if (l is >= 0 and <= 0xff) { Emit((byte)'K'); _out.WriteByte((byte)l); return; }
        if (l is >= 0 and <= 0xffff)
        {
            Emit((byte)'M');
            Span<byte> b2 = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b2, (ushort)l);
            _out.Write(b2);
            return;
        }
        if (l is >= int.MinValue and <= int.MaxValue)
        {
            Emit((byte)'J');
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b4, (int)l);
            _out.Write(b4);
            return;
        }
        SaveBigInt(l);
    }

    private void SaveBigInt(BigInteger big)
    {
        byte[] bytes = big.IsZero ? [] : big.ToByteArray(isUnsigned: false, isBigEndian: false);
        if (bytes.Length < 256)
        {
            Emit(0x8a); // LONG1
            _out.WriteByte((byte)bytes.Length);
        }
        else
        {
            Emit(0x8b); // LONG4
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)bytes.Length);
            _out.Write(b4);
        }
        _out.Write(bytes);
    }

    private void SaveString(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        Emit((byte)'X'); // BINUNICODE
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)utf8.Length);
        _out.Write(b4);
        _out.Write(utf8);
        Memoize(s);
    }

    private void SaveBytes(byte[] bytes)
    {
        _minProtocol = Math.Max(_minProtocol, 3);
        if (bytes.Length <= 0xff)
        {
            Emit((byte)'C'); // SHORT_BINBYTES
            _out.WriteByte((byte)bytes.Length);
        }
        else
        {
            Emit((byte)'B'); // BINBYTES
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)bytes.Length);
            _out.Write(b4);
        }
        _out.Write(bytes);
        Memoize(bytes);
    }

    private void SaveTuple(PyTuple tuple)
    {
        if (tuple.Items.Count == 0) { Emit((byte)')'); return; }
        if (tuple.Items.Count <= 3)
        {
            foreach (var item in tuple.Items) Save(item);
            Emit(tuple.Items.Count switch { 1 => (byte)0x85, 2 => (byte)0x86, _ => (byte)0x87 });
        }
        else
        {
            Emit((byte)'('); // MARK
            foreach (var item in tuple.Items) Save(item);
            Emit((byte)'t'); // TUPLE
        }
        Memoize(tuple);
    }

    private void SaveList(PyList list)
    {
        Emit((byte)']'); // EMPTY_LIST
        Memoize(list);
        SaveBatch(list.Items, single: (byte)'a', batch: (byte)'e');
    }

    private void SaveDict(PyDict dict)
    {
        Emit((byte)'}'); // EMPTY_DICT
        Memoize(dict);
        SaveDictItems(dict.Items);
    }

    private void SaveDictItems(List<KeyValuePair<object?, object?>> items)
    {
        const int chunk = 500;
        for (int start = 0; start < items.Count; start += chunk)
        {
            int end = Math.Min(start + chunk, items.Count);
            if (end - start == 1)
            {
                Save(items[start].Key);
                Save(items[start].Value);
                Emit((byte)'s'); // SETITEM
                continue;
            }
            Emit((byte)'('); // MARK
            for (int i = start; i < end; i++)
            {
                Save(items[i].Key);
                Save(items[i].Value);
            }
            Emit((byte)'u'); // SETITEMS
        }
    }

    private void SaveSet(PySet set)
    {
        _minProtocol = Math.Max(_minProtocol, 4);
        if (set.Frozen)
        {
            Emit((byte)'('); // MARK
            foreach (var item in set.Items) Save(item);
            Emit(0x91); // FROZENSET
            Memoize(set);
            return;
        }
        Emit(0x8f); // EMPTY_SET
        Memoize(set);
        if (set.Items.Count > 0)
        {
            Emit((byte)'('); // MARK
            foreach (var item in set.Items) Save(item);
            Emit(0x90); // ADDITEMS
        }
    }

    private void SaveGlobal(PyGlobal global)
    {
        Emit((byte)'c'); // GLOBAL
        WriteLine(global.Module);
        WriteLine(global.Name);
        Memoize(global);
    }

    private void SaveObject(PyObject obj)
    {
        Save(obj.Callable);
        if (obj.Kind == "newobj_ex")
        {
            SaveTupleValue(obj.Args);
            Save(obj.KwArgs);
            Emit(0x92); // NEWOBJ_EX
            _minProtocol = Math.Max(_minProtocol, 4);
        }
        else
        {
            SaveTupleValue(obj.Args);
            Emit(obj.Kind == "newobj" ? (byte)0x81 : (byte)'R'); // NEWOBJ / REDUCE
        }
        Memoize(obj);
        if (obj.Appends is not null)
        {
            SaveBatch(obj.Appends.Items, single: (byte)'a', batch: (byte)'e');
        }
        if (obj.SetItems is not null)
        {
            SaveDictItems(obj.SetItems.Items);
        }
        if (obj.HasState)
        {
            Save(obj.State);
            Emit((byte)'b'); // BUILD
        }
    }

    /// <summary>Saves a tuple without memo sharing (argument tuples are structural).</summary>
    private void SaveTupleValue(PyTuple tuple)
    {
        if (_memo.TryGetValue(tuple, out int memoId))
        {
            EmitGet(memoId);
            return;
        }
        SaveTuple(tuple);
    }

    private void SaveBatch(List<object?> items, byte single, byte batch)
    {
        const int chunk = 1000;
        for (int start = 0; start < items.Count; start += chunk)
        {
            int end = Math.Min(start + chunk, items.Count);
            if (end - start == 1)
            {
                Save(items[start]);
                Emit(single);
                continue;
            }
            Emit((byte)'('); // MARK
            for (int i = start; i < end; i++) Save(items[i]);
            Emit(batch);
        }
    }

    private void Memoize(object value)
    {
        int id = _memo.Count;
        _memo[value] = id;
        if (id <= 0xff)
        {
            Emit((byte)'q'); // BINPUT
            _out.WriteByte((byte)id);
        }
        else
        {
            Emit((byte)'r'); // LONG_BINPUT
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)id);
            _out.Write(b4);
        }
    }

    private void EmitGet(int id)
    {
        if (id <= 0xff)
        {
            Emit((byte)'h'); // BINGET
            _out.WriteByte((byte)id);
        }
        else
        {
            Emit((byte)'j'); // LONG_BINGET
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)id);
            _out.Write(b4);
        }
    }

    private void WriteLine(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        _out.Write(bytes);
        _out.WriteByte((byte)'\n');
    }

    private void Emit(byte op) => _out.WriteByte(op);
}
