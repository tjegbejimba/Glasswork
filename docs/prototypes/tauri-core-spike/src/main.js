// PROTOTYPE ONLY -- Wayfinder ticket #372. Frontend for the shared
// cross-platform vertical slice (#370): My Day, Task Detail, reserved
// Planner nav stub. Talks to the bounded Rust Core exclusively via Tauri
// IPC commands (see src-tauri/src/lib.rs) -- no domain logic lives here.

const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;
const { getCurrentWindow } = window.__TAURI__.window;

const state = {
  page: "myday",
  tasks: [],
  expandedTaskId: null,
  expandedSubtaskIndex: null,
  artifactCache: new Map(), // `${taskId}/${filename}` -> payload
  artifactSourceOpen: new Set(),
};

// ---- OS detection: toggles the Variant B chrome rules in styles.css.
// WKWebView reports "MacIntel"/"MacARM..."; WebView2 reports "Win32"/"Win64".
function detectOs() {
  const platform = navigator.platform || navigator.userAgent;
  if (/Win/i.test(platform)) return "windows";
  return "mac"; // default -- this spike is developed and measured on macOS.
}
document.documentElement.dataset.os = detectOs();

// ---- Windows custom caption wiring (no-op on macOS, native traffic lights).
document.getElementById("win-minimize").addEventListener("click", () => getCurrentWindow().minimize());
document.getElementById("win-maximize").addEventListener("click", () => getCurrentWindow().toggleMaximize());
document.getElementById("win-close").addEventListener("click", () => getCurrentWindow().close());

// ---- Bounded Markdown rendering (NOT a CommonMark port -- headings, bold,
// italic, unordered lists, paragraphs, and links gated the same way
// ArtifactLinkPolicy gates them in the C# Core: http/https allowed, wiki
// links `[[slug]]` shown as inert text since Backlinks/Related are out of
// scope for this slice per #370). All artifact content is treated as
// untrusted, matching CONTEXT.md's markdown-rendering rule.
function renderMarkdown(md) {
  const escapeHtml = (s) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const lines = md.split("\n");
  let html = "";
  let inList = false;
  for (const raw of lines) {
    const line = raw.trimEnd();
    if (/^###\s+/.test(line)) { closeList(); html += `<h3>${inline(line.replace(/^###\s+/, ""))}</h3>`; continue; }
    if (/^##\s+/.test(line)) { closeList(); html += `<h2>${inline(line.replace(/^##\s+/, ""))}</h2>`; continue; }
    if (/^#\s+/.test(line)) { closeList(); html += `<h1>${inline(line.replace(/^#\s+/, ""))}</h1>`; continue; }
    if (/^[-*]\s+/.test(line)) {
      if (!inList) { html += "<ul>"; inList = true; }
      html += `<li>${inline(line.replace(/^[-*]\s+/, ""))}</li>`;
      continue;
    }
    closeList();
    if (line.trim().length === 0) continue;
    html += `<p>${inline(line)}</p>`;
  }
  closeList();
  return html;

  function closeList() { if (inList) { html += "</ul>"; inList = false; } }
  function inline(text) {
    let out = escapeHtml(text);
    out = out.replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>");
    out = out.replace(/\[\[([^\]|]+)(\|([^\]]+))?\]\]/g, (_m, slug, _p2, label) => `<span class="muted">${escapeHtml(label || slug)}</span>`);
    out = out.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_m, label, url) => {
      const isSafe = /^https?:\/\//i.test(url);
      return isSafe
        ? `<a href="${url}" class="ext-link" data-external-url="${url}">${label}</a>`
        : `<span class="muted blocked-link" title="Blocked by ArtifactLinkPolicy-equivalent: only http/https links are allowed">${label}</span>`;
    });
    return out;
  }
}

// ---- Rendering ----------------------------------------------------------

function chipsHtml(task) {
  const chips = [];
  if (task.priority === "high") chips.push(['priority-high', 'High']);
  if (task.priority === "medium") chips.push(['priority-medium', 'Med']);
  if (task.priority === "low") chips.push(['priority-low', 'Low']);
  if (task.due) chips.push(['due-today', `Due ${task.due}`]);
  if (task.ado_title) chips.push(['ado', task.ado_title]);
  return chips.map(([cls, label]) => `<span class="chip ${cls}">${label}</span>`).join("");
}

function isRich(task) {
  return task.subtasks.length > 0 || (task.notes && task.notes.trim().length > 0);
}
function showAsCard(task) {
  return isRich(task) && task.status !== "done";
}

function currentStepText(task) {
  const step = task.subtasks.find((s) => s.status === "in_progress");
  return step ? step.text : "";
}
function blockerRow(task) {
  const blocked = task.subtasks.find((s) => s.status === "blocked" && s.metadata.blocker);
  return blocked ? `&#128683; ${blocked.metadata.blocker}` : "";
}

function taskRowHtml(task) {
  const expanded = state.expandedTaskId === task.id;
  if (!showAsCard(task)) {
    return `<div class="task-row quiet">
      <button class="task-row-btn" data-open-task="${task.id}" aria-expanded="${expanded}">
        <div class="title-row">${task.title}${chipsHtml(task)}</div>
      </button>
      ${expanded ? detailPanelHtml(task) : ""}
    </div>`;
  }
  const segs = task.subtasks.map((s) => `<div class="seg ${s.status || (s.is_completed ? 'done' : 'todo')}"></div>`).join("");
  return `<div class="task-row card">
    <button class="task-row-btn" data-open-task="${task.id}" aria-expanded="${expanded}">
      <div class="title-row">${task.title}${chipsHtml(task)}</div>
      <div class="segbar">${segs}</div>
      <div class="current-step">Current: ${currentStepText(task)}</div>
      <div class="blocker-row">${blockerRow(task)}</div>
      <div class="blurb">${task.description ? task.description.slice(0, 140) : ""}</div>
    </button>
    ${expanded ? detailPanelHtml(task) : ""}
  </div>`;
}

function subtaskRowHtml(task, sub, index) {
  const done = sub.status ? (sub.status === "done" || sub.status === "dropped") : sub.is_completed;
  const circleCls = sub.status === "blocked" ? "blocked" : done ? "done" : "";
  const expanded = state.expandedSubtaskIndex === index;
  const hasExpandable = (sub.status === "blocked" && sub.metadata.blocker) || (sub.notes && sub.notes.trim());
  return `
    <div class="subtask-row2" draggable="true" data-subtask-index="${index}">
      <button class="grip" aria-label="Drag to reorder '${sub.text}', or use Alt+Up / Alt+Down while focused" data-drag-handle="${index}">&#10241;</button>
      <button class="circle-btn ${circleCls}" data-toggle-subtask="${index}" aria-pressed="${done}"
        aria-label="Mark '${sub.text}' ${done ? 'not done' : 'done'}"></button>
      <button class="subtext-btn ${done ? 'done' : ''}" data-expand-subtask="${index}"
        aria-expanded="${expanded}" aria-label="Open detail for '${sub.text}'">${sub.text}</button>
    </div>
    ${expanded && hasExpandable ? `<div class="subtask-expand">${sub.status === 'blocked' ? `Blocked: ${sub.metadata.blocker || ''}` : ''} ${sub.notes || ''}</div>` : ""}
  `;
}

function artifactHtml(task, filename, kindHint) {
  const key = `${task.id}/${filename}`;
  const cached = state.artifactCache.get(key);
  if (!cached) {
    // Fire the load; render a placeholder now, re-render on arrival.
    invoke("read_artifact", { taskId: task.id, filename }).then((payload) => {
      state.artifactCache.set(key, payload);
      render();
    });
    return `<div class="artifact-row">${filename} <span class="muted">loading&hellip;</span></div>`;
  }
  if (cached.kind === "Markdown") {
    return `<div class="artifact-row">${filename} &mdash; shared Markdown view
      <div class="md-body">${renderMarkdown(cached.content)}</div>
    </div>`;
  }
  if (cached.kind === "Html") {
    const sourceOpen = state.artifactSourceOpen.has(key);
    const escapeHtml = (s) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const meta = cached.csp ? `<meta http-equiv="Content-Security-Policy" content="${cached.csp}">` : "";
    const srcdoc = `${meta}${cached.content}`;
    return `<div class="artifact-row">${filename}<span class="sandbox-badge">sandboxed preview</span>
      <button class="artifact-toggle" data-toggle-source="${key}">${sourceOpen ? "Show preview" : "Show source"}</button>
      <button class="artifact-toggle" data-open-externally="${task.id}/${filename}">Open externally</button>
      ${sourceOpen
        ? `<div class="artifact-source">${escapeHtml(cached.content)}</div>`
        : `<iframe class="html-preview-frame" sandbox="allow-same-origin" title="Sandboxed preview of ${filename}" srcdoc="${srcdoc.replace(/"/g, "&quot;")}"></iframe>`}
    </div>`;
  }
  return `<div class="artifact-row">${filename} &mdash; unsupported kind, open externally only</div>`;
}

function detailPanelHtml(task) {
  const hasArtifacts = task.id === "budget-q3-review";
  return `<div class="detail-panel">
    <div class="detail-section"><h3>Description</h3><p>${task.description || "<span class=\"muted\">No description.</span>"}</p></div>
    <div class="detail-section"><h3>Notes</h3><p>${task.notes || "<span class=\"muted\">No notes.</span>"}</p></div>
    ${task.subtasks.length > 0 ? `
    <div class="detail-section"><h3>Subtasks (active -- drag to reorder)</h3>
      <div id="subtask-list" data-task-id="${task.id}">
        ${task.subtasks.map((s, i) => subtaskRowHtml(task, s, i)).join("")}
      </div>
    </div>` : ""}
    ${hasArtifacts ? `
    <div class="detail-section"><h3>Artifacts</h3>
      ${artifactHtml(task, "summary.md", "Markdown")}
      ${artifactHtml(task, "report.html", "Html")}
    </div>` : ""}
  </div>`;
}

function myDayPageHtml() {
  return `<div class="page-head"><h2>My Day</h2><p class="muted">${state.tasks.length} tasks in scope today</p></div>
    <div class="page-body">${state.tasks.map(taskRowHtml).join("")}</div>`;
}

function plannerPageHtml() {
  return `<div class="page-head"><h2>Planner</h2><p class="muted">Nav entry reserved only -- no Planner content in this spike</p></div>
    <div class="planner-banner">TJ's call (2026-07-23): this nav item is reserved so the framework proves it can add a
    nav destination later, but ships no Planner content -- not even a static layout. Wayfinder map "Specify the
    capacity-first Planner" remains the sole source of truth for Planner A.</div>`;
}

function captureFocus() {
  const el = document.activeElement;
  if (!el || !el.dataset) return null;
  for (const key of ["dragHandle", "toggleSubtask", "expandSubtask"]) {
    if (el.dataset[key] !== undefined) return { key, index: Number(el.dataset[key]) };
  }
  return null;
}

function restoreFocus(captured) {
  if (!captured) return;
  const attr = { dragHandle: "data-drag-handle", toggleSubtask: "data-toggle-subtask", expandSubtask: "data-expand-subtask" }[captured.key];
  const el = document.querySelector(`[${attr}="${captured.index}"]`);
  if (el) el.focus();
}

function render() {
  const focused = captureFocus();
  const main = document.getElementById("main-content");
  main.innerHTML = state.page === "myday" ? myDayPageHtml() : plannerPageHtml();
  document.querySelectorAll(".nav-item").forEach((btn) => {
    const active = btn.dataset.page === state.page;
    btn.classList.toggle("active", active);
    if (active) btn.setAttribute("aria-current", "page"); else btn.removeAttribute("aria-current");
  });
  document.getElementById("status-count").textContent = `${state.tasks.length} tasks`;
  restoreFocus(focused);
}

// ---- Event wiring (delegated -- content is re-rendered wholesale) -------

document.querySelector(".nav").addEventListener("click", (e) => {
  const btn = e.target.closest("[data-page]");
  if (!btn) return;
  state.page = btn.dataset.page;
  render();
});

document.getElementById("main-content").addEventListener("click", async (e) => {
  const openTask = e.target.closest("[data-open-task]");
  if (openTask) {
    const clickStart = performance.now();
    const id = openTask.dataset.openTask;
    state.expandedTaskId = state.expandedTaskId === id ? null : id;
    state.expandedSubtaskIndex = null;
    render();
    // Perf-measurement hook (scorecard #376 "task-detail interaction
    // latency"): click to Task Detail fully rendered. Only meaningful when
    // opening (not collapsing) a task.
    if (state.expandedTaskId === id) {
      window.__lastInteractionLatencyMs = performance.now() - clickStart;
    }
    return;
  }
  const toggleSub = e.target.closest("[data-toggle-subtask]");
  if (toggleSub) {
    const index = Number(toggleSub.dataset.toggleSubtask);
    const taskId = state.expandedTaskId;
    const updated = await invoke("toggle_subtask", { taskId, index });
    applyTaskUpdate(updated);
    return;
  }
  const expandSub = e.target.closest("[data-expand-subtask]");
  if (expandSub) {
    const index = Number(expandSub.dataset.expandSubtask);
    state.expandedSubtaskIndex = state.expandedSubtaskIndex === index ? null : index;
    render();
    return;
  }
  const toggleSource = e.target.closest("[data-toggle-source]");
  if (toggleSource) {
    const key = toggleSource.dataset.toggleSource;
    if (state.artifactSourceOpen.has(key)) state.artifactSourceOpen.delete(key);
    else state.artifactSourceOpen.add(key);
    render();
    return;
  }
  const openExternally = e.target.closest("[data-open-externally]");
  if (openExternally) {
    const [taskId, filename] = openExternally.dataset.openExternally.split("/");
    invoke("open_in_obsidian", { taskId }); // spike scope: file-level open, see #372 notes
    return;
  }
  const extLink = e.target.closest("a.ext-link");
  if (extLink) {
    e.preventDefault();
    // ArtifactLinkPolicy-equivalent: allowed scheme, but still routed through
    // the native opener rather than an in-app navigation.
    invoke("open_in_obsidian", { taskId: state.expandedTaskId });
  }
});

// Keyboard alternative to drag-reorder (accessibility gate): Alt+ArrowUp /
// Alt+ArrowDown while a subtask row's grip or text has focus moves it.
document.getElementById("main-content").addEventListener("keydown", async (e) => {
  if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown")) {
    const row = e.target.closest("[data-subtask-index], [data-drag-handle], [data-expand-subtask]");
    if (!row) return;
    const index = Number(row.dataset.subtaskIndex ?? row.dataset.dragHandle ?? row.dataset.expandSubtask);
    const taskId = state.expandedTaskId;
    const task = state.tasks.find((t) => t.id === taskId);
    if (!task) return;
    const target = e.key === "ArrowUp" ? index - 1 : index + 1;
    if (target < 0 || target >= task.subtasks.length) return;
    const order = task.subtasks.map((_, i) => i);
    [order[index], order[target]] = [order[target], order[index]];
    const updated = await invoke("reorder_subtasks", { taskId, newOrder: order });
    applyTaskUpdate(updated);
    e.preventDefault();
  }
});

// ---- Native HTML5 drag-and-drop reorder ---------------------------------
let dragFromIndex = null;
document.getElementById("main-content").addEventListener("dragstart", (e) => {
  const row = e.target.closest(".subtask-row2");
  if (!row) return;
  dragFromIndex = Number(row.dataset.subtaskIndex);
  row.classList.add("dragging");
});
document.getElementById("main-content").addEventListener("dragend", (e) => {
  const row = e.target.closest(".subtask-row2");
  if (row) row.classList.remove("dragging");
});
document.getElementById("main-content").addEventListener("dragover", (e) => {
  if (e.target.closest(".subtask-row2")) e.preventDefault();
});
document.getElementById("main-content").addEventListener("drop", async (e) => {
  const row = e.target.closest(".subtask-row2");
  if (!row || dragFromIndex === null) return;
  e.preventDefault();
  const toIndex = Number(row.dataset.subtaskIndex);
  if (toIndex === dragFromIndex) return;
  const taskId = state.expandedTaskId;
  const task = state.tasks.find((t) => t.id === taskId);
  if (!task) return;
  const order = task.subtasks.map((_, i) => i);
  const [moved] = order.splice(dragFromIndex, 1);
  order.splice(toIndex, 0, moved);
  dragFromIndex = null;
  const updated = await invoke("reorder_subtasks", { taskId, newOrder: order });
  applyTaskUpdate(updated);
});

function applyTaskUpdate(updatedTask) {
  const i = state.tasks.findIndex((t) => t.id === updatedTask.id);
  if (i >= 0) state.tasks[i] = updatedTask;
  render();
}

// ---- Live file-watch parity: external frontmatter edits update the row
// without restart (the "Confirm Tailscale ACL update" fixture requirement).
listen("task-changed", (event) => {
  applyTaskUpdate(event.payload);
});

// ---- Boot ----------------------------------------------------------------
invoke("load_tasks").then((tasks) => {
  state.tasks = tasks;
  document.getElementById("status-vault").textContent = "Obsidian Vault / Glasswork (fixture)";
  render();
  // Perf-measurement hook (scorecard #376 "cold launch time": time from
  // process launch to My Day rendered and interactive). performance.now()
  // is relative to navigation start, which begins shortly after the Rust
  // process initializes the webview -- avoids needing OS-level automation
  // permissions (AppleScript/Accessibility) just to time a render.
  window.__myDayRenderedAtMs = performance.now();
});
