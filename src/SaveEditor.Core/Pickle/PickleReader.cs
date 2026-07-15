using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace SaveEditor.Core.Pickle;

/// <summary>Python pickle deserializer (protocols 0-5) building a PyModel graph.</summary>
public sealed class PickleReader(byte[] data)
{
    private int _pos;
    private readonly List<object?> _stack = [];
    private readonly Stack<int> _marks = new();
    private readonly Dictionary<long, object?> _memo = [];

    public int Protocol { get; private set; } = 1;

    private byte _lastOp;
    private long _lastOpPos;

    /// <summary>Diagnostic hook: called with (position, opcode) for every opcode consumed.</summary>
    public Action<long, byte>? OpcodeTrace { get; set; }

    public object? Read()
    {
        try
        {
            return ReadCore();
        }
        catch (SaveFormatException ex)
        {
            char printable = _lastOp is >= 0x20 and < 0x7f ? (char)_lastOp : '?';
            throw new SaveFormatException(
                $"{ex.Message} [opcode 0x{_lastOp:X2} '{printable}', konum {_lastOpPos}/{data.Length}]", ex);
        }
    }

    private object? ReadCore()
    {
        while (true)
        {
            _lastOpPos = _pos;
            byte op = NextByte();
            _lastOp = op;
            OpcodeTrace?.Invoke(_lastOpPos, op);
            switch (op)
            {
                case 0x80: Protocol = Math.Max(Protocol, NextByte()); break; // PROTO
                case 0x95: _pos += 8; break;                                  // FRAME
                case (byte)'.': return Pop();                                 // STOP

                case (byte)'N': Push(null); break;
                case 0x88: Push(true); break;    // NEWTRUE
                case 0x89: Push(false); break;   // NEWFALSE

                case (byte)'I':                  // INT (text)
                {
                    string line = ReadLine();
                    if (line == "01") Push(true);
                    else if (line == "00") Push(false);
                    else Push(long.Parse(line, CultureInfo.InvariantCulture));
                    break;
                }
                case (byte)'J': Push((long)ReadInt32()); break;                       // BININT
                case (byte)'K': Push((long)NextByte()); break;                        // BININT1
                case (byte)'M': Push((long)ReadUInt16()); break;                      // BININT2
                case (byte)'L':                                                       // LONG (text)
                {
                    string line = ReadLine().TrimEnd('L');
                    var big = line.Length == 0 ? BigInteger.Zero : BigInteger.Parse(line, CultureInfo.InvariantCulture);
                    Push(Normalize(big));
                    break;
                }
                case 0x8a: Push(ReadLong(NextByte())); break;                         // LONG1
                case 0x8b: Push(ReadLong(checked((int)ReadUInt32()))); break;         // LONG4

                case (byte)'F': Push(double.Parse(ReadLine(), CultureInfo.InvariantCulture)); break; // FLOAT
                case (byte)'G':                                                       // BINFLOAT (big-endian)
                    Push(BinaryPrimitives.ReadDoubleBigEndian(Next(8)));
                    break;

                case (byte)'S':                                                       // STRING (repr'd)
                {
                    string line = ReadLine();
                    Push(UnescapePyString(line));
                    break;
                }
                case (byte)'T': Push(Latin1(Next(checked((int)ReadUInt32())))); break; // BINSTRING
                case (byte)'U': Push(Latin1(Next(NextByte()))); break;                 // SHORT_BINSTRING
                case (byte)'V': Push(ReadLine()); break;                               // UNICODE (raw-unicode-escape; near-ASCII in practice)
                case (byte)'X': Push(Encoding.UTF8.GetString(Next(checked((int)ReadUInt32())))); break; // BINUNICODE
                case 0x8c: Push(Encoding.UTF8.GetString(Next(NextByte()))); break;     // SHORT_BINUNICODE
                case 0x8d: Push(Encoding.UTF8.GetString(Next(checked((int)ReadUInt64())))); break; // BINUNICODE8

                case (byte)'B': Push(Next(checked((int)ReadUInt32())).ToArray()); break; // BINBYTES
                case (byte)'C': Push(Next(NextByte()).ToArray()); break;                 // SHORT_BINBYTES
                case 0x8e: Push(Next(checked((int)ReadUInt64())).ToArray()); break;      // BINBYTES8
                case 0x96: Push(Next(checked((int)ReadUInt64())).ToArray()); break;      // BYTEARRAY8

                case (byte)'(': _marks.Push(_stack.Count); break;    // MARK
                case (byte)'0': Pop(); break;                         // POP
                case (byte)'1': PopToMark(); break;                   // POP_MARK
                case (byte)'2': Push(Peek()); break;                  // DUP

                case (byte)')': Push(new PyTuple()); break;           // EMPTY_TUPLE
                case (byte)'t':                                       // TUPLE
                {
                    var items = PopToMark();
                    Push(new PyTuple { Items = items });
                    break;
                }
                case 0x85: { var a = Pop(); Push(new PyTuple { Items = [a] }); break; }                         // TUPLE1
                case 0x86: { var b2 = Pop(); var a = Pop(); Push(new PyTuple { Items = [a, b2] }); break; }     // TUPLE2
                case 0x87: { var c = Pop(); var b2 = Pop(); var a = Pop(); Push(new PyTuple { Items = [a, b2, c] }); break; } // TUPLE3

                case (byte)']': Push(new PyList()); break;            // EMPTY_LIST
                case (byte)'l': Push(new PyList { Items = PopToMark() }); break; // LIST
                case (byte)'a':                                       // APPEND
                {
                    var item = Pop();
                    AppendOn(Peek(), [item]);
                    break;
                }
                case (byte)'e':                                       // APPENDS
                {
                    var items = PopToMark();
                    AppendOn(Peek(), items);
                    break;
                }

                case (byte)'}': Push(new PyDict()); break;            // EMPTY_DICT
                case (byte)'d':                                       // DICT
                {
                    var items = PopToMark();
                    var dict = new PyDict();
                    for (int i = 0; i + 1 < items.Count; i += 2) dict.Items.Add(new(items[i], items[i + 1]));
                    Push(dict);
                    break;
                }
                case (byte)'s':                                       // SETITEM
                {
                    var value = Pop();
                    var key = Pop();
                    SetItemOn(Peek(), key, value);
                    break;
                }
                case (byte)'u':                                       // SETITEMS
                {
                    var items = PopToMark();
                    var target = Peek();
                    for (int i = 0; i + 1 < items.Count; i += 2) SetItemOn(target, items[i], items[i + 1]);
                    break;
                }

                case 0x8f: Push(new PySet()); break;                  // EMPTY_SET
                case 0x91: Push(new PySet { Items = PopToMark(), Frozen = true }); break; // FROZENSET
                case 0x90:                                            // ADDITEMS
                {
                    var items = PopToMark();
                    if (Peek() is PySet set) set.Items.AddRange(items);
                    else throw Bad($"ADDITEMS hedefi küme değil (hedef: {TypeName(Peek())})");
                    break;
                }

                case (byte)'p': _memo[long.Parse(ReadLine(), CultureInfo.InvariantCulture)] = Peek(); break; // PUT
                case (byte)'q': _memo[NextByte()] = Peek(); break;                    // BINPUT
                case (byte)'r': _memo[ReadUInt32()] = Peek(); break;                  // LONG_BINPUT
                case 0x94: _memo[_memo.Count] = Peek(); break;                        // MEMOIZE
                case (byte)'g': Push(_memo[long.Parse(ReadLine(), CultureInfo.InvariantCulture)]); break; // GET
                case (byte)'h': Push(_memo[NextByte()]); break;                       // BINGET
                case (byte)'j': Push(_memo[ReadUInt32()]); break;                     // LONG_BINGET

                case (byte)'c': Push(new PyGlobal { Module = ReadLine(), Name = ReadLine() }); break; // GLOBAL
                case 0x93:                                            // STACK_GLOBAL
                {
                    var name = Pop() as string ?? throw Bad("STACK_GLOBAL adı dize değil");
                    var module = Pop() as string ?? throw Bad("STACK_GLOBAL modülü dize değil");
                    Push(new PyGlobal { Module = module, Name = name });
                    break;
                }

                case (byte)'R':                                       // REDUCE
                {
                    var args = Pop();
                    var callable = Pop();
                    Push(new PyObject { Kind = "reduce", Callable = callable, Args = AsTuple(args) });
                    break;
                }
                case 0x81:                                            // NEWOBJ
                {
                    var args = Pop();
                    var cls = Pop();
                    Push(new PyObject { Kind = "newobj", Callable = cls, Args = AsTuple(args) });
                    break;
                }
                case 0x92:                                            // NEWOBJ_EX
                {
                    var kwargs = Pop();
                    var args = Pop();
                    var cls = Pop();
                    Push(new PyObject { Kind = "newobj_ex", Callable = cls, Args = AsTuple(args), KwArgs = kwargs });
                    break;
                }
                case (byte)'b':                                       // BUILD
                {
                    var state = Pop();
                    if (Peek() is PyObject obj)
                    {
                        obj.HasState = true;
                        obj.State = state;
                    }
                    else
                    {
                        var target = Pop();
                        Push(new PyObject { Kind = "reduce", Callable = target, HasState = true, State = state });
                    }
                    break;
                }
                case (byte)'i':                                       // INST (protocol 0)
                {
                    string module = ReadLine();
                    string name = ReadLine();
                    var args = new PyTuple { Items = PopToMark() };
                    Push(new PyObject { Kind = "reduce", Callable = new PyGlobal { Module = module, Name = name }, Args = args });
                    break;
                }
                case (byte)'o':                                       // OBJ (protocol 1)
                {
                    var items = PopToMark();
                    if (items.Count == 0) throw Bad("OBJ için sınıf yok");
                    var cls = items[0];
                    items.RemoveAt(0);
                    Push(new PyObject { Kind = "reduce", Callable = cls, Args = new PyTuple { Items = items } });
                    break;
                }

                case (byte)'P': Push(new PyPersId { Value = ReadLine() }); break;  // PERSID
                case (byte)'Q': Push(new PyPersId { Value = Pop() }); break;       // BINPERSID

                default:
                    throw Bad($"Desteklenmeyen pickle opcode: 0x{op:X2} (konum {_pos - 1})");
            }
        }
    }

    private static object Normalize(BigInteger big)
        => big >= long.MinValue && big <= long.MaxValue ? (long)big : big;

    private object ReadLong(int byteCount)
    {
        if (byteCount == 0) return 0L;
        var bytes = Next(byteCount);
        var big = new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
        return Normalize(big);
    }

    private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static string UnescapePyString(string line)
    {
        // STRING opcode payload is a repr'd, quoted python string.
        if (line.Length >= 2 && (line[0] == '\'' || line[0] == '"') && line[^1] == line[0])
            line = line[1..^1];
        var sb = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c != '\\' || i + 1 >= line.Length) { sb.Append(c); continue; }
            char n = line[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '0': sb.Append('\0'); break;
                case 'x' when i + 2 < line.Length:
                    sb.Append((char)Convert.ToInt32(line.Substring(i + 1, 2), 16));
                    i += 2;
                    break;
                default: sb.Append('\\').Append(n); break;
            }
        }
        return sb.ToString();
    }

    // APPEND(S)/SETITEM(S) also apply to reconstructed class instances
    // (OrderedDict, Ren'Py RevertableList/RevertableDict...).
    private void AppendOn(object? target, IEnumerable<object?> items)
    {
        switch (target)
        {
            case PyList l: l.Items.AddRange(items); return;
            case PyObject o: (o.Appends ??= new PyList()).Items.AddRange(items); return;
            case PySet s: s.Items.AddRange(items); return;
            default: throw Bad($"APPEND hedefi liste değil (hedef: {TypeName(target)})");
        }
    }

    // obj[key] = value; dict-like targets collect pairs, list-like targets
    // treat integer keys as index assignment.
    private void SetItemOn(object? target, object? key, object? value)
    {
        switch (target)
        {
            case PyDict d:
                d.Items.Add(new(key, value));
                return;
            case PyObject o:
                (o.SetItems ??= new PyDict()).Items.Add(new(key, value));
                return;
            case PyList l when key is long idx && idx >= 0 && idx <= l.Items.Count:
                if (idx == l.Items.Count) l.Items.Add(value);
                else l.Items[(int)idx] = value;
                return;
            default:
                throw Bad($"SETITEM hedefi sözlük değil (hedef: {TypeName(target)}, anahtar: {TypeName(key)})");
        }
    }

    private static string TypeName(object? v) => v switch
    {
        null => "None",
        PyDict => "dict",
        PyList => "list",
        PyTuple => "tuple",
        PySet => "set",
        PyObject o => $"instance({(o.Callable as PyGlobal)?.Module}.{(o.Callable as PyGlobal)?.Name})",
        PyGlobal g => $"class({g.Module}.{g.Name})",
        string => "str",
        long or System.Numerics.BigInteger => "int",
        double => "float",
        bool => "bool",
        byte[] => "bytes",
        _ => v.GetType().Name,
    };

    private static PyTuple AsTuple(object? v) => v switch
    {
        PyTuple t => t,
        null => new PyTuple(),
        _ => new PyTuple { Items = [v] },
    };

    private static SaveFormatException Bad(string message) => new($"Pickle çözümleme hatası: {message}");

    private void Push(object? value) => _stack.Add(value);

    private object? Pop()
    {
        if (_stack.Count == 0) throw Bad("Yığın boş");
        var v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    private object? Peek() => _stack.Count > 0 ? _stack[^1] : throw Bad("Yığın boş");

    private List<object?> PopToMark()
    {
        if (_marks.Count == 0) throw Bad("MARK bulunamadı");
        int mark = _marks.Pop();
        var items = _stack.GetRange(mark, _stack.Count - mark);
        _stack.RemoveRange(mark, _stack.Count - mark);
        return items;
    }

    private byte NextByte()
    {
        if (_pos >= data.Length) throw Bad("Beklenmeyen dosya sonu");
        return data[_pos++];
    }

    private ReadOnlySpan<byte> Next(int count)
    {
        if (count < 0 || _pos + count > data.Length) throw Bad("Beklenmeyen dosya sonu");
        var span = data.AsSpan(_pos, count);
        _pos += count;
        return span;
    }

    private int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Next(4));
    private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Next(2));
    private uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Next(4));
    private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Next(8));

    private string ReadLine()
    {
        int start = _pos;
        while (_pos < data.Length && data[_pos] != (byte)'\n') _pos++;
        if (_pos >= data.Length) throw Bad("Satır sonu bulunamadı");
        string s = Encoding.UTF8.GetString(data, start, _pos - start);
        _pos++;
        return s;
    }
}
