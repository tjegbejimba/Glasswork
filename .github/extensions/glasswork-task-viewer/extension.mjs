import { createServer } from "node:http";
import { homedir } from "node:os";
import { extname, join, relative, resolve, sep } from "node:path";
import { readdir, readFile, stat } from "node:fs/promises";
import { existsSync } from "node:fs";
import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";

const CANVAS_ID = "glasswork-task";
const VISIBLE_STATUS = new Map([
    ["todo", "todo"],
    ["in-progress", "doing"],
    ["in_progress", "doing"],
    ["doing", "doing"],
    ["done", "done"],
]);

const servers = new Map();

const inputSchema = {
    type: "object",
    properties: {
        task_id: {
            type: "string",
            description: "Glasswork task ID to view, e.g. my-task-id.",
        },
        depth: {
            type: "integer",
            minimum: 0,
            maximum: 3,
            description: "Child-task depth to include. Defaults to 1 and is clamped to 0-3.",
        },
    },
    required: ["task_id"],
    additionalProperties: false,
};

const refreshInputSchema = {
    type: "object",
    properties: {
        task_id: {
            type: "string",
            description: "Optional new task ID. Omit to refresh the task already opened in this canvas.",
        },
        depth: {
            type: "integer",
            minimum: 0,
            maximum: 3,
            description: "Optional child-task depth override.",
        },
    },
    additionalProperties: false,
};

function sanitizeTaskId(id) {
    return String(id ?? "").trim().toLowerCase().replace(/[^a-z0-9-]/g, "");
}

function clampDepth(depth) {
    if (!Number.isInteger(depth)) return 1;
    return Math.max(0, Math.min(3, depth));
}

function normalizeInput(input) {
    const taskId = sanitizeTaskId(input?.task_id);
    if (!taskId) {
        throw new CanvasError("invalid_task_id", "task_id must contain at least one lowercase letter, number, or hyphen.");
    }

    return {
        task_id: taskId,
        depth: clampDepth(input?.depth),
    };
}

function localAppDataPath() {
    return process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local");
}

async function discoverVault() {
    const envVault = process.env.GLASSWORK_VAULT;
    const envRoot = normalizeVaultRoot(envVault);
    if (envRoot) {
        return envRoot;
    }

    const statePath = join(localAppDataPath(), "Glasswork", "ui-state.json");
    if (!existsSync(statePath)) {
        return defaultVaultRoot();
    }

    let state;
    try {
        const raw = await readFile(statePath, "utf8");
        state = JSON.parse(raw);
    } catch {
        return defaultVaultRoot();
    }
    const persisted = state["vault.path"];
    const persistedRoot = normalizeVaultRoot(persisted);
    if (persistedRoot) {
        return persistedRoot;
    }

    return defaultVaultRoot();
}

function normalizeVaultRoot(candidate) {
    if (typeof candidate !== "string" || !candidate.trim()) return null;
    const fullPath = resolve(candidate);
    if (!existsSync(fullPath)) return null;

    if (existsSync(join(fullPath, "wiki", "todo"))) {
        return fullPath;
    }

    const todoSuffix = `${sep}wiki${sep}todo`;
    if (fullPath.endsWith(todoSuffix)) {
        return fullPath.slice(0, -todoSuffix.length);
    }

    return null;
}

function defaultVaultRoot() {
    const root = join(homedir(), "Wiki");
    return existsSync(join(root, "wiki", "todo")) ? root : null;
}

function todoDir(vaultRoot) {
    return join(vaultRoot, "wiki", "todo");
}

function taskPath(vaultRoot, taskId) {
    return join(todoDir(vaultRoot), `${taskId}.md`);
}

function normalizeSlashes(path) {
    return path.split(sep).join("/");
}

function toVaultRelative(vaultRoot, fullPath) {
    return normalizeSlashes(relative(vaultRoot, fullPath));
}

function parseYamlScalar(value) {
    const trimmed = String(value ?? "").trim();
    if (!trimmed) return "";
    if ((trimmed.startsWith("\"") && trimmed.endsWith("\"")) ||
        (trimmed.startsWith("'") && trimmed.endsWith("'"))) {
        return trimmed.slice(1, -1);
    }
    return trimmed;
}

function parseFrontmatter(yaml) {
    const lines = yaml.split("\n").map((line) => line.replace(/\r$/, ""));
    const data = {};
    let currentList = null;
    let currentObject = null;

    for (const line of lines) {
        if (!line.trim()) continue;

        const topLevel = line.match(/^([A-Za-z0-9_]+):(?:\s*(.*))?$/);
        if (topLevel) {
            const key = topLevel[1];
            const value = topLevel[2] ?? "";
            currentObject = null;
            if (value.trim() === "") {
                data[key] = [];
                currentList = key;
            } else {
                data[key] = parseYamlScalar(value);
                currentList = null;
            }
            continue;
        }

        if (currentList) {
            const itemWithPair = line.match(/^\s*-\s*([A-Za-z0-9_]+):\s*(.*)$/);
            if (itemWithPair) {
                currentObject = { [itemWithPair[1]]: parseYamlScalar(itemWithPair[2]) };
                data[currentList].push(currentObject);
                continue;
            }

            const itemScalar = line.match(/^\s*-\s*(.*)$/);
            if (itemScalar) {
                currentObject = null;
                data[currentList].push(parseYamlScalar(itemScalar[1]));
                continue;
            }

            const nestedPair = line.match(/^\s+([A-Za-z0-9_]+):\s*(.*)$/);
            if (nestedPair && currentObject) {
                currentObject[nestedPair[1]] = parseYamlScalar(nestedPair[2]);
            }
        }
    }

    return data;
}

function sectionBody(body, heading) {
    const escaped = heading.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const match = body.match(new RegExp(`(^|\\n)## ${escaped}\\s*(?:\\n|$)([\\s\\S]*?)(?=\\n## |$)`));
    return match ? match[2].trim() : "";
}

function stripSection(body, heading) {
    const escaped = heading.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    return body.replace(new RegExp(`(^|\\n)## ${escaped}\\s*(?:\\n|$)[\\s\\S]*?(?=\\n## |$)`), "").trim();
}

function parseInlineSubtasks(subtasksSection) {
    const subtasks = [];
    const lines = subtasksSection.split("\n").map((line) => line.replace(/\r$/, ""));
    let current = null;
    let notes = [];
    let inMetadata = false;

    function finish() {
        if (!current) return;
        current.notes = notes.join("\n").trim();
        subtasks.push(current);
        current = null;
        notes = [];
    }

    for (const line of lines) {
        const heading = line.match(/^### \[([ xX])\] (.+?)\s*$/);
        if (heading) {
            finish();
            current = {
                text: heading[2].trim(),
                completed: heading[1].toLowerCase() === "x",
                status: null,
                metadata: {},
                notes: "",
            };
            inMetadata = true;
            continue;
        }

        if (!current) continue;

        if (inMetadata) {
            if (!line.trim()) {
                inMetadata = false;
                continue;
            }

            const metadata = line.match(/^- ([a-z_][a-z0-9_]*): (.*)$/);
            if (metadata) {
                if (metadata[1] === "status") current.status = metadata[2].trim();
                else current.metadata[metadata[1]] = metadata[2].trim();
                continue;
            }

            inMetadata = false;
        }

        notes.push(line);
    }

    finish();
    return subtasks;
}

function parseWikiLinks(section) {
    const links = [];
    const regex = /\[\[([^\]|]+?)(?:\|([^\]]+))?\]\]/g;
    let match;
    while ((match = regex.exec(section)) !== null) {
        links.push({
            target: match[1].trim(),
            label: match[2]?.trim() || null,
        });
    }
    return links;
}

function parseTaskMarkdown(raw, filePath) {
    const normalized = raw.replace(/\r\n/g, "\n");
    const match = normalized.match(/^---\s*\n([\s\S]*?)\n---\s*\n?([\s\S]*)$/);
    if (!match) {
        throw new Error(`Invalid task file: missing YAML frontmatter in ${filePath}`);
    }

    const frontmatter = parseFrontmatter(match[1]);
    const body = match[2].trim();
    const subtasksSection = sectionBody(body, "Subtasks");
    const notes = sectionBody(body, "Notes");
    const related = sectionBody(body, "Related");
    const description = stripSection(stripSection(stripSection(body, "Subtasks"), "Notes"), "Related");

    return {
        id: String(frontmatter.id ?? "").trim(),
        title: String(frontmatter.title ?? "").trim(),
        status: mapStatus(frontmatter.status),
        priority: String(frontmatter.priority ?? "medium").trim(),
        created: String(frontmatter.created ?? "").trim(),
        completed_at: String(frontmatter.completed_at ?? "").trim() || null,
        due: String(frontmatter.due ?? "").trim() || null,
        my_day: String(frontmatter.my_day ?? "").trim() || null,
        parent_id: String(frontmatter.parent ?? "").trim() || null,
        tags: Array.isArray(frontmatter.tags) ? frontmatter.tags.filter(Boolean) : [],
        links: Array.isArray(frontmatter.links) ? frontmatter.links : [],
        description,
        notes,
        inline_subtasks: parseInlineSubtasks(subtasksSection),
        related_links: parseWikiLinks(related),
        path: filePath,
    };
}

function mapStatus(status) {
    const normalized = String(status ?? "todo").trim();
    return VISIBLE_STATUS.get(normalized) ?? normalized;
}

async function loadTask(vaultRoot, taskId) {
    const filePath = taskPath(vaultRoot, taskId);
    const raw = await readFile(filePath, "utf8");
    return parseTaskMarkdown(raw, filePath);
}

async function listTaskFiles(vaultRoot) {
    const dir = todoDir(vaultRoot);
    const entries = await readdir(dir, { withFileTypes: true });
    return entries
        .filter((entry) => entry.isFile() && extname(entry.name).toLowerCase() === ".md" && !entry.name.startsWith("_"))
        .map((entry) => join(dir, entry.name));
}

async function loadAllTasks(vaultRoot) {
    const files = await listTaskFiles(vaultRoot);
    const tasks = [];
    const warnings = [];
    for (const file of files) {
        try {
            tasks.push(parseTaskMarkdown(await readFile(file, "utf8"), file));
        } catch (error) {
            warnings.push(`Skipped malformed task file ${normalizeSlashes(relative(todoDir(vaultRoot), file))}: ${error.message}`);
        }
    }
    return { tasks, warnings };
}

async function loadArtifacts(vaultRoot, taskId) {
    const folder = join(todoDir(vaultRoot), `${taskId}.artifacts`);
    if (!existsSync(folder)) return [];

    const entries = await readdir(folder, { withFileTypes: true });
    const artifacts = [];
    for (const entry of entries) {
        if (!entry.isFile() || extname(entry.name).toLowerCase() !== ".md") continue;
        const fullPath = join(folder, entry.name);
        const raw = await readFile(fullPath, "utf8");
        const info = await stat(fullPath);
        artifacts.push({
            filename: entry.name,
            title: resolveMarkdownTitle(raw, entry.name),
            path: normalizeSlashes(relative(todoDir(vaultRoot), fullPath)),
            vault_relative_path: toVaultRelative(vaultRoot, fullPath),
            modified_utc: info.mtime.toISOString(),
            content: raw,
        });
    }

    return artifacts.sort((a, b) => a.filename.localeCompare(b.filename, undefined, { sensitivity: "base" }));
}

function resolveMarkdownTitle(raw, filename) {
    const heading = raw.match(/^#\s+(.+?)\s*$/m);
    if (heading) return heading[1].trim();
    return filename.replace(/\.md$/i, "");
}

async function walkMarkdownFiles(root) {
    const result = [];
    async function visit(dir) {
        const entries = await readdir(dir, { withFileTypes: true });
        for (const entry of entries) {
            const fullPath = join(dir, entry.name);
            if (entry.isDirectory()) {
                if (entry.name.startsWith(".")) continue;
                await visit(fullPath);
            } else if (entry.isFile() && extname(entry.name).toLowerCase() === ".md") {
                result.push(fullPath);
            }
        }
    }

    await visit(root);
    return result;
}

async function loadBacklinks(vaultRoot, taskId, currentTaskPath) {
    const files = await walkMarkdownFiles(vaultRoot);
    const backlinks = [];
    const taskPattern = new RegExp(`\\[\\[${taskId.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(?:\\|[^\\]]+)?\\]\\]`, "i");

    for (const file of files) {
        if (resolve(file) === resolve(currentTaskPath)) continue;
        let raw;
        try {
            raw = await readFile(file, "utf8");
        } catch {
            continue;
        }

        if (!taskPattern.test(raw)) continue;
        backlinks.push({
            source_path: toVaultRelative(vaultRoot, file),
            source_title: resolveMarkdownTitle(raw, file.split(sep).pop() ?? file),
            page_type: classifyVaultPage(vaultRoot, file),
        });
    }

    return backlinks.sort((a, b) => a.source_path.localeCompare(b.source_path));
}

function classifyVaultPage(vaultRoot, fullPath) {
    const rel = toVaultRelative(vaultRoot, fullPath);
    if (rel.startsWith("wiki/todo/")) return "task";
    const parts = rel.split("/");
    return parts.length > 1 ? parts[1] : "page";
}

async function buildChildTree(task, depth, allTasks, vaultRoot, visited) {
    if (depth <= 0) return [];

    const children = allTasks
        .filter((candidate) => candidate.parent_id === task.id)
        .sort((a, b) => (a.created || "").localeCompare(b.created || "") || a.id.localeCompare(b.id));

    const result = [];
    for (const child of children) {
        if (!child.id || visited.has(child.id)) continue;
        visited.add(child.id);
        result.push({
            task: summarizeTask(child),
            inline_subtasks: child.inline_subtasks,
            artifacts: await loadArtifacts(vaultRoot, child.id),
            subtasks: await buildChildTree(child, depth - 1, allTasks, vaultRoot, visited),
        });
    }

    return result;
}

function summarizeTask(task) {
    return {
        id: task.id,
        title: task.title,
        status: task.status,
        priority: task.priority,
        parent_id: task.parent_id,
        created: task.created,
        due: task.due,
        my_day: task.my_day,
        tags: task.tags,
        links: task.links,
        description: task.description,
        notes: task.notes,
        path: normalizeSlashes(relative(todoDir(currentVaultRootFor(task.path)), task.path)),
    };
}

function currentVaultRootFor(taskFilePath) {
    const marker = `${sep}wiki${sep}todo${sep}`;
    const index = taskFilePath.indexOf(marker);
    return index >= 0 ? taskFilePath.slice(0, index) : "";
}

async function loadContext(input) {
    const normalized = normalizeInput(input);
    const vaultRoot = await discoverVault();
    if (!vaultRoot) {
        return {
            input: normalized,
            loaded_at: new Date().toISOString(),
            error: "vault_not_configured",
            message: "Set GLASSWORK_VAULT or configure a vault path in the Glasswork app.",
        };
    }

    const filePath = taskPath(vaultRoot, normalized.task_id);
    if (!existsSync(filePath)) {
        return {
            input: normalized,
            loaded_at: new Date().toISOString(),
            vault_root: vaultRoot,
            error: "not_found",
            message: `Task '${normalized.task_id}' was not found in ${todoDir(vaultRoot)}.`,
        };
    }

    let task;
    try {
        task = await loadTask(vaultRoot, normalized.task_id);
    } catch (error) {
        return {
            input: normalized,
            loaded_at: new Date().toISOString(),
            vault_root: vaultRoot,
            error: "parse_failed",
            message: error.message,
        };
    }

    const artifacts = await loadArtifacts(vaultRoot, normalized.task_id);
    const { tasks: allTasks, warnings } = await loadAllTasks(vaultRoot);
    const subtasks = await buildChildTree(task, normalized.depth, allTasks, vaultRoot, new Set([task.id]));
    const backlinks = await loadBacklinks(vaultRoot, normalized.task_id, task.path);

    return {
        input: normalized,
        loaded_at: new Date().toISOString(),
        vault_root: vaultRoot,
        task: summarizeTask(task),
        inline_subtasks: task.inline_subtasks,
        artifacts,
        subtasks,
        backlinks,
        warnings,
    };
}

function taskCounts(context) {
    return {
        artifact_count: context.artifacts?.length ?? 0,
        inline_subtask_count: context.inline_subtasks?.length ?? 0,
        child_task_count: countChildTasks(context.subtasks ?? []),
        backlink_count: context.backlinks?.length ?? 0,
    };
}

function countChildTasks(children) {
    return children.reduce((total, child) => total + 1 + countChildTasks(child.subtasks ?? []), 0);
}

function renderHtml(context) {
    const title = context.task?.title ?? "Glasswork task";
    return `<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>${escapeHtml(title)}</title>
  <style>
    :root { color-scheme: light dark; }
    body {
      margin: 0;
      background: var(--background-color-default, #ffffff);
      color: var(--text-color-default, #1f2328);
      font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
      font-size: var(--text-body-medium, 14px);
      line-height: var(--leading-body-medium, 20px);
    }
    main { max-width: 1080px; margin: 0 auto; padding: 24px; }
    header {
      display: grid;
      gap: 12px;
      padding-bottom: 20px;
      border-bottom: 1px solid var(--border-color-default, #d0d7de);
    }
    h1 {
      margin: 0;
      font-size: var(--text-title-large, 26px);
      line-height: var(--leading-title-large, 32px);
      font-weight: var(--font-weight-semibold, 600);
    }
    h2 {
      margin: 0 0 10px;
      font-size: var(--text-title-medium, 18px);
      line-height: var(--leading-title-medium, 24px);
      font-weight: var(--font-weight-semibold, 600);
    }
    h3 { margin: 0; font-size: 15px; }
    button {
      width: fit-content;
      border: 1px solid var(--border-color-default, #d0d7de);
      border-radius: 8px;
      padding: 7px 12px;
      background: var(--background-color-muted, #f6f8fa);
      color: var(--text-color-default, #1f2328);
      cursor: pointer;
    }
    button:focus-visible { outline: 2px solid var(--color-focus-outline, #0969da); outline-offset: 2px; }
    .muted { color: var(--text-color-muted, #656d76); }
    .chips { display: flex; flex-wrap: wrap; gap: 8px; }
    .chip {
      border: 1px solid var(--border-color-default, #d0d7de);
      border-radius: 999px;
      padding: 2px 8px;
      background: var(--background-color-muted, #f6f8fa);
      font-size: 12px;
    }
    .sections { display: grid; gap: 16px; margin-top: 20px; }
    .card {
      border: 1px solid var(--border-color-default, #d0d7de);
      border-radius: 12px;
      background: var(--background-color-default, #ffffff);
      padding: 16px;
    }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px; }
    .field-label { display: block; color: var(--text-color-muted, #656d76); font-size: 12px; }
    .markdown { display: grid; gap: 8px; }
    .markdown p, .markdown pre, .markdown ul, .markdown ol, .markdown blockquote { margin: 0; }
    .markdown pre {
      overflow: auto;
      padding: 12px;
      border-radius: 8px;
      background: var(--background-color-muted, #f6f8fa);
      font-family: var(--font-mono, Consolas, monospace);
      font-size: var(--text-code-block, 12px);
    }
    .markdown blockquote {
      padding-left: 12px;
      border-left: 3px solid var(--border-color-default, #d0d7de);
      color: var(--text-color-muted, #656d76);
    }
    .subtask, .child-task, .artifact, .backlink {
      display: grid;
      gap: 6px;
      padding: 12px;
      border: 1px solid var(--border-color-muted, #d8dee4);
      border-radius: 10px;
      background: var(--background-color-muted, #f6f8fa);
    }
    .stack { display: grid; gap: 10px; }
    .status-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 6px; background: var(--true-color-blue, #0969da); }
    .status-done .status-dot { background: var(--true-color-green, #1a7f37); }
    .status-todo .status-dot { background: var(--text-color-muted, #656d76); }
    .error { border-color: var(--true-color-red, #cf222e); }
  </style>
</head>
<body>
  <main>
    ${context.error ? renderError(context) : renderTask(context)}
  </main>
  <script>
    const button = document.querySelector("[data-refresh]");
    if (button) {
      button.addEventListener("click", async () => {
        button.disabled = true;
        button.textContent = "Refreshing...";
        await fetch("/refresh", { method: "POST" });
        location.reload();
      });
    }
  </script>
</body>
</html>`;
}

function renderError(context) {
    return `<header class="error card">
      <h1>Glasswork task unavailable</h1>
      <p>${escapeHtml(context.message ?? context.error)}</p>
      <button data-refresh>Refresh</button>
    </header>`;
}

function renderTask(context) {
    const task = context.task;
    const counts = taskCounts(context);
    return `<header>
      <div>
        <span class="muted">Glasswork task</span>
        <h1>${escapeHtml(task.title || task.id)}</h1>
      </div>
      <div class="chips">
        ${statusChip(task.status)}
        ${task.priority ? chip(`priority: ${task.priority}`) : ""}
        ${task.due ? chip(`due: ${task.due}`) : ""}
        ${task.parent_id ? chip(`parent: ${task.parent_id}`) : ""}
        ${task.tags.map((tag) => chip(`#${tag}`)).join("")}
      </div>
      <div class="grid">
        ${field("ID", task.id)}
        ${field("Created", task.created || "unknown")}
        ${field("Artifacts", String(counts.artifact_count))}
        ${field("Inline subtasks", String(counts.inline_subtask_count))}
        ${field("Child tasks", String(counts.child_task_count))}
        ${field("Backlinks", String(counts.backlink_count))}
      </div>
      <div class="muted">Loaded ${escapeHtml(new Date(context.loaded_at).toLocaleString())}</div>
      <button data-refresh>Refresh from vault</button>
    </header>
    <div class="sections">
      ${section("Description", renderMarkdown(task.description), !task.description)}
      ${section("Notes", renderMarkdown(task.notes), !task.notes)}
      ${section("Inline subtasks", renderInlineSubtasks(context.inline_subtasks), context.inline_subtasks.length === 0)}
      ${section("Artifacts", renderArtifacts(context.artifacts), context.artifacts.length === 0)}
      ${section("Child tasks", renderChildTasks(context.subtasks), context.subtasks.length === 0)}
      ${section("Backlinks", renderBacklinks(context.backlinks), context.backlinks.length === 0)}
      ${section("Links", renderLinks(task.links), task.links.length === 0)}
      ${context.warnings?.length ? section("Warnings", renderWarnings(context.warnings), false) : ""}
    </div>`;
}

function statusChip(status) {
    return `<span class="chip status-${escapeHtml(status)}"><span class="status-dot"></span>${escapeHtml(status)}</span>`;
}

function chip(content) {
    return `<span class="chip">${escapeHtml(content)}</span>`;
}

function field(label, value) {
    return `<div><span class="field-label">${escapeHtml(label)}</span>${escapeHtml(value)}</div>`;
}

function section(title, body, empty) {
    return `<section class="card">
      <h2>${escapeHtml(title)}</h2>
      ${empty ? `<p class="muted">No ${escapeHtml(title.toLowerCase())}.</p>` : body}
    </section>`;
}

function renderInlineSubtasks(subtasks) {
    return `<div class="stack">${subtasks.map((subtask) => `<article class="subtask">
      <h3>${subtask.completed ? "[x]" : "[ ]"} ${escapeHtml(subtask.text)}</h3>
      <div class="chips">
        ${subtask.status ? chip(`status: ${subtask.status}`) : ""}
        ${Object.entries(subtask.metadata).map(([key, value]) => chip(`${key}: ${value}`)).join("")}
      </div>
      ${subtask.notes ? renderMarkdown(subtask.notes) : ""}
    </article>`).join("")}</div>`;
}

function renderArtifacts(artifacts) {
    return `<div class="stack">${artifacts.map((artifact) => `<article class="artifact">
      <div>
        <h3>${escapeHtml(artifact.title)}</h3>
        <div class="muted">${escapeHtml(artifact.path)} &middot; ${escapeHtml(new Date(artifact.modified_utc).toLocaleString())}</div>
      </div>
      ${renderMarkdown(artifact.content)}
    </article>`).join("")}</div>`;
}

function renderChildTasks(children) {
    return `<div class="stack">${children.map(renderChildTask).join("")}</div>`;
}

function renderChildTask(child) {
    return `<article class="child-task">
      <div>
        <h3>${escapeHtml(child.task.title || child.task.id)}</h3>
        <div class="muted">${escapeHtml(child.task.id)} &middot; ${escapeHtml(child.task.status)}</div>
      </div>
      ${child.task.description ? renderMarkdown(child.task.description) : ""}
      ${child.inline_subtasks.length ? `<details><summary>Inline subtasks (${child.inline_subtasks.length})</summary>${renderInlineSubtasks(child.inline_subtasks)}</details>` : ""}
      ${child.artifacts.length ? `<details><summary>Artifacts (${child.artifacts.length})</summary>${renderArtifacts(child.artifacts)}</details>` : ""}
      ${child.subtasks.length ? `<details open><summary>Child tasks (${child.subtasks.length})</summary>${renderChildTasks(child.subtasks)}</details>` : ""}
    </article>`;
}

function renderBacklinks(backlinks) {
    return `<div class="stack">${backlinks.map((backlink) => `<article class="backlink">
      <h3>${escapeHtml(backlink.source_title)}</h3>
      <div class="muted">${escapeHtml(backlink.source_path)} &middot; ${escapeHtml(backlink.page_type)}</div>
    </article>`).join("")}</div>`;
}

function renderLinks(links) {
    return `<div class="stack">${links.map((link) => `<article class="backlink">
      <h3>${escapeHtml(link.label || link.value || "Link")}</h3>
      <div class="muted">${escapeHtml(link.type || "other")}: ${escapeHtml(link.value || "")}</div>
    </article>`).join("")}</div>`;
}

function renderWarnings(warnings) {
    return `<div class="stack">${warnings.map((warning) => `<p class="muted">${escapeHtml(warning)}</p>`).join("")}</div>`;
}

function renderMarkdown(markdown) {
    const raw = String(markdown ?? "").trim();
    if (!raw) return "";

    const blocks = [];
    const lines = raw.replace(/\r\n/g, "\n").split("\n");
    let paragraph = [];
    let list = [];
    let code = [];
    let inCode = false;

    function flushParagraph() {
        if (!paragraph.length) return;
        blocks.push(`<p>${inlineMarkdown(paragraph.join(" "))}</p>`);
        paragraph = [];
    }

    function flushList() {
        if (!list.length) return;
        blocks.push(`<ul>${list.map((item) => `<li>${inlineMarkdown(item)}</li>`).join("")}</ul>`);
        list = [];
    }

    function flushCode() {
        if (!code.length) return;
        blocks.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`);
        code = [];
    }

    for (const line of lines) {
        const fence = line.match(/^```/);
        if (fence) {
            if (inCode) {
                flushCode();
                inCode = false;
            } else {
                flushParagraph();
                flushList();
                inCode = true;
            }
            continue;
        }

        if (inCode) {
            code.push(line);
            continue;
        }

        if (!line.trim()) {
            flushParagraph();
            flushList();
            continue;
        }

        const heading = line.match(/^(#{1,4})\s+(.+)$/);
        if (heading) {
            flushParagraph();
            flushList();
            const level = Math.min(heading[1].length + 2, 6);
            blocks.push(`<h${level}>${inlineMarkdown(heading[2])}</h${level}>`);
            continue;
        }

        const bullet = line.match(/^\s*[-*]\s+(?:\[[ xX]\]\s*)?(.+)$/);
        if (bullet) {
            flushParagraph();
            list.push(bullet[1]);
            continue;
        }

        const quote = line.match(/^>\s*(.+)$/);
        if (quote) {
            flushParagraph();
            flushList();
            blocks.push(`<blockquote>${inlineMarkdown(quote[1])}</blockquote>`);
            continue;
        }

        paragraph.push(line.trim());
    }

    flushParagraph();
    flushList();
    flushCode();
    return `<div class="markdown">${blocks.join("")}</div>`;
}

function inlineMarkdown(value) {
    let html = escapeHtml(value);
    html = html.replace(/`([^`]+)`/g, "<code>$1</code>");
    html = html.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    html = html.replace(/\[\[([^\]|]+)\|([^\]]+)\]\]/g, "$2");
    html = html.replace(/\[\[([^\]]+)\]\]/g, "$1");
    html = html.replace(/\[([^\]]+)\]\([^)]+\)/g, "$1");
    return html;
}

function escapeHtml(value) {
    return String(value ?? "")
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#39;");
}

async function startServer(instanceId) {
    const server = createServer(async (req, res) => {
        const entry = servers.get(instanceId);
        if (!entry) {
            res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
            res.end("Canvas instance closed.");
            return;
        }

        if (req.method === "POST" && req.url === "/refresh") {
            entry.context = await loadContext(entry.input);
            res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
            res.end(JSON.stringify({ ok: true, ...taskCounts(entry.context) }));
            return;
        }

        res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
        res.end(renderHtml(entry.context));
    });

    await new Promise((resolveListen) => server.listen(0, "127.0.0.1", resolveListen));
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    return { server, url: `http://127.0.0.1:${port}/` };
}

async function openTaskCanvas(ctx) {
    const input = normalizeInput(ctx.input);
    let entry = servers.get(ctx.instanceId);
    if (!entry) {
        entry = { input, context: await loadContext(input), server: null, url: null };
        const server = await startServer(ctx.instanceId);
        entry.server = server.server;
        entry.url = server.url;
        servers.set(ctx.instanceId, entry);
    } else {
        entry.input = input;
        entry.context = await loadContext(input);
    }

    return {
        title: entry.context.task?.title ?? `Glasswork task: ${input.task_id}`,
        status: entry.context.error ? entry.context.message : `Loaded ${input.task_id}`,
        url: entry.url,
    };
}

async function refreshTaskCanvas(ctx) {
    const entry = servers.get(ctx.instanceId);
    if (!entry) {
        throw new CanvasError("instance_not_open", `Canvas instance '${ctx.instanceId}' is not open.`);
    }

    if (ctx.input?.task_id || Number.isInteger(ctx.input?.depth)) {
        entry.input = normalizeInput({
            task_id: ctx.input?.task_id ?? entry.input.task_id,
            depth: Number.isInteger(ctx.input?.depth) ? ctx.input.depth : entry.input.depth,
        });
    }

    entry.context = await loadContext(entry.input);
    return {
        input: entry.input,
        loaded_at: entry.context.loaded_at,
        error: entry.context.error,
        message: entry.context.message,
        task: entry.context.task,
        ...taskCounts(entry.context),
        context: entry.context,
    };
}

await joinSession({
    canvases: [
        createCanvas({
            id: CANVAS_ID,
            displayName: "Glasswork task",
            description: "Read-only full Glasswork task viewer with Description, Notes, inline subtasks, artifacts, child tasks, and backlinks.",
            inputSchema,
            actions: [
                {
                    name: "refresh",
                    description: "Reload the opened Glasswork task from the vault and return its current context bundle.",
                    inputSchema: refreshInputSchema,
                    handler: refreshTaskCanvas,
                },
            ],
            open: openTaskCanvas,
            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (!entry) return;
                servers.delete(ctx.instanceId);
                if (entry.server) {
                    await new Promise((resolveClose) => entry.server.close(() => resolveClose()));
                }
            },
        }),
    ],
});
