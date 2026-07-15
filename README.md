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
| JSON / XML / INI / metin | `.json`, `.xml`, `.ini`, `.cfg`, ... | Doğrudan düzenleme |
| Bilinmeyen ikili | * | Base64 olarak görüntülenir, değiştirilmeden geri indirilebilir |

Sarmalayıcı katmanlar (gzip, zlib, base64, LZString) özyinelemeli olarak açılır ve indirirken aynı sırayla geri uygulanır — ör. `base64(gzip(json))` bir dosya otomatik çözülür.

## Mimari

```
src/SaveEditor.Core   – format algılama + çözücüler (bağımsız kütüphane)
  FormatDetector      – format/sarmalayıcı algılama hattı
  Wrappers            – gzip, zlib, base64, lz-string katmanları
  Formats/            – ISaveFormat uygulamaları (JSON, ES3, GVAS, Ren'Py, SQLite, ...)
  Pickle/             – Python pickle okuyucu/yazıcı (protokol 0-5) + JSON köprüsü
src/SaveEditor.Web    – ASP.NET Core API + tarayıcı arayüzü (wwwroot)
tests/SaveEditor.Tests – round-trip testleri
```

Doğrulama notları: pickle motoru gerçek CPython çıktılarıyla çapraz doğrulandı (paylaşılan referanslar, döngüler, set/frozenset, OrderedDict, büyük tamsayılar dahil); GVAS gerçek oyun kayıtlarıyla (Deep Rock Galactic dahil) **bayt-uyumlu** round-trip verir; Ren'Py çıktıları CPython `pickle.loads` ile yapısal olarak birebir doğrulanmıştır. JSON'un temsil edemediği özel float değerleri (NaN, ±Infinity, -0.0) etiketli olarak taşınır ve kayıpsız geri yazılır.

Her format `Read` ile düzenlenebilir bir JSON ağacına açılır ve `Write` ile orijinal ikili biçimine geri döner. İkilik formatlarda tanınmayan bölümler `{"__raw": "<base64>"}` düğümleriyle bayt-uyumlu taşınır; Python'a özgü değerler `{"py": "tuple" | "global" | "reduce" | ...}` etiketiyle temsil edilir.

## Bilinen sınırlamalar

- 2^53'ten büyük tamsayılar tarayıcı JSON'unda hassasiyet kaybedebilir (Ham JSON modunda dokunulmayan değerler sunucu tarafında etkilenmez... ağaçta düzenlenen düğümler için geçerlidir).
- GVAS `MapProperty` / `TextProperty` salt-ham (base64) taşınır, alan alan düzenlenemez.
- RPG Maker VX Ace (`.rvdata2`, Ruby Marshal) ve Flash `.sol` (AMF) henüz desteklenmiyor.
- INI dosyalarında yorum satırları kaydederken korunmaz.

**Önemli:** Düzenlemeden önce kayıt dosyanızın yedeğini alın.
