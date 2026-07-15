using System.Globalization;
using System.Numerics;
using System.Text;

namespace SaveEditor.Core.Marshal;

/// <summary>Ruby Marshal format 4.8 serializer for an RbModel graph.</summary>
public sealed class MarshalWriter
{
    private const int MaxDepth = 500;

    private readonly MemoryStream _out = new();
    private readonly Dictionary<object, int> _objectMemo = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RbSymbol, int> _symbolMemo = new(ReferenceEqualityComparer.Instance);
    private int _depth;

    public byte[] Write(object? root)
    {
        _out.WriteByte(4);
        _out.WriteByte(8);
        WriteValue(root);
        return _out.ToArray();
    }

    private void WriteValue(object? value)
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw new SaveFormatException("Veri çok derin iç içe geçmiş.");
        }
        try
        {
            WriteValueCore(value);
        }
        finally
        {
            _depth--;
        }
    }

    private void WriteValueCore(object? value)
    {
        switch (value)
        {
            case null: Emit('0'); return;
            case bool b: Emit(b ? 'T' : 'F'); return;
            case long l:
                // The Fixnum tag's length prefix is structurally capped at 4
                // bytes (values 5..127/-128..-5 are reserved for direct small
                // values), so anything that doesn't fit gets promoted to
                // Bignum, exactly like real Ruby does for large Fixnums.
                if (FitsFixnumEncoding(l)) { Emit('i'); WriteLong(l); }
                else WriteValueCore(new BigInteger(l));
                return;
            case RbSymbol sym: WriteSymbolValue(sym); return;
        }

        if (_objectMemo.TryGetValue(value, out int id))
        {
            Emit('@');
            WriteLong(id);
            return;
        }

        var ivars = GetIVars(value);
        if (ivars is { Count: > 0 })
        {
            Emit('I');
            WriteBody(value);
            WriteLong(ivars.Count);
            foreach (var kv in ivars)
            {
                WriteValue(kv.Key);
                WriteValue(kv.Value);
            }
            return;
        }
        WriteBody(value);
    }

    private void WriteBody(object value)
    {
        _objectMemo[value] = _objectMemo.Count;
        WriteTagAndPayload(value);
    }

    /// <summary>Writes a value's tag and payload without claiming a link
    /// slot for it. Used for the wrapped content of 'e'/'C' (Extended/
    /// UserClass): unlike 'U' (UserMarshal), where marshal_dump returns a
    /// genuinely independent object, the wrapped value there IS the same
    /// underlying Ruby object as the wrapper — e.g. for
    /// `class MyArr &lt; Array; end`, the "wrapped array" isn't a second
    /// object, it's `self` read as an Array. Registering it separately
    /// would consume a link-table slot real Ruby never allocates, which
    /// desyncs every subsequent '@' back-reference in the file. Verified
    /// against real Marshal.dump output (see MarshalTests.cs).</summary>
    private void WriteInlineWrapped(object? value)
    {
        switch (value)
        {
            case null: Emit('0'); return;
            case bool b: Emit(b ? 'T' : 'F'); return;
            case long l:
                if (FitsFixnumEncoding(l)) { Emit('i'); WriteLong(l); }
                else WriteInlineWrapped(new BigInteger(l));
                return;
            case RbSymbol sym: WriteSymbolValue(sym); return;
        }
        if (_objectMemo.TryGetValue(value, out int id))
        {
            Emit('@');
            WriteLong(id);
            return;
        }
        WriteTagAndPayload(value);
    }

    private void WriteTagAndPayload(object value)
    {
        switch (value)
        {
            case double d:
                Emit('f');
                WriteByteString(Encoding.ASCII.GetBytes(FormatFloat(d)));
                return;
            case BigInteger big:
                Emit('l');
                WriteBignumBody(big);
                return;
            case RbString s:
                Emit('"');
                WriteByteString(s.Bytes);
                return;
            case RbArray a:
                Emit('[');
                WriteLong(a.Items.Count);
                foreach (var item in a.Items) WriteValue(item);
                return;
            case RbHash h:
                Emit(h.HasDefault ? '}' : '{');
                WriteLong(h.Items.Count);
                foreach (var kv in h.Items) { WriteValue(kv.Key); WriteValue(kv.Value); }
                if (h.HasDefault) WriteValue(h.Default);
                return;
            case RbObject o:
                Emit('o');
                WriteValue(o.ClassName);
                WriteLong(o.IVars.Count);
                foreach (var kv in o.IVars) { WriteValue(kv.Key); WriteValue(kv.Value); }
                return;
            case RbStruct st:
                Emit('S');
                WriteValue(st.ClassName);
                WriteLong(st.Members.Count);
                foreach (var kv in st.Members) { WriteValue(kv.Key); WriteValue(kv.Value); }
                return;
            case RbClassOrModule cm:
                Emit(cm.IsModule ? 'm' : 'c');
                WriteByteString(Encoding.UTF8.GetBytes(cm.Name));
                return;
            case RbExtended ext:
                Emit('e');
                WriteValue(ext.ModuleName);
                WriteInlineWrapped(ext.Value);
                return;
            case RbUserClass uc:
                Emit('C');
                WriteValue(uc.ClassName);
                WriteInlineWrapped(uc.Wrapped);
                return;
            case RbUserMarshal um:
                // Unlike 'e'/'C', the dumped value here (marshal_dump's
                // return) is a genuinely independent Ruby object, so it gets
                // its own slot — confirmed against real Marshal.dump output.
                Emit('U');
                WriteValue(um.ClassName);
                WriteValue(um.Data);
                return;
            case RbUserDefined ud:
                Emit('u');
                WriteValue(ud.ClassName);
                WriteByteString(ud.Data);
                return;
            case RbRegexp re:
                Emit('/');
                WriteByteString(re.Pattern);
                _out.WriteByte(re.Options);
                return;
            default:
                throw new SaveFormatException($"Marshal'a yazılamayan tip: {value.GetType().Name}");
        }
    }

    private static List<KeyValuePair<RbSymbol, object?>>? GetIVars(object value) => value switch
    {
        RbString s => s.IVars,
        RbArray a => a.IVars,
        RbHash h => h.IVars,
        RbRegexp r => r.IVars,
        _ => null,
    };

    private void WriteSymbolValue(RbSymbol sym)
    {
        if (_symbolMemo.TryGetValue(sym, out int id))
        {
            Emit(';');
            WriteLong(id);
            return;
        }
        _symbolMemo[sym] = _symbolMemo.Count;
        Emit(':');
        WriteByteString(Encoding.UTF8.GetBytes(sym.Name));
    }

    /// <summary>Ruby's variable-length integer encoding (marshal.c w_long).
    /// The length prefix is capped at 4 bytes: values 5..127/-128..-5 are
    /// reserved for direct small values, leaving only 1..4/-4..-1 free to
    /// mean "N-byte length prefix follows".</summary>
    private void WriteLong(long x)
    {
        if (x == 0) { _out.WriteByte(0); return; }
        if (x > 0 && x < 123) { _out.WriteByte((byte)(x + 5)); return; }
        if (x < 0 && x > -124) { _out.WriteByte(unchecked((byte)(x - 5))); return; }

        Span<byte> buf = stackalloc byte[4];
        int len = 0;
        long v = x;
        for (int i = 0; i < 4; i++)
        {
            buf[i] = (byte)(v & 0xff);
            v >>= 8;
            if (v == 0 || v == -1) { len = i + 1; break; }
        }
        if (len == 0) throw new SaveFormatException("Marshal tamsayı/uzunluk değeri çok büyük.");
        _out.WriteByte((byte)(x < 0 ? -len : len));
        _out.Write(buf[..len]);
    }

    /// <summary>Whether x must be written as a Fixnum ('i') rather than
    /// promoted to Bignum ('l'). Real Ruby's rule (marshal.c) is the signed
    /// int32 range, not merely "fits in 4 little-endian bytes": a value like
    /// 3_000_000_000 fits in 4 bytes but exceeds int32.MaxValue, so Ruby
    /// dumps it as Bignum — writing it as Fixnum would round-trip as a
    /// negative number on any Ruby built with a 32-bit C `long` (still true
    /// of Windows Ruby).</summary>
    private static bool FitsFixnumEncoding(long x) => x >= int.MinValue && x <= int.MaxValue;

    private void WriteBignumBody(BigInteger big)
    {
        _out.WriteByte(big.Sign < 0 ? (byte)'-' : (byte)'+');
        byte[] mag = BigInteger.Abs(big).ToByteArray(isUnsigned: true, isBigEndian: false);
        if (mag.Length % 2 != 0)
        {
            byte[] padded = new byte[mag.Length + 1];
            mag.CopyTo(padded, 0);
            mag = padded;
        }
        WriteLong(mag.Length / 2);
        _out.Write(mag);
    }

    private static string FormatFloat(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == 0.0) return double.IsNegative(d) ? "-0" : "0";
        string s = d.ToString(CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
        return s;
    }

    private void WriteByteString(byte[] bytes)
    {
        WriteLong(bytes.Length);
        _out.Write(bytes);
    }

    private void Emit(char c) => _out.WriteByte((byte)c);
}
