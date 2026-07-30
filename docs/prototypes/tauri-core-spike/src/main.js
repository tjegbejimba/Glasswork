// PROTOTYPE ONLY -- Wayfinder ticket #372. Frontend for the shared
// cross-platform vertical slice (#370): My Day, Task Detail, reserved
// Planner nav stub. Talks to the bounded Rust Core exclusively via Tauri
// IPC commands (see src-tauri/src/lib.rs) -- no domain logic lives here.

const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;
const { getCurrentWindow } = window.__TAURI__.window;

// ---- Untrusted-content boundary -----------------------------------------
// Everything in the Vault is untrusted (CONTEXT.md / ADR 0006): task prose is
// largely agent-authored, so Description, Notes, titles, subtask text and
// blocker strings can all contain markup. Nothing from a Task reaches
// innerHTML without passing through here (or through renderMarkdown, which
// escapes first). Defined once at module scope so there is a single boundary
// to audit rather than per-function copies that can drift.
const escapeHtml = (s) =>
  String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const state = {
  page: "myday",
  selectedTaskId: null,
  tasks: [],
  expandedTaskId: null,
  expandedSubtaskIndex: null,
  artifactCache: new Map(), // `${taskId}/${filename}` -> payload
  artifactPreviewOpen: new Set(), // ADR 0015: Source is the default; Preview is opt-in.
  pendingArtifactLoads: 0,
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
  if (task.due) chips.push(['due-today', `Due ${escapeHtml(task.due)}`]);
  if (task.ado_title) chips.push(['ado', escapeHtml(task.ado_title)]);
  return chips.map(([cls, label]) => `<span class="chip ${cls}">${label}</span>`).join("");
}

function currentStepText(task) {
  const step = task.subtasks.find((s) => s.status === "in_progress");
  return step ? escapeHtml(step.text) : "";
}
function blockerRow(task) {
  const blocked = task.subtasks.find((s) => s.status === "blocked" && s.metadata.blocker);
  return blocked ? `&#128683; ${escapeHtml(blocked.metadata.blocker)}` : "";
}

function taskRowHtml(task) {
  // `show_as_card` is decided by the bounded Rust Core and travels on the
  // payload (CONTEXT.md: Presentation holds no domain logic). The frontend
  // reads the decision; it does not re-derive it.
  if (!task.show_as_card) {
    return `<div class="task-row quiet">
      <button class="task-row-btn" data-open-task="${escapeHtml(task.id)}">
        <div class="title-row">${escapeHtml(task.title)}${chipsHtml(task)}</div>
      </button>
    </div>`;
  }
  // `status` is Vault-authored subtask metadata, so it is untrusted even
  // though it only ever *should* be one of a few known keywords. It lands in
  // a class attribute, where an unescaped quote would let it close the
  // attribute and inject sibling markup -- so it is escaped like any other
  // Vault value, not trusted because of where it came from.
  const segs = task.subtasks
    .map((s) => `<div class="seg ${escapeHtml(s.status || (s.is_completed ? "done" : "todo"))}"></div>`)
    .join("");
  return `<div class="task-row card">
    <button class="task-row-btn" data-open-task="${escapeHtml(task.id)}">
      <div class="title-row">${escapeHtml(task.title)}${chipsHtml(task)}</div>
      <div class="segbar">${segs}</div>
      <div class="current-step">Current: ${currentStepText(task)}</div>
      <div class="blocker-row">${blockerRow(task)}</div>
      <div class="blurb">${escapeHtml(task.description ? task.description.slice(0, 140) : "")}</div>
    </button>
  </div>`;
}

function subtaskRowHtml(task, sub, index) {
  const done = sub.status ? (sub.status === "done" || sub.status === "dropped") : sub.is_completed;
  const circleCls = sub.status === "blocked" ? "blocked" : done ? "done" : "";
  const expanded = state.expandedSubtaskIndex === index;
  const hasExpandable = (sub.status === "blocked" && sub.metadata.blocker) || (sub.notes && sub.notes.trim());
  return `
    <div class="subtask-row2" draggable="true" data-subtask-index="${index}">
      <button class="grip" aria-label="Drag to reorder '${escapeHtml(sub.text)}', or use Alt+Up / Alt+Down while focused" data-drag-handle="${index}">&#10241;</button>
      <button class="circle-btn ${circleCls}" data-toggle-subtask="${index}" aria-pressed="${done}"
        aria-label="Mark '${escapeHtml(sub.text)}' ${done ? 'not done' : 'done'}"></button>
      <button class="subtext-btn ${done ? 'done' : ''}" data-expand-subtask="${index}"
        aria-expanded="${expanded}" aria-label="Open detail for '${escapeHtml(sub.text)}'">${escapeHtml(sub.text)}</button>
    </div>
    ${expanded && hasExpandable ? `<div class="subtask-expand">${sub.status === 'blocked' ? `Blocked: ${escapeHtml(sub.metadata.blocker || '')}` : ''} ${escapeHtml(sub.notes || '')}</div>` : ""}
  `;
}

function artifactHtml(task, filename, kindHint) {
  const key = `${task.id}/${filename}`;
  const cached = state.artifactCache.get(key);
  if (!cached) {
    // Fire the load; render a placeholder now, re-render on arrival.
    state.pendingArtifactLoads += 1;
    invoke("read_artifact", { taskId: task.id, filename }).then((payload) => {
      state.artifactCache.set(key, payload);
      state.pendingArtifactLoads -= 1;
      render();
    });
    return `<div class="artifact-row">${escapeHtml(filename)} <span class="muted">loading&hellip;</span></div>`;
  }
  if (cached.kind === "Markdown") {
    return `<div class="artifact-row">${escapeHtml(filename)} &mdash; shared Markdown view
      <div class="md-body">${renderMarkdown(cached.content)}</div>
    </div>`;
  }
  if (cached.kind === "Html") {
    // ADR 0015: untrusted HTML renders as **Source by default**, with an
    // opt-in Preview that is only created on click ("Lazy (created only on
    // Preview click)"). Defaulting to Preview would auto-render
    // agent-authored HTML the user never asked to see.
    const previewOpen = state.artifactPreviewOpen.has(key);
    const meta = cached.csp ? `<meta http-equiv="Content-Security-Policy" content="${cached.csp}">` : "";
    const srcdoc = `${meta}${cached.content}`;
    return `<div class="artifact-row">${escapeHtml(filename)}<span class="sandbox-badge">${previewOpen ? "sandboxed preview" : "source view"}</span>
      <button class="artifact-toggle" data-toggle-preview="${escapeHtml(key)}">${previewOpen ? "Show source" : "Show preview"}</button>
      <button class="artifact-toggle" data-open-externally="${escapeHtml(task.id)}/${escapeHtml(filename)}">Open externally</button>
      ${previewOpen
        ? `<iframe class="html-preview-frame" sandbox="allow-same-origin" title="Sandboxed preview of ${escapeHtml(filename)}" srcdoc="${srcdoc.replace(/"/g, "&quot;")}"></iframe>`
        : `<div class="artifact-source">${escapeHtml(cached.content)}</div>`}
    </div>`;
  }
  return `<div class="artifact-row">${escapeHtml(filename)} &mdash; unsupported kind, open externally only</div>`;
}

function detailPanelHtml(task) {
  const hasArtifacts = task.id === "budget-q3-review";
  // Description and Notes are agent-authored Vault prose -- untrusted. They
  // go through the same bounded Markdown renderer as Artifact content
  // (ADR 0006: one renderer, all rendered content untrusted), which escapes
  // before applying any inline formatting.
  return `<div class="detail-panel">
    <div class="detail-section"><h3>Description</h3>
      ${task.description ? renderMarkdown(task.description) : `<p class="muted">No description.</p>`}</div>
    <div class="detail-section"><h3>Notes</h3>
      ${task.notes ? renderMarkdown(task.notes) : `<p class="muted">No notes.</p>`}</div>
    ${task.subtasks.length > 0 ? `
    <div class="detail-section"><h3>Subtasks (active -- drag to reorder)</h3>
      <div id="subtask-list" data-task-id="${escapeHtml(task.id)}">
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

// Task Detail is its own Page in the three-page shell (#370), reached by
// navigating away from My Day -- not an inline expansion of the row.
function taskDetailPageHtml() {
  const task = state.tasks.find((t) => t.id === state.selectedTaskId);
  if (!task) return myDayPageHtml();
  return `<div class="page-head detail-head">
      <button class="back-btn" data-back-to-myday aria-label="Back to My Day">&#8592; My Day</button>
      <h2>${escapeHtml(task.title)}</h2>
      <div class="detail-chips">${chipsHtml(task)}</div>
    </div>
    <div class="page-body">${detailPanelHtml(task)}</div>`;
}

// Reserved nav destination only. Per #370 the Planner "ships no Planner
// content, not even a static layout", so this Page renders nothing at all --
// the nav item exists purely to prove the framework can host another
// destination later. The nav item itself is aria-disabled and never routes
// here; this function is the belt-and-braces guarantee that even if it did,
// there is no Planner content to show.
function plannerPageHtml() {
  return "";
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

function pageHtml() {
  if (state.page === "planner") return plannerPageHtml();
  if (state.page === "detail") return taskDetailPageHtml();
  return myDayPageHtml();
}

function render() {
  const focused = captureFocus();
  const main = document.getElementById("main-content");
  main.innerHTML = pageHtml();
  document.querySelectorAll(".nav-item").forEach((btn) => {
    const active = btn.dataset.page === (state.page === "detail" ? "myday" : state.page);
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
  // The Planner nav entry is reserved, not routable: it must not navigate to
  // any content, explanatory or otherwise (#370).
  if (btn.getAttribute("aria-disabled") === "true") return;
  state.page = btn.dataset.page;
  state.selectedTaskId = null;
  render();
});

document.getElementById("main-content").addEventListener("click", async (e) => {
  const openTask = e.target.closest("[data-open-task]");
  if (openTask) {
    const clickStart = performance.now();
    const id = openTask.dataset.openTask;
    state.selectedTaskId = id;
    state.expandedTaskId = id;
    state.page = "detail";
    state.expandedSubtaskIndex = null;
    render();
    // Perf-measurement hook (scorecard #376 "task-detail interaction
    // latency"): click to Task Detail fully rendered. Only meaningful when
    // opening (not collapsing) a task.
    // Scorecard metric 2 is "click to Task Detail fully rendered and
    // interactive". The first paint can still contain artifact placeholders,
    // so only stamp the metric once every artifact load has settled and the
    // final re-render has happened -- otherwise this measures first paint and
    // flatters the number.
    stampInteractionLatencyWhenSettled(clickStart);
    return;
  }
  const back = e.target.closest("[data-back-to-myday]");
  if (back) {
    state.page = "myday";
    state.selectedTaskId = null;
    state.expandedTaskId = null;
    state.expandedSubtaskIndex = null;
    render();
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
  const togglePreview = e.target.closest("[data-toggle-preview]");
  if (togglePreview) {
    const key = togglePreview.dataset.togglePreview;
    if (state.artifactPreviewOpen.has(key)) state.artifactPreviewOpen.delete(key);
    else state.artifactPreviewOpen.add(key);
    render();
    return;
  }
  const openExternally = e.target.closest("[data-open-externally]");
  if (openExternally) {
    // Open the Artifact itself, not its parent task: the payload is already
    // `<taskId>/<filename>`, which maps to the Vault-relative Artifact path.
    const [taskId, filename] = openExternally.dataset.openExternally.split("/");
    invoke("open_in_obsidian", { vaultRelativePath: `${taskId}.artifacts/${filename}` });
    return;
  }
  const extLink = e.target.closest("a.ext-link");
  if (extLink) {
    e.preventDefault();
    // ArtifactLinkPolicy-equivalent: allowed scheme, but still routed through
    // the native opener rather than an in-app navigation.
    invoke("open_in_obsidian", { vaultRelativePath: `${state.selectedTaskId}.md` });
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

// Uses timers rather than requestAnimationFrame: rAF is throttled (often
// stopped entirely) while the window is not frontmost, which made this
// measurement drop out under WebDriver. This stamps once every artifact load
// has settled and the resulting re-render has been committed to the DOM --
// i.e. "click -> Task Detail fully rendered", not first paint. It does not
// claim to include the final compositor paint.
function stampInteractionLatencyWhenSettled(clickStart) {
  if (state.pendingArtifactLoads > 0) {
    setTimeout(() => stampInteractionLatencyWhenSettled(clickStart), 4);
    return;
  }
  setTimeout(() => {
    window.__lastInteractionLatencyMs = performance.now() - clickStart;
  }, 0);
}

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
