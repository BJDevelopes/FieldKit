// Fieldkit demo desktop. Settings, the panel and the Dynamic Island are drawn from data/ui.json, which
// `Fieldkit.exe --export-site site/data` writes from the real app; this file draws them and makes them work.
"use strict";

const REPO = "BJDevelopes/FieldKit";
const $ = (sel, root = document) => root.querySelector(sel);
const el = (tag, attrs = {}, ...children) => {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v;
    else if (k === "style") Object.assign(node.style, v);
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else if (v !== false && v != null) node.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) if (c != null && c !== false) node.append(c.nodeType ? c : document.createTextNode(c));
  return node;
};

const CATEGORIES = { Essentials: "Essentials", WindowControls: "Window controls", Files: "Files", Sound: "Sound", Utilities: "Utilities", DynamicIsland: "Dynamic Island", Advanced: "Advanced" };
const ISLAND_SECTIONS = ["controls", "music", "files", "calendar", "timer", "downloads", "agents", "watch"];
const ISLAND_STATE = { controls: "ExpandedControls", music: "ExpandedMusic", files: "ExpandedFiles", calendar: "ExpandedCalendar", timer: "ExpandedTimer", downloads: "ExpandedDownloads", agents: "ExpandedAgents", watch: "ExpandedWatch" };

// ------------------------------------------------------------------ state (this browser only)
const store = {
  get(key, fallback) { try { const v = localStorage.getItem("fk-site-" + key); return v == null ? fallback : JSON.parse(v); } catch { return fallback; } },
  set(key, value) { try { localStorage.setItem("fk-site-" + key, JSON.stringify(value)); } catch { /* private mode */ } },
};
let DATA = null;
const state = { installed: new Set(), values: {}, page: "features", panel: 0, island: "ExpandedMusic" };
const installed = (id) => state.installed.has(id);
const save = () => { store.set("installed", [...state.installed]); store.set("values", state.values); };
const valueOf = (id, fallback) => (id && id in state.values ? state.values[id] : fallback);
function setValue(id, value) {
  if (!id) return;
  state.values[id] = value;
  save();
  if (id === "notch.enabled" || id === "notch.placement" || id === "general.taskbarButton") renderTray();
}

// ------------------------------------------------------------------ drawing exported nodes
/** Draws one exported surface (a page, a panel section, an island state) as positioned, working elements. */
function draw(surface, { onButton } = {}) {
  const root = el("div", { class: "ui", style: { width: surface.w + "px", height: surface.h + "px" } });
  const buttons = [];
  for (const n of surface.nodes) {
    const box = { left: n.x + "px", top: n.y + "px", width: n.w + "px", height: n.h + "px" };
    if (n.o) box.opacity = n.o;
    let node;
    switch (n.k) {
      case "t": {
        // WPF measured these boxes to the exact text width; a fraction of a pixel more in the browser would wrap
        // a title or trim the last letters, so one-line text stays on one line and trimmed text gets a little room.
        const oneLine = n.h < n.fs * 1.9;
        const cls = ["n-t", n.f, n.wr && !oneLine ? "wr" : "", n.el ? "el" : ""].filter(Boolean).join(" ");
        const width = n.el || n.wr ? n.w + 3 + "px" : box.width;
        const left = n.al === "right" ? n.x - 3 + "px" : n.al === "center" ? n.x - 1.5 + "px" : box.left;
        if (n.f === "icon" && n.m) {
          // Windows' icon font is missing on phones and Macs: draw the exported glyph tinted with the text colour.
          node = el("div", { class: "n-ico", style: box }, glyph(n.m, n.fs, n.c));
          break;
        }
        node = el("div", { class: cls, style: { ...box, left, width, fontSize: n.fs + "px", fontWeight: n.fw || 400, color: n.c, textAlign: n.al || "left" } }, n.s);
        if (n.f === "icon") node.style.lineHeight = n.h + "px";
        break;
      }
      case "r":
        node = el("div", { style: { ...box, background: n.bg || "none", border: n.bd ? `${n.bw}px solid ${n.bd}` : "none", borderRadius: (n.rad || 0) + "px", boxSizing: "border-box" } });
        break;
      case "img":
        node = el("img", { class: "n-img", src: "data/" + n.src, alt: "", style: box });
        break;
      case "sw": {
        node = el("div", { class: "n-sw" + (valueOf(n.id, n.on) ? " on" : ""), role: "switch", tabindex: 0, "aria-checked": String(!!valueOf(n.id, n.on)), style: box });
        const flip = () => { const on = !node.classList.contains("on"); node.classList.toggle("on", on); node.setAttribute("aria-checked", String(on)); setValue(n.id, on); };
        node.addEventListener("click", flip);
        node.addEventListener("keydown", (e) => { if (e.key === " " || e.key === "Enter") { e.preventDefault(); flip(); } });
        break;
      }
      case "cb":
        node = el("div", { class: "n-cb" + (valueOf(n.id, n.on) ? " on" : ""), role: "checkbox", style: box, onclick: () => { const on = !node.classList.contains("on"); node.classList.toggle("on", on); setValue(n.id, on); } }, n.s ? el("span", { style: { marginLeft: "26px", fontSize: "13px", lineHeight: n.h + "px" } }, n.s) : null);
        break;
      case "sel": {
        node = el("select", { class: "n-sel", "aria-label": n.items[n.i] || "Choice", style: box, onchange: (e) => setValue(n.id, e.target.selectedIndex) },
          n.items.map((t, i) => el("option", { value: i }, t)));
        node.selectedIndex = valueOf(n.id, n.i);
        break;
      }
      case "sl":
        node = el("input", { class: "n-sl", type: "range", min: n.min, max: n.max, step: n.step || "any", value: valueOf(n.id, n.v), style: box, oninput: (e) => setValue(n.id, +e.target.value) });
        break;
      case "in":
        node = el(n.ml ? "textarea" : "input", { class: "n-in", style: box, oninput: (e) => setValue(n.id, e.target.value) });
        node.value = valueOf(n.id, n.s);
        break;
      case "btn": {
        node = el("div", { class: `n-btn ${n.style}${n.on ? " on" : ""}`, role: "button", tabindex: 0, style: box });
        if (n.style === "nav" && n.bg) node.style.background = n.bg;
        const entry = { n, node, texts: [] };
        if (n.s) {
          const label = el("span", { class: "n-btn-label" }, n.s);
          node.append(label);
          entry.texts.push(label);
        }
        node.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); node.click(); } });
        buttons.push(entry);
        node.addEventListener("click", (e) => { e.stopPropagation(); onButton?.(entry); });
        break;
      }
      default:
        continue;
    }
    if (n.id?.startsWith("installed:")) node.dataset.installed = n.id.slice(10); // the "Installed" badge of a feature row
    root.append(node);
    if (n.k === "t") for (const b of buttons) if (inside(n, b.n)) b.texts.push(node);
  }
  return { root, buttons };
}
/** An icon glyph from its exported mask, sized as the font would lay it out and tinted (currentColor when no colour). */
function glyph(mask, size, color) {
  const url = `url("data/${mask.src}")`;
  return el("span", { class: "glyph", style: { width: mask.w * size + "px", height: mask.h * size + "px", background: color || "currentColor",
    maskImage: url, webkitMaskImage: url } });
}
const inside = (a, b) => a.x >= b.x - 1 && a.y >= b.y - 1 && a.x + a.w <= b.x + b.w + 1 && a.y + a.h <= b.y + b.h + 1;
const labelOf = (b) => b.texts.map((t) => t.textContent).join(" ").trim();

// ------------------------------------------------------------------ Settings window
function visiblePages() {
  const q = $("#search").value.trim().toLowerCase();
  return DATA.pages.filter((p) => !p.gate.length || p.gate.some(installed)).filter((p) => {
    if (!q) return true;
    if (p.title.toLowerCase().includes(q)) return true;
    return p.gate.some((id) => { const f = DATA.featureById[id]; return f && (f.title + " " + f.description).toLowerCase().includes(q); });
  });
}

function renderNav() {
  const nav = $("#nav");
  nav.replaceChildren();
  const pages = visiblePages();
  for (const cat of Object.keys(CATEGORIES)) {
    const inCat = pages.filter((p) => p.category === cat);
    if (!inCat.length) continue;
    nav.append(el("div", { class: "nav-head" }, CATEGORIES[cat]));
    for (const p of inCat) {
      nav.append(el("button", { class: "nav-item" + (p.id === state.page ? " active" : ""), onclick: () => { state.page = p.id; showNav(false); renderSettings(); $("#page-scroll").scrollTop = 0; } },
        el("span", { class: "i", "aria-hidden": "true" }, p.gm ? glyph(p.gm, 16) : p.glyph), p.title));
    }
  }
}

function renderPage() {
  const page = DATA.pages.find((p) => p.id === state.page && (!p.gate.length || p.gate.some(installed))) ?? DATA.pages.find((p) => p.id === "features");
  state.page = page.id;
  // On a phone, the page as the app lays it out in a narrow window.
  const room = $("#page").clientWidth;
  const surface = page.phone && room < page.w * 0.8 ? page.phone : page;
  const { root, buttons } = draw(surface, { onButton: onPageButton });
  $("#page").replaceChildren(fitWidth(root, surface, room));
  if (page.id === "features") syncFeatureButtons(root, buttons);
}

/** Shrinks a drawn surface to the room there is (a phone), keeping the space it takes in step so it scrolls right. */
function fitWidth(root, surface, room) {
  const s = room > 0 ? Math.min(1, room / surface.w) : 1;
  if (s === 1) return root;
  root.style.transform = `scale(${s})`;
  root.style.transformOrigin = "0 0";
  return el("div", { style: { width: surface.w * s + "px", height: surface.h * s + "px" } }, root);
}

function onPageButton({ n, node, texts }) {
  const label = labelOf({ texts });
  if (n.id?.startsWith("feature:")) {
    const id = n.id.slice(8);
    if (installed(id)) state.installed.delete(id); else state.installed.add(id);
    save();
    afterInstallChange();
    return;
  }
  if (label === "Install all") { DATA.features.forEach((f) => state.installed.add(f.id)); save(); afterInstallChange(); return; }
  if (label === "Uninstall all") { state.installed.clear(); save(); afterInstallChange(); return; }
  if (label) toast("Fieldkit", `“${label}” works in the app. Download Fieldkit to try it on your PC.`);
}

/** The Features page was exported with everything installed: relabel each button for the demo's state. */
function syncFeatureButtons(root, buttons) {
  for (const b of buttons) {
    if (!b.n.id?.startsWith("feature:")) continue;
    const on = installed(b.n.id.slice(8));
    b.node.className = `n-btn ${on ? "plain" : "accent"}`;
    if (b.texts[0]) { b.texts[0].textContent = on ? "Uninstall" : "Install"; b.texts[0].style.color = on ? "" : "#fff"; }
  }
  for (const badge of root.querySelectorAll("[data-installed]")) badge.hidden = !installed(badge.dataset.installed);
  const total = DATA.features.length;
  for (const t of root.querySelectorAll(".n-t")) {
    if (/^\d+ of \d+ features installed$/.test(t.textContent)) t.textContent = `${state.installed.size} of ${total} features installed`;
  }
  // Like the app, "Install all" rests when everything is in and "Uninstall all" when nothing is.
  for (const b of buttons) {
    const label = labelOf(b);
    const idle = label === "Install all" ? state.installed.size >= total : label === "Uninstall all" ? state.installed.size === 0 : null;
    if (idle === null) continue;
    for (const part of [b.node, ...b.texts]) part.style.opacity = idle ? 0.45 : 1;
    b.node.style.pointerEvents = idle ? "none" : "";
  }
}

function renderSettings() { renderNav(); renderPage(); }

function afterInstallChange() {
  renderTray();
  if (!$("#settings").hidden) {
    const top = $("#page-scroll").scrollTop;
    renderSettings();
    $("#page-scroll").scrollTop = top;
  }
  if (!$("#panel").hidden) renderPanel();
}

function openSettings(page) {
  if (page) state.page = page;
  closeFlyouts();
  const w = $("#settings");
  if (w.hidden) {
    w.hidden = false;
    fitSettings();
  }
  showNav(false);
  renderSettings();
  $("#toast").hidden = true;
  $(".tb-app.running").classList.add("active");
}

/** Phones and small windows: Settings fills the screen and the page list becomes a drawer. */
const compact = () => innerWidth < 760 || innerHeight < 520;

function showNav(open) {
  $("#settings").classList.toggle("nav-open", open);
  $("[data-menu]").setAttribute("aria-expanded", String(open));
}

/** The window keeps the app's own size (1080 × 760) and scales down on smaller screens, never past the edges. */
function fitSettings() {
  const w = $("#settings");
  const small = compact();
  w.classList.toggle("compact", small);
  if (small) {
    if (!w.classList.contains("max")) { w.classList.add("max"); w.dataset.autoMax = "1"; }
    return;
  }
  showNav(false);
  if (w.dataset.autoMax) { w.classList.remove("max"); delete w.dataset.autoMax; }
  if (w.classList.contains("max")) return;
  const W = 1080, H = 760;
  const s = Math.min(1, (innerWidth - 40) / W, (innerHeight - 48 - 30) / H);
  w.style.width = W + "px";
  w.style.height = H + "px";
  w.style.setProperty("--s", s);
  w.style.transform = s < 1 ? `scale(${s})` : "";
  w.style.left = Math.max(10, (innerWidth - W * s) / 2 + (innerWidth > 1300 ? 50 : 0)) + "px";
  w.style.top = Math.max(10, (innerHeight - 48 - H * s) / 2) + "px";
}

function makeDraggable(win) {
  const bar = $("[data-drag]", win);
  let start = null;
  bar.addEventListener("pointerdown", (e) => {
    if (e.target.closest("button") || win.classList.contains("max")) return;
    start = { x: e.clientX, y: e.clientY, left: win.offsetLeft, top: win.offsetTop };
    bar.setPointerCapture(e.pointerId);
  });
  bar.addEventListener("pointermove", (e) => {
    if (!start) return;
    win.style.left = Math.min(innerWidth - 140, Math.max(-600, start.left + e.clientX - start.x)) + "px";
    win.style.top = Math.min(innerHeight - 90, Math.max(0, start.top + e.clientY - start.y)) + "px";
  });
  bar.addEventListener("pointerup", () => { start = null; });
  bar.addEventListener("dblclick", () => { win.classList.toggle("max"); if (!win.classList.contains("max")) fitSettings(); });
  $("[data-close]", win).addEventListener("click", () => { win.hidden = true; $(".tb-app.running").classList.remove("active"); });
  $("[data-min]", win).addEventListener("click", () => { win.hidden = true; $(".tb-app.running").classList.remove("active"); });
  $("[data-max]", win).addEventListener("click", () => { win.classList.toggle("max"); if (!win.classList.contains("max")) fitSettings(); });
  $("[data-menu]", win).addEventListener("click", () => showNav(!win.classList.contains("nav-open")));
  $("[data-scrim]", win).addEventListener("click", () => showNav(false));
}

// ------------------------------------------------------------------ panel
function renderPanel() {
  const section = DATA.panel[state.panel] ?? DATA.panel[0];
  const panel = $("#panel");
  const { root, buttons } = draw(section, {
    onButton: (b) => {
      const label = labelOf(b);
      if (b.n.style === "nav") {
        const tabs = buttons.filter((x) => x.n.style === "nav").sort((a, c) => a.n.x - c.n.x);
        state.panel = tabs.indexOf(b);
        renderPanel();
      } else if (label === "Settings") openSettings();
      else if (label === "Quit") { closeFlyouts(); toast("Fieldkit", "Quit is off in the demo. On your PC it closes Fieldkit."); }
      else if (label) toast("Fieldkit", `“${label}” works in the app. Download Fieldkit to try it.`);
    },
  });
  panel.replaceChildren(root);
  panel.style.width = section.w + "px";
  panel.style.height = section.h + "px";
  const scale = Math.min(1, (innerHeight - 48 - 20) / section.h, (innerWidth - 24) / section.w);
  panel.style.transform = scale < 1 ? `scale(${scale})` : "";
}

function togglePanel() {
  const p = $("#panel");
  const show = p.hidden;
  closeFlyouts();
  if (show) { renderPanel(); p.hidden = false; }
}

// ------------------------------------------------------------------ island
const islandOn = () => installed("notch") && valueOf("notch.enabled", true) !== false;

function renderTray() {
  const slot = $("#island");
  const button = $("#fk-button");
  if (!islandOn()) {
    slot.hidden = true;
    button.hidden = valueOf("general.taskbarButton", true) === false;
    closeIsland();
    return;
  }
  slot.hidden = false;
  button.hidden = true;
  const id = installed("notchAgents") ? "CollapsedAgents" : "CollapsedMusic";
  const pill = DATA.islandById[id];
  slot.replaceChildren(draw(pill).root);
  slot.style.width = pill.w + "px";
  slot.style.borderRadius = pill.r + "px";
}

function openIsland(stateId = state.island) {
  closeFlyouts();
  state.island = stateId;
  const wide = DATA.islandById[stateId] ?? DATA.islandById.ExpandedMusic;
  // On a phone, the island at its narrowest size (360), as the app lays it out.
  const s = wide.phone && innerWidth - 20 < wide.w ? wide.phone : wide;
  const box = $("#island-open");
  const { root } = draw(s, {
    onButton: (b) => { const label = labelOf(b); if (label) toast("Fieldkit", `“${label}” works in the app.`); },
  });
  // The header's section icons, the gear and the collapse arrow become buttons.
  const icons = s.nodes.filter((n) => n.k === "t" && n.f === "icon" && n.y < 40).sort((a, b) => a.x - b.x);
  icons.forEach((n, i) => {
    const hot = el("div", { class: "hot", title: ISLAND_SECTIONS[i] ?? (i === icons.length - 1 ? "Close" : "Settings"),
      style: { left: n.x - 8 + "px", top: n.y - 8 + "px", width: n.w + 16 + "px", height: n.h + 16 + "px" } });
    hot.addEventListener("click", (e) => {
      e.stopPropagation();
      if (i < ISLAND_SECTIONS.length && ISLAND_STATE[ISLAND_SECTIONS[i]]) openIsland(ISLAND_STATE[ISLAND_SECTIONS[i]]);
      else if (i === icons.length - 1) closeIsland();
      else openSettings("island");
    });
    root.append(hot);
  });
  // "Lyrics" on Now Playing opens the lyrics view, like the app.
  for (const n of s.nodes) {
    if (n.k === "t" && n.s === "Lyrics") {
      const hot = el("div", { class: "hot", title: "Lyrics", style: { left: n.x - 30 + "px", top: n.y - 8 + "px", width: n.w + 44 + "px", height: n.h + 16 + "px" } });
      hot.addEventListener("click", (e) => { e.stopPropagation(); openIsland(stateId === "ExpandedLyrics" ? "ExpandedMusic" : "ExpandedLyrics"); });
      root.append(hot);
    }
  }
  box.replaceChildren(root);
  box.style.width = s.w + "px";
  box.style.height = s.h + "px";
  box.style.borderRadius = Math.min(28, s.h * 0.34) + "px";
  // On a phone the island shrinks to the screen's width, opening up from the taskbar as on the PC.
  const scale = Math.min(1, (innerWidth - 20) / s.w, (innerHeight - 48 - 20) / s.h);
  const width = s.w * scale;
  box.style.transform = scale < 1 ? `scale(${scale})` : "";
  box.style.transformOrigin = "bottom left";
  const slot = $("#island").getBoundingClientRect();
  box.style.left = Math.max(10, Math.min(innerWidth - width - 10, slot.right - width + 10)) + "px";
  box.hidden = false;
}
function closeIsland() { $("#island-open").hidden = true; }

// ------------------------------------------------------------------ shared
function closeFlyouts() {
  $("#panel").hidden = true;
  closeIsland();
  $("#toast").hidden = true;
}

let toastTimer;
function toast(title, body) {
  $("#toast-title").textContent = title;
  $("#toast-body").textContent = body;
  $("#toast-action").hidden = true;
  $("#toast").hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $("#toast").hidden = true; }, 4200);
}

function tickClock() {
  const now = new Date();
  $("#clock").replaceChildren(now.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }), el("br"), now.toLocaleDateString());
}

async function findLatestRelease() {
  try {
    const r = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=1`, { headers: { Accept: "application/vnd.github+json" } });
    if (!r.ok) return;
    const [release] = await r.json();
    // The installer when the release has one; the portable zip otherwise.
    const file = release?.assets.find((a) => /setup-x64\.exe$/i.test(a.name)) ?? release?.assets.find((a) => /win-x64\.zip$/i.test(a.name));
    if (!file) return;
    for (const a of [$("#download-icon"), $("#download-tb")]) a.href = file.browser_download_url;
    const version = release.tag_name.replace(/^v/, "");
    $("#legend-version").textContent = version;
    if (version !== topo.version) { topo.version = version; drawTopo(); }
  } catch { /* offline or rate limited: the releases page link stays */ }
}

// ------------------------------------------------------------------ wallpaper: a contour map with the latest release on the summit
const topo = { version: "0.4.0", summit: null };

/** Seeded value noise, so the terrain is the same on every visit. */
function valueNoise(seed) {
  let s = seed >>> 0;
  const rnd = () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
  const perm = Array.from({ length: 256 }, (_, i) => i);
  for (let i = 255; i > 0; i--) { const j = Math.floor(rnd() * (i + 1)); [perm[i], perm[j]] = [perm[j], perm[i]]; }
  const vals = Float32Array.from({ length: 256 }, rnd);
  const at = (x, y) => vals[perm[(perm[x & 255] + y) & 255]];
  const fade = (t) => t * t * (3 - 2 * t);
  return (x, y) => {
    const xi = Math.floor(x), yi = Math.floor(y), u = fade(x - xi), v = fade(y - yi);
    const a = at(xi, yi), b = at(xi + 1, yi), c = at(xi, yi + 1), d = at(xi + 1, yi + 1);
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  };
}
const noise = valueNoise(20261015);

function drawTopo() {
  const canvas = $("#topo");
  const dpr = Math.min(2, devicePixelRatio || 1);
  const W = innerWidth + 32, H = innerHeight + 32; // a little extra for the parallax
  canvas.width = Math.round(W * dpr);
  canvas.height = Math.round(H * dpr);
  canvas.style.width = W + "px";
  canvas.style.height = H + "px";
  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

  // Height field: rolling noise plus a summit and a lower ridge.
  const cs = 6, cols = Math.ceil(W / cs) + 1, rows = Math.ceil(H / cs) + 1;
  const unit = 520;
  const peaks = [
    { x: W * 0.64, y: H * 0.36, a: 0.55, r: Math.min(W, H) * 0.30 },
    { x: W * 0.24, y: H * 0.78, a: 0.30, r: Math.min(W, H) * 0.26 },
    { x: W * 0.90, y: H * 0.86, a: 0.18, r: Math.min(W, H) * 0.18 },
  ];
  const h = new Float32Array(cols * rows);
  let min = Infinity, max = -Infinity, top = 0;
  for (let j = 0; j < rows; j++) {
    for (let i = 0; i < cols; i++) {
      const x = i * cs, y = j * cs, nx = x / unit, ny = y / unit;
      let v = 0.5 * noise(nx, ny) + 0.25 * noise(nx * 2.1 + 5, ny * 2.1 + 9) + 0.125 * noise(nx * 4.3 + 11, ny * 4.3 + 3) + 0.06 * noise(nx * 8.7, ny * 8.7);
      for (const p of peaks) v += p.a * Math.exp(-((x - p.x) ** 2 + (y - p.y) ** 2) / (2 * p.r * p.r));
      const k = j * cols + i;
      h[k] = v;
      if (v < min) min = v;
      if (v > max) { max = v; top = k; }
    }
  }
  topo.summit = { x: (top % cols) * cs, y: Math.floor(top / cols) * cs };

  // Ground: deep green with a faint lift around the summit.
  ctx.fillStyle = "#0b1311";
  ctx.fillRect(0, 0, W, H);
  const glow = ctx.createRadialGradient(topo.summit.x, topo.summit.y, 0, topo.summit.x, topo.summit.y, Math.max(W, H) * 0.55);
  glow.addColorStop(0, "rgba(38, 104, 86, .42)");
  glow.addColorStop(0.5, "rgba(22, 60, 52, .18)");
  glow.addColorStop(1, "rgba(0, 0, 0, 0)");
  ctx.fillStyle = glow;
  ctx.fillRect(0, 0, W, H);

  // Survey grid with references along the top and right edges.
  ctx.strokeStyle = "rgba(190, 235, 215, .045)";
  ctx.lineWidth = 1;
  ctx.font = '10px "Cascadia Mono", Consolas, monospace';
  ctx.fillStyle = "rgba(190, 235, 215, .26)";
  const g = 180;
  ctx.beginPath();
  for (let x = g, n = 0; x < W; x += g, n++) { ctx.moveTo(x + 0.5, 0); ctx.lineTo(x + 0.5, H); ctx.fillText(String.fromCharCode(66 + n), x + 6, 30); }
  for (let y = g, n = 2; y < H; y += g, n++) { ctx.moveTo(0, y + 0.5); ctx.lineTo(W, y + 0.5); ctx.fillText(String(n).padStart(2, "0"), W - 52, y - 6); }
  ctx.stroke();

  // Contours by marching squares; every fifth is an index contour, the highest few run warm.
  const steps = 28, step = (max - min) / steps;
  for (let l = 1; l < steps; l++) {
    const L = min + l * step, t = l / steps;
    const index = l % 5 === 0, warm = l >= steps - 3;
    ctx.beginPath();
    for (let j = 0; j < rows - 1; j++) {
      for (let i = 0; i < cols - 1; i++) {
        const a = h[j * cols + i], b = h[j * cols + i + 1], c = h[(j + 1) * cols + i + 1], d = h[(j + 1) * cols + i];
        const id = (a > L ? 8 : 0) | (b > L ? 4 : 0) | (c > L ? 2 : 0) | (d > L ? 1 : 0);
        if (id === 0 || id === 15) continue;
        const x0 = i * cs, y0 = j * cs;
        const T = () => [x0 + cs * (L - a) / (b - a), y0];
        const R = () => [x0 + cs, y0 + cs * (L - b) / (c - b)];
        const B = () => [x0 + cs * (L - d) / (c - d), y0 + cs];
        const Lf = () => [x0, y0 + cs * (L - a) / (d - a)];
        const seg = (p, q) => { const [px, py] = p(), [qx, qy] = q(); ctx.moveTo(px, py); ctx.lineTo(qx, qy); };
        switch (id) {
          case 1: case 14: seg(Lf, B); break;
          case 2: case 13: seg(B, R); break;
          case 3: case 12: seg(Lf, R); break;
          case 4: case 11: seg(T, R); break;
          case 5: seg(T, R); seg(Lf, B); break;
          case 6: case 9: seg(T, B); break;
          case 7: case 8: seg(T, Lf); break;
          case 10: seg(T, Lf); seg(B, R); break;
        }
      }
    }
    ctx.lineWidth = index ? 1.3 : 0.8;
    ctx.strokeStyle = warm ? `rgba(255, 168, 92, ${index ? 0.55 : 0.42})` : `rgba(150, 214, 186, ${(index ? 0.2 : 0.08) + t * 0.14})`;
    ctx.stroke();
  }

  // The summit: the latest release.
  const { x: sx, y: sy } = topo.summit;
  ctx.fillStyle = "#ffa85c";
  ctx.beginPath();
  ctx.moveTo(sx, sy - 6); ctx.lineTo(sx + 5.5, sy + 4); ctx.lineTo(sx - 5.5, sy + 4); ctx.closePath();
  ctx.fill();
  ctx.font = '600 11px "Cascadia Mono", Consolas, monospace';
  ctx.fillStyle = "rgba(255, 214, 178, .85)";
  const label = `FIELDKIT ${topo.version}`, lw = ctx.measureText(label).width;
  ctx.fillText(label, sx + 12 + lw > innerWidth - 8 ? sx - 12 - lw : sx + 12, sy + 4);
}

function startTopo() {
  drawTopo();
  let timer;
  addEventListener("resize", () => { clearTimeout(timer); timer = setTimeout(drawTopo, 150); });
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  // A slight parallax, as if the map sat just under the glass.
  let frame = 0, px = 0, py = 0;
  addEventListener("pointermove", (e) => {
    px = (e.clientX / innerWidth - 0.5) * -12;
    py = (e.clientY / innerHeight - 0.5) * -12;
    if (!frame) frame = requestAnimationFrame(() => { frame = 0; $("#topo").style.transform = `translate(${px - 16}px, ${py - 16}px)`; });
  });
}

// ------------------------------------------------------------------ start
async function start() {
  startTopo();
  DATA = await fetch("data/ui.json", { cache: "no-cache" }).then((r) => r.json()); // revalidated, so an update never pairs old data with new code
  DATA.featureById = Object.fromEntries(DATA.features.map((f) => [f.id, f]));
  DATA.islandById = Object.fromEntries(DATA.island.map((s) => [s.id, s]));
  // Like the app: everything starts installed except experiments and Fieldkit's extras.
  state.installed = new Set(store.get("installed", DATA.features.filter((f) => !f.beta && !f.extra).map((f) => f.id)));
  state.values = store.get("values", {});

  makeDraggable($("#settings"));
  for (const b of document.querySelectorAll("[data-open=settings]")) b.addEventListener("click", (e) => { e.stopPropagation(); openSettings(); });
  $("#search").addEventListener("input", renderSettings);
  $("#island").addEventListener("click", (e) => { e.stopPropagation(); $("#island-open").hidden ? openIsland() : closeIsland(); });
  // Right-click opens the panel; on a touch screen, press and hold does.
  let hold = 0, held = 0;
  $("#island").addEventListener("pointerdown", (e) => {
    if (e.pointerType === "mouse") return;
    clearTimeout(hold);
    hold = setTimeout(() => { held = Date.now(); togglePanel(); navigator.vibrate?.(10); }, 450);
  });
  for (const type of ["pointerup", "pointercancel", "pointerleave"]) $("#island").addEventListener(type, () => clearTimeout(hold));
  $("#island").addEventListener("click", (e) => { if (Date.now() - held < 700) e.stopImmediatePropagation(); }, true);
  $("#island").addEventListener("contextmenu", (e) => { e.preventDefault(); e.stopPropagation(); if (Date.now() - held > 700) togglePanel(); });
  $("#island").addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); openIsland(); } });
  $("#fk-button").addEventListener("click", (e) => { e.stopPropagation(); togglePanel(); });
  $("#toast-x").addEventListener("click", () => { $("#toast").hidden = true; });
  for (const f of ["#panel", "#island-open", "#toast", "#settings"]) $(f).addEventListener("click", (e) => e.stopPropagation());
  document.addEventListener("click", closeFlyouts);
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeFlyouts(); });
  let refit;
  addEventListener("resize", () => {
    clearTimeout(refit);
    refit = setTimeout(() => {
      if (!$("#settings").hidden) { fitSettings(); renderPage(); }
      if (!$("#panel").hidden) renderPanel();
      if (!$("#island-open").hidden) openIsland();
    }, 120);
  });
  if (matchMedia("(pointer: coarse)").matches) {
    $("#toast-body").textContent = "This is a live demo of the real app. Tap the island in the taskbar, press and hold it for the panel, or open Settings to install and remove features.";
  }

  renderTray();
  tickClock();
  setInterval(tickClock, 15000);
  const linked = openFromHash();
  addEventListener("hashchange", openFromHash);
  if (!linked) setTimeout(() => { if ($("#settings").hidden) { $("#toast-action").hidden = false; $("#toast").hidden = false; } }, 900);
  findLatestRelease();
}

/** Links straight to a surface: #settings/<page>, #panel/<tab number>, #island/<state>. */
function openFromHash() {
  const [what, arg] = decodeURIComponent(location.hash.slice(1)).split("/");
  if (what === "settings") { openSettings(arg); return true; }
  if (what === "panel") { closeFlyouts(); state.panel = Math.max(0, (+arg || 1) - 1); renderPanel(); $("#panel").hidden = false; return true; }
  if (what === "island" && islandOn()) { openIsland(arg ? "Expanded" + arg[0].toUpperCase() + arg.slice(1) : undefined); return true; }
  return false;
}

start().catch((e) => { console.error(e); document.body.classList.add("failed"); });
