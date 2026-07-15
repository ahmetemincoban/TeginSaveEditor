"use strict";

// ---------- state ----------
const state = {
  id: null,
  fileName: null,
  editable: true,
  data: undefined,       // the editable root value
  rawMode: false,
};

const $ = (sel) => document.querySelector(sel);

// ---------- upload ----------
const dropzone = $("#dropzone");
const fileInput = $("#file-input");

dropzone.addEventListener("click", () => fileInput.click());
dropzone.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") fileInput.click(); });
fileInput.addEventListener("change", () => { if (fileInput.files.length) uploadFile(fileInput.files[0]); });

["dragover", "dragenter"].forEach((ev) =>
  dropzone.addEventListener(ev, (e) => { e.preventDefault(); dropzone.classList.add("dragover"); }));
["dragleave", "drop"].forEach((ev) =>
  dropzone.addEventListener(ev, (e) => { e.preventDefault(); dropzone.classList.remove("dragover"); }));
dropzone.addEventListener("drop", (e) => {
  if (e.dataTransfer.files.length) uploadFile(e.dataTransfer.files[0]);
});
// Also accept drops anywhere on the page while upload screen is visible.
document.addEventListener("dragover", (e) => e.preventDefault());
document.addEventListener("drop", (e) => {
  e.preventDefault();
  if (!$("#upload-screen").hidden && e.dataTransfer.files.length) uploadFile(e.dataTransfer.files[0]);
});

$("#paste-toggle").addEventListener("click", () => {
  const area = $("#paste-area");
  area.hidden = !area.hidden;
});

$("#paste-submit").addEventListener("click", async () => {
  const text = $("#paste-text").value;
  if (!text.trim()) return;
  await detectRequest("/api/paste", JSON.stringify({
    text,
    fileName: "pasted.save",
    password: $("#password").value || null,
  }), { "Content-Type": "application/json" });
});

async function uploadFile(file) {
  const form = new FormData();
  form.append("file", file);
  form.append("password", $("#password").value || "");
  await detectRequest("/api/upload", form, null);
}

async function detectRequest(url, body, headers) {
  showError(null);
  dropzone.classList.add("busy");
  try {
    const res = await fetch(url, { method: "POST", body, headers: headers || undefined });
    const json = await res.json();
    if (!res.ok) { showError(json.error || "Bilinmeyen hata."); return; }
    openEditor(json);
  } catch (err) {
    showError("Sunucuya ulaşılamadı: " + err.message);
  } finally {
    dropzone.classList.remove("busy");
  }
}

function showError(msg) {
  const el = $("#upload-error");
  el.hidden = !msg;
  el.textContent = msg || "";
}

// ---------- editor ----------
function openEditor(info) {
  state.id = info.id;
  state.fileName = info.fileName;
  state.editable = info.editable;
  state.data = info.root;
  state.rawMode = false;

  $("#upload-screen").hidden = true;
  $("#editor-screen").hidden = false;

  $("#fi-name").textContent = info.fileName;
  $("#fi-format").textContent = info.formatName;
  $("#fi-wrappers").textContent = info.wrappers && info.wrappers.length ? "sarmal: " + info.wrappers.join(" → ") : "";
  $("#fi-size").textContent = formatSize(info.size);

  const warnings = $("#warnings");
  warnings.innerHTML = "";
  (info.warnings || []).forEach((w) => {
    const div = document.createElement("div");
    div.className = "warn";
    div.textContent = "⚠ " + w;
    warnings.appendChild(div);
  });
  if (!info.editable) {
    const div = document.createElement("div");
    div.className = "warn";
    div.textContent = "⚠ Bu dosya salt okunur açıldı; indirme orijinal veriyi üretir.";
    warnings.appendChild(div);
  }

  $("#search").value = "";
  $("#search-results").hidden = true;
  renderTree();
  setRawMode(typeof state.data === "string"); // text/xml formats edit as raw text
}

$("#btn-reset").addEventListener("click", () => {
  if (!confirm("Kaydedilmemiş değişiklikler kaybolur. Kapatılsın mı?")) return;
  state.id = null;
  $("#editor-screen").hidden = true;
  $("#upload-screen").hidden = false;
  fileInput.value = "";
});

$("#btn-download").addEventListener("click", async () => {
  if (state.rawMode && !applyRaw()) return;
  const btn = $("#btn-download");
  btn.disabled = true;
  btn.textContent = "Hazırlanıyor...";
  try {
    const res = await fetch("/api/download/" + state.id, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ root: state.data === undefined ? null : state.data }),
    });
    if (!res.ok) {
      const json = await res.json().catch(() => ({}));
      alert("Hata: " + (json.error || res.statusText));
      return;
    }
    const blob = await res.blob();
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = state.fileName;
    a.click();
    URL.revokeObjectURL(a.href);
  } finally {
    btn.disabled = false;
    btn.textContent = "⬇ İndir";
  }
});

// ---------- raw mode ----------
$("#btn-view-toggle").addEventListener("click", () => {
  if (state.rawMode) {
    if (!applyRaw()) return;
    setRawMode(false);
  } else {
    setRawMode(true);
  }
});

function setRawMode(on) {
  state.rawMode = on;
  $("#raw-editor").hidden = !on;
  $("#tree").hidden = on;
  $("#toolbar").style.display = on ? "none" : "";
  $("#search-results").hidden = true;
  $("#btn-view-toggle").textContent = on ? "🌳 Ağaç Görünümü" : "{ } Ham JSON";
  $("#raw-error").textContent = "";
  if (on) {
    $("#raw-text").value = typeof state.data === "string"
      ? state.data
      : JSON.stringify(state.data, null, 2);
  } else {
    renderTree();
  }
}

$("#raw-apply").addEventListener("click", applyRaw);

function applyRaw() {
  const text = $("#raw-text").value;
  if (typeof state.data === "string") {
    state.data = text;
    $("#raw-error").textContent = "";
    return true;
  }
  try {
    state.data = JSON.parse(text);
    $("#raw-error").textContent = "";
    return true;
  } catch (err) {
    $("#raw-error").textContent = "Geçersiz JSON: " + err.message;
    return false;
  }
}

// ---------- tree ----------
const treeEl = $("#tree");
let rootApi = null;

function renderTree() {
  treeEl.innerHTML = "";
  rootApi = buildNode("(kök)", { get: () => state.data, set: (v) => (state.data = v) }, [], 0, null);
  treeEl.appendChild(rootApi.el);
  rootApi.expand();
}

function isContainer(v) { return v !== null && typeof v === "object"; }

function pyTag(v) {
  if (!isContainer(v) || Array.isArray(v)) return null;
  if (typeof v.py === "string") return "py:" + v.py;
  if (typeof v.__type === "string") return v.__type;
  return null;
}

// accessor: {get(), set(v)}; container access to reflect edits back.
function buildNode(label, accessor, path, depth, parentApi) {
  const value = accessor.get();
  const node = document.createElement("div");
  node.className = "node";
  const row = document.createElement("div");
  row.className = "row";
  node.appendChild(row);

  const api = {
    el: node, row, path,
    childApis: new Map(),
    expanded: false,
    expand: null,
    collapse: null,
    rebuild: null,
    parentApi,
  };

  const expander = document.createElement("span");
  expander.className = "expander";
  row.appendChild(expander);

  const keyEl = document.createElement("span");
  keyEl.className = "key" + (typeof label === "number" ? " index" : "");
  keyEl.textContent = typeof label === "number" ? "[" + label + "]" : label + ":";
  row.appendChild(keyEl);

  if (isContainer(value)) {
    const tag = pyTag(value);
    if (tag) {
      const tagEl = document.createElement("span");
      tagEl.className = "pytag";
      tagEl.textContent = tag;
      row.appendChild(tagEl);
    }

    const entries = () => Array.isArray(value) ? value.map((_, i) => i) : Object.keys(value);
    const countEl = document.createElement("span");
    countEl.className = "count";
    const updateCount = () => {
      const n = entries().length;
      countEl.textContent = (Array.isArray(value) ? "[" + n + " öğe]" : "{" + n + " alan}");
    };
    updateCount();
    row.appendChild(countEl);

    let childrenEl = null;
    expander.textContent = "▶";

    api.expand = () => {
      if (api.expanded) return;
      api.expanded = true;
      expander.textContent = "▼";
      if (!childrenEl) {
        childrenEl = document.createElement("div");
        childrenEl.className = "children";
        node.appendChild(childrenEl);
        buildChildren();
      }
      childrenEl.style.display = "";
    };
    api.collapse = () => {
      if (!api.expanded) return;
      api.expanded = false;
      expander.textContent = "▶";
      if (childrenEl) childrenEl.style.display = "none";
    };
    api.rebuild = () => {
      if (childrenEl) { childrenEl.innerHTML = ""; api.childApis.clear(); buildChildren(); }
      updateCount();
    };

    function buildChildren() {
      const keys = entries();
      const LIMIT = 2000;
      keys.slice(0, LIMIT).forEach((k) => {
        const childAccessor = {
          get: () => value[k],
          set: (v) => { value[k] = v; },
        };
        const childApi = buildNode(k, childAccessor, path.concat([k]), depth + 1, api);
        api.childApis.set(String(k), childApi);
        addRowActions(childApi, value, k, api);
        childrenEl.appendChild(childApi.el);
      });
      if (keys.length > LIMIT) {
        const more = document.createElement("div");
        more.className = "sr-more";
        more.textContent = "... " + (keys.length - LIMIT) + " öğe daha (performans için gizlendi; Ham JSON görünümünü kullanın)";
        childrenEl.appendChild(more);
      }
    }

    expander.addEventListener("click", () => (api.expanded ? api.collapse() : api.expand()));
    row.addEventListener("dblclick", (e) => {
      if (e.target.closest(".val,.val-input,button")) return;
      api.expanded ? api.collapse() : api.expand();
    });

    // "+" add child
    const actions = document.createElement("span");
    actions.className = "row-actions";
    const addBtn = document.createElement("button");
    addBtn.className = "add";
    addBtn.title = "Öğe ekle";
    addBtn.textContent = "＋";
    addBtn.addEventListener("click", () => {
      let key;
      if (Array.isArray(value)) {
        key = value.length;
      } else {
        key = prompt("Yeni alan adı:");
        if (!key) return;
        if (Object.prototype.hasOwnProperty.call(value, key)) { alert("Bu alan zaten var."); return; }
      }
      const raw = prompt("Değer (JSON — ör. 0, \"metin\", true, {}):", "0");
      if (raw === null) return;
      let parsed;
      try { parsed = JSON.parse(raw); } catch { alert("Geçersiz JSON değeri."); return; }
      value[key] = parsed;
      api.expand();
      api.rebuild();
    });
    actions.appendChild(addBtn);
    row.appendChild(actions);

    if (depth < 2) setTimeout(() => api.expand(), 0);
  } else {
    expander.classList.add("leaf");
    api.expand = () => {};
    api.collapse = () => {};
    api.rebuild = () => {};
    row.appendChild(makeValueEl(accessor));
  }

  return api;
}

function addRowActions(childApi, container, key, parentApi) {
  const actions = childApi.row.querySelector(".row-actions") || (() => {
    const a = document.createElement("span");
    a.className = "row-actions";
    childApi.row.appendChild(a);
    return a;
  })();
  const del = document.createElement("button");
  del.title = "Sil";
  del.textContent = "🗑";
  del.addEventListener("click", () => {
    if (!confirm("Bu öğe silinsin mi?")) return;
    if (Array.isArray(container)) container.splice(key, 1);
    else delete container[key];
    parentApi.rebuild();
  });
  actions.appendChild(del);
}

function makeValueEl(accessor) {
  const span = document.createElement("span");
  const paint = () => {
    const v = accessor.get();
    const type = v === null ? "null" : typeof v;
    span.className = "val " + type;
    span.textContent = v === null ? "null" : type === "string" ? JSON.stringify(v) : String(v);
    span.title = "Düzenlemek için tıkla";
  };
  paint();

  span.addEventListener("click", () => {
    const oldValue = accessor.get();
    const input = document.createElement("input");
    input.className = "val-input";
    input.value = oldValue === null ? "null" : typeof oldValue === "string" ? oldValue : String(oldValue);
    span.replaceWith(input);
    input.focus();
    input.select();

    let done = false;
    const commit = () => {
      if (done) return;
      const result = parseEdited(input.value, oldValue);
      if (!result.ok) {
        input.classList.add("invalid");
        input.title = result.error;
        return;
      }
      done = true;
      accessor.set(result.value);
      paint();
      input.replaceWith(span);
    };
    const cancel = () => {
      if (done) return;
      done = true;
      input.replaceWith(span);
    };
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") commit();
      else if (e.key === "Escape") cancel();
      else input.classList.remove("invalid");
    });
    input.addEventListener("blur", () => { if (!done) commit(); if (!done) cancel(); });
  });

  return span;
}

function parseEdited(text, oldValue) {
  const t = text.trim();
  if (typeof oldValue === "number") {
    if (t === "") return { ok: false, error: "Sayı girin" };
    const n = Number(t);
    if (!Number.isFinite(n)) return { ok: false, error: "Geçersiz sayı" };
    return { ok: true, value: n };
  }
  if (typeof oldValue === "boolean") {
    if (t === "true" || t === "1") return { ok: true, value: true };
    if (t === "false" || t === "0") return { ok: true, value: false };
    return { ok: false, error: "true veya false girin" };
  }
  if (oldValue === null) {
    if (t === "null" || t === "") return { ok: true, value: null };
    try { return { ok: true, value: JSON.parse(t) }; }
    catch { return { ok: true, value: text }; }
  }
  // string: keep verbatim
  return { ok: true, value: text };
}

// ---------- expand / collapse all ----------
$("#btn-expand").addEventListener("click", () => expandAll(rootApi, 0));
$("#btn-collapse").addEventListener("click", () => {
  renderTree(); // cheapest full collapse
});

function expandAll(api, depth) {
  if (!api || depth > 6) return; // avoid exploding huge trees
  api.expand();
  let count = 0;
  for (const child of api.childApis.values()) {
    expandAll(child, depth + 1);
    if (++count > 500) break;
  }
}

// ---------- search ----------
const searchInput = $("#search");
let searchTimer = null;
searchInput.addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(runSearch, 300);
});

function runSearch() {
  const q = searchInput.value.trim().toLowerCase();
  const panel = $("#search-results");
  panel.innerHTML = "";
  if (!q) { panel.hidden = true; return; }

  const results = [];
  const LIMIT = 200;
  (function walk(value, path) {
    if (results.length >= LIMIT) return;
    if (isContainer(value)) {
      const keys = Array.isArray(value) ? value.map((_, i) => i) : Object.keys(value);
      for (const k of keys) {
        if (results.length >= LIMIT) return;
        const child = value[k];
        if (typeof k === "string" && k.toLowerCase().includes(q)) {
          results.push({ path: path.concat([k]), value: child });
        } else if (!isContainer(child)) {
          const text = child === null ? "null" : String(child);
          if (text.toLowerCase().includes(q)) results.push({ path: path.concat([k]), value: child });
        }
        walk(child, path.concat([k]));
      }
    }
  })(state.data, []);

  panel.hidden = false;
  if (!results.length) {
    const div = document.createElement("div");
    div.className = "sr-more";
    div.textContent = "Sonuç yok.";
    panel.appendChild(div);
    return;
  }
  results.forEach((r) => {
    const item = document.createElement("div");
    item.className = "sr-item";
    const pathEl = document.createElement("span");
    pathEl.className = "sr-path";
    pathEl.textContent = r.path.map((p) => (typeof p === "number" ? "[" + p + "]" : p)).join(" › ");
    const valEl = document.createElement("span");
    valEl.className = "sr-val";
    valEl.textContent = isContainer(r.value)
      ? (Array.isArray(r.value) ? "[...]" : "{...}")
      : JSON.stringify(r.value);
    if (valEl.textContent.length > 60) valEl.textContent = valEl.textContent.slice(0, 60) + "…";
    item.append(pathEl, valEl);
    item.addEventListener("click", () => revealPath(r.path));
    panel.appendChild(item);
  });
  if (results.length >= LIMIT) {
    const div = document.createElement("div");
    div.className = "sr-more";
    div.textContent = "İlk " + LIMIT + " sonuç gösteriliyor; aramayı daraltın.";
    panel.appendChild(div);
  }
}

function revealPath(path) {
  let api = rootApi;
  api.expand();
  for (const key of path) {
    const child = api.childApis.get(String(key));
    if (!child) break;
    api = child;
    api.expand();
  }
  document.querySelectorAll(".row.highlight").forEach((el) => el.classList.remove("highlight"));
  api.row.classList.add("highlight");
  api.row.scrollIntoView({ behavior: "smooth", block: "center" });
}

// ---------- misc ----------
function formatSize(bytes) {
  if (bytes < 1024) return bytes + " B";
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
  return (bytes / 1024 / 1024).toFixed(1) + " MB";
}
