[Türkçe](README.tr.md) · **English**

# Save Editor

A web app for editing game save files in your browser (similar to [saveeditonline.com](https://www.saveeditonline.com/), but fully local). Upload a save file, let the format be detected automatically, edit values in a tree view, and download the file back.

## Features

- Drag-and-drop upload or text/base64 pasting
- Automatic format and wrapper (gzip/zlib/base64/LZString) detection
- Tree-view editing: key/value search, add/remove entries, raw JSON mode
- Password input for encrypted .es3 files (common default passwords are tried automatically)
- Unrecognized binary sections are preserved as base64 — the file can always be written back
- Files never leave your machine; everything is processed in local server memory

## Running

Requires the [.NET SDK 10](https://dotnet.microsoft.com/download) or later.

```powershell
dotnet run --project src/SaveEditor.Web --urls http://localhost:5210
# Open in your browser: http://localhost:5210
```

Tests: `dotnet test` &nbsp;·&nbsp; Publish: `dotnet publish src/SaveEditor.Web -c Release -o publish`

> **Note:** The app is designed for local use only (there is no authentication); do not expose it to your LAN or the internet.

## Supported formats

| Format | Extensions | Notes |
|---|---|---|
| RPG Maker MV | `.rpgsave` | LZString-Base64 is unwrapped, edited as JSON |
| RPG Maker MZ | `.rmmzsave` | zlib is unwrapped, edited as JSON |
| Ren'Py | `.save` | The pickled `log` inside the ZIP is decoded; shared references, cycles and `OrderedDict`/`RevertableDict` patterns are preserved |
| Unity Easy Save 3 | `.es3` | Plain, gzipped and AES-encrypted (PBKDF2-SHA1; default passwords are tried automatically) |
| Unreal Engine 4/5 | `.sav` (GVAS) | Common property types are editable; unrecognized ones are preserved raw (base64) and the file always writes back byte-identical |
| Godot / HTML games | `.save`, `.dat`, ... | JSON plus gzip/zlib/base64 wrapper combinations are unwrapped automatically |
| SQLite | `.sqlite`, `.db`, `.sav` | Tables are edited as JSON; written back with schema/indexes preserved |
| RPG Maker XP/VX/VX Ace | `.rxdata`, `.rvdata`, `.rvdata2` | Ruby Marshal (4.8) is decoded; shared references and cycles are preserved |
| Minecraft | `.dat` (e.g. `level.dat`) | NBT; gzipped files are unwrapped automatically |
| JSON / XML / INI / text | `.json`, `.xml`, `.ini`, `.cfg`, ... | Direct editing; XML/INI/text support UTF-8 and UTF-16 (LE/BE) encodings |
| Unknown binary | * | Displayed as base64, can be downloaded back unchanged |

Wrapper layers (gzip, zlib, base64, LZString) are unwrapped recursively and re-applied in the same order on download — e.g. a `base64(gzip(json))` file is decoded automatically.

## Architecture

```
src/SaveEditor.Core   – format detection + codecs (standalone library)
  FormatDetector      – format/wrapper detection pipeline
  Wrappers            – gzip, zlib, base64, lz-string layers
  Formats/            – ISaveFormat implementations (JSON, ES3, GVAS, Ren'Py, SQLite, NBT, ...)
  Pickle/             – Python pickle reader/writer (protocols 0-5) + JSON bridge
  Marshal/            – Ruby Marshal (4.8) reader/writer + JSON bridge
src/SaveEditor.Web    – ASP.NET Core API + browser UI (wwwroot)
tests/SaveEditor.Tests – round-trip tests
```

Validation notes: the pickle engine is cross-validated against real CPython output (including shared references, cycles, set/frozenset, OrderedDict and big integers); the Ruby Marshal engine is validated byte-for-byte against real Ruby 3.3 `Marshal.dump` output (including object-link identity and the Fixnum/Bignum boundary); GVAS round-trips **byte-identical** against real game saves (including Deep Rock Galactic); Ren'Py output is verified structurally identical via CPython `pickle.loads`. Special float values JSON cannot represent (NaN, ±Infinity, -0.0) are carried tagged and written back losslessly.

Each format is decoded by `Read` into an editable JSON tree and encoded back to its original binary form by `Write`. In binary formats, unrecognized sections travel byte-identical as `{"__raw": "<base64>"}` nodes; Python-specific values are represented with `{"py": "tuple" | "global" | "reduce" | ...}` tags.

## Known limitations

- GVAS `Int64Property`/`UInt64Property` values and Python `int` values in pickle survive above 2^53 by being carried tagged (as strings), so they lose no precision in browser JSON. Plain text formats such as JSON/XML/INI have no such tagging: nodes containing integers above 2^53 may lose precision if edited in the browser.
- In the plain JSON format, special float values such as `-0.0` and `NaN`/`Infinity` are not carried losslessly due to JSON's own limits (deliberately left as-is for that format). In GVAS and pickle these values are tagged and preserved losslessly.
- GVAS `MapProperty` / `TextProperty` travel raw-only (base64) and cannot be edited field by field.
- Flash `.sol` (AMF) is not supported yet.
- INI comment lines are not preserved on save.

**Important:** Back up your save file before editing.
