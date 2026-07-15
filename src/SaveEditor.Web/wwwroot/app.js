"use strict";

// ---------- state ----------
const state = {
  id: null,
  fileName: null,
  editable: true,
  data: undefined,       // the editable root value
  rawMode: false,
  dirty: false,           // unsaved edits since the last open/download
};

const $ = (sel) => document.querySelector(sel);

function markDirty() { state.dirty = true; }

window.addEventListener("beforeunload", (e) => {
  if (!state.dirty) return;
  e.preventDefault();
  e.returnValue = "";
});

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
    if (!res.ok) { showError(json.error || t("unknownError")); return; }
    openEditor(json);
  } catch (err) {
    showError(t("serverUnreachable") + err.message);
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
  state.dirty = false;

  state.lastInfo = info;

  $("#upload-screen").hidden = true;
  $("#editor-screen").hidden = false;

  renderFileInfo();

  $("#search").value = "";
  $("#search-results").hidden = true;
  renderTree();
  setRawMode(typeof state.data === "string"); // text/xml formats edit as raw text
}

function renderFileInfo() {
  const info = state.lastInfo;
  if (!info) return;
  $("#fi-name").textContent = info.fileName;
  $("#fi-format").textContent = info.formatName;
  $("#fi-wrappers").textContent = info.wrappers && info.wrappers.length ? t("wrappersLabel") + info.wrappers.join(" → ") : "";
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
    div.textContent = "⚠ " + t("readonlyWarning");
    warnings.appendChild(div);
  }
}

$("#btn-reset").addEventListener("click", () => {
  if (state.dirty && !confirm(t("confirmClose"))) return;
  state.id = null;
  state.dirty = false;
  $("#editor-screen").hidden = true;
  $("#upload-screen").hidden = false;
  fileInput.value = "";
});

$("#btn-download").addEventListener("click", async () => {
  if (state.rawMode && !applyRaw()) return;
  const btn = $("#btn-download");
  btn.disabled = true;
  btn.textContent = t("preparing");
  try {
    const res = await fetch("/api/download/" + state.id, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ root: state.data === undefined ? null : state.data }),
    });
    if (!res.ok) {
      const json = await res.json().catch(() => ({}));
      alert(t("errorPrefix") + (json.error || res.statusText));
      return;
    }
    const blob = await res.blob();
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = state.fileName;
    a.click();
    URL.revokeObjectURL(a.href);
    state.dirty = false;
  } finally {
    btn.disabled = false;
    btn.textContent = t("download");
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
  $("#btn-view-toggle").textContent = on ? t("treeView") : t("rawJson");
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
    markDirty();
    $("#raw-error").textContent = "";
    return true;
  }
  try {
    state.data = JSON.parse(text);
    markDirty();
    $("#raw-error").textContent = "";
    return true;
  } catch (err) {
    $("#raw-error").textContent = t("invalidJson") + err.message;
    return false;
  }
}

// ---------- tree ----------
const treeEl = $("#tree");
let rootApi = null;

function renderTree() {
  treeEl.innerHTML = "";
  rootApi = buildNode(t("rootLabel"), { get: () => state.data, set: (v) => (state.data = v) }, [], 0, null);
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
      countEl.textContent = Array.isArray(value) ? t("itemsCount", { n }) : t("fieldsCount", { n });
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
      const BATCH = 2000;
      let rendered = 0;
      let moreBtn = null;

      function renderBatch() {
        const batch = keys.slice(rendered, rendered + BATCH);
        batch.forEach((k) => {
          const childAccessor = {
            get: () => value[k],
            set: (v) => { value[k] = v; },
          };
          const childApi = buildNode(k, childAccessor, path.concat([k]), depth + 1, api);
          api.childApis.set(String(k), childApi);
          addRowActions(childApi, value, k, api);
          if (moreBtn) childrenEl.insertBefore(childApi.el, moreBtn);
          else childrenEl.appendChild(childApi.el);
        });
        rendered += batch.length;

        if (moreBtn) { moreBtn.remove(); moreBtn = null; }
        if (rendered < keys.length) {
          moreBtn = document.createElement("button");
          moreBtn.type = "button";
          moreBtn.className = "sr-more load-more";
          moreBtn.textContent = t("showMore", { n: keys.length - rendered });
          moreBtn.addEventListener("click", renderBatch);
          childrenEl.appendChild(moreBtn);
        }
      }

      renderBatch();
      // Keeps loading batches until `key` has been rendered (or everything
      // has), so revealPath() can jump straight to a search result that
      // falls past the first batch.
      api.loadMoreUntil = (key) => {
        while (!api.childApis.has(String(key)) && rendered < keys.length) renderBatch();
      };
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
    addBtn.title = t("addItem");
    addBtn.textContent = "＋";
    addBtn.addEventListener("click", () => {
      let key;
      if (Array.isArray(value)) {
        key = value.length;
      } else {
        key = prompt(t("newFieldName"));
        if (!key) return;
        if (Object.prototype.hasOwnProperty.call(value, key)) { alert(t("fieldExists")); return; }
      }
      const raw = prompt(t("valuePrompt"), "0");
      if (raw === null) return;
      let parsed;
      try { parsed = JSON.parse(raw); } catch { alert(t("invalidJsonValue")); return; }
      value[key] = parsed;
      markDirty();
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
  del.title = t("deleteItem");
  del.textContent = "🗑";
  del.addEventListener("click", () => {
    if (!confirm(t("confirmDelete"))) return;
    if (Array.isArray(container)) container.splice(key, 1);
    else delete container[key];
    markDirty();
    parentApi.rebuild();
  });
  actions.appendChild(del);
}

function makeValueEl(accessor) {
  const span = document.createElement("span");
  const paint = () => {
    const v = accessor.get();
    const type = valueTypeOf(v);
    span.className = "val " + type;
    span.textContent = v === null ? "null" : type === "string" ? JSON.stringify(v) : String(v);
    span.title = t("clickToEdit");
  };
  paint();

  span.addEventListener("click", () => {
    const oldValue = accessor.get();
    const initialType = valueTypeOf(oldValue);

    const wrapper = document.createElement("span");
    wrapper.className = "val-edit";

    const typeSel = document.createElement("select");
    typeSel.className = "val-type";
    VALUE_TYPES.forEach(({ type, labelKey }) => {
      const opt = document.createElement("option");
      opt.value = type;
      opt.textContent = t(labelKey);
      typeSel.appendChild(opt);
    });
    typeSel.value = initialType;

    const input = document.createElement("input");
    input.className = "val-input";
    input.value = textForType(oldValue, initialType);

    let done = false;
    const syncInputForType = () => {
      const type = typeSel.value;
      input.disabled = type === "null";
      if (type === "boolean" && input.value !== "true" && input.value !== "false") input.value = "true";
      if (type === "null") input.value = "";
      input.classList.remove("invalid");
    };
    typeSel.addEventListener("change", syncInputForType);

    wrapper.append(typeSel, input);
    span.replaceWith(wrapper);
    input.focus();
    input.select();

    const commit = () => {
      if (done) return;
      const result = parseByType(input.value, typeSel.value);
      if (!result.ok) {
        input.classList.add("invalid");
        input.title = t(result.error);
        return;
      }
      done = true;
      accessor.set(result.value);
      markDirty();
      paint();
      wrapper.replaceWith(span);
    };
    const cancel = () => {
      if (done) return;
      done = true;
      wrapper.replaceWith(span);
    };
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") commit();
      else if (e.key === "Escape") cancel();
      else input.classList.remove("invalid");
    });
    input.addEventListener("blur", (e) => {
      if (e.relatedTarget === typeSel) return; // switching type, not leaving the editor
      if (!done) commit();
      if (!done) cancel();
    });
    typeSel.addEventListener("blur", (e) => {
      if (e.relatedTarget === input) return;
      if (!done) commit();
      if (!done) cancel();
    });
  });

  return span;
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
["filter-keys", "filter-values", "filter-exact"].forEach((id) => {
  $("#" + id).addEventListener("change", runSearch);
});

function runSearch() {
  const q = searchInput.value.trim().toLowerCase();
  const panel = $("#search-results");
  panel.innerHTML = "";
  if (!q) { panel.hidden = true; return; }

  const matchKeys = $("#filter-keys").checked;
  const matchValues = $("#filter-values").checked;
  const exact = $("#filter-exact").checked;

  const results = [];
  const LIMIT = 200;
  (function walk(value, path) {
    if (results.length >= LIMIT) return;
    if (isContainer(value)) {
      const keys = Array.isArray(value) ? value.map((_, i) => i) : Object.keys(value);
      for (const k of keys) {
        if (results.length >= LIMIT) return;
        const child = value[k];
        if (matchKeys && typeof k === "string" && matchesQuery(k.toLowerCase(), q, exact)) {
          results.push({ path: path.concat([k]), value: child });
        } else if (matchValues && !isContainer(child)) {
          const text = child === null ? "null" : String(child);
          if (matchesQuery(text.toLowerCase(), q, exact)) results.push({ path: path.concat([k]), value: child });
        }
        walk(child, path.concat([k]));
      }
    }
  })(state.data, []);

  panel.hidden = false;
  if (!results.length) {
    const div = document.createElement("div");
    div.className = "sr-more";
    div.textContent = t("noResults");
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
    div.textContent = t("firstNResults", { n: LIMIT });
    panel.appendChild(div);
  }
}

function revealPath(path) {
  let api = rootApi;
  api.expand();
  for (const key of path) {
    if (api.loadMoreUntil) api.loadMoreUntil(key);
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

// Called by i18n.js after the language changes: refresh every dynamic text
// that isn't covered by data-i18n attributes.
function onLanguageChange() {
  if (state.id === null) return;
  renderFileInfo();
  $("#btn-view-toggle").textContent = state.rawMode ? t("treeView") : t("rawJson");
  if (!$("#btn-download").disabled) $("#btn-download").textContent = t("download");
  if (!state.rawMode) renderTree();
  runSearch();
}
