import { randomBytes } from "node:crypto";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";

const CANVAS_ID = "glasswork-task";
const hosts = new Map();
const extensionRoot = dirname(fileURLToPath(import.meta.url));

const inputSchema = {
    type: "object",
    properties: {
        task_id: { type: "string", minLength: 1, description: "Optional Glasswork task ID to load and select. Compatible shorthand for task_ids: [task_id]." },
        task_ids: {
            type: "array",
            items: { type: "string", minLength: 1 },
            description: "Optional canonical Glasswork task IDs to load into the Session Task Set. Merged with task_id when both are present.",
        },
    },
    additionalProperties: false,
};

const actionSchema = {
    type: "object",
    properties: { task_id: { type: "string", minLength: 1 } },
    additionalProperties: false,
};

const taskIdSchema = {
    type: "object",
    required: ["task_id"],
    properties: { task_id: { type: "string", minLength: 1, description: "Canonical Glasswork task ID." } },
    additionalProperties: false,
};

const taskIdsSchema = {
    type: "object",
    required: ["task_ids"],
    properties: {
        task_ids: {
            type: "array",
            items: { type: "string", minLength: 1 },
            minItems: 1,
            description: "Canonical Glasswork task IDs to load into the Session Task Set.",
        },
    },
    additionalProperties: false,
};

const noInputSchema = { type: "object", properties: {}, additionalProperties: false };

const artifactActionSchema = {
    type: "object",
    required: ["task_id", "artifact_name", "operation"],
    properties: {
        task_id: { type: "string", minLength: 1 },
        artifact_name: { type: "string", minLength: 1, description: "Exact Artifact filename from the shared projection." },
        operation: {
            type: "string",
            enum: ["open_externally", "show_in_folder", "open_in_obsidian"],
            description: "Trusted user action. Unsafe file extensions reject open_externally and require show_in_folder.",
        },
    },
    additionalProperties: false,
};

function hostCommand() {
    const configured = process.env.GLASSWORK_CANVAS_HOST;
    if (configured) return { command: configured, args: [] };

    const hostRoot = join(extensionRoot, "host");
    const activeFile = join(hostRoot, "active.txt");
    const activeVersion = existsSync(activeFile) ? readFileSync(activeFile, "utf8").trim() : "";
    const bundledExecutable = join(hostRoot, activeVersion, "Glasswork.CanvasHost.exe");
    if (existsSync(bundledExecutable)) return { command: bundledExecutable, args: [] };

    const bundled = join(hostRoot, activeVersion, "Glasswork.CanvasHost.dll");
    if (existsSync(bundled)) return { command: "dotnet", args: [bundled] };

    throw new CanvasError(
        "host_not_configured",
        "Glasswork.CanvasHost is not installed. Set GLASSWORK_CANVAS_HOST or install the Glasswork canvas bundle.",
    );
}

function startHost(sessionId) {
    const existing = hosts.get(sessionId);
    if (existing) return existing;

    const token = randomBytes(32).toString("base64url");
    const command = hostCommand();
    const child = spawn(command.command, [...command.args, "--session-id", sessionId, "--token", token], {
        cwd: extensionRoot,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
    });
    const host = { child, token, url: null, ready: null };
    hosts.set(sessionId, host);
    host.ready = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("Canvas host did not become ready within 10 seconds.")), 10000);
        child.stdout.setEncoding("utf8");
        let buffer = "";
        child.stdout.on("data", (chunk) => {
            buffer += chunk;
            const lines = buffer.split(/\r?\n/);
            buffer = lines.pop() ?? "";
            for (const line of lines) {
                if (!line.trim()) continue;
                try {
                    const message = JSON.parse(line);
                    if (message.ready && message.url) {
                        clearTimeout(timeout);
                        host.url = message.url;
                        resolve(host);
                    }
                } catch { /* Ignore non-JSON framework log lines. */ }
            }
        });
        child.once("error", (error) => {
            clearTimeout(timeout);
            reject(error);
        });
        child.once("exit", (code) => {
            clearTimeout(timeout);
            if (hosts.get(sessionId) === host) hosts.delete(sessionId);
            if (!host.url) reject(new Error(`Canvas host exited before becoming ready (code ${code ?? "unknown"}).`));
        });
    });
    host.ready.catch(() => {
        if (hosts.get(sessionId) === host) hosts.delete(sessionId);
        if (!child.killed) child.kill();
    });
    return host;
}

function stopHost(sessionId) {
    const host = hosts.get(sessionId);
    if (!host) return;
    hosts.delete(sessionId);
    if (!host.child.killed) host.child.kill();
}

function canvasUrl(host) {
    const url = new URL(host.url);
    url.pathname = "/canvas";
    url.searchParams.set("token", host.token);
    return url.toString();
}

function requestedTaskIds(input) {
    const ids = [];
    const single = typeof input?.task_id === "string" ? input.task_id.trim() : "";
    if (single) ids.push(single);
    if (Array.isArray(input?.task_ids)) {
        for (const id of input.task_ids) {
            const trimmed = typeof id === "string" ? id.trim() : "";
            if (trimmed) ids.push(trimmed);
        }
    }
    return ids;
}

async function callTasksEndpoint(host, path, body) {
    const response = await fetch(`${host.url}${path}?token=${encodeURIComponent(host.token)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body ?? {}),
    });
    const payload = await response.json();
    if (!response.ok) throw new CanvasError(payload.code ?? "host_request_failed", payload.message ?? "Canvas host request failed.");
    return payload;
}

async function open(ctx) {
    const host = startHost(ctx.sessionId);
    await host.ready;
    const taskIds = requestedTaskIds(ctx.input);
    // Explicit load opens/focuses the stable canvas and selects the requested
    // Task; the background refresh poll never calls this, so it can never
    // steal host focus the way an explicit load does.
    if (taskIds.length > 0) await callTasksEndpoint(host, "/api/tasks/load", { taskIds });
    return {
        title: "Glasswork Tasks",
        status: taskIds.length > 0 ? `Loading ${taskIds.join(", ")}` : "Manage your Session Task Set",
        url: canvasUrl(host),
    };
}

async function loadAction(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    const taskIds = requestedTaskIds(ctx.input);
    if (taskIds.length === 0) throw new CanvasError("invalid_input", "Provide task_id or task_ids to load.");
    return callTasksEndpoint(host, "/api/tasks/load", { taskIds });
}

async function unloadAction(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    const taskId = typeof ctx.input?.task_id === "string" ? ctx.input.task_id.trim() : "";
    if (!taskId) throw new CanvasError("invalid_input", "Provide task_id to remove from the canvas.");
    return callTasksEndpoint(host, "/api/tasks/unload", { taskId });
}

async function clearAction(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    return callTasksEndpoint(host, "/api/tasks/clear");
}

async function selectAction(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    const taskId = typeof ctx.input?.task_id === "string" ? ctx.input.task_id.trim() : "";
    if (!taskId) throw new CanvasError("invalid_input", "Provide task_id to select.");
    return callTasksEndpoint(host, "/api/tasks/select", { taskId });
}

async function selectedRefreshAction(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    return callTasksEndpoint(host, "/api/tasks/refresh-selected");
}

async function refreshAllAction(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    return callTasksEndpoint(host, "/api/tasks/refresh-all");
}

async function refresh(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    const taskId = typeof ctx.input?.task_id === "string" ? ctx.input.task_id.trim() : "";
    try {
        const response = await fetch(`${host.url}/api/task?token=${encodeURIComponent(host.token)}${taskId ? `&task_id=${encodeURIComponent(taskId)}` : ""}`);
        const payload = await response.json();
        if (!response.ok) throw new CanvasError(payload.code ?? "host_request_failed", payload.message ?? "Canvas host request failed.");
        return payload;
    } catch (error) {
        if (error instanceof CanvasError) throw error;
        throw new CanvasError("host_unavailable", "The session canvas host is unavailable.");
    }
}

async function artifactAction(ctx) {
    const host = hosts.get(ctx.sessionId);
    if (!host) throw new CanvasError("host_not_running", "The session canvas host is not running.");
    await host.ready;
    const taskId = typeof ctx.input?.task_id === "string" ? ctx.input.task_id.trim() : "";
    const name = typeof ctx.input?.artifact_name === "string" ? ctx.input.artifact_name.trim() : "";
    const operation = typeof ctx.input?.operation === "string" ? ctx.input.operation : "";
    try {
        const response = await fetch(`${host.url}/api/artifact/action?token=${encodeURIComponent(host.token)}`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ taskId, name, operation }),
        });
        const payload = await response.json();
        if (!response.ok) throw new CanvasError(payload.code ?? "artifact_action_failed", payload.message ?? "Artifact action failed.");
        return payload;
    } catch (error) {
        if (error instanceof CanvasError) throw error;
        throw new CanvasError("host_unavailable", "The session canvas host is unavailable.");
    }
}

const session = await joinSession({
    canvases: [
        createCanvas({
            id: CANVAS_ID,
            displayName: "Glasswork Tasks",
            description: "Manage an in-memory Session Task Set of loaded Glasswork Tasks in a responsive master-detail canvas.",
            inputSchema,
            actions: [
                { name: "refresh", description: "Reload the selected Task from the shared projection.", inputSchema: actionSchema, handler: refresh },
                {
                    name: "artifact_action",
                    description: "Open or reveal one projected Artifact under the native external-open safety policy.",
                    inputSchema: artifactActionSchema,
                    handler: artifactAction,
                },
                {
                    name: "load",
                    description: "Load one or several canonical Task IDs into the Session Task Set, selecting the last one that loads successfully.",
                    inputSchema: taskIdsSchema,
                    handler: loadAction,
                },
                {
                    name: "unload",
                    description: "Remove a loaded Task from the Session Task Set. Never mutates the Vault.",
                    inputSchema: taskIdSchema,
                    handler: unloadAction,
                },
                {
                    name: "clear",
                    description: "Empty the Session Task Set back to its guidance state. Never mutates the Vault.",
                    inputSchema: noInputSchema,
                    handler: clearAction,
                },
                {
                    name: "select",
                    description: "Select an already-loaded Task without changing its recency order.",
                    inputSchema: taskIdSchema,
                    handler: selectAction,
                },
                {
                    name: "selected_refresh",
                    description: "Re-read the selected Task from the Vault, refreshing it or marking it unavailable.",
                    inputSchema: noInputSchema,
                    handler: selectedRefreshAction,
                },
                {
                    name: "refresh_all",
                    description: "Re-read every loaded Task from the Vault, preserving order and selection.",
                    inputSchema: noInputSchema,
                    handler: refreshAllAction,
                },
            ],
            open,
            onClose: async () => {},
        }),
    ],
});

function shutdown() {
    for (const sessionId of hosts.keys()) stopHost(sessionId);
}
process.once("exit", shutdown);
process.once("SIGTERM", () => { shutdown(); process.exit(0); });
process.once("SIGINT", () => { shutdown(); process.exit(0); });
