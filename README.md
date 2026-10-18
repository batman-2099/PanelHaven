<img src="web/wwwroot/icon.svg" width="96" height="96" alt="Panel Haven logo: a comic speech bubble divided into three panels">
<img src="web/wwwroot/wordmark.svg" width="352" height="56" alt="Panel Haven">

# Panel Haven

**A self-hosted comic library and browser reader with private progress for every reader.**

Panel Haven is a standalone web replacement for ComicRack, built with ASP.NET Core, SQLite, and native JavaScript/CSS. Keep your comics on your server, browse a shared library, and read from a browser without installing the desktop application.

> [!IMPORTANT]
> Panel Haven is a working port in development, **not full ComicRack feature parity**. Linux has been verified; Windows is a target but has not been verified. Do not replace your desktop database with this application. See the [parity inventory](#full-port-parity-inventory) for remaining work and exclusions.

## Highlights

- Read **CBZ/ZIP, CBR/RAR, CB7/7z, and CBT/TAR** archives with single-page, continuous, and two-page views.
- Resume with private per-user progress, bookmarks, ratings, and reader preferences.
- Organize a shared collection with search, filters, series/folder browsing, manual lists, and smart lists.
- Import embedded ComicInfo/ComicBook metadata and edit library metadata **without modifying source archives**.
- Watch library folders automatically, report exact duplicates, and retain catalog history when files go missing.
- Manage administrator/reader accounts and perform offline backups, restores, and verified root relocation.

**Navigation:** [Quick start](#quick-start) · [Configuration](#configuration) · [Reader controls](#reader-controls) · [Backup and restore](#backup-restore-and-root-relocation) · [Remote access](#https-and-trusted-proxies) · [Safety](#safety-and-operating-limits) · [Development](#development) · [Help](#help-and-contributing) · [License](#license-and-attribution)

## Quick start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Git to clone the repository.
- A server-accessible folder containing supported comic archives and writable storage for application state.
- A modern browser. Node.js is only needed for the JavaScript development check.

The application uses one ASP.NET Core process and SQLite; no separate database server or frontend build is required. Library folders are configured on the server, not selected from the browser's local filesystem.

### Get the source

```bash
git clone https://github.com/batman-2099/PanelHaven.git
cd PanelHaven
```

### Linux (Bash)

Run from the repository root. Replace the library path with your own:

```bash
export PANELHAVEN_ADMIN_USERNAME=admin
read -rsp 'Initial administrator password (12–256 characters): ' PANELHAVEN_ADMIN_PASSWORD; echo
export PANELHAVEN_ADMIN_PASSWORD
export Libraries__0=/absolute/path/to/comics
# Optional additional roots: export Libraries__1=/another/path
# Optional state location: export DataPath=/absolute/path/to/private-state
dotnet run --project web/PanelHaven.Web.csproj --urls http://127.0.0.1:5178
```

Open <http://127.0.0.1:5178> and sign in with the credentials you supplied. Configured folders scan automatically at startup and after changes; **Scan libraries** remains available for an immediate manual scan. No sample books or default credentials are installed.

### Windows (PowerShell; not yet verified)

```powershell
$env:PANELHAVEN_ADMIN_USERNAME = 'admin'
$password = Read-Host 'Initial administrator password (12–256 characters)' -AsSecureString
$env:PANELHAVEN_ADMIN_PASSWORD = [System.Net.NetworkCredential]::new('', $password).Password
$env:Libraries__0 = 'D:\Comics'
dotnet run --project web/PanelHaven.Web.csproj --urls http://127.0.0.1:5178
```

> [!WARNING]
> These commands start a **local-only** server. Before enabling remote access, configure [HTTPS and trusted proxies](#https-and-trusted-proxies) and review the [operating limits](#safety-and-operating-limits). Every authenticated user can read the entire configured library.

## Configuration

App-owned environment variables use `PANELHAVEN_`; the application project is `PanelHaven.Web`. Existing installations must complete the [offline rename migration](#upgrading-an-existing-installation-to-panel-haven) before starting this version. Old names are not accepted as aliases.

Administrator credentials are required only when the accounts database is empty. Subsequent launches use stored password hashes; changing these environment variables does **not** reset an existing password. Remove them from your environment after initialization. Use the account controls below for password changes and administrator resets. Email/self-service recovery is not implemented; retain administrator credentials securely.

Configuration uses ASP.NET Core's standard environment-variable/command-line providers. `Libraries__0`, `Libraries__1`, etc. are server-side paths; browsers cannot supply arbitrary filesystem paths. Relative roots and `DataPath` resolve from the application's content root (`web/` when using the commands above). Default state is `web/data/`, with the database at `DataPath/panelhaven.sqlite` and persistent signing keys in `DataPath/keys`.

## Working behavior

- Password-hashed accounts, database-validated cookie sessions, login/password-change rate limiting, administrator/reader roles, account creation/removal, password changes and administrator resets.
- All authenticated users can read the same configured library after completing any required password change. Only administrators can manually scan, edit shared metadata, inspect maintenance reports, remove confirmed missing catalog entries or manage accounts. Reading position, completion, bookmarks, ratings, lists, list folders and reader preferences are per-user.
- Paginated browsing (24/48/96 books per page), series/publisher/folder stacks with exact drilldown, sorting by series/title/natural issue number/publisher/page count/personal rating, ascending/descending order, publisher/availability/exact-rating filtering, and per-user unread/in-progress/completed filters. Series stacks show private reading-status counts and completed-book percentage for the current filters. Search covers title/series/number/publisher/summary. Opening page zero counts as started; unread means no saved progress row.
- Recursive background scans of CBZ/ZIP, CBR/RAR4/RAR5, CB7/7z and CBT/TAR, including supported solid archives, with progressive counters, cancellation and reconnection after browser reload. Scans run at startup, after debounced filesystem changes and on manual request. Only one scan runs at a time. Already-hashed unchanged archives are skipped using file size and modification time, without opening them. Successful imports are committed individually; cancelling retains them.
- Embedded `ComicInfo.xml` import, preserving unknown fields, attributes and nested page metadata. Embedded `ComicBook.xml` is retained separately. ComicInfo wins when both exist; ComicBook fields are used if ComicInfo is absent. Legacy read position is not assigned to any new user.
- Database-only editing of 42 scalar metadata fields, including creators, dates, ratings, tags and custom values; sparse atomic bulk edits of up to 100 books. Rescans preserve edited metadata. **Source archives are never modified.**
- Private manual reading lists with add/remove/reorder controls, single-level list folders, and smart lists whose rules use current metadata, the owner's reading progress and personal ratings. List membership combines with normal search, sorting, filters and pagination.
- Native lazy-loading generated JPEG thumbnails and a responsive reader with single-page, continuous-scroll and two-page-spread modes; fit-page/fit-width, 50–300% zoom with native scroll/pan, fullscreen, RTL navigation, touch swipes and saved per-user preferences/progress.
- PNG, JPEG, GIF and WebP pages. Unsupported page formats are not counted/displayed. Thumbnails use the first page/frame, respect EXIF orientation, preserve aspect ratio and fit within 320×480 without upscaling. Transparent backgrounds become white.
- Thumbnails are cached under `DataPath/thumbnails`, one atomically replaced file per book. Cache hits validate source location/revision but do not reopen or decode archives. Rescanning a changed archive invalidates its cached thumbnail; removed/unsafe sources cannot serve cached covers.

## Library maintenance

- **Missing versus unavailable:** scans retain catalog entries and mark confirmed absence under an accessible library as missing. Disconnected roots, unsafe paths and invalid sources are unavailable, not proof of deletion. Metadata, progress, bookmarks and list membership remain intact; restoration at the same path retains book identity. Availability reflects scan observations, not a guarantee against a subsequent filesystem change.
- **Explicit removal:** administrators can open **Library maintenance**, choose missing records and confirm **Remove catalog entry**. The server rechecks absence under a safe accessible root and refuses concurrent scans or reappeared/inaccessible sources. Removal permanently deletes that book's shared metadata and every user's progress, bookmarks and list memberships. It never deletes source files. Nothing is automatically removed.
- **Exact duplicates:** the maintenance report groups available archives by SHA-256 of their complete file bytes and displays each location. This is not title, cover or decompressed-page similarity. Hashes are computed on new/changed imports and backfilled once for old records. Missing/unavailable records retain their last validated hash as relocation evidence but never participate in current duplicate reports. Duplicate groups may continue across report pages. Present duplicates have no removal button.
- **Folder watching:** native watchers debounce bursts for roughly two seconds; events during a scan queue one follow-up. Startup reconciliation recovers offline changes. A roughly one-minute reconciliation recreates watchers and recovers missed events, overflow or disconnected roots when scanning is idle. State-directory changes are ignored. Cancelling stops the current burst, not watching permanently; later events or reconciliation can start another scan. Manual scanning remains available.
- **Groups:** select series, publisher or folder to browse paginated stacks, then open a stack and use **Back to groups** to return. Search, reading lists, status, publisher and availability filters apply before counts and pagination. Group keys are exact and case-sensitive; blank metadata gets its own group. Locations use `Library N/relative/path`, where N follows the configured, deduplicated library-root order; absolute server paths are not exposed.


## Reader controls

Mode, fit, zoom, reading direction and the **Cover alone** option are saved per account on the server and restored across sessions/devices. Rotation, brightness, contrast and skipped page types are saved the same way; existing accounts retain their previous settings on upgrade. Fullscreen is temporary and depends on browser support. Preferences, bookmarks and reading progress are saved separately; failed writes expose retry controls and prevent silently leaving with unsaved changes.

- **Single page:** previous/next buttons and page-number jumps. At 100% reader zoom, left/right arrows and dominant horizontal single-finger swipes turn pages, reversed in RTL. Page Up/Down also turn pages in paged modes.
- **Two-page spreads:** fixed logical pairs, optionally preceded by a standalone first-page cover. RTL reverses their visual order. If either member is landscape, both members of that pair are displayed separately; later pairs do not shift. An unmatched final page appears alone. A normal spread records its last sufficiently visible logical page so resuming restores that spread.
- **Continuous:** scroll inside the page area. Nearby pages load with at most three image requests/decodes in flight; distant images are released while placeholders retain their dimensions. Progress follows sufficiently visible, successfully decoded pages—not prefetched images. Page jumps and mode changes preserve the reading position; initial placeholder dimensions are estimated until the image is decoded.
- **Zoom/pan:** zoom is relative to the selected fit mode. Enlarged pages use native horizontal/vertical scrolling. Touch pinch zoom remains native; swipes are disabled during reader/browser zoom, multi-touch and continuous mode to avoid accidental page turns.
- **Fullscreen:** the reader controls and errors remain inside the fullscreen view. Leaving the reader exits fullscreen. Unsupported browsers show a disabled fullscreen control.
- **Rotation and color:** choose 0°, 90°, 180° or 270°; brightness and contrast accept 50–200% (100% is unchanged). Rotation adjusts fitted dimensions and landscape-spread splitting rather than cropping the image. **Reset visuals** restores rotation to 0° and brightness/contrast to 100%, without changing mode, zoom, progress or skip filters.
- **Bookmarks:** expand **Bookmarks and page types**, name the current physical page and choose **Add / update bookmark**. Each account has one named bookmark per page; use its button to jump or **Delete** to remove it. Embedded ComicInfo bookmarks appear separately as read-only jumps. Personal bookmarks survive reloads; bookmarks beyond a shortened archive's current page count are hidden.
- **Page types and skipping:** the reader shows embedded ComicInfo classifications and lets you skip selected types, including covers, advertisements, previews and deleted pages. Missing, unknown or ambiguous metadata stays visible rather than being silently skipped. Explicit valid Page records with no Type use ComicRack's Story default. Physical page numbers do not change; spreads keep their original pairs and omit skipped members.
- **Explicit jumps:** entering a skipped page number or jumping to its bookmark temporarily reveals that page. Previous/Next still follow skip filters. With every page skipped, the reader shows an empty state without updating progress; **Show all page types**, a filter change or an explicit jump restores a view. Completion is recorded when the last included page is actually visible and decoded, not merely skipped or prefetched.

Page errors have individual retry buttons and do not advance saved progress. The page-number field retains an unfinished typed jump while asynchronous images/progress updates arrive.

## Metadata and reading lists

Open book details and choose **Edit metadata**, or select books across library pages and choose **Edit selected metadata**. Only checked fields/custom keys change; unchecked values are preserved. Blank is an explicit empty value; **Remove / reset** removes the field or custom key. Bulk updates are transactional: an invalid value or missing book rejects the whole request. Only administrators may edit shared metadata.

Toolbar bulk edits—including a selection of one book—require **Preview checked changes**, then **Apply reviewed changes**. The preview lists actual before/after values for each book, distinguishing absent/removed fields from explicit empty strings and showing unchanged books. Editing the draft invalidates the preview. If metadata changes elsewhere before applying, the entire request is rejected; your draft remains available for a new preview. Single-book editing from details/report retains its direct save flow. No-op edits do not mark books as edited.

The editor covers scalar ComicInfo fields and ComicBook `CustomValuesStore` custom values, preserving unknown XML, attributes and nested page structures. Custom names are case-insensitive. Literal `&#44;` and `&#61;` strings are rejected because the upstream custom-value encoding cannot represent them unambiguously. Declared metadata page count does not change the reader's actual archive page count. Embedded page structures and legacy progress are not editable through this form.

Administrators can open **Validate metadata** for a read-only, paginated report of missing series/issue numbers, malformed or impossible publication dates, and potential publisher/creator name variations. Issue numbers remain text. Blank/-1 dates are unknown; partial dates are allowed, including February 29 when the year is unknown. Name checks compare case and whitespace within each field; creator entries are comma-separated, with ordinary separator padding ignored. They do not infer identities, correct spelling or change values. Reports analyze current catalog metadata on demand without opening archives or caching stale findings. Use the report's details/edit buttons to inspect a book.

Any signed-in user can select up to 100 books across library pages and choose **Export selected JSON** or **Export selected CSV**. JSON includes scalar/custom values and the stored ComicInfo/ComicBook XML, preserving unknown embedded fields. CSV contains stable scalar columns and a `CustomValues` JSON column, with standard comma/quote/newline escaping. Potential spreadsheet formulas receive a leading apostrophe, including values with leading whitespace/control characters; JSON preserves the original values.

Downloads are named `panelhaven-metadata.json` and `panelhaven-metadata.csv`.

Exports contain no application-account IDs, private ratings/progress or catalog filesystem paths. Raw embedded XML and custom metadata may themselves contain legacy paths or other source-provided information; review exports before sharing. Unknown selected IDs reject the whole export rather than silently dropping books. Neither reports, previews nor exports alter source files; archive write-back remains unimplemented.

Every account may create its own lists. Select books in **All books**, choose a manual list and **Add selected**; select that list to remove members or use **List order** and move buttons. Moves use the full list, including members on other pages or hidden by filters. Deleting a list never deletes books or progress.

Use **Manage list folders** to create, rename or delete private folders, and select a folder when creating/editing a list. List selectors group entries by folder. Folders are single-level; deleting one moves its lists to **Unfiled**, preserving their rules, books and ordering. Existing lists remain unfiled after upgrading.

Smart lists accept 1–20 rules with **All** or **Any** matching. Title, series, number, publisher, summary, writer, artist (ComicInfo Penciller) and genre support contains/equality/inequality. Physical page count, year (0–9999) and personal rating (0–5) support equality/greater/less comparisons; unknown years never match numeric rules. Reading status supports unread/in-progress/completed equality/inequality. Issue numbers are text in rules. Membership updates on the next library load after metadata, scans, progress or rating changes; it is not a copied snapshot. List kind cannot change after creation. Lists are native to this web app; legacy list import remains excluded.

In book details, choose **My private rating** and **Save rating**. Values 1–5 are stars; **0 · Unrated** removes your rating. These ratings never alter shared metadata or another account's rating. Use **My rating** to sort or filter by an exact value, including unrated. Failed saves keep your selection for retry.

Select **Group by → Series** for total books, unread/in-progress/completed counts and a completion bar. Counts use your progress and the current search, list and filters. Completion is the percentage of matching books explicitly completed, rounded down—not an estimate of pages read.

## Account management

- **Change password** requires your current password and a different new password of 12–256 characters. Success ends all your sessions; sign in again with the new password. Attempts are limited to five per minute per account.
- Administrators use **Manage accounts** to list/create accounts, change administrator/reader roles, reset another account's password and delete accounts. A reset ends the target's sessions immediately. Their next login shows only the mandatory password-change screen; library APIs remain blocked until they choose a different password. Administrators change their own password through **Change password**, not the reset control.
- Changing a role ends the affected account's sessions. Saving an unchanged role does not. Demoting or deleting the last administrator is rejected, including concurrent requests. An administrator can demote/delete their own account only when another administrator remains.
- Deleting an account permanently removes its progress, bookmarks, ratings, manual/smart lists, list folders, memberships and reader preferences. Shared comics, metadata, source files and other accounts' private data remain intact. Reusing a deleted username creates a new account without inherited history.

Sessions use the `PanelHaven.Session` cookie and are checked against the account database on every authenticated request. Password changes/resets, role changes and deletion invalidate existing cookies; all sessions from before the Panel Haven rename require a fresh sign-in. There is no email recovery or library-specific access control.

## Backup, restore and root relocation

These are offline operator commands, not browser endpoints. Stop the server first. Server and maintenance processes hold an exclusive sibling `<DataPath>.state.lock`; do not delete that lock file to bypass a running process. Use absolute paths and the same `DataPath` as the server. Source archives are never copied, moved or deleted by these commands.

```bash
# The backup's parent directory must already exist; destination must be new.
dotnet run --project web/PanelHaven.Web.csproj -- --DataPath /private/panelhaven-state --backup /private/backups/panelhaven-before-change

# Restore into a new/empty state directory, never over an existing installation.
dotnet run --project web/PanelHaven.Web.csproj -- --DataPath /private/panelhaven-restored --restore /private/backups/panelhaven-before-change
```

Backups contain a consistent `panelhaven.sqlite` snapshot and the complete cookie-signing key directory, including revocations. Thumbnails regenerate and are excluded, as are transient SQLite sidecars and the process lock. Library paths, TLS configuration and comic files are external to the backup. Backup destinations and restored state are staged and published only after validation; existing destinations, overlaps, unsafe links, malformed key XML and invalid databases are rejected. Restore checks integrity and required schema, not the trustworthiness of an arbitrary third-party database: use only trusted backups from this app version.

On Unix, created state/backup directories are private (`0700`) and persistent files are `0600`. Windows inherits parent-directory ACLs; secure those directories yourself. Backups contain password hashes and signing keys and are not encrypted by the application. Machine/certificate-encrypted keys, if configured externally, still require their original decryption material. Test a restored instance before replacing live state; point `DataPath` at it and supply your library/deployment configuration. Existing accounts and private reading data remain usable.

For a root move, back up first and preview an exact relative-path mapping:

```bash
dotnet run --project web/PanelHaven.Web.csproj -- --DataPath /private/panelhaven-state --Libraries:0 /new/comics --relocate-from /old/comics --relocate-to /new/comics
# After reviewing MAPPED/SKIPPED/CONFLICT output:
dotnet run --project web/PanelHaven.Web.csproj -- --DataPath /private/panelhaven-state --Libraries:0 /new/comics --relocate-from /old/comics --relocate-to /new/comics --apply-relocation true
```

The destination must be a configured safe root. Each file is freshly validated and hashed; it must match the retained historical hash, or—when older catalogs have no hash—the old root must be an explicitly verified symlink to that exact destination root. The latter proves current path correspondence, not historical byte identity. Ordinary scans/readers still reject symlink roots and internal links. No filename-only or fuzzy matching occurs.

Relocation preserves the old book ID. Already-imported destination rows merge only when private progress, ratings, bookmarks, list positions and edited metadata do not conflict; otherwise both rows are retained and reported. Each pair is transactional, so a batch can apply safe pairs while leaving conflicts/skips untouched. Rerunning does not remap already-relocated entries. Review the final counts, not just the process exit code. Restart with the new root after applying; committed imports are skipped on the next scan.

### Upgrading an existing installation to Panel Haven

This is an offline rename of this web application's state, **not** an import of the ComicRack desktop database. There is no automatic migration or legacy-name fallback. Starting against an unmigrated state directory can create an empty `panelhaven.sqlite` instead of opening your existing library.

1. Stop the old server and disable service/container auto-restarts. Keep it stopped throughout migration.
2. Before replacing the old deployment, use its **old binary** to make a backup: `dotnet /path/to/old/ComicRack.Web.dll --DataPath /absolute/existing/state --backup /absolute/backups/before-panelhaven`. Use your actual paths and a new backup destination; retain the backup unchanged. This command validates database integrity with the application's required `NATURAL_NUMBER` collation; generic SQLite tooling cannot run that integrity check without registering the collation.
3. Confirm that `DataPath/comicrack.sqlite` exists and `DataPath/panelhaven.sqlite` does not. With no application or maintenance process running, open the existing database with SQLite tooling, run `PRAGMA wal_checkpoint(TRUNCATE);`, and require a successful, non-busy result (`0|0|0`), then close the SQLite connection. Do not proceed while `comicrack.sqlite-wal` or `comicrack.sqlite-shm` remains; investigate remaining connections/checkpoint failures rather than deleting a WAL that may contain committed data.
4. Rename only `DataPath/comicrack.sqlite` to `DataPath/panelhaven.sqlite`. Preserve permissions, the entire `keys/` directory (including revocations), thumbnails and all other state. Keep the same `DataPath`, library roots, local repository checkout path and Git remote.
5. Update deployment commands/project references to `web/PanelHaven.Web.csproj` or the published `PanelHaven.Web.dll`; the checks project is `web-checks/PanelHaven.Checks.csproj`. Replace any configured `COMICRACK_ADMIN_USERNAME` / `COMICRACK_ADMIN_PASSWORD` with `PANELHAVEN_ADMIN_USERNAME` / `PANELHAVEN_ADMIN_PASSWORD`; existing accounts still use their stored passwords, not bootstrap values. Update custom API clients to send `X-PanelHaven-Request: 1` for non-GET/HEAD API requests.
6. Start the renamed application using the same state directory and confirm the existing accounts, library and private reading data are present. **Every user must sign in again:** both the cookie name (`PanelHaven.Session`) and data-protection application identity (`PanelHaven.Web`) changed. Retaining the key ring does not make old cookies valid.

New backups require the `panelhaven.sqlite` basename. To restore a trusted pre-rename backup, work on an **offline copy** of that backup, rename its `comicrack.sqlite` to `panelhaven.sqlite`, and leave `keys/` unchanged before running the new restore command into a new/empty state directory. Old backups contain a consistent snapshot without WAL sidecars; do not substitute an uncheckpointed live database copy. Keep the original backup intact for rollback with the old binary. Old-name backups are not accepted directly.

## HTTPS and trusted proxies

Default local mode accepts only loopback clients and ignores forwarded headers. For remote access, set `Deployment__RequireHttps=true` and explicitly list allowed hostnames. HTTPS-required mode rejects insecure requests rather than redirecting credentials, uses `Secure` session cookies, validates Host and sends HSTS over HTTPS.

Direct TLS uses Kestrel's native certificate configuration:

```bash
export Deployment__RequireHttps=true
export Deployment__AllowedHosts__0=comics.example.com
export Kestrel__Certificates__Default__Path=/private/certificates/comics.pfx
# Supply Kestrel__Certificates__Default__Password through your secret/environment manager.
dotnet run --project web/PanelHaven.Web.csproj --urls https://0.0.0.0:5178
```

Replace the example hostname and certificate with your actual deployment values. No public DNS, certificate issuance or firewall changes are performed by this app.

Behind a TLS-terminating reverse proxy, use the same HTTPS requirement/host list, bind Kestrel HTTP to loopback or a private interface, and set `Deployment__KnownProxies__0` to the proxy's exact IP (additional proxies use indexed keys). The proxy must preserve the allowed Host, strip untrusted incoming forwarding headers and supply both `X-Forwarded-For` and `X-Forwarded-Proto` with matching counts. Only one hop from an explicitly trusted peer is processed, before authentication and IP-based rate limiting. `X-Forwarded-Host` is never trusted. Wildcard hostnames, proxy networks and implicit proxy trust are not supported; configuring proxies without HTTPS-required mode is rejected.

## Safety and operating limits

The local test site remains loopback HTTP. The HTTPS/host/proxy controls above are implemented, but public deployment still requires correct TLS termination, firewall rules, protected secrets and an operational security review. All users share access to all books; library-specific permissions are not implemented.

The private state directory contains SQLite data and persistent cookie-signing keys. Keys are not encrypted at rest by the application; protect this directory with OS permissions and encrypted storage where needed. Keep it outside publicly served folders. Use the offline backup/restore commands above; do not copy only the live SQLite main file while WAL writes are active.

Non-GET/HEAD API requests require `X-PanelHaven-Request: 1`; the browser client supplies it. The session cookie is HTTP-only and SameSite Strict, with Secure enabled in HTTPS-required mode.

Archives are read without extraction. Requests have a 64 KiB body limit. Archive checks reject traversal names, symlink/reparse paths, duplicate entries, DTDs and unexpected XML roots. Limits: 1 GiB archive, 10,000 entries, 16 MiB ZIP central directory or RAR/7z headers, 32 MiB per page, 2 MiB per XML document, 2 GiB declared expanded size and 1000:1 expansion ratio. ZIP64, multipart and encrypted archives are unsupported. RAR5 and 7z decoder dictionaries are limited to 64 MiB; RAR4 PPMd has a format ceiling of 256 MiB. These are decoder limits, not a total process-memory guarantee. Unsupported 7z codecs, multi-input coder graphs and external header streams are rejected. Solid page access may replay earlier entries, so late pages can be slower. Raster signatures are checked when pages are served. Thumbnail decoding rejects sources above 32 million pixels before raster allocation and runs one decode at a time to bound memory. Native decode calls are not interruptible mid-call.

Library directories must remain administrator-controlled: path checks are not a sandbox against a local attacker concurrently replacing filesystem entries. Symlinks in any path component, including root ancestors, are rejected during ordinary scans/reads. A changed archive requires rescanning before pages or thumbnails can be served. Missing/broken archives retain catalog history unless an administrator explicitly removes a confirmed missing entry. Moving files normally creates new identities; use the explicit verified root-relocation workflow to retain existing IDs and private data. Incremental scanning and cached duplicate hashes assume content changes also change size or modification time; unchanged archives are not rehashed on every scan.

Scan status and its completion revision are process-local, not a durable job queue. HTTP disconnects do not cancel a scan; server shutdown does. Startup automatically reconciles again and skips previously committed unchanged files. Progress reports discovered/attempted archives, not a speculative percentage. Error count is complete; only the first 100 messages are retained. Cancellation is checked between traversal/import steps and archive/hash reads; filesystem or SQLite blocking operations may delay it.

Library responses are SQL-paginated with a maximum of 100 books per API request; totals and each page share a read snapshot. Sorting uses a stable ID tie-break. Separate page requests can reflect concurrent library edits. Search/count queries still scale with catalog size; SQLite search case folding is primarily ASCII. Publisher filtering is exact. Thumbnail cache files may be deleted while the server is stopped to reclaim disk space; they regenerate on demand.

The native SQLite bundle is explicitly updated beyond the provider's default dependency to avoid [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
Thumbnail generation uses MIT-licensed SkiaSharp with Windows and Linux native assets because .NET has no supported cross-platform raster-resize API. Preserve the packages' native third-party notices when distributing.
RAR/7z/TAR decoding uses MIT-licensed SharpCompress because .NET has no built-in RAR or 7z reader. No external archive executable is needed at runtime. Retained format fixtures contain original synthetic data; generation commands are recorded in `web-checks/ArchiveChecks.cs`.


## Development

### Repository layout

- [`web/`](web/) — ASP.NET Core application, SQLite storage, and browser assets in [`web/wwwroot/`](web/wwwroot/).
- [`web-checks/`](web-checks/) — console-based behavioral checks, synthetic archive fixtures, and the JavaScript reader check.
- `upstream/` — optional local ComicRackCE reference checkout; ignored by Git, not included in this repository, and not a build or runtime dependency.

Build from the repository root:

```bash
dotnet build web/PanelHaven.Web.csproj
```

### Checks

```bash
dotnet run --project web-checks/PanelHaven.Checks.csproj
node web-checks/reader-check.mjs
```

The retained checks exercise real temporary archives/databases: pagination boundaries and stable ordering, natural number sorting, per-user status/publisher filters, unchanged-file skipping without archive reopening, changed-file preservation, cancellation/resumption, bounded errors, thumbnail dimensions/colors/orientation/first frame, cache reuse/invalidation, unsafe images/archives, metadata preservation, account validation and persisted/validated per-user reader preferences. The dependency-free JavaScript check covers spread grouping, forward/back boundaries, landscape splitting and swipe decisions. Node.js is needed only for that check, not to run the application. These checks do not change your configured library.

### Verification history

The following records earlier development verification, not CI status or a guarantee for every platform.

Verified on Linux: build and retained checks; live HTTP pagination/filter bounds, scan authorization/single-flight handling and thumbnail authentication; desktop/mobile browser pagination, sorting, filters, generated covers, reader return state and completion. A real 4,061-archive scan was cancelled after partial import, reconnected after reload and resumed with committed archives skipped. Windows execution has not been verified.

Reader verification additionally exercised desktop and mobile-emulated Chromium: fullscreen entry/exit, zoom overflow/panning, odd-page and RTL spreads, landscape splits, lazy continuous rendering, visible-page progress and reload restoration, account-specific preferences, real browser touch swipes and pinch gestures, and retry behavior for simulated image/preference-save failures. Physical-device and other-browser verification remains outstanding.

Archive/management verification also exercised retained checks with real RAR4/RAR5, solid/non-solid 7z and TAR fixtures, including directory entries, encrypted/multipart rejection and dictionary bounds. Live HTTP/browser checks covered archive scans/pages/thumbnails, extended single-book metadata, sparse 24-book edits and custom values, cross-page list membership/reordering, removal/rename, live unread membership after reading, restart persistence, atomic failures and reader-role/list-ownership restrictions.

The remaining reader batch was checked with an eight-page classified archive in live Chromium: saved rotation/color settings and bookmark reloads, bookmark delete/jumps, embedded bookmark jumps, failed bookmark save/retry and leave protection, advertisement skipping, explicit skipped-page reveal, empty filtered state, rotation-aware spreads, continuous lazy loading, filtered completion and mobile-emulated layout without horizontal overflow. Retained checks cover old preference-schema migration, strict new preference validation, bookmark upserts/ownership/bounds and malformed/unknown/duplicate page metadata. Other browsers and physical devices remain unverified.

Maintenance verification exercised a live 31-book catalog: automatic startup, exact-copy reporting, series/publisher/folder stacks including empty-key drilldown, source disappearance/restoration, preserved metadata/progress/bookmarks/lists, corrupt-source unavailability, explicit confirmed catalog removal without touching surviving source bytes, administrator restrictions and mobile grouping layout. Retained watcher checks exercise real filesystem bursts, changes during scans, cancellation, state-directory exclusion, offline restart changes and recovery of a disconnected root through the real one-minute reconciliation interval; the check suite therefore takes roughly 80–90 seconds.

Account verification exercised real browser/API creation, current-password rejection, password changes, administrator resets, forced-change login without library requests, cross-session revocation, promotions/demotions, protected last-administrator errors, deletion of private rows and memberships, unchanged shared metadata/source bytes, username reuse and password-attempt throttling. Mobile-emulated Chromium checked account/password screens. Retained checks cover legacy account migration, stale/malformed sessions, concurrent password changes and last-administrator mutations, and rejection of private-data writes after account deletion.

Organization verification upgraded a real pre-feature catalog with existing lists/progress, then exercised live API and desktop/mobile browser rating saves/filtering, failure/retry and discard protection, folder creation/rename/deletion without list loss, smart-rule freshness after single/bulk metadata edits, filtered series statistics, account-switch isolation and account deletion cleanup. Source archive bytes remained unchanged. Retained checks cover migration, sorting/pagination, rule all/any semantics, invalid inputs, private ownership, deleted-account writes and catalog cascades.

Metadata-workflow verification exercised real API/browser reports and book navigation, missing/partial/leap-date handling, name variations, exact previews, draft invalidation, concurrent-edit rejection and successful re-preview/apply on mobile. Reader JSON/CSV downloads were captured and parsed, including formula protection and quotes/newlines; simulated export failures retained selection for retry. Retained checks cover read-only behavior, no-op preservation, concurrent apply, custom-key casing, unknown XML, private-data exclusion and invalid selections. Source archive bytes remained unchanged.

Operations verification exercised real offline preview/apply against duplicate catalog rows, unchanged original IDs/private state and real page serving afterward, active-server maintenance refusal, backup/restore into fresh state and original-login/private-data recovery. HTTPS smoke used a disposable certificate with certificate verification, Secure cookies, HSTS, rejected hosts/plain HTTP, explicitly trusted forwarding, an untrusted loopback peer and forwarded-client rate-limit separation. Retained checks cover conflict refusal, unsafe mappings, corrupt/unsafe backups, exclusive state access and middleware trust boundaries. Public deployment and Windows execution remain unverified.

The real root relocation preserved 21 existing book IDs and their saved progress. The subsequent full 5,541-archive scan completed with zero errors, missing entries, unavailable entries or byte-identical duplicates.

## Full-port parity inventory

This inventory is based on the upstream engine, database, provider, display and dialog source. It is a roadmap, not a claim that the missing functionality exists.

| Area | Current implementation | Remaining for full port |
| --- | --- | --- |
| Embedded metadata | ComicInfo/ComicBook metadata import | Legacy desktop database and list import explicitly excluded |
| Archive/image support | CBZ/ZIP, CBR/RAR4/RAR5, CB7/7z, CBT/TAR with PNG/JPEG/GIF/WebP | DjVu, web comics, additional image codecs and large/ZIP64 archives; PDF excluded by request |
| Library organization | Paginated grid/stacks, series/publisher/folder grouping, filtered series reading statistics, search/filters, cached thumbnails, watched folders, missing/unavailable tracking, exact-duplicate reports and explicit missing-entry removal | Richer collection organization and duplicate-resolution workflows |
| Reader | Single/continuous/spread modes, zoom/pan/fullscreen, RTL/touch, rotation/brightness/contrast, personal/embedded bookmarks, page-type labels/skipping and saved preferences/progress | Further desktop parity and per-page editing |
| Metadata | 42 scalar fields, custom values, read-only validation, exact stale-safe bulk previews and JSON/CSV export; unknown embedded data retained | Metadata write-back and embedded page-structure editing |
| Lists | Private manual lists, ordering, single-level folders and live all/any smart rules including creators, genre, year and personal rating | Further rule fields/operators and nested organization; no legacy list import |
| File management | Read-only sources; verified offline root relocation preserving catalog identities | Safe rename/move/organize, export/conversion and reversible destructive operations |
| Automation | Automatic folder watching and startup/periodic scan reconciliation | Scrapers, automation rules and persistent background jobs; no legacy plugin-runtime requirement |
| Accounts/operations | Admin/reader roles; account lifecycle and private data; offline backup/restore; HTTPS-required mode, Secure cookies and explicit proxy/host trust | Self-service recovery, library permissions, public-deployment review and scheduled/encrypted backups |

Legacy ComicRack database/list migration and PDF support are out of scope by request; embedded comic metadata support remains. Both recommended reader batches are implemented, including rotation/color adjustment, bookmarks and embedded page-type handling. No legacy plugin or Android-client protocol compatibility is required.

## Help and contributing

Panel Haven is maintained in [batman-2099/PanelHaven](https://github.com/batman-2099/PanelHaven).

- For questions, bugs, or feature requests, search [existing issues](https://github.com/batman-2099/PanelHaven/issues) before [opening an issue](https://github.com/batman-2099/PanelHaven/issues/new).
- Include your commit/version, operating system, browser, reproduction steps, expected behavior, and relevant sanitized logs. Never attach passwords, signing keys, a private database, or copyrighted comic files.
- For substantial changes, open an issue first to agree on scope. Keep pull requests focused, run the [checks](#checks), and update documentation when behavior changes. UI changes should include desktop/mobile verification.
- Do not publish credentials or exploit details in a public issue. Use GitHub's private vulnerability reporting option if it is enabled; this repository does not currently document a separate private security contact.

## License and attribution

Panel Haven references [maforget/ComicRackCE](https://github.com/maforget/ComicRackCE), whose [README](https://github.com/maforget/ComicRackCE/blob/master/README.md) describes its decompiled origins and whose [license file](https://github.com/maforget/ComicRackCE/blob/master/LICENSE.txt) contains GPL-2.0 terms. The reference checkout is not included here; this application does not load its Windows assemblies or native DLLs.

**This repository does not currently include a project-level license file.** Do not assume that public source availability grants permission to redistribute it. Project licensing and upstream provenance must be resolved before distributing a derivative; dependency licenses do not license Panel Haven itself.

SkiaSharp and SharpCompress are MIT-licensed dependencies. Preserve applicable package and native-component third-party notices when distributing.
