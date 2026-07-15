**Türkçe** · [English](README.md)

# Save Editor

Oyun kayıt dosyalarını tarayıcıda düzenleyen web uygulaması ([saveeditonline.com](https://www.saveeditonline.com/) benzeri, tamamen yerel çalışır). Dosyayı yükleyin, format otomatik algılanır, değerleri ağaç görünümünde düzenleyin ve dosyayı geri indirin.

## Özellikler

- Sürükle-bırak yükleme veya metin/base64 yapıştırma
- Otomatik format ve sarmalayıcı (gzip/zlib/base64/LZString) algılama
- Ağaç görünümünde düzenleme: anahtar/değer arama, öğe ekleme/silme, Ham JSON modu
- Şifreli .es3 için şifre girişi (varsayılan şifreler otomatik denenir)
- Tanınmayan ikili bölümler base64 olarak korunur — dosya her zaman geri yazılabilir
- Dosyalar makinenizden çıkmaz; her şey yerel sunucu belleğinde işlenir

## Çalıştırma

Gereksinim: [.NET SDK 10](https://dotnet.microsoft.com/download) veya üzeri.

```powershell
dotnet run --project src/SaveEditor.Web --urls http://localhost:5210
# Tarayıcıda açın: http://localhost:5210
```

Testler: `dotnet test` &nbsp;·&nbsp; Dağıtım: `dotnet publish src/SaveEditor.Web -c Release -o publish`

> **Not:** Uygulama yalnızca yerel kullanım için tasarlandı (kimlik doğrulama yoktur); LAN/internete açmayın.

## Desteklenen formatlar

| Format | Uzantılar | Notlar |
|---|---|---|
| RPG Maker MV | `.rpgsave` | LZString-Base64 açılır, JSON olarak düzenlenir |
| RPG Maker MZ | `.rmmzsave` | zlib açılır, JSON olarak düzenlenir |
| Ren'Py | `.save` | ZIP içindeki pickle `log` çözülür; paylaşılan referanslar, döngüler, `OrderedDict`/`RevertableDict` desenleri korunur |
| Unity Easy Save 3 | `.es3` | Düz, gzip'li ve AES-şifreli (PBKDF2-SHA1, varsayılan şifre otomatik denenir) |
| Unreal Engine 4/5 | `.sav` (GVAS) | Yaygın property tipleri düzenlenebilir; tanınmayanlar ham (base64) korunur, dosya her zaman bayt-uyumlu geri yazılır |
| Godot / HTML oyunları | `.save`, `.dat`, ... | JSON + gzip/zlib/base64 sarmalayıcı kombinasyonları otomatik çözülür |
| SQLite | `.sqlite`, `.db`, `.sav` | Tablolar JSON olarak düzenlenir; şema/indeksler korunarak geri yazılır |
| RPG Maker XP/VX/VX Ace | `.rxdata`, `.rvdata`, `.rvdata2` | Ruby Marshal (4.8) çözülür; paylaşılan referanslar ve döngüler korunur |
| Minecraft | `.dat` (ör. `level.dat`) | NBT; gzip'li dosyalar otomatik açılır |
| JSON / XML / INI / metin | `.json`, `.xml`, `.ini`, `.cfg`, ... | Doğrudan düzenleme; XML/INI/metin UTF-8 ve UTF-16 (LE/BE) kodlamalarını destekler |
| Bilinmeyen ikili | * | Base64 olarak görüntülenir, değiştirilmeden geri indirilebilir |

Sarmalayıcı katmanlar (gzip, zlib, base64, LZString) özyinelemeli olarak açılır ve indirirken aynı sırayla geri uygulanır — ör. `base64(gzip(json))` bir dosya otomatik çözülür.

## Mimari

```
src/SaveEditor.Core   – format algılama + çözücüler (bağımsız kütüphane)
  FormatDetector      – format/sarmalayıcı algılama hattı
  Wrappers            – gzip, zlib, base64, lz-string katmanları
  Formats/            – ISaveFormat uygulamaları (JSON, ES3, GVAS, Ren'Py, SQLite, NBT, ...)
  Pickle/             – Python pickle okuyucu/yazıcı (protokol 0-5) + JSON köprüsü
  Marshal/            – Ruby Marshal (4.8) okuyucu/yazıcı + JSON köprüsü
src/SaveEditor.Web    – ASP.NET Core API + tarayıcı arayüzü (wwwroot)
tests/SaveEditor.Tests – round-trip testleri
```

Doğrulama notları: pickle motoru gerçek CPython çıktılarıyla çapraz doğrulandı (paylaşılan referanslar, döngüler, set/frozenset, OrderedDict, büyük tamsayılar dahil); Ruby Marshal motoru gerçek Ruby 3.3 `Marshal.dump` çıktılarıyla bayt düzeyinde doğrulandı (object-link kimliği ve Fixnum/Bignum sınırı dahil); GVAS gerçek oyun kayıtlarıyla (Deep Rock Galactic dahil) **bayt-uyumlu** round-trip verir; Ren'Py çıktıları CPython `pickle.loads` ile yapısal olarak birebir doğrulanmıştır. JSON'un temsil edemediği özel float değerleri (NaN, ±Infinity, -0.0) etiketli olarak taşınır ve kayıpsız geri yazılır.

Her format `Read` ile düzenlenebilir bir JSON ağacına açılır ve `Write` ile orijinal ikili biçimine geri döner. İkilik formatlarda tanınmayan bölümler `{"__raw": "<base64>"}` düğümleriyle bayt-uyumlu taşınır; Python'a özgü değerler `{"py": "tuple" | "global" | "reduce" | ...}` etiketiyle temsil edilir.

## Bilinen sınırlamalar

- GVAS `Int64Property`/`UInt64Property` ve pickle'daki Python `int` değerleri 2^53'ten büyük olsa da etiketlenerek (string olarak) taşınır ve tarayıcı JSON'unda hassasiyet kaybetmez. Düz JSON/XML/INI gibi salt metin formatlarında böyle bir etiketleme yoktur: 2^53'ten büyük tamsayılar içeren düğümler tarayıcıda düzenlenirse hassasiyet kaybedebilir.
- Düz JSON formatında `-0.0` ve `NaN`/`Infinity` gibi özel float değerleri JSON'un kendi sınırı gereği kayıpsız taşınmaz (bu formatta bilinçli olarak düzeltilmedi). GVAS ve pickle'da bu değerler ayrıca etiketlenerek kayıpsız korunur.
- GVAS `MapProperty` / `TextProperty` salt-ham (base64) taşınır, alan alan düzenlenemez.
- Flash `.sol` (AMF) henüz desteklenmiyor.
- INI dosyalarında yorum satırları kaydederken korunmaz.

**Önemli:** Düzenlemeden önce kayıt dosyanızın yedeğini alın.
