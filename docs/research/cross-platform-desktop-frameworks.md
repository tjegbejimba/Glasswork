# Cross-platform desktop framework research

Date: 2026-07-22

## Question

What is the best way to make Glasswork a first-class local application on
macOS while preserving its Windows experience?

This comparison optimizes for Glasswork's actual constraints rather than
framework popularity alone:

- `Glasswork.Core` is already portable .NET 10 and owns the task model, vault
  parsing, indexing, and file-watching behavior.
- The current presentation is WinUI 3 with about 10 pages/dialogs and 5 custom
  XAML controls.
- The Vault is an arbitrary user-selected Obsidian folder. Glasswork must read,
  write, and continuously watch it.
- Artifact rendering includes Markdown, images, text, and untrusted HTML.
- The target is an offline-first desktop application for Windows and macOS.

## Agreed direction

The architecture discussion concluded with these decisions:

- **Scope:** v1 uses a dedicated personal Vault. Work-Vault access is a future
  policy and security decision.
- **Desktop:** Windows and macOS remain local-first. Each desktop app opens its
  local Obsidian-Synced Vault and performs reads and writes locally.
- **Phone:** the first phone client is a Tailscale-only PWA. It talks to a
  hosted Glasswork service and may only complete/reopen Tasks and Subtasks,
  change status, and add/remove from My Day. Prose editing, reordering, and
  deletion remain desktop-only in v1.
- **Hosting:** the service and an Obsidian Headless Vault replica initially run
  as a portable container stack on the Synology NAS. The same stack later
  migrates to the Mac Mini or MS-01 without changing the application
  architecture. Obsidian Sync replaces OneDrive as the Vault transport.
- **iOS:** a native TestFlight client is deferred. Apple Developer membership
  is per account, not per app, so multiple personal applications can share the
  membership when native distribution becomes worthwhile.
- **Framework spike:** compare Tauri 2 with a bounded Rust port of the vertical
  slice against Avalonia and Uno using the existing C# Core. Rust must earn a
  full Core rewrite through measured results.
- **Design:** create a fresh shared design system with deliberate macOS and
  Windows adaptations rather than reproducing the current WinUI appearance.
- **Planner:** reserve navigation and responsive-layout space for the preferred
  capacity-first Planner direction, but do not implement Planner or calendar
  integration in this spike. The separate Planner investigation remains the
  source of truth until it settles.

Implementation is intentionally deferred until the Planner investigation is
complete.

## Recommendation

**Prototype Tauri 2, Avalonia, and Uno Platform against the same vertical
slice.** The user's clarified priority order is:

1. A polished, platform-appropriate Windows and macOS experience.
2. Fast startup and interaction.
3. Long-term maintainability.
4. A Tailscale-only PWA soon after desktop and native iOS later.
5. Migration cost is acceptable.

That order makes Tauri a first-class candidate rather than a conditional one.
Its HTML/CSS frontend offers the most per-platform visual freedom, the same
frontend can become the PWA, and Tauri uses the operating system WebView rather
than bundling Chromium.[8][9][12] Windows is a fully supported Rust and Tauri
target.

Avalonia remains the lowest-risk candidate. It runs `Glasswork.Core` in-process,
is desktop-first and unsandboxed by default, has an official WinUI migration
guide, uses WebView2 on Windows and WKWebView on macOS, and offers headless UI
testing.[1][2][3]

Uno remains the single-codebase challenger. Its official migration guidance
claims 99% shared code from WinUI/UWP,[5] while its platform targets cover
desktop, WebAssembly/PWA, and iOS. The prototype must verify that desktop polish
and automated testing are strong enough for Glasswork.

Do **not** rewrite `Glasswork.Core` merely to select a UI framework. Rust offers
native binaries without a garbage collector and strong compile-time
memory/thread safety,[6][7] but Glasswork has no measured Core bottleneck. The
Core contains tested parser round-tripping, targeted byte-preserving edits,
watcher overflow recovery, self-write coordination, indexing, and Artifact
policy. A service/API boundary can expose that behavior to any UI. Reconsider a
rewrite only if the prototype identifies a measured performance or deployment
constraint, or if one-language ownership is worth deliberately revalidating all
of those contracts.

## Shortlist

| Framework | Core reuse | Desktop fit | WinUI migration | Vault/watch fit | Artifact fit | Main risk | Verdict |
|---|---|---:|---:|---:|---:|---|---|
| **Tauri 2** | .NET service/sidecar or Core rewrite | High | Rewrite | High | High | IPC or risky Core rewrite; HTML sandbox is app-owned | **Prototype: visual/PWA leader** |
| **Avalonia** | In-process C# | High | Medium-high | High | High | Drawn controls; some WinUI controls need replacement | **Prototype: desktop/risk leader** |
| **Uno Platform** | In-process C# | Medium-high | Highest | High | High | Skia desktop maturity and desktop UI testing need proof | **Prototype: code-sharing leader** |
| **Electron** | .NET sidecar/IPC | High | Rewrite | High | High | Chromium footprint and a larger security/update surface | Mature but unnecessarily heavy |
| **.NET MAUI / Mac Catalyst** | In-process C# | Medium | Low | Medium-low | High | Catalyst sandbox and mobile-first controls | Reject for this Vault-centric app |
| **Flutter** | Rewrite or C ABI bridge | Medium | None | High | Medium | Dart rewrite; official desktop WebView gap | Reject |
| **React Native** | Platform-specific bridges | Medium | None | High | High | Different Windows/macOS native-module paths | Reject |
| **Slint** | Sidecar or FFI | Medium-high | None | High | Medium-low | Custom Markdown/HTML and desktop operations | Reject |
| **Dioxus Desktop** | Sidecar or FFI | Medium | None | High | High | Thinner updater/security/desktop operations story | Reject |
| **egui** | Sidecar or FFI | Medium | None | High | Low | Immediate-mode, non-native, document UI mismatch | Reject |
| **PWA/local web** | Local server/sidecar | Low | Rewrite | Low | High | Reliable folder watching is not broadly available | Reject |

## Why retaining Core is different from rejecting Rust

Windows runs Rust, and Tauri is now a recommended prototype candidate. The
question is not whether Rust works on Windows; it does. The question is whether
the Rust shell also needs ownership of Glasswork's domain and Vault behavior.
The relevant comparison is:

| Avalonia | Rust/Tauri |
|---|---|
| One process and one runtime | WebView frontend, Rust shell, and .NET sidecar |
| Direct calls into `Glasswork.Core` | Serialized IPC across a process boundary |
| Existing models and MVVM code remain usable | UI rewrite plus a new protocol and lifecycle model |
| Native WebView2/WKWebView available for HTML artifacts[2] | System WebView available |
| Conventional .NET exception/debugging path | Failures can occur in UI, Rust shell, IPC, or sidecar |
| Larger runtime than a pure native Rust binary | Potentially smaller shell, but still ships .NET for Core |

Tauri's security capabilities are valuable: they constrain which frontend
windows can invoke privileged operations and can reduce the impact of a
frontend compromise.[9] They do not eliminate risk in the Rust core, overly
broad scopes, the system WebView, or the .NET sidecar. Glasswork can achieve a
similarly narrow trust boundary in Avalonia by keeping untrusted Artifact HTML
inside a locked-down native WebView and exposing no privileged bridge to it.

Rewriting Core in Rust becomes compelling only if one of these is demonstrated:

- Glasswork intends to replace the C# Core rather than preserve it.
- Measurements reveal startup, memory, indexing, or distribution constraints
  that .NET cannot meet.
- A single Rust-owned service is strategically more important than preserving
  the tested Core.
- A Tauri capability boundary is judged worth the IPC and two-runtime cost.

The PWA requirement establishes a reason to prototype a web frontend and Tauri
shell. It does not by itself establish a reason to rewrite Core.

## Framework details

### Avalonia

Avalonia's official WinUI guide says its XAML dialect, binding system, control
model, and MVVM patterns are familiar, while documenting real differences:
Visual State Manager becomes pseudo-classes, adaptive triggers become container
queries, and controls such as `NavigationView`, `InfoBar`, and `ContentDialog`
need replacement or recomposition.[1] This is a port, not a recompile.

Its official WebView uses WebView2 on Windows and WKWebView on macOS, avoiding
a bundled Chromium runtime.[2] Its headless platform can create controls and
windows without a display and simulate input for automated tests.[3]

Primary risk: Avalonia draws its controls rather than using AppKit/WinUI native
widgets. The Mac app can feel coherent, but it will not automatically inherit
every native macOS behavior. Accessibility and text-control behavior need a
real prototype with VoiceOver before commitment.[10]

### Uno Platform

Uno provides the closest source-level relationship to WinUI. Its official
migration guide directs developers to create an Uno project and transfer the
existing C# and XAML files, claiming 99% shared code in the final codebase.[5]
Uno's WebView2 documentation lists support across Uno targets, using the native
web engine for each platform.[11]

Primary risk: that migration claim must be tested against Glasswork rather than
accepted generically. The decisive spike is one vertical slice containing
navigation, a task card, Markdown, an Artifact HTML preview, drag reorder, a
file-system change, and an automated desktop UI test.

### Tauri 2

Tauri is the strongest Rust-hybrid choice. It uses the operating system WebView
instead of bundling Chromium,[12] supports signed application updates,[13] and
can bundle a .NET executable as a target-specific sidecar.[8] Its capabilities
system constrains frontend access to privileged commands and plugins.[9]

Primary risk: retaining `Glasswork.Core` behind Tauri creates a distributed
local application. Glasswork would need an explicit request/event protocol,
process supervision, startup synchronization, crash recovery, version
negotiation, and packaging for both Rust and .NET binaries. Rewriting Core
removes that two-runtime cost but requires revalidating every Vault and watcher
correctness contract.

### Electron

Electron is mature and popular, but it ships Chromium and Node.js with the app.
Its own security guide emphasizes that Electron code can access the filesystem
and shell, that untrusted content is dangerous, and that applications must keep
Electron current, isolate contexts, sandbox renderers, disable Node integration,
restrict navigation, validate IPC senders, and constrain external launching.[14]
Glasswork would also need the same .NET sidecar architecture as Tauri.

Electron is appropriate when maximum web ecosystem compatibility is worth the
runtime and update surface. Glasswork does not currently need that trade.

### .NET MAUI / Mac Catalyst

MAUI preserves C# Core reuse and uses native platform controls, but its Mac
target is Mac Catalyst. Microsoft's documentation states that Catalyst apps run
inside a sandbox and use entitlements to request access to system resources and
user data.[15] Glasswork's defining operation is persistent access to and
watching of an arbitrary Vault, so this is architectural friction rather than a
minor packaging detail. MAUI also uses a different XAML/control/navigation model
from WinUI, making it a larger UI rewrite than Avalonia or Uno.

## Popularity snapshot

GitHub repository metrics are a rough adoption/visibility signal, not a quality
ranking. Snapshot from the GitHub API on 2026-07-22:

| Repository | Stars | Forks |
|---|---:|---:|
| `flutter/flutter` | 177,928 | 30,750 |
| `electron/electron` | 122,131 | 17,324 |
| `tauri-apps/tauri` | 109,304 | 3,788 |
| `DioxusLabs/dioxus` | 37,803 | 1,778 |
| `AvaloniaUI/Avalonia` | 31,192 | 2,754 |
| `dotnet/maui` | 23,284 | 1,962 |
| `slint-ui/slint` | 23,278 | 933 |
| `unoplatform/uno` | 9,987 | 867 |

The three most-starred options all require rewriting Glasswork's UI in another
language and either rewriting or isolating its C# Core behind IPC. Their
popularity does not offset that architecture cost. Avalonia and Uno are less
popular globally but much better matched to this specific codebase.

## Decision process

Do not begin a full migration yet.

1. Build the same narrow vertical slice in Tauri, Avalonia, and Uno.
2. Include a real Vault read/watch cycle and an untrusted local HTML Artifact,
   not just static task-list UI.
3. Verify native file launching, drag reorder, keyboard navigation, VoiceOver,
   Windows Narrator, and automated desktop UI tests.
4. Compare platform-specific polish, source reuse, cold startup, interaction
   latency, resident memory, package size, accessibility, and automated testing.
5. Require each candidate to demonstrate how the My Day slice reaches the
   Tailscale-only PWA without duplicating domain behavior.
6. Choose the framework from measured spike results, then record the migration
   decision in an ADR.

## Primary sources

1. [Avalonia: Migrating from WinUI/UWP](https://docs.avaloniaui.net/docs/migration/winui/)
2. [Avalonia: Embedding web content](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)
3. [Avalonia: Headless testing](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)
4. [Avalonia repository](https://github.com/AvaloniaUI/Avalonia)
5. [Uno Platform: Migrating a WinUI/UWP application](https://platform.uno/docs/articles/migrating-apps.html)
6. [Rust](https://www.rust-lang.org/)
7. [The Rust Programming Language: Ownership](https://doc.rust-lang.org/book/ch04-00-understanding-ownership.html)
8. [Tauri 2: Embedding external binaries](https://v2.tauri.app/develop/sidecar/)
9. [Tauri 2: Capabilities](https://v2.tauri.app/security/capabilities/)
10. [Avalonia: Accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility)
11. [Uno Platform: WebView2](https://platform.uno/docs/articles/controls/WebView.html)
12. [Tauri 2: Architecture](https://v2.tauri.app/concept/architecture/)
13. [Tauri 2: Updater](https://v2.tauri.app/plugin/updater/)
14. [Electron: Security](https://www.electronjs.org/docs/latest/tutorial/security)
15. [.NET MAUI: Mac Catalyst entitlements](https://learn.microsoft.com/en-us/dotnet/maui/mac-catalyst/entitlements?view=net-maui-10.0)
16. [GitHub REST API: Repositories](https://docs.github.com/en/rest/repos/repos)
