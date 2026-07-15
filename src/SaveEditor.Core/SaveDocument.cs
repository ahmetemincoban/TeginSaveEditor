using System.Text.Json.Nodes;

namespace SaveEditor.Core;

/// <summary>
/// A parsed save file: an editable JSON tree plus everything needed to
/// re-encode it back to the original on-disk format.
/// </summary>
public sealed class SaveDocument
{
    public required string FormatId { get; init; }
    public required string FormatName { get; init; }
    public required string FileName { get; init; }

    /// <summary>Editable tree. Binary/opaque leaves use tagged objects like {"__type":"bytes","b64":...}.</summary>
    public JsonNode? Root { get; set; }

    /// <summary>Wrapper codec ids applied outermost-first (e.g. ["lzstring-base64"] for .rpgsave).</summary>
    public List<string> Wrappers { get; } = [];

    /// <summary>Format-specific round-trip state (original bytes, encryption settings, zip side entries...).</summary>
    public Dictionary<string, object?> State { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>False when the file was recognized but can only be viewed, not re-encoded.</summary>
    public bool Editable { get; set; } = true;
}

public sealed class ReadContext
{
    public string? Password { get; init; }
}

public interface ISaveFormat
{
    string Id { get; }
    string Name { get; }

    /// <summary>Lower runs first.</summary>
    int Priority { get; }

    bool CanRead(byte[] data, string fileName);
    SaveDocument Read(byte[] data, string fileName, ReadContext ctx);
    byte[] Write(SaveDocument doc);
}

/// <summary>A reversible encoding layer (compression / text encoding) around a payload.</summary>
public interface IWrapper
{
    string Id { get; }
    bool TryUnwrap(byte[] data, out byte[] inner);
    byte[] Wrap(byte[] data);
}

public sealed class SaveFormatException : Exception
{
    public SaveFormatException(string message) : base(message) { }
    public SaveFormatException(string message, Exception inner) : base(message, inner) { }
}
