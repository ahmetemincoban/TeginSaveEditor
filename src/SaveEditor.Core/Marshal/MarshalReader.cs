using System.Numerics;
using System.Text;

namespace SaveEditor.Core.Marshal;

/// <summary>Ruby Marshal format 4.8 deserializer, building an RbModel graph.</summary>
public sealed class MarshalReader(byte[] data)
{
    private const int MaxDepth = 500;

    private int _pos;
    private readonly List<RbSymbol> _symbols = [];
    private readonly List<object?> _objects = [];
    private int _depth;

    public int MajorVersion { get; private set; }
    public int MinorVersion { get; private set; }

    public object? Read()
    {
        MajorVersion = NextByte();
        MinorVersion = NextByte();
        if (MajorVersion != 4)
            throw Bad($"Desteklenmeyen Marshal sürümü: {MajorVersion}.{MinorVersion}");
        return ReadValue();
    }

    private object? ReadValue() => ReadValue(register: true);

    /// <summary>Reads a value that shares its caller's already-claimed link
    /// slot instead of getting its own. Used for the wrapped content of
    /// 'e'/'C' (Extended/UserClass): unlike 'U' (UserMarshal), where
    /// marshal_dump returns a genuinely independent object, the wrapped
    /// value there IS the same underlying Ruby object as the wrapper — e.g.
    /// for `class MyArr &lt; Array; end`, the "wrapped array" isn't a second
    /// object, it's `self` read as an Array. Registering it separately
    /// consumes a link-table slot real Ruby never allocates, which
    /// desyncs every subsequent '@' back-reference in the file. Verified
    /// against real Marshal.dump output (see MarshalTests.cs).</summary>
    private object? ReadValueInline() => ReadValue(register: false);

    private object? ReadValue(bool register)
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw Bad("Veri çok derin iç içe geçmiş.");
        }
        try
        {
            return ReadValueCore(register);
        }
        finally
        {
            _depth--;
        }
    }

    private object? ReadValueCore(bool register)
    {
        byte tag = NextByte();
        switch (tag)
        {
            case (byte)'0': return null;
            case (byte)'T': return true;
            case (byte)'F': return false;
            case (byte)'i': return ReadFixnum();
            case (byte)'l':
            {
                // Real Ruby's marshal format caps the Fixnum tag's own length
                // prefix at 4 bytes, so it dumps large Fixnums as Bignum too;
                // Marshal.load normalizes those back to a native Fixnum on
                // read. Mirror that: collapse back to `long` when it fits.
                BigInteger big = ReadBignum();
                // Deliberately not a ternary: `cond ? (long)big : big` would
                // unify both branches to BigInteger (long implicitly widens
                // to BigInteger), silently defeating the narrowing.
                object boxed;
                if (big >= long.MinValue && big <= long.MaxValue) boxed = (long)big;
                else boxed = big;
                if (register) Register(boxed);
                return boxed;
            }
            case (byte)'f':
            {
                object boxed = ReadRubyFloat(ReadByteString());
                if (register) Register(boxed);
                return boxed;
            }

            case (byte)':': return ReadSymbolDef();
            case (byte)';': return ReadSymlink();

            case (byte)'@': return ReadObjectLink();

            case (byte)'"':
            {
                var s = new RbString { Bytes = ReadByteString() };
                if (register) Register(s);
                return s;
            }

            case (byte)'[':
            {
                var arr = new RbArray();
                if (register) Register(arr);
                long count = ReadLong();
                for (long i = 0; i < count; i++) arr.Items.Add(ReadValue());
                return arr;
            }

            case (byte)'{':
            case (byte)'}':
            {
                var hash = new RbHash();
                if (register) Register(hash);
                long count = ReadLong();
                for (long i = 0; i < count; i++)
                {
                    object? key = ReadValue();
                    object? value = ReadValue();
                    hash.Items.Add(new(key, value));
                }
                if (tag == (byte)'}')
                {
                    hash.HasDefault = true;
                    hash.Default = ReadValue();
                }
                return hash;
            }

            case (byte)'o':
            {
                var obj = new RbObject { ClassName = ReadSymbolRef() };
                if (register) Register(obj);
                long count = ReadLong();
                for (long i = 0; i < count; i++)
                {
                    var name = ReadSymbolRef();
                    obj.IVars.Add(new(name, ReadValue()));
                }
                return obj;
            }

            case (byte)'S':
            {
                var s = new RbStruct { ClassName = ReadSymbolRef() };
                if (register) Register(s);
                long count = ReadLong();
                for (long i = 0; i < count; i++)
                {
                    var name = ReadSymbolRef();
                    s.Members.Add(new(name, ReadValue()));
                }
                return s;
            }

            case (byte)'c':
            case (byte)'m':
            {
                string name = Encoding.UTF8.GetString(ReadByteString());
                var cm = new RbClassOrModule { Name = name, IsModule = tag == (byte)'m' };
                if (register) Register(cm);
                return cm;
            }

            case (byte)'e':
            {
                var moduleName = ReadSymbolRef();
                var ext = new RbExtended { ModuleName = moduleName };
                if (register) Register(ext);
                ext.Value = ReadValueInline();
                return ext;
            }

            case (byte)'C':
            {
                var className = ReadSymbolRef();
                var uc = new RbUserClass { ClassName = className };
                if (register) Register(uc);
                uc.Wrapped = ReadValueInline();
                return uc;
            }

            case (byte)'U':
            {
                // Unlike 'e'/'C', the dumped value here (marshal_dump's
                // return) is a genuinely independent Ruby object, so it gets
                // its own slot — confirmed against real Marshal.dump output.
                var className = ReadSymbolRef();
                var um = new RbUserMarshal { ClassName = className };
                if (register) Register(um);
                um.Data = ReadValue();
                return um;
            }

            case (byte)'u':
            {
                var className = ReadSymbolRef();
                byte[] blob = ReadByteString();
                var ud = new RbUserDefined { ClassName = className, Data = blob };
                if (register) Register(ud);
                return ud;
            }

            case (byte)'/':
            {
                byte[] pattern = ReadByteString();
                byte options = NextByte();
                var re = new RbRegexp { Pattern = pattern, Options = options };
                if (register) Register(re);
                return re;
            }

            case (byte)'I':
            {
                object? value = ReadValue();
                long count = ReadLong();
                var ivars = new List<KeyValuePair<RbSymbol, object?>>();
                for (long i = 0; i < count; i++)
                {
                    var name = ReadSymbolRef();
                    ivars.Add(new(name, ReadValue()));
                }
                AttachIVars(value, ivars);
                return value;
            }

            default:
                throw Bad($"Desteklenmeyen Marshal tipi: 0x{tag:X2} '{(char)tag}'");
        }
    }

    private static void AttachIVars(object? value, List<KeyValuePair<RbSymbol, object?>> ivars)
    {
        switch (value)
        {
            case RbString s: s.IVars = ivars; break;
            case RbArray a: a.IVars = ivars; break;
            case RbHash h: h.IVars = ivars; break;
            case RbRegexp r: r.IVars = ivars; break;
            default:
                throw Bad($"'I' etiketi desteklenmeyen bir değeri sarıyor: {value?.GetType().Name ?? "null"}");
        }
    }

    private RbSymbol ReadSymbolRef()
    {
        object? v = ReadValue();
        return v as RbSymbol ?? throw Bad($"Sembol bekleniyordu, {v?.GetType().Name ?? "null"} bulundu.");
    }

    private RbSymbol ReadSymbolDef()
    {
        byte[] bytes = ReadByteString();
        var sym = new RbSymbol(Encoding.UTF8.GetString(bytes));
        _symbols.Add(sym);
        return sym;
    }

    private RbSymbol ReadSymlink()
    {
        long id = ReadLong();
        if (id < 0 || id >= _symbols.Count) throw Bad($"Geçersiz sembol bağlantısı: id={id}");
        return _symbols[(int)id];
    }

    private object? ReadObjectLink()
    {
        long id = ReadLong();
        if (id < 0 || id >= _objects.Count) throw Bad($"Geçersiz nesne bağlantısı: id={id}");
        return _objects[(int)id];
    }

    private void Register(object value) => _objects.Add(value);

    private long ReadFixnum() => ReadLong();

    /// <summary>Ruby's variable-length integer encoding (also used for every
    /// length/count field, hence "Long" here matching Ruby's w_long/r_long).</summary>
    private long ReadLong()
    {
        sbyte c = unchecked((sbyte)NextByte());
        if (c == 0) return 0;
        if (c > 0)
        {
            // 5..127 encodes 0..122 directly; 1..4 is a length prefix for a
            // little-endian positive integer of that many bytes.
            if (c > 4) return c - 5;
            int len = c;
            long x = 0;
            for (int i = 0; i < len; i++) x |= (long)NextByte() << (8 * i);
            return x;
        }
        else
        {
            // -128..-5 encodes -123..0 directly; -4..-1 is a length prefix
            // for a little-endian (sign-extended) negative integer.
            if (c < -4) return c + 5;
            int len = -c;
            long x = -1;
            for (int i = 0; i < len; i++)
            {
                x &= ~((long)0xff << (8 * i));
                x |= (long)NextByte() << (8 * i);
            }
            return x;
        }
    }

    private BigInteger ReadBignum()
    {
        byte sign = NextByte();
        if (sign != (byte)'+' && sign != (byte)'-') throw Bad("Geçersiz Bignum işareti.");
        long shortLen = ReadLong();
        if (shortLen < 0 || shortLen > int.MaxValue / 2) throw Bad("Geçersiz Bignum uzunluğu.");
        byte[] bytes = Next((int)(shortLen * 2));
        var magnitude = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        return sign == (byte)'-' ? -magnitude : magnitude;
    }

    private static double ReadRubyFloat(byte[] raw)
    {
        string s = Encoding.ASCII.GetString(raw);
        return s switch
        {
            "nan" => double.NaN,
            "inf" => double.PositiveInfinity,
            "-inf" => double.NegativeInfinity,
            "0" => 0.0,
            "-0" => -0.0,
            _ => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private byte[] ReadByteString()
    {
        long len = ReadLong();
        if (len < 0 || len > 64 * 1024 * 1024) throw Bad("Geçersiz dize uzunluğu.");
        return Next((int)len);
    }

    private byte NextByte()
    {
        if (_pos >= data.Length) throw Bad("Beklenmeyen dosya sonu.");
        return data[_pos++];
    }

    private byte[] Next(int count)
    {
        if (count < 0 || _pos + count > data.Length) throw Bad("Beklenmeyen dosya sonu.");
        byte[] result = new byte[count];
        Array.Copy(data, _pos, result, 0, count);
        _pos += count;
        return result;
    }

    private static SaveFormatException Bad(string message) => new($"Marshal çözümleme hatası: {message}");
}
