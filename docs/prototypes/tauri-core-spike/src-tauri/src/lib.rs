// PROTOTYPE ONLY -- Wayfinder ticket #372. Thin Tauri IPC layer over the
// bounded `glasswork_core_spike` Rust Core. Owns no domain logic itself:
// every command below is a direct call into the Core crate, mirroring how
// `Glasswork.App`'s service locator composes `Glasswork.Core` today.

use glasswork_core_spike::{artifact, obsidian_uri, parser, self_write::SelfWriteCoordinator, vault, watcher, GlassworkTask};
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

fn write_task(state: &AppState, task: &GlassworkTask) -> Result<(), String> {
    let path = task_file_path(state, &task.id);
    state.coordinator.mark_self_write(&path);
    std::fs::write(&path, parser::serialize(task)).map_err(|e| e.to_string())
}

#[tauri::command]
fn load_tasks(state: State<AppState>) -> Result<Vec<GlassworkTask>, String> {
    vault::load_all(&state.vault_dir).map_err(|e| e.to_string())
}

#[tauri::command]
fn toggle_subtask(state: State<AppState>, task_id: String, index: usize) -> Result<GlassworkTask, String> {
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
    Ok(task)
}

#[tauri::command]
fn reorder_subtasks(state: State<AppState>, task_id: String, new_order: Vec<usize>) -> Result<GlassworkTask, String> {
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
    Ok(task)
}

#[derive(Serialize)]
struct ArtifactPayload {
    kind: String,
    content: String,
    csp: Option<String>,
}

#[tauri::command]
fn read_artifact(state: State<AppState>, task_id: String, filename: String) -> Result<ArtifactPayload, String> {
    let path = state.vault_dir.join(format!("{task_id}.artifacts")).join(&filename);
    let content = std::fs::read_to_string(&path).map_err(|e| e.to_string())?;
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
/// production's `ObsidianUriBuilder.ForVaultRelativePath`. Falls back to
/// opening the raw file with the OS default handler only if a well-formed
/// deep link can't be constructed (e.g. the task file somehow resolves
/// outside the vault root).
#[tauri::command]
fn open_in_obsidian(state: State<AppState>, task_id: String, app: tauri::AppHandle) -> Result<(), String> {
    let vault_root = state
        .vault_dir
        .canonicalize()
        .unwrap_or_else(|_| state.vault_dir.clone());
    let vault_relative = format!("{task_id}.md");

    if let Some(uri) =
        obsidian_uri::for_vault_relative_path(&vault_root.to_string_lossy(), &vault_relative)
    {
        return app.opener().open_url(uri, None::<String>).map_err(|e| e.to_string());
    }

    let path = task_file_path(&state, &task_id);
    let path_str = path
        .canonicalize()
        .unwrap_or(path)
        .to_string_lossy()
        .to_string();
    app.opener()
        .open_path(path_str, None::<String>)
        .map_err(|e| e.to_string())
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
                            let _ = handle.emit("task-changed", &task);
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
            open_in_obsidian
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
