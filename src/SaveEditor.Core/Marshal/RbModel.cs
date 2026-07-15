namespace SaveEditor.Core.Marshal;

// In-memory model of a Ruby Marshal (format version 4.8) object graph. Kept
// as CLR types where possible (null, bool, long, double, BigInteger) plus
// the container/object nodes below. Mirrors SaveEditor.Core.Pickle's PyModel
// shape: shared/linked objects use reference identity, exactly like pickle's
// memo-based sharing.

/// <summary>Ruby Symbol (:foo). Distinct from RbString because symbols get
/// their own link table in the marshal format and always round-trip as the
/// same interned value.</summary>
public sealed class RbSymbol(string name)
{
    public string Name { get; } = name;
}

/// <summary>Ruby String. Binary-safe (not guaranteed UTF-8), so raw bytes are
/// kept rather than a CLR string. May carry ivars (e.g. Ruby 1.9+'s "E"
/// encoding flag) attached via the `I` wrapper tag.</summary>
public sealed class RbString
{
    public required byte[] Bytes;
    public List<KeyValuePair<RbSymbol, object?>>? IVars;
}

public sealed class RbArray
{
    public List<object?> Items = [];
    public List<KeyValuePair<RbSymbol, object?>>? IVars;
}

public sealed class RbHash
{
    public List<KeyValuePair<object?, object?>> Items = [];
    public bool HasDefault;
    public object? Default;
    public List<KeyValuePair<RbSymbol, object?>>? IVars;
}

/// <summary>A regular Ruby object (`o` tag): class name + ordered ivars.</summary>
public sealed class RbObject
{
    public required RbSymbol ClassName;
    public List<KeyValuePair<RbSymbol, object?>> IVars = [];
}

/// <summary>Ruby Struct.new instance (`S` tag): class name + ordered members.</summary>
public sealed class RbStruct
{
    public required RbSymbol ClassName;
    public List<KeyValuePair<RbSymbol, object?>> Members = [];
}

/// <summary>A bare Class (`c`) or Module (`m`) reference, stored by name.</summary>
public sealed class RbClassOrModule
{
    public required string Name;
    public required bool IsModule;
}

/// <summary>Object extended with a module at the singleton level (`e` tag).</summary>
public sealed class RbExtended
{
    public required RbSymbol ModuleName;
    public object? Value;
}

/// <summary>Instance of a user-defined subclass of a built-in (String/Array/
/// Hash/...) (`C` tag): class name + the wrapped built-in value.</summary>
public sealed class RbUserClass
{
    public required RbSymbol ClassName;
    public object? Wrapped;
}

/// <summary>Object serialized via marshal_dump/marshal_load (`U` tag):
/// class name + the dumped object graph.</summary>
public sealed class RbUserMarshal
{
    public required RbSymbol ClassName;
    public object? Data;
}

/// <summary>Object serialized via _dump/_load (`u` tag). The dumped bytes are
/// opaque without the class's _load method, so they're kept as raw bytes and
/// round-tripped verbatim rather than interpreted.</summary>
public sealed class RbUserDefined
{
    public required RbSymbol ClassName;
    public required byte[] Data;
}

public sealed class RbRegexp
{
    public required byte[] Pattern;
    public required byte Options;
    public List<KeyValuePair<RbSymbol, object?>>? IVars;
}
