// PROTOTYPE ONLY -- Wayfinder ticket #372. Thin Tauri IPC layer over the
// bounded `glasswork_core_spike` Rust Core. Owns no domain logic itself:
// every command below is a direct call into the Core crate, mirroring how
// `Glasswork.App`'s service locator composes `Glasswork.Core` today.

use glasswork_core_spike::{
    artifact, obsidian_uri, parser, self_write::SelfWriteCoordinator, vault, watcher, GlassworkTask,
    TaskView,
};
use serde::Serialize;
use std::path::PathBuf;
use std::sync::Mutex;
use tauri::{Emitter, Manager, State};
use tauri_plugin_opener::OpenerExt;

struct AppState {
    vault_dir: PathBuf,
    coordinator: SelfWriteCoordinator,
    // Keeps the OS watch handle alive for the app's lifetime.
    _watch_handle: Mutex<Option<watcher::WatchHandle>>,
}

fn vault_dir() -> PathBuf {
    // Fixture Vault is checked into this disposable spike (see #370's fixed
    // 3-task fixture). Resolved relative to the Tauri executable's CWD in dev
    // (`src-tauri/`), so we walk up one level to the spike root.
    let candidate = PathBuf::from("../fixture-vault");
    if candidate.exists() {
        return candidate;
    }
    PathBuf::from("fixture-vault")
}

fn task_file_path(state: &AppState, task_id: &str) -> PathBuf {
    state.vault_dir.join(format!("{task_id}.md"))
}

fn load_task(state: &AppState, task_id: &str) -> Result<GlassworkTask, String> {
    let path = task_file_path(state, task_id);
    let content = std::fs::read_to_string(&path).map_err(|e| e.to_string())?;
    parser::parse(&content).map_err(|e| e.to_string())
}

/// Serialize a task as the Presentation-facing payload, i.e. the underlying
/// fields *plus* the Core's row-form derivations. Every command that hands a
/// task to the frontend goes through here, so the UI never has to (and never
/// gets the chance to) recompute domain rules itself.
fn view_of(task: &GlassworkTask) -> Result<serde_json::Value, String> {
    serde_json::to_value(TaskView::of(task)).map_err(|e| e.to_string())
}

fn write_task(state: &AppState, task: &GlassworkTask) -> Result<(), String> {
    let path = task_file_path(state, &task.id);
    state.coordinator.mark_self_write(&path);
    std::fs::write(&path, parser::serialize(task)).map_err(|e| e.to_string())
}

#[tauri::command]
fn load_tasks(state: State<AppState>) -> Result<Vec<serde_json::Value>, String> {
    let tasks = vault::load_all(&state.vault_dir).map_err(|e| e.to_string())?;
    tasks.iter().map(view_of).collect()
}

#[tauri::command]
fn toggle_subtask(state: State<AppState>, task_id: String, index: usize) -> Result<serde_json::Value, String> {
    let mut task = load_task(&state, &task_id)?;
    let sub = task
        .subtasks
        .get_mut(index)
        .ok_or_else(|| "subtask index out of range".to_string())?;
    // Circle glyph toggles done (ADR 0004 hit-zone split): flip the checkbox,
    // and if a rich `status` is present it becomes the source of truth so it
    // must flip too (done <-> todo), matching SubTask.IsEffectivelyDone.
    sub.is_completed = !sub.is_completed;
    if sub.status.is_some() {
        sub.status = Some(if sub.is_completed { "done".into() } else { "todo".into() });
    }
    write_task(&state, &task)?;
    view_of(&task)
}

#[tauri::command]
fn reorder_subtasks(state: State<AppState>, task_id: String, new_order: Vec<usize>) -> Result<serde_json::Value, String> {
    let mut task = load_task(&state, &task_id)?;
    if new_order.len() != task.subtasks.len() {
        return Err("reorder length mismatch".into());
    }
    let mut reordered = Vec::with_capacity(task.subtasks.len());
    for &i in &new_order {
        reordered.push(
            task.subtasks
                .get(i)
                .cloned()
                .ok_or_else(|| "reorder index out of range".to_string())?,
        );
    }
    task.subtasks = reordered;
    write_task(&state, &task)?;
    view_of(&task)
}

#[derive(Serialize)]
struct ArtifactPayload {
    kind: String,
    content: String,
    csp: Option<String>,
}

#[tauri::command]
fn read_artifact(state: State<AppState>, task_id: String, filename: String) -> Result<ArtifactPayload, String> {
    // `filename` comes from the frontend, so the Vault root is a trust
    // boundary here. This is about to *read* the file, so it needs the
    // filesystem-level check: the lexical one alone cannot see through a
    // symlink that sits inside the Vault but points outside it.
    let path = vault::canonical_contained(&state.vault_dir, &format!("{task_id}.artifacts"), &filename)
        .ok_or_else(|| {
            format!("Refusing to read '{filename}': it does not resolve to a real file inside the Vault root.")
        })?;
    // Read from one opened handle rather than re-resolving the path:
    // `read_to_string(&path)` would resolve the path a second time, widening
    // the window between the containment check and the read.
    //
    // Residual risk, stated plainly rather than papered over: a check-then-open
    // race still exists in principle (an attacker swapping a component of the
    // canonical path between `canonicalize` and `open`). Closing it fully needs
    // openat/O_NOFOLLOW, which std does not expose portably. The attacker model
    // it requires -- local write access inside the user's Vault -- already
    // implies they can put hostile content directly in an Artifact, so the
    // marginal gain is small for a disposable prototype. Flagged here so a real
    // port makes the call deliberately.
    let mut file = std::fs::File::open(&path).map_err(|e| e.to_string())?;
    let mut content = String::new();
    std::io::Read::read_to_string(&mut file, &mut content).map_err(|e| e.to_string())?;
    let kind = artifact::classify_kind(&filename);
    let (kind_str, csp) = match kind {
        artifact::ArtifactKind::Markdown => ("Markdown".to_string(), None),
        artifact::ArtifactKind::Html => ("Html".to_string(), Some(artifact::sandbox_csp().to_string())),
        artifact::ArtifactKind::Other => ("Other".to_string(), None),
    };
    Ok(ArtifactPayload { kind: kind_str, content, csp })
}

/// Native launch (ADR-required real OS API, not a browser mock): deep-links
/// into Obsidian via `obsidian://open?vault=...&file=...`, mirroring
/// production's `ObsidianUriBuilder.ForVaultRelativePath`.
///
/// Takes a **Vault-relative path**, not a task id, so it can open an Artifact
/// (`<task>.artifacts/<file>`) as well as a task file. An earlier version
/// took only the task id, which meant the Artifact row's "Open externally"
/// button silently opened the parent task instead of the Artifact.
///
/// There is deliberately **no** default-handler fallback. ADR 0006 rejects
/// handing raw Vault files to whatever the OS has registered — that can open
/// an arbitrary editor against the user's real notes, which is a data-loss
/// risk, and it silently masks a broken deep link instead of surfacing it.
/// If a well-formed `obsidian://` URI can't be built — including when the
/// path escapes the Vault root — this fails loudly and the UI says so.
#[tauri::command]
fn open_in_obsidian(
    state: State<AppState>,
    vault_relative_path: String,
    app: tauri::AppHandle,
) -> Result<(), String> {
    let vault_root = state
        .vault_dir
        .canonicalize()
        .unwrap_or_else(|_| state.vault_dir.clone());

    let uri = obsidian_uri::for_vault_relative_path(
        &vault_root.to_string_lossy(),
        &vault_relative_path,
    )
    .ok_or_else(|| {
        format!(
            "Could not build an obsidian:// deep link for '{vault_relative_path}' \
             (it does not resolve inside the Vault root). Refusing to fall back to \
             the OS default handler -- see ADR 0006."
        )
    })?;

    app.opener().open_url(uri, None::<String>).map_err(|e| e.to_string())
}

/// Launch an external link found in untrusted Vault markdown.
///
/// ADR 0006 routes such links through one policy and, when allowed, opens
/// them as **URLs**. An earlier version invoked the Obsidian deep-link
/// command with the current task instead, so clicking an `https://` link in
/// an Artifact opened the task in Obsidian rather than the link.
#[tauri::command]
fn open_external_url(url: String, app: tauri::AppHandle) -> Result<(), String> {
    if !artifact::is_allowed_external_url(&url) {
        return Err(format!(
            "Refusing to launch '{url}': only http/https links are allowed (ArtifactLinkPolicy)."
        ));
    }
    app.opener().open_url(url, None::<String>).map_err(|e| e.to_string())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_wdio::init())
        .plugin(tauri_plugin_wdio_webdriver::init())
        .setup(|app| {
            let vdir = vault_dir();
            let (watch_handle, rx) = watcher::watch(&vdir).expect("fixture vault watch starts");

            let state = AppState {
                vault_dir: vdir.clone(),
                coordinator: SelfWriteCoordinator::default(),
                _watch_handle: Mutex::new(Some(watch_handle)),
            };
            app.manage(state);

            // Variant B (Deliberate Adaptation, accepted design brief from #370):
            // native traffic lights are handled by titleBarStyle "Overlay" in
            // tauri.conf.json; here we add the subtle platform material tint
            // under the shared layout -- vibrancy on macOS, Mica-like tint on
            // Windows. Both use real platform APIs via `window-vibrancy`, not a
            // CSS approximation.
            #[cfg(target_os = "macos")]
            {
                use window_vibrancy::{apply_vibrancy, NSVisualEffectMaterial};
                let window = app.get_webview_window("main").unwrap();
                apply_vibrancy(&window, NSVisualEffectMaterial::Sidebar, None, None)
                    .expect("macOS vibrancy applies (window-vibrancy requires macOS 10.14+)");
            }
            #[cfg(target_os = "windows")]
            {
                use window_vibrancy::apply_mica;
                let window = app.get_webview_window("main").unwrap();
                apply_mica(&window, None)
                    .expect("Windows Mica applies (window-vibrancy requires Windows 11 22H2+; falls back gracefully on older Windows per its own docs)");
            }

            let handle = app.handle().clone();
            std::thread::spawn(move || {
                for change in rx {
                    let state: State<AppState> = handle.state();
                    if state.coordinator.should_suppress(&change.path) {
                        continue;
                    }
                    if let Some(stem) = change.path.file_stem().and_then(|s| s.to_str()) {
                        if let Ok(task) = load_task(&state, stem) {
                            if let Ok(payload) = view_of(&task) {
                                let _ = handle.emit("task-changed", payload);
                            }
                        }
                    }
                }
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            load_tasks,
            toggle_subtask,
            reorder_subtasks,
            read_artifact,
            open_in_obsidian,
            open_external_url
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
