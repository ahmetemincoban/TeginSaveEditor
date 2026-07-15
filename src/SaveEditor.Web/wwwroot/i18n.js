"use strict";

// UI language support. Static texts in index.html carry data-i18n /
// data-i18n-placeholder / data-i18n-title attributes resolved by applyI18n();
// dynamic strings in app.js go through t(). Language choice persists in
// localStorage; first visit follows the browser language.

const I18N = {
  tr: {
    title: "Save Editor — Oyun Kayıt Dosyası Düzenleyici",
    subtitle: "Oyun kayıt dosyanı yükle, formatı otomatik algılansın, değerleri düzenle ve geri indir.",
    dropText: "Dosyayı buraya sürükle",
    dropOr: "veya tıklayıp seç",
    passwordLabel: "Şifre (gerekliyse, örn. şifreli .es3):",
    passwordPlaceholder: "varsayılan otomatik denenir",
    pasteToggle: "✂ Metin / kod yapıştır",
    pastePlaceholder: "Base64, JSON veya kayıt metnini buraya yapıştırın...",
    pasteSubmit: "Çözümle",
    rawJson: "{ } Ham JSON",
    treeView: "🌳 Ağaç Görünümü",
    download: "⬇ İndir",
    preparing: "Hazırlanıyor...",
    close: "✕ Kapat",
    searchPlaceholder: "Anahtar veya değer ara (ör. gold, hp, para)...",
    filterKeys: "anahtar",
    filterValues: "değer",
    filterExact: "tam eşleşme",
    expand: "＋ Genişlet",
    collapse: "－ Daralt",
    apply: "Uygula",
    footer: "Tüm işlem yerel sunucunuzda yapılır; dosyalar hiçbir yere gönderilmez. Düzenlemeden önce kayıt dosyanızı yedekleyin!",
    unknownError: "Bilinmeyen hata.",
    serverUnreachable: "Sunucuya ulaşılamadı: ",
    wrappersLabel: "sarmal: ",
    readonlyWarning: "Bu dosya salt okunur açıldı; indirme orijinal veriyi üretir.",
    confirmClose: "Kaydedilmemiş değişiklikler kaybolur. Kapatılsın mı?",
    errorPrefix: "Hata: ",
    invalidJson: "Geçersiz JSON: ",
    rootLabel: "(kök)",
    itemsCount: "[{n} öğe]",
    fieldsCount: "{{n} alan}",
    showMore: "Daha fazla göster ({n} öğe kaldı)",
    addItem: "Öğe ekle",
    newFieldName: "Yeni alan adı:",
    fieldExists: "Bu alan zaten var.",
    valuePrompt: "Değer (JSON — ör. 0, \"metin\", true, {}):",
    invalidJsonValue: "Geçersiz JSON değeri.",
    deleteItem: "Sil",
    confirmDelete: "Bu öğe silinsin mi?",
    clickToEdit: "Düzenlemek için tıkla",
    noResults: "Sonuç yok.",
    firstNResults: "İlk {n} sonuç gösteriliyor; aramayı daraltın.",
    typeString: "metin",
    typeNumber: "sayı",
    typeBoolean: "mantıksal",
    typeNull: "null",
    errEnterNumber: "Sayı girin",
    errInvalidNumber: "Geçersiz sayı",
    errEnterBoolean: "true veya false girin",
  },
  en: {
    title: "Save Editor — Game Save File Editor",
    subtitle: "Upload your game save, let the format be detected automatically, edit values and download it back.",
    dropText: "Drop your file here",
    dropOr: "or click to browse",
    passwordLabel: "Password (if needed, e.g. encrypted .es3):",
    passwordPlaceholder: "defaults are tried automatically",
    pasteToggle: "✂ Paste text / code",
    pastePlaceholder: "Paste Base64, JSON or save text here...",
    pasteSubmit: "Parse",
    rawJson: "{ } Raw JSON",
    treeView: "🌳 Tree View",
    download: "⬇ Download",
    preparing: "Preparing...",
    close: "✕ Close",
    searchPlaceholder: "Search keys or values (e.g. gold, hp, coins)...",
    filterKeys: "keys",
    filterValues: "values",
    filterExact: "exact match",
    expand: "＋ Expand",
    collapse: "－ Collapse",
    apply: "Apply",
    footer: "Everything runs on your local server; files are never sent anywhere. Back up your save file before editing!",
    unknownError: "Unknown error.",
    serverUnreachable: "Could not reach the server: ",
    wrappersLabel: "wrapped: ",
    readonlyWarning: "This file was opened read-only; downloading returns the original data.",
    confirmClose: "Unsaved changes will be lost. Close anyway?",
    errorPrefix: "Error: ",
    invalidJson: "Invalid JSON: ",
    rootLabel: "(root)",
    itemsCount: "[{n} items]",
    fieldsCount: "{{n} fields}",
    showMore: "Show more ({n} items left)",
    addItem: "Add item",
    newFieldName: "New field name:",
    fieldExists: "This field already exists.",
    valuePrompt: "Value (JSON — e.g. 0, \"text\", true, {}):",
    invalidJsonValue: "Invalid JSON value.",
    deleteItem: "Delete",
    confirmDelete: "Delete this item?",
    clickToEdit: "Click to edit",
    noResults: "No results.",
    firstNResults: "Showing first {n} results; narrow your search.",
    typeString: "string",
    typeNumber: "number",
    typeBoolean: "boolean",
    typeNull: "null",
    errEnterNumber: "Enter a number",
    errInvalidNumber: "Invalid number",
    errEnterBoolean: "Enter true or false",
  },
};

let LANG = (() => {
  try {
    const saved = localStorage.getItem("se-lang");
    if (saved === "tr" || saved === "en") return saved;
  } catch { /* storage may be unavailable (e.g. plain node) */ }
  const browserLang = typeof navigator !== "undefined" ? navigator.language || "" : "";
  return browserLang.toLowerCase().startsWith("tr") ? "tr" : "en";
})();

function t(key, vars) {
  let s = (I18N[LANG] && I18N[LANG][key]) ?? I18N.en[key] ?? key;
  if (vars) for (const k in vars) s = s.replace("{" + k + "}", vars[k]);
  return s;
}

function setLang(lang) {
  LANG = lang;
  try { localStorage.setItem("se-lang", lang); } catch { /* ignore */ }
  applyI18n();
  if (typeof onLanguageChange === "function") onLanguageChange();
}

function applyI18n() {
  document.documentElement.lang = LANG;
  document.title = t("title");
  document.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.dataset.i18n); });
  document.querySelectorAll("[data-i18n-placeholder]").forEach((el) => { el.placeholder = t(el.dataset.i18nPlaceholder); });
  document.querySelectorAll("#lang-switch button").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.lang === LANG);
  });
}

if (typeof document !== "undefined") {
  document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll("#lang-switch button").forEach((btn) => {
      btn.addEventListener("click", () => setLang(btn.dataset.lang));
    });
    applyI18n();
  });
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = { I18N, t };
}
