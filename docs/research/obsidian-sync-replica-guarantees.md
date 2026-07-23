# Obsidian Sync replica guarantees

Date: 2026-07-23

## Question

What guarantees and failure modes do official Obsidian Sync and Obsidian
Headless provide for simultaneous local desktop replicas plus a hosted replica,
and what conflict, recovery, and single-writer constraints must Glasswork
respect?

## Answer

Obsidian Sync provides file-level eventual replication, not a transactional
shared filesystem. It documents no cross-file atomicity, ordering guarantee,
latency SLA, or safe concurrent-write protocol for application data.

Markdown conflicts are automatically merged with a text-diff algorithm by
default, or can produce a `Conflicted copy` file when that per-device setting is
selected. The merge is not YAML-aware and can duplicate text or damage
formatting. Non-Markdown files use last-modified-wins, which can silently discard
one concurrent edit. Obsidian Headless uses the same Sync model, remains in open
beta, and must not run alongside desktop Sync over the same local folder.

Glasswork must therefore treat concurrent writes to the same Task file on two
replicas as unsafe. The current `SelfWriteCoordinator` does not solve this
across machines: its marker lives under the dot-prefixed `.glasswork` folder,
which Obsidian Sync excludes.

## Evidence classification

- **Documented** means Obsidian explicitly describes the behavior in its Help
  or official Headless repository.
- **Implementation evidence** means the behavior appears in the official
  Headless changelog but is not a compatibility promise.
- **Unknown** means Glasswork must not rely on the behavior.
- **Glasswork** identifies an existing repository invariant affected by Sync.

## Replica and synchronization model

**Documented:** Each device has a full local Vault. A remote Vault is the
central encrypted copy through which local Vaults independently exchange
file-level changes. Sync only transfers modified files; it does not expose
folder-level locks or transactions.[1]

**Documented:** Desktop Sync operates only while Obsidian is running. Headless
operates only while `ob sync` or `ob sync --continuous` is running.[2][3]
`ob sync --continuous` watches the local directory, while `ob sync` performs a
one-shot synchronization.[3][4]

**Documented:** The official Headless client is open beta. Obsidian explicitly
warns not to run desktop Sync and Headless Sync over the same local folder,
because doing so can cause conflicts.[3][4]

**Implementation evidence:** The Headless changelog records fixes for a
download race that froze synchronization, a WebSocket that could remain stuck,
filter changes that could delete instead of download remote files, and locking
failures on filesystems with floating-point modification times.[5] Glasswork
must supervise and health-check the process rather than assuming continuous
progress.

## Offline and concurrent changes

**Documented:** Local Vaults remain normal readable and writable folders while
offline. Obsidian warns that conflicts become more likely as offline duration
and the number of unsynchronized changes increase.[6][7]

**Documented:** A conflict occurs when the same file changes on multiple
devices before synchronization completes.[7]

- Markdown uses Google's `diff-match-patch` text algorithm.
- Other file types, including binary Artifacts, use last-modified-wins.
- JSON settings merge keys, applying local values over remote values.

The default Markdown strategy can duplicate text or damage formatting.
Glasswork frontmatter has no special protection. Starting with Obsidian 1.9.7,
a device may instead create a conflict file named like
`name (Conflicted copy device YYYYMMDDHHMM).md`; the original retains the
remote version and the conflict file retains local changes.[7] Conflict mode is
configured independently on every device and is not synchronized.[8]

**Documented:** A newly created local note can be replaced without merging when
Sync downloads a remote file with the same name within a short window. Obsidian
documents local File Recovery as the recovery path.[9] Agent- or MCP-created
Task files can encounter the same class of race.

## Ordering and atomicity

**Unknown:** Obsidian documents no latency SLA, cross-file ordering, or
transactional unit. A replica can therefore observe related file changes in a
different order from that in which the origin wrote them.

**Unknown:** Obsidian does not document whether its client exposes partially
written files during replacement.

**Glasswork:** `VaultService.Save` currently uses `File.WriteAllText`, whereas
`SelfWriteCoordinator` uses temp-file replacement and ADR 0015 requires
temp-file-then-rename for Artifact writers. Task saves should become atomic
before adding another active replica writer.

## Deletes, history, and recovery

**Documented:** Sync provides separate deleted-file recovery and version
history. Note history is retained for one month on Standard and twelve months
on Plus; attachment history is two weeks on either plan.[10][11]

**Documented:** Changing a remote Vault's Sync region deletes and re-uploads
the remote data and discards version history.[12] Moving the Headless process
between physical hosts while retaining the same remote Vault and region is not
a region migration.

## Exclusions and Glasswork self-write tracking

**Documented:** Dot-prefixed files and folders are excluded except for the
Vault's `.obsidian` configuration folder.[8]

**Glasswork:** `SelfWriteCoordinator` stores its cross-process marker at
`<vault>/.glasswork/recent-writes.json`. That marker never crosses replicas.
It coordinates processes sharing one local Vault, not processes on different
machines. Cross-replica reconciliation must rely on task revisions/state and
`IndexService.Rehydrate`, not the marker.

## Artifact and storage limits

**Documented:** Standard allows a 5 MB maximum file and 1 GB total storage.
Plus allows a 200 MB maximum file and plan-dependent total storage from
10–100 GB.[10][11] Attachments retain only two weeks of history. Glasswork
must validate Artifacts against the active plan's per-file cap and monitor
total storage because exceeding limits can pause synchronization.

## Encryption and credentials

**Documented:** End-to-end encryption uses AES-256-GCM and a scrypt-derived
key. Obsidian cannot recover a lost end-to-end-encryption password.[13]
Headless supports the same encryption model and accepts the password during
remote-Vault creation or setup.[3][4]

The hosted deployment must treat account credentials and the encryption
password as secrets outside the Vault and backup.

## Process and watcher behavior

**Unknown:** Headless documents no daemon supervision, liveness guarantee, or
push notification when synchronization stops. `ob sync-status --json` is the
documented state-inspection surface.[4]

The hosted stack must restart failed processes and poll status. A process can be
alive yet unhealthy, given the historical stuck-WebSocket and download-freeze
bugs.[5]

**Glasswork:** A Headless catch-up can create a burst of filesystem events.
`FileWatcherService` already exposes buffer overflow and `IndexService.Rehydrate`
already performs version-counter-based reconciliation. Full rehydration remains
the safe response to missed events.

## Required Glasswork constraints

1. Do not permit uncoordinated free-form edits to the same Task file from
   multiple replicas.
2. Keep phone v1 commands narrow and idempotent: status/completion and My Day
   changes only.
3. Attach an expected revision or premise to every hosted command. Reject or
   reconcile stale commands instead of blindly overwriting the current Task.
4. Make Task writes atomic before enabling hosted writes.
5. Never use `SelfWriteCoordinator` as a cross-machine protocol.
6. Detect conflict-copy files and surface them as an explicit error requiring
   reconciliation; do not index them as ordinary Tasks.
7. Configure conflict behavior deliberately on every replica because the
   setting does not synchronize.
8. Never run desktop Sync and Headless Sync over the same local folder.
9. Supervise Headless, poll `ob sync-status --json`, and expose stale-sync
   health to the PWA.
10. Validate Artifact size against the selected Sync plan.

These constraints reduce risk but do not create a distributed lock. The later
write-consistency decision must specify how a desktop and hosted service avoid
simultaneously mutating the same Task revision.

## Migration and host cutover

1. Back up the full Vault before changing sync systems.[3][14]
2. Move the Vault out of OneDrive before enabling Obsidian Sync. Obsidian warns
   against simultaneous third-party sync and files-on-demand placeholders.[8][14]
3. Seed and verify one desktop replica before adding more replicas.
4. Create the hosted replica with Headless only, assign a unique device name,
   and supervise `ob sync --continuous`.
5. For NAS-to-Mac-Mini/MS-01 cutover, stop Glasswork writes and Headless on the
   old host before starting the new host. Reuse the remote Vault and region.
6. Verify a fully synchronized state and Glasswork rehydration before restoring
   phone commands.

## Remaining unknowns

- No documented webhook or push event announces remote synchronization
  completion; current Headless integration appears to require polling.
- Behavior when two physical hosts reuse one Headless device name is
  undocumented.
- Headless uses local SQLite-backed state, but its schema, backup requirements,
  and restoration behavior are undocumented.

The last two unknowns should be tested before the host-migration runbook is
considered implementation-ready.

## Primary sources

1. [Local and remote Vaults](https://obsidian.md/help/sync/vault-types)
2. [Obsidian Sync FAQ](https://obsidian.md/help/sync/faq)
3. [Headless Sync](https://obsidian.md/help/sync/headless)
4. [Official Obsidian Headless README](https://github.com/obsidianmd/obsidian-headless/blob/dafd2c7226d2635c25a005489abf4e7a6680b08d/README.md)
5. [Official Obsidian Headless changelog](https://github.com/obsidianmd/obsidian-headless/blob/dafd2c7226d2635c25a005489abf4e7a6680b08d/CHANGELOG.md)
6. [Sync status messages](https://obsidian.md/help/sync/messages)
7. [Troubleshoot Obsidian Sync](https://obsidian.md/help/sync/troubleshoot)
8. [Sync settings and selective syncing](https://obsidian.md/help/sync/settings)
9. [Troubleshoot: Sync deleted a newly created note](https://obsidian.md/help/sync/troubleshoot)
10. [Version history](https://obsidian.md/help/sync/version-history)
11. [Sync plans and storage limits](https://obsidian.md/help/sync/plans)
12. [Sync regions](https://obsidian.md/help/sync/region)
13. [Sync security and privacy](https://obsidian.md/help/sync/security)
14. [Switch to Obsidian Sync](https://obsidian.md/help/sync/switch)

## Glasswork references

- `src/Glasswork.Core/Services/VaultService.cs`
- `src/Glasswork.Core/Services/SelfWriteCoordinator.cs`
- `src/Glasswork.Core/Services/FileWatcherService.cs`
- `src/Glasswork.Core/Services/IndexService.cs`
- `docs/adr/0010-index-in-memory-aggregate.md`
- `docs/adr/0015-multi-format-artifacts.md`
