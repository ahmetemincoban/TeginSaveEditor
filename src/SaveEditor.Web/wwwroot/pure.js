"use strict";

// DOM-free helper functions shared with app.js, kept separate so they can be
// sanity-checked with plain `node` (no browser, no test framework) — see
// scripts/check-pure.js. Loaded as a normal <script> before app.js; in the
// browser these just become globals like everything else in this project.

const VALUE_TYPES = [
  { type: "string", label: "metin" },
  { type: "number", label: "sayı" },
  { type: "boolean", label: "mantıksal" },
  { type: "null", label: "null" },
];

function valueTypeOf(v) { return v === null ? "null" : typeof v; }

function textForType(v, type) {
  if (type === "null") return "";
  if (type === "boolean") return (v === true || v === "true") ? "true" : "false";
  if (v === null || v === undefined) return "";
  return typeof v === "string" ? v : String(v);
}

/// Parses `text` as an explicit target `type` ("string"/"number"/"boolean"/"null").
function parseByType(text, type) {
  const t = text.trim();
  if (type === "null") return { ok: true, value: null };
  if (type === "number") {
    if (t === "") return { ok: false, error: "Sayı girin" };
    const n = Number(t);
    if (!Number.isFinite(n)) return { ok: false, error: "Geçersiz sayı" };
    return { ok: true, value: n };
  }
  if (type === "boolean") {
    if (t === "true" || t === "1") return { ok: true, value: true };
    if (t === "false" || t === "0") return { ok: true, value: false };
    return { ok: false, error: "true veya false girin" };
  }
  // string: keep verbatim (untrimmed, so leading/trailing spaces are preserved)
  return { ok: true, value: text };
}

/// Match predicate shared by the search box's key and value filters.
function matchesQuery(text, query, exact) {
  return exact ? text === query : text.includes(query);
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = { VALUE_TYPES, valueTypeOf, textForType, parseByType, matchesQuery };
}
