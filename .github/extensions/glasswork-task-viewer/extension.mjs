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
        task_id: { type: "string", minLength: 1, description: "Optional Glasswork task ID to view." },
    },
    additionalProperties: false,
};

const actionSchema = {
    type: "object",
    properties: { task_id: { type: "string", minLength: 1 } },
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

function canvasUrl(host, taskId) {
    const url = new URL(host.url);
    url.pathname = "/canvas";
    url.searchParams.set("token", host.token);
    if (taskId) url.searchParams.set("task_id", taskId);
    return url.toString();
}

async function open(ctx) {
    const host = startHost(ctx.sessionId);
    await host.ready;
    const taskId = typeof ctx.input?.task_id === "string" ? ctx.input.task_id.trim() : "";
    return {
        title: taskId ? `Glasswork task: ${taskId}` : "Glasswork task",
        status: taskId ? `Loading ${taskId}` : "Choose a task to view",
        url: canvasUrl(host, taskId),
    };
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

const session = await joinSession({
    canvases: [
        createCanvas({
            id: CANVAS_ID,
            displayName: "Glasswork task",
            description: "Read-only Task detail backed by the shared Glasswork projection.",
            inputSchema,
            actions: [{ name: "refresh", description: "Reload the selected Task from the shared projection.", inputSchema: actionSchema, handler: refresh }],
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
