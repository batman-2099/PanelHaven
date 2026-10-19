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
- Import embedded ComicInfo/ComicBook metadata, edit catalog metadata independently, and explicitly write reviewed changes to CBZ/ZIP with verified original backups and undo.
- Watch library folders automatically, report exact duplicates, and retain catalog history when files go missing.
- Manage administrator/reader accounts and perform live or offline backups, offline restores, and verified root relocation.

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

Initial configuration uses ASP.NET Core's environment-variable/command-line providers. `Libraries__0`, `Libraries__1`, etc. are server-side paths. Relative bootstrap roots and `DataPath` resolve from the application's content root (`web/` when using the commands above). Default state is `web/data/`, with the database at `DataPath/panelhaven.sqlite` and persistent signing keys in `DataPath/keys`. Administrators can subsequently manage configuration through **Server settings**.

### Web-based server administration

**Server settings** is an administrator-only page at `/#settings`, opened through the header's hamburger menu (left of **Sign out**) under **Administration → Server settings**. The same menu contains **Change password** for signed-in users and the administration actions for administrators. Section buttons jump to configuration groups or Save without changing the route. It supports bookmarks, reload and browser Back/Forward; **Back to library** returns to the catalog. Leaving with unsaved settings or credentials requires confirmation, and navigation is blocked while a settings request is running. Every save, startup change, restart or data-relocation request requires the current administrator password and request-protection header; stale revisions are rejected rather than overwriting another administrator's changes.

To check page navigation, sign in as an administrator on an idle library page and run `web-checks/settings-page-check.js` in the browser console. It checks routing, skip navigation, focus return, draft-discard confirmation, secret-input cleanup and browser Back without saving settings.

The screen edits library roots, Comic Vine credentials, listening addresses/ports, HTTPS enforcement, allowed hosts, trusted proxies, certificate/private-key paths and certificate password, plus backup destination, daily time and retention. Secrets are write-only: blank keeps the existing value, a replacement sets it, and **Clear** removes it. Existing secrets are never returned to the browser. Certificate files must already exist on the server; HTTPS listener configuration is validated before saving.

Settings are atomically persisted with private permissions. By default the file is the **initial** `DataPath` followed by `.settings.json`, outside the movable data directory. For a stable explicit location, launch with `--ServerSettingsPath /private/config/server-settings.json`; its parent must exist. This bootstrap locator remains host-managed so the server can find its configuration. Saved managed settings override their original environment/CLI values, including cleared secrets and shortened lists. Offline backup, restore and library-root relocation commands retain their explicit CLI paths and bypass server-settings overrides.

Application/library/network/secret changes require restart. The screen distinguishes saved settings from the running configuration. Backup scheduling is applied when saved and checked against actual user-service status; a saved schedule is not proof of a running timer. A private `.previous` file retains the known running configuration for manual recovery. Both files can contain secrets: keep them outside web roots, protect their storage, and back them up separately. If a network change locks you out, stop the service, restore that recovery file over the settings file and restart. Saved network bindings may need a reachable hostname instead of `*` or `0.0.0.0` when reconnecting.

Git ignores the default `web/data.settings.json`, its `.previous` recovery copy, and their temporary-write files. Custom settings paths may not be ignored: keep secret-bearing configuration outside the checkout, and never stage it with source files.

On Linux with user systemd, startup controls install/update this installation's owned `panelhaven.service`, then enable or disable it. **Restart server** performs a controlled handoff/restart; it never runs an administrator-supplied shell command. The managed service refuses to start if its settings file is missing. Foreign units, another installation's units and custom drop-ins are not overwritten. Startup without login depends on user-manager lingering, which the app does not grant. Unsupported hosts can edit application settings but show host controls as unavailable. Firewall rules, DNS and certificate issuance remain operating-system/external-service responsibilities.

**Relocate application data** is a separate confirmed workflow, not an ordinary path edit. Choose a new, nonexistent destination whose parent exists, separate from library roots, backups, application files and settings. The next startup acquires exclusive leases, copies and verifies the database, signing keys, archive-backup vault and other state, then commits the new path only after durable publication. Allow downtime and disk space for the full copy. A failed move retains the original active location and reports the failure; lease contention leaves the request queued. The source is never deleted. After successful cutover that retained copy becomes stale as new reading activity occurs—do not switch back casually or run both copies.

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
- Static assets revalidate with the server to avoid mixing old scripts with updated HTML after deployment. Hard-refresh once when upgrading a browser that cached assets from an older release.

Browsing keeps search, grouping, sorting and reading status visible; **Group by** sits before **Sort by** and defaults to **Series**. **Filters & display** contains the remaining filters. The **Reading lists** sidebar sits to the left of the catalog on desktop and tablet, with a folder-grouped list picker and controls to create, edit, delete and organize lists. On narrow screens it stacks above the catalog. The header hamburger menu contains account and administration actions and closes after an action, an outside click, or Escape; Escape returns focus to its trigger. Bulk actions appear after selecting books, and pagination is available above and below the grid. **Refresh library** explicitly retries failed covers. Completed scans without errors can be dismissed and appear only in the library; active scans and errors remain visible across pages. Dismissals are remembered per account in this browser, including across reloads and identical automatic scans. Changed results remain visible, and starting a manual scan clears the dismissal. If browser storage is blocked, dismissal lasts only until reload.

Each account receives a **Recently added** smart reading list once, containing comics first added to the catalog within the last 30 days. It updates automatically and can be renamed, edited (using the **Days since added** rule), moved into a folder, or deleted without being recreated. Existing same-name lists are preserved. Addition time is recorded only when a comic is first imported; rescans, metadata edits, and moves do not reset it. Books cataloged before this tracking was introduced have an unknown addition date and are excluded rather than being falsely marked new.

Book cards omit file paths and the routine “available” label. Missing/unavailable warnings remain visible; full locations remain in book details. Series cards show the book count and completion percentage centered inside the progress bar, without the unread/in-progress/completed count breakdown. Progress bars align along the bottom of each grid row even when series titles wrap.

The library defaults to sorting by **Number**, ascending, while grouping by **Series**. Within a group, **Home** returns to the group overview, with the series/group name centered underneath in larger semibold text and the top Previous/Next controls below that.

Select comics using the checkbox at the bottom-right of each cover. Its compact 28px background retains a 44px touch target. While any comic is selected, clicking a cover toggles its selection and the selection toolbar exposes **Select page** alongside the other bulk actions. Once the page is selected or the 100-book limit is reached, that button becomes **Unselect all**; clicking it clears selections across all pages. Otherwise, clicking a readable comic opens the reader directly at the saved reading position; book details remain accessible from the reader. Missing/unavailable comics and books without readable pages still open details. Clearing the selection restores direct reading and hides the selection toolbar. Run `web-checks/selection-check.js` in the browser console on an idle library containing at least two readable books to check selection and reader navigation; opening the reader may update reading progress.

Selection mode also offers **Read** and **Unread** for up to 100 selected comicbooks across pages. Read sets completion to 100% at the final physical page; Unread clears personal progress to 0% and returns the comicbook to the Unread filter, not In progress. These actions affect only the signed-in account and refresh cards, filters, and group percentages without clearing the selection. `POST /api/reading-status` accepts `{bookIds, completed}` and validates the whole selection before an atomic update; comicbook files, metadata, bookmarks, ratings, list memberships, and other accounts remain unchanged.

Selection controls keep **Read** and **Unread** together under **Mark selected**. Metadata, exports, organization, and list-membership actions are under **More selected actions**. Clearing selection announces the result and returns keyboard focus to the visible result summary.

All dialog windows share a wider 1200px desktop layout, constrained to the viewport on smaller screens. Primary actions and form submissions use solid blue buttons; secondary actions use blue-tinted fills, while quiet controls remain outlined. Delete/remove buttons and selected metadata Remove / reset actions use red. Keyboard focus retains a high-contrast light-blue ring. The book-details rating dropdown retains a screen-reader label without a visible heading.

The Library/Unsorted navigation sits at the left in place of the visible catalog heading. The heading remains available to screen readers. Bottom pagination keeps a 2rem gap below the comicbook grid and percentage bars.

Reading lists remain a left sidebar on desktop. At 900px and below, a collapsed **Reading lists** disclosure contains a compact selector; **Manage lists** exposes list creation and management. **Library information** contains catalog help and watcher status. Scan status is a compact disclosure: new scan or watcher errors reveal their details, while unchanged polling respects manual collapse. Account identity appears inside the account menu.

Pagination focuses the visible result summary rather than the screen-reader-only heading. Results cannot be activated while replacement results load. Administration dialogs return focus to the account-menu trigger; Move / reorganize opens at its heading; metadata filters move focus before hiding the active field. Server settings separates routine status from actionable notices, and **Go to save** explicitly navigates to the save section without submitting it.

Application labels, help text, and server messages call collection files **comicbooks**, not archives. Technical API routes, identifiers, formats, and existing on-disk names such as `archive-backups` remain unchanged.

Administrators can open **Move / reorganize** directly from Book details to enter an existing destination folder path or create a new nested folder within a configured library, and optionally rename the comicbook. Use **Create folder** before previewing a new destination. The existing exact-path preview and explicit confirmation are required before any comicbook moves. For template-based organization of one or more comics, select their checkboxes and use **More selected actions → Organize selected**.

Folder browsing/autocomplete and resized page-organizer thumbnails remain backend recommendations, not part of the frontend-only UI fixes. Existing APIs expose destination roots and folder creation, but not directory listing; the thumbnail API serves covers, not arbitrary page previews. Manual destination entry and full-resolution organizer images remain supported without changing those API contracts.

**Archived and Unsorted locations:** set the optional **Archived** and **Unsorted intake** paths under Server settings → Libraries, then save and restart. The configuration keys are `ArchivePath` and `UnsortedPath`; regular roots remain in `Libraries`. All paths must be separate and non-overlapping. Existing settings without these optional paths continue to work.

Normal library browsing excludes both areas. Choose **Location → Archived** to browse and read archived comicbooks, or **Unsorted** to open the separate intake for comicbooks awaiting organization. All areas are scanned and watched. **Move / reorganize** and **Organize selected** offer Library, Archived, and Unsorted destinations; reviewed moves change the comicbook's area immediately while preserving its identity, contents, private data, and list memberships. Nothing moves automatically.

Initial indexing runs in the background; comicbooks appear as they are imported. Already indexed comicbooks remain readable during scanning: page-order reads retain database/source validation without waiting for the whole scan. Moves and page-order edits retain their existing scan/write locking.

The books, book-groups, and book-filters APIs accept `location=library|archived|unsorted`, defaulting to `library`. Area scope applies before search/list filtering, grouping, counts, publisher choices, and pagination. Smart-list editing previews the regular library; saved lists can be browsed within the selected area. Direct comicbook reading remains available in all areas. Run `web-checks/location-check.js` on an idle library page with a readable intake comicbook to check area navigation and stale-response isolation.

Metadata editing groups fields by purpose, with field search and a checked-fields filter/summary. Filtering never removes checked changes from the draft. Common fields are expanded initially; custom values and less-used categories remain available in disclosures. Move/organization destinations and account drafts are protected from accidental dismissal.

The existing `#library`, `#reader/<id>` and `#settings` routes remain supported. `#unsorted` opens intake; reader links may carry `?location=library|archived|unsorted` to preserve the return area across reloads. Other search, filters and library pagination remain in-memory rather than bookmarkable URL state.

## Library maintenance

- **Missing versus unavailable:** scans retain catalog entries and mark confirmed absence under an accessible library as missing. Disconnected roots, unsafe paths and invalid sources are unavailable, not proof of deletion. Metadata, progress, bookmarks and list membership remain intact; restoration at the same path retains book identity. Availability reflects scan observations, not a guarantee against a subsequent filesystem change.
- **Explicit removal:** administrators can open **Library maintenance**, choose missing records and confirm **Remove catalog entry**. The server rechecks absence under a safe accessible root and refuses concurrent scans or reappeared/inaccessible sources. Removal permanently deletes that book's shared metadata and every user's progress, bookmarks and list memberships. It never deletes source files. Nothing is automatically removed.
- **Exact duplicates:** the maintenance report groups available archives by SHA-256 of their complete file bytes and displays each location. This is not title, cover or decompressed-page similarity. Hashes are computed on new/changed imports and backfilled once for old records. Missing/unavailable records retain their last validated hash as relocation evidence but never participate in current duplicate reports. Duplicate groups may continue across report pages. Present duplicates have no removal button.
- **Compare copies:** administrators can compare a selected duplicate with up to three other copies side by side, paging through peers while keeping the selected copy visible. The comparison includes scalar/custom metadata, physical page counts, relative locations and **your own** progress and ratings; it never exposes another account's reading state. Differences are highlighted. Identity is based on the last scan's archive hash, not a fresh file verification. Comparison does not edit metadata, merge records or delete files.
- **Folder watching:** native watchers debounce bursts for roughly two seconds; events during a scan queue one follow-up. Startup reconciliation recovers offline changes. A roughly one-minute reconciliation recreates watchers and recovers missed events, overflow or disconnected roots when scanning is idle. State-directory changes are ignored. Cancelling stops the current burst, not watching permanently; later events or reconciliation can start another scan. Manual scanning remains available.
- **Groups:** select series, publisher or folder to browse paginated stacks, then open a stack and use **Home** to return. Sorting, page size and compatible filters retain the opened group; changing search, grouping, reading list or catalog area resets it. Search, reading lists, status, publisher and availability filters apply before counts and pagination. Group keys are exact and case-sensitive; blank metadata gets its own group. Locations use `Library N/relative/path`, where N follows the configured, deduplicated library-root order; absolute server paths are not exposed.


## Reader controls

Mode, fit, zoom, reading direction and the **Cover alone** option are saved per account on the server and restored across sessions/devices. Rotation, brightness, contrast and skipped page types are saved the same way; existing accounts retain their previous settings on upgrade. Fullscreen is temporary and depends on browser support. Preferences, bookmarks and reading progress are saved separately; failed writes expose retry controls and prevent silently leaving with unsaved changes.

**Display settings** contains reader mode and image controls; bookmarks/page types follow the comic surface. Page-turn keyboard shortcuts apply only while the comic surface is focused, leaving normal scrolling keys available elsewhere. Skip to content focuses the current page without navigating away. An image failure retains its Retry action but no longer blocks turning past that page in spread mode.

Reader instructions are available under **Help**, leaving more room for comic pages on mobile. Mode and skip changes retain images and in-flight loads still needed by the new view rather than downloading them again. Distant continuous-mode pages are still released, and leaving a comicbook releases all of its image resources.

- **Single page:** previous/next buttons and reading-position jumps. With the comic surface focused at 100% reader zoom, left/right arrows turn pages, reversed in RTL; Page Up/Down also turn pages in paged modes. Dominant horizontal single-finger swipes retain the same page-turn behavior.
- **Two-page spreads:** fixed logical pairs, optionally preceded by a standalone first-page cover. RTL reverses their visual order. If either member is landscape, both members of that pair are displayed separately; later pairs do not shift. An unmatched final page appears alone. A normal spread records its last sufficiently visible logical page so resuming restores that spread.
- **Continuous:** scroll inside the page area. Nearby pages load with at most three image requests/decodes in flight; distant images are released while placeholders retain their dimensions. Progress follows sufficiently visible, successfully decoded pages—not prefetched images. Page jumps and mode changes preserve the reading position; initial placeholder dimensions are estimated until the image is decoded.
- **Zoom/pan:** zoom is relative to the selected fit mode. Enlarged pages use native horizontal/vertical scrolling. Touch pinch zoom remains native; swipes are disabled during reader/browser zoom, multi-touch and continuous mode to avoid accidental page turns.
- **Fullscreen:** the reader controls and errors remain inside the fullscreen view. Leaving the reader exits fullscreen. Unsupported browsers show a disabled fullscreen control.
- **Rotation and color:** choose 0°, 90°, 180° or 270°; brightness and contrast accept 50–200% (100% is unchanged). Rotation adjusts fitted dimensions and landscape-spread splitting rather than cropping the image. **Reset visuals** restores rotation to 0° and brightness/contrast to 100%, without changing mode, zoom, progress or skip filters.
- **Bookmarks:** expand **Bookmarks and page types**, name the current physical page and choose **Add / update bookmark**. Each account has one named bookmark per page; use its button to jump or **Delete** to remove it. Embedded ComicInfo bookmarks appear separately as read-only jumps. Personal bookmarks survive reloads; bookmarks beyond a shortened archive's current page count are hidden.
- **Page types and skipping:** the reader shows embedded ComicInfo classifications and lets you skip selected types, including covers, advertisements, previews and deleted pages. Missing, unknown or ambiguous metadata stays visible rather than being silently skipped. Explicit valid Page records with no Type use ComicRack's Story default. Physical page numbers do not change; spreads keep fixed pairs of the saved reading order and omit skipped members.
- **Explicit jumps:** entering a skipped reading position or jumping to its physical-page bookmark temporarily reveals that page. Previous/Next still follow skip filters. With every page skipped, the reader shows an empty state without updating progress; **Show all page types**, a filter change or an explicit jump restores a view. Completion is recorded when the final included page in reading order is actually visible and decoded, not merely skipped or prefetched.

Page errors have individual retry buttons and do not advance saved progress. The **Position** field retains an unfinished typed jump while asynchronous images/progress updates arrive.

### Organize page order

Administrators can choose **Organize page order** in book details, including through **Book details** while reading. Drag a thumbnail onto another position, or use its **Move earlier / Move later** buttons with keyboard or touch. Cards show both reading position and original physical page number. Thumbnails load lazily from existing page endpoints.

Changes remain a draft until **Save page order**. **Reset to comicbook order (draft)** also requires saving; reload/discard restores the server's saved order. A concurrent edit or changed source rejects a stale save without overwriting it; the draft stays visible until explicitly discarded and reloaded. Failed or uncertain saves are not automatically retried.

The order is shared by all readers and stored only in the catalog. Archive bytes, ACLs, embedded ComicInfo page indices, image URLs and each account's physical bookmark/progress IDs are unchanged. Single, continuous and spread modes use the saved order; **Position** selects a reading position while labels identify its physical page. New readers start at the first ordered page, existing readers resume their saved physical page, and completion follows the final included reading position. Applying an order from the active reader keeps its current physical page.

Orders are bound to the scanned archive hash and page count. Changed source bytes invalidate custom order, including metadata write-back/restore even when the page count is unchanged; externally changed sources must be rescanned before organizing. Unchanged scans, byte-identical moves/undo, offline root relocation and application-state backups retain order. Conflicting custom orders block duplicate merges rather than silently dropping either order. No archive page insertion, deletion, conversion or rewriting is performed.

## Metadata and reading lists

Open book details and choose **Edit metadata**, or select books across library pages and choose **Edit selected metadata**. Only checked fields/custom keys change; unchecked values are preserved. Blank is an explicit empty value; **Remove / reset** removes the field or custom key. Bulk updates are transactional: an invalid value or missing book rejects the whole request. Only administrators may edit shared metadata.

Toolbar bulk edits—including a selection of one book—require **Preview checked changes**, then **Apply reviewed changes**. The preview lists actual before/after values for each book, distinguishing absent/removed fields from explicit empty strings and showing unchanged books. Editing the draft invalidates the preview. If metadata changes elsewhere before applying, the entire request is rejected; your draft remains available for a new preview. Single-book editing from details/report retains its direct save flow. No-op edits do not mark books as edited.

The editor covers scalar ComicInfo fields and ComicBook `CustomValuesStore` custom values, preserving unknown XML, attributes and nested page structures. Custom names are case-insensitive. Literal `&#44;` and `&#61;` strings are rejected because the upstream custom-value encoding cannot represent them unambiguously. Declared metadata page count does not change the reader's actual archive page count. Embedded page structures and legacy progress are not editable through this form.

Administrators can separately choose **Edit page types and embedded bookmarks** in book details. Select physical pages, change their type/bookmark label, then **Preview page changes** and **Save reviewed changes to catalog**. Switching pages retains the draft; editing after preview disables save. Concurrent catalog changes reject the save without discarding the draft. Reload saved metadata before reviewing again. Blank removes the explicit attribute; an existing Page record without Type defaults to Story. Duplicate/invalid page indices are refused rather than guessed. This never changes page order, image bytes, personal bookmarks or reading progress.

Saved page edits immediately drive reader labels, embedded bookmarks and skip filters. Only the explicitly edited Type/Bookmark attributes become write-back intent; unrelated source page attributes and extensions remain intact. Use the existing archive preview/confirmation to deliberately write those edits to CBZ/ZIP. Undo restores original archive bytes while retaining catalog page edits. Other archive formats support catalog-only page edits.

Administrators can open **Validate metadata** for a read-only, paginated report of missing series/issue numbers, malformed or impossible publication dates, and potential publisher/creator name variations. Issue numbers remain text. Blank/-1 dates are unknown; partial dates are allowed, including February 29 when the year is unknown. Name checks compare case and whitespace within each field; creator entries are comma-separated, with ordinary separator padding ignored. They do not infer identities, correct spelling or change values. Reports analyze current catalog metadata on demand without opening archives or caching stale findings. Use the report's details/edit buttons to inspect a book.

Any signed-in user can select up to 100 books across library pages and choose **Export selected JSON** or **Export selected CSV**. JSON includes scalar/custom values and the stored ComicInfo/ComicBook XML, preserving unknown embedded fields. CSV contains stable scalar columns and a `CustomValues` JSON column, with standard comma/quote/newline escaping. Potential spreadsheet formulas receive a leading apostrophe, including values with leading whitespace/control characters; JSON preserves the original values.

Downloads are named `panelhaven-metadata.json` and `panelhaven-metadata.csv`.

Exports contain no application-account IDs, private ratings/progress or catalog filesystem paths. Raw embedded XML and custom metadata may themselves contain legacy paths or other source-provided information; review exports before sharing. Unknown selected IDs reject the whole export rather than silently dropping books. Reports, previews, exports and ordinary metadata edits never alter source files.

### Comic Vine metadata search

Administrators can open **Find metadata** in book details to search [Comic Vine](https://comicvine.gamespot.com/). Series, issue number and publication year are prefilled from the catalog and remain editable. Choose the correct volume using its start year/publisher, then choose an issue. Publication year is only a matching hint: an issue's publication year must not exclude a series that started earlier. Volume results page by 10; issue results page by 20. Clear the issue number to browse the selected volume.

Obtain your own key from [Comic Vine's API page](https://comicvine.gamespot.com/api/), enter it under **Server settings → Comic Vine**, save with your administrator password, then restart the server. `ComicVine__ApiKey` remains available for initial environment-based configuration, but saved web settings take precedence.

Never put the key in browser source, a URL you share, or source control. Without a key, the search dialog directs administrators to Server settings and the rest of the library remains usable. The server sends search terms and selected provider IDs, not comic files or filesystem paths. Only the fixed HTTPS Comic Vine API is contacted; redirects are refused, responses are size/time bounded, and upstream errors are sanitized.

Selecting an issue stages nonempty supported fields in the existing metadata editor. Uncheck unwanted suggestions, choose **Preview checked changes**, then **Apply reviewed changes**. Concurrent edits reject a stale preview while retaining your draft. Missing provider fields do not erase existing values; no genre is invented. Saved changes affect only the catalog, preserving unknown XML and private reading data. Archive write-back remains a separate explicit operation. Search results and review include Comic Vine attribution; a saved Comic Vine `Web` field is linked in full metadata.

Responses are cached in memory for five minutes, up to 32 entries. A conservative shared budget allows at most 180 uncached provider requests per rolling hour with at least one second between starts. Provider rate limits impose a cooldown; failures are not automatically retried. Cache and budget reset on process restart, but the provider's limits do not. Requests made by other applications sharing your key also count against Comic Vine's limits.

Comic Vine's [terms](https://comicvine.gamespot.com/api/) require non-commercial use, attribution and compliance with restrictions on redistribution. Review them before using or exporting provider-derived metadata. This is manual, single-comic matching; no automatic matching, bulk scraping or provider images are included.

### Downloading comics as CBZ copies

Any signed-in reader or administrator can choose **Export CBZ** in book details or **Export selected comics** for up to **100 selected books**. One book produces a CBZ; multiple books produce one ZIP containing numbered, uniquely named CBZs. Each complete download is limited to **1 GiB**; select fewer or smaller books if it exceeds that limit.

Exports contain supported page images byte-for-byte in the saved reading order, encoded by numbered filenames. Catalog ComicInfo metadata is included with page indices, embedded bookmarks/types, preferred cover and page count adjusted to the exported order. Unknown shared XML extensions survive. Shared custom values use a newly constructed minimal `ComicBook.xml` containing only `CustomValuesStore`; the original legacy document is never copied. Application-private progress, ratings, bookmarks, lists and preferences are excluded, as are known legacy reading-state fields. Shared metadata/custom values can still contain source-provided personal information; review before sharing.

CBZ/ZIP, CBR/RAR4/RAR5, CB7/7z and CBT/TAR sources use the same safety limits as reading. Unsupported images and unrelated source attachments are not included; this is a comic conversion, not an original-archive backup. Images are not transcoded. Source files, ACLs, catalog records, saved page order and private data remain unchanged.

Choose **Download CBZ / Download ZIP** explicitly. Preparation is indeterminate while the server stages and verifies the complete output on private disk; transfer then reports received bytes. The browser receives the complete file before handing it to its download manager. Allow server and browser storage space. Cancel, Close, Escape or navigation abort an active request; partial files are not offered as downloads, temporary server files are removed, and errors retain selection for manual retry.

Exports capture a coherent shared metadata/order snapshot and verify source hashes before and after copying. Changed, missing, unsafe or invalid sources reject the entire selection rather than silently omitting books. A saved order tied to different source bytes is refused. Export itself does not rescan or change catalog availability.

### Explicit comicbook write-back and undo

Administrators can open a book's **Write metadata to comicbook** action, preview the saved catalog metadata, then explicitly confirm and apply. Only CBZ/ZIP comicbooks support writing; other formats remain read-only. This is a single-book operation, never an automatic scan side effect. The preview shows changed fields and metadata XML: existing XML is normalized for display; write-preview output is the exact intended UTF-8 XML. Unknown metadata remains intact. Nonmetadata entry payloads remain unchanged, but ZIP compression/container bytes may change.

Preview revisions bind the current archive bytes and catalog metadata. Changed sources, stale catalog edits and unsafe paths are rejected rather than overwritten. A validated same-directory temporary ZIP replaces the source atomically only after a full original copy and its hash-verified manifest are saved under `DataPath/archive-backups`. Catalog refresh retains book identity, catalog edits, lists and each account's private data. No-op writes do not create backups or modify files.

Backup history offers a separate restore preview and confirmation. Undo restores the original archive bytes, **not earlier catalog edits or personal data**, and only while the current archive matches that backup's recorded written hash. A changed source, wrong-book backup or corrupted original is refused. A moved source is accepted only when committed file-operation history proves the path chain; immutable backup manifests are not rewritten. Interrupted/failed attempts can appear in history; a record is usable only when those hash checks pass. Do not modify archives externally during an operation: portable filesystem APIs cannot atomically coordinate an unrelated writer.

Backups are private, unencrypted full archive copies, with no automatic pruning. **State snapshots and the scheduled state-backup timer exclude `archive-backups`**; preserve that directory separately if you need off-host archive undo. Disk space is required for both the original backup and staged replacement. Payloads/manifests are flushed; Linux also flushes containing directories. Atomic replacement does not protect against storage failure, and Windows directory power-loss durability remains unverified.

Writing is supported on Linux and Windows (Windows remains unverified). Linux preserves owner/group/mode and conservatively refuses extended attributes, including ACLs, rather than silently discarding security metadata; other Unix platforms and unsupported permission arrangements are refused. Normal read-only library access is unaffected.

### Exporting comicbook originals

Open **Library maintenance → Comicbook original backups → Export all comicbook backups**, review the count/size, then use the native ZIP download link. This exports all complete originals and validated manifests, including backups for books removed from the catalog. It remains accessible with an empty catalog. Incomplete unpublished attempts are counted but excluded; a missing, unsafe or corrupted published original prevents a successful download.

The server stages the complete ZIP on private disk and verifies each original's SHA-256 before sending it; allow disk space for the entire export. The download does not buffer whole archives in browser memory. The temporary server file is removed when the response stream closes. The export captures records present when preparation starts; later backups require another export. It never prunes or modifies originals.

Downloads contain `archive-backups/{bookId}/{backupId}/original.bin` and `manifest.json`, not the database or signing keys. They are **unencrypted**, include complete comics and original filesystem paths, and are separate from scheduled application-state snapshots. Store both off-host securely. To recover, stop the application, retain the existing vault, and extract a trusted export's `archive-backups` directory into the matching restored `DataPath`. Preserve private permissions (Linux directories `0700`, files `0600`; ZIP extraction tools differ). Start the application and use the existing verified restore preview. Book IDs, original or journal-proven source paths, and written-file hashes must still match; export does not bypass undo safeguards or automatically restore comics.

### Reading lists

Every account may create its own lists. Select books in **All books**, choose a manual list and **Add selected**; select that list to remove members or use **List order** and move buttons. Moves use the full list, including members on other pages or hidden by filters. Deleting a list never deletes books or progress.

Use **Manage list folders** to create, rename, move or delete private folders. Choose a parent to nest a folder; list selectors display full folder paths. Self-parenting, descendant cycles and another account's parents are rejected. Deleting a folder moves its direct lists to **Unfiled** and promotes its immediate child folders to the top level; their lists and deeper hierarchy remain intact. No lists, books, rules, ordering or reading progress are deleted. Existing folders become top-level folders on upgrade.

Smart lists accept 1–20 total leaf rules in nested **All (AND)** / **Any (OR)** groups. Use **Add group** to nest conditions, up to four group levels and twenty groups including the root; empty groups and mixed leaf/group requests are rejected. Existing flat lists retain their behavior. Text fields include title, series, number, publisher, summary, writer, artist (ComicInfo Penciller), genre, relative location and source folder, plus Colorist, Inker, Letterer, CoverArtist, Editor, LanguageISO, Format and AgeRating. They support contains/does-not-contain, equality/inequality, starts/ends-with and empty/not-empty checks; `%` and `_` are literal characters, not wildcards. Page count, year, personal rating, Volume, Month, Day and Count support numeric comparisons. Unknown metadata numbers do not match numeric comparisons; empty/not-empty checks find them. Reading status and availability support equality/inequality. Issue numbers remain text. Extra metadata fields are read from catalog XML rather than duplicated indexed columns.

Use **Preview matches** before saving a smart list to see paginated results from the unsaved rules. Preview does not create or modify a list and uses the same matcher and your private progress/ratings as saved lists. Changing any nested rule or All/Any choice invalidates the preview; the browser requires a current successful preview before saving a smart draft. Results reflect the catalog at preview time, not a frozen membership: saved lists update on the next library load after metadata, scans, progress or rating changes. List kind cannot change after creation. Lists are native to this web app; legacy list import remains excluded.

In book details, choose **My private rating** and **Save rating**. Values 1–5 are stars; **0 · Unrated** removes your rating. These ratings never alter shared metadata or another account's rating. Use **My rating** to sort or filter by an exact value, including unrated. Failed saves keep your selection for retry.

Select **Group by → Series** for total books, unread/in-progress/completed counts and a completion bar. Counts use your progress and the current search, list and filters. Completion is the percentage of matching books explicitly completed, rounded down—not an estimate of pages read.

## Rename, move and undo

Administrators can open a book's **Move / reorganize** action, choose a configured library and relative folder, then enter a filename with the same archive extension. Existing folders can be used directly. For a new destination, enter its relative path and choose **Create folder**; this creates missing parent folders immediately without moving the archive. Review the exact source, destination, SHA-256 and conflicts, then choose **Apply reviewed move** to confirm; no separate checkbox is required. Editing the destination or creating a folder invalidates the preview. Every supported archive format can be moved without conversion; use **Organize selected** for a reviewed batch.

- **No overwrite:** existing files, cataloged destinations and pending reservations block the operation, even when bytes match. Absolute paths, traversal, symbolic links and application-state destinations are refused. Preview/apply do not create folders implicitly. The separate admin-only `POST /api/file-operation-folders` action accepts `{rootIndex, folder}` and returns the canonical relative folder; it requires the request-protection header, safely creates nested directories without following symlinks, and leaves existing directory permissions unchanged. Failed creation may leave empty parent folders; it never moves or deletes archives.
- **Missing folders:** move previews identify the missing library-relative folder rather than reporting a missing source archive. Choose **Create folder** before previewing a new destination; restore a missing source folder before moving its comic.
- **Identity and contents:** book ID, catalog metadata, page-edit intent and every account's private data/list memberships stay intact. Archive bytes do not change. Same-mount moves retain the original inode; cross-mount moves require space for a fully verified destination copy. The original is held until the catalog commit succeeds.
- **Linux only:** moves prefer native no-replace renames and flush files/directories. Filesystems such as NFS that reject that rename primitive use exclusive hardlink publication followed by unlink; existing destinations are never overwritten. Same-mount moves preserve the inode, owner, group, mode, modification time and every extended attribute/ACL, including `system.nfs4_acl`. Attribute fingerprints are checked before moving and during undo/recovery.
- **Permission boundaries:** read-only files, external hardlinks and unsupported permission arrangements remain refused. Cross-mount transfers involving source attributes or destination-directory attributes/ACLs are unsupported; archive metadata write-back/restore still refuses extended attributes. Never strip ACLs to bypass a refusal. Stop other programs from editing or moving the archive; these checks cannot coordinate arbitrary external writers.
- **History and undo:** per-book history shows up to 100 records, with pending work first. Undo requires a fresh preview, explicit confirmation, unchanged archive bytes and a free original path. It does not undo metadata/private-data edits. Archive-original backups remain usable after journal-proven moves, subject to their existing hash checks.
- **Interrupted operations:** a durable journal reserves both paths against scanning/import and blocks removal, further moves and archive writes. Open the same book's move dialog even if it is unavailable, then choose **Preview pending recovery**. Uncommitted moves are canceled back to the verified original; committed moves finish verified cleanup. Recovery accepts temporary hardlinks only when every link is accounted for by that journal's exact paths and identity; changed ACLs, extra links or unrecognized files block cleanup. An interrupted partial staging copy may be left untouched when cancellation can safely restore the original; history names its exact relative `.stage` path. Inspect it manually; it is never imported. Never delete or overwrite reserved files blindly.

Application-state snapshots contain the journal and path/backup-ownership provenance, **not source comics or staging/held files**. Restoring state does not rewind filesystem changes; retain matching state and filesystem backups. Explicit catalog removal also removes that book's file-operation history. Offline root relocation preserves completed history and archive-backup provenance; resolve relevant pending operations first.

### Bulk organization and naming templates

Select up to **100 books across library pages**, then choose **Organize selected**. Choose a destination root and templates, generate the preview, and review every source, destination, hash and conflict. Each row's relative folder and complete filename can be edited; use **Re-preview edited destinations** afterward. Changing the root or templates discards row edits. Any edit invalidates confirmation.

Supported tokens are `{Series}`, `{Year}`, `{Number}`, `{Title}`, `{Publisher}`, `{Volume}` and `{FileName}`. `{FileName}` is the current filename without its extension; the server appends the unchanged archive extension. For example, folder `{Series} ({Year})` and filename `{Series} #{Number}` produce metadata-based destinations. Keep `{FileName}` for a simple bulk move; an empty folder means the selected library root. All destination folders must already exist. Missing metadata, malformed/unknown tokens and unsafe values are reported, not silently replaced or sanitized.

The server revalidates **the entire batch before its first move**. Existing destinations, collisions between selected books, path swaps/cycles and stale source/catalog data block application. Unchanged rows are skipped without journal entries; an entirely unchanged batch cannot be applied.

Execution is **sequential, not all-or-nothing**, using the same verified per-book move engine. The first execution failure stops the batch; earlier committed moves remain. Results distinguish completed, unchanged, failed and not-attempted books. A committed move awaiting journal cleanup can appear as both completed and failed. A lost response leaves the outcome unknown, never an automatic retry. Use the dialog's per-book history buttons—even for unavailable books—to inspect, undo or recover before generating another preview.

## Account management

- **Change password** requires your current password and a different new password of 12–256 characters. Success ends all your sessions; sign in again with the new password. Attempts are limited to five per minute per account.
- Administrators use **Manage accounts** to list/create accounts, change administrator/reader roles, reset another account's password and delete accounts. A reset ends the target's sessions immediately. Their next login shows only the mandatory password-change screen; library APIs remain blocked until they choose a different password. Administrators change their own password through **Change password**, not the reset control.
- Changing a role ends the affected account's sessions. Saving an unchanged role does not. Demoting or deleting the last administrator is rejected, including concurrent requests. An administrator can demote/delete their own account only when another administrator remains.
- Deleting an account permanently removes its progress, bookmarks, ratings, manual/smart lists, list folders, memberships and reader preferences. Shared comics, metadata, source files and other accounts' private data remain intact. Reusing a deleted username creates a new account without inherited history.

Sessions use the `PanelHaven.Session` cookie and are checked against the account database on every authenticated request. Password changes/resets, role changes and deletion invalidate existing cookies; all sessions from before the Panel Haven rename require a fresh sign-in. There is no email recovery or library-specific access control.

## Backup, restore and root relocation

These are operator commands, not browser endpoints. Stop the server for offline `--backup`, restore and relocation; `--backup-live` deliberately permits the running server. Offline operations hold an exclusive sibling `<DataPath>.state.lock`; live backups coordinate with maintenance using `<DataPath>.backup.lock`. Never delete lock files to bypass a running process. Use absolute paths and the same `DataPath` as the server. Source archives are never copied, moved or deleted by these commands.

```bash
# The backup's parent directory must already exist; destination must be new.
dotnet run --project web/PanelHaven.Web.csproj -- --DataPath /private/panelhaven-state --backup /private/backups/panelhaven-before-change

# Live snapshot using an already-built matching application; no server restart.
dotnet /absolute/app/PanelHaven.Web.dll --DataPath /private/panelhaven-state --backup-live /private/backups/panelhaven-live

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

Resolve relevant pending file operations **before moving library files offline**; preview and apply refuse them before changing the catalog. Completed move/undo histories are remapped with durable path provenance, including books now in another configured root whose earlier paths were inside the relocated root. Pass all remaining library roots in the command configuration too. Original archive-backup manifests and payloads are never rewritten.

Relocation preserves the old book ID. Already-imported destination rows merge only when private progress, ratings, bookmarks, list positions and edited metadata do not conflict; otherwise both rows are retained and reported. Successful merges also retain explicit page-edit intent, move histories and access to originals stored under the previous book ID. Each book's catalog/private changes, history remapping and backup-ownership mapping are transactional, so a batch can apply safe pairs while leaving conflicts/skips untouched. Rerunning does not remap already-relocated entries. Review the final counts, not just the process exit code. Restart with the new roots after applying; committed imports are skipped on the next scan.

### Scheduled live backups (web-managed Linux user timer)

Under **Server settings → Scheduled backups**, enable daily backups and choose a destination, local `HH:mm` time and retention from 1–3650 successful snapshots. Defaults are **03:00** and **14** snapshots. Save with your current administrator password and check the displayed timer status. The app generates the owned `panelhaven-backup.service` and `.timer` units; do not install static templates or maintain a second scheduler.

Python 3.9+, Linux user systemd and a durable published application/runtime are required. The publish output includes `scripts/backup.py`, which reads the private managed settings file. A matching existing installation's original backup configuration is imported on initial setup; subsequent saves migrate its units. Its former `backup.env` and separately installed helper are no longer used after migration.

The backup root must be dedicated, outside state, library, settings and application trees, owned by this user and mode `0700`; its parent must exist. Changes apply without restarting the application. Errors are reported without pretending the saved schedule is active; startup reapplies the saved schedule. Disabling backups stops/disables the timer without deleting snapshots.

For host-side diagnostics or an immediate run of the same job:

```bash
systemctl --user start panelhaven-backup.service
systemctl --user list-timers panelhaven-backup.timer
journalctl --user -u panelhaven-backup.service
```

`Persistent=true` catches a missed run when the user manager next becomes active; it does not wake a powered-off machine. Without lingering, the user manager generally runs only while logged in. Use the web settings for schedule changes; custom unit drop-ins are refused rather than silently overridden.

Each run takes an exclusive scheduler lock and invokes the validated `--backup-live` CLI. Busy maintenance, a changing key ring or any backup failure fails the job without pruning old backups. Only after success does retention remove excess marked `scheduled-*` containers with the exact expected snapshot layout; manual/unmarked directories and symlinks (including snapshots containing links) remain untouched. Failed/interrupted containers can remain unmarked for manual inspection, so retention is not a disk quota. Never add files to or repurpose scheduled containers. The newest successful run is always retained, even after a backward clock adjustment.

Restore a **trusted** scheduled backup using the existing offline command with `--restore /private/panelhaven-scheduled-backups/scheduled-.../snapshot` and a new/empty `DataPath`, not the outer marked container. Keep the original intact and test the restored instance before switching state. Snapshots contain password hashes and cookie-signing keys, **are not encrypted**, and exclude comic archives, archive-write undo backups, thumbnails and deployment settings; protect their storage and keep separate off-host copies as needed. The deterministic wrapper check is `python3 scripts/test_backup.py`.

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

The test installation supports LAN HTTPS, but public deployment still requires trusted TLS, firewall rules, protected secrets and an operational security review. Web settings do not configure the firewall or issue certificates. All users share access to all books; library-specific permissions are not implemented.

The private state directory contains SQLite data and persistent cookie-signing keys. Keys are not encrypted at rest by the application; protect this directory with OS permissions and encrypted storage where needed. Keep it outside publicly served folders. Use the live/offline backup and offline restore commands above; do not copy only the live SQLite main file while WAL writes are active.

Non-GET/HEAD API requests require `X-PanelHaven-Request: 1`; the browser client supplies it. The session cookie is HTTP-only and SameSite Strict, with Secure enabled in HTTPS-required mode.

Archives are read without extraction. Requests have a 64 KiB body limit. Archive checks reject traversal names, symlink/reparse paths, duplicate entries, DTDs and unexpected XML roots. Limits: 1 GiB archive, 10,000 entries, 16 MiB ZIP central directory or RAR/7z headers, 32 MiB per page, 2 MiB per XML document, 2 GiB declared expanded size and 1000:1 expansion ratio. ZIP64, multipart and encrypted archives are unsupported. RAR5 and 7z decoder dictionaries are limited to 64 MiB; RAR4 PPMd has a format ceiling of 256 MiB. These are decoder limits, not a total process-memory guarantee. Unsupported 7z codecs, multi-input coder graphs and external header streams are rejected. Solid page access may replay earlier entries, so late pages can be slower. Raster signatures are checked when pages are served. Thumbnail decoding rejects sources above 32 million pixels before raster allocation and runs one decode at a time to bound memory. Native decode calls are not interruptible mid-call.

Library directories must remain administrator-controlled: path checks are not a sandbox against a local attacker concurrently replacing filesystem entries. Symlinks in any path component, including root ancestors, are rejected during ordinary scans/reads. A changed archive requires rescanning before pages or thumbnails can be served. Missing/broken archives retain catalog history unless an administrator explicitly removes a confirmed missing entry. Moving files externally normally creates new identities; use the reviewed rename/move action, or eligible verified offline root relocation, to retain existing IDs and private data. Incremental scanning and cached duplicate hashes assume content changes also change size or modification time; unchanged archives are not rehashed on every scan.

Scan status and its completion revision are process-local, not a durable job queue. HTTP disconnects do not cancel a scan; server shutdown does. Startup automatically reconciles again and skips previously committed unchanged files. Progress reports discovered/attempted archives, not a speculative percentage. Error count is complete; only the first 100 messages are retained. Cancellation is checked between traversal/import steps and archive/hash reads; filesystem or SQLite blocking operations may delay it.

Reader page and thumbnail requests propagate cancellation into archive reads, unlike background scans. Cancellation is checked between bounded reads; a blocking filesystem or decoder call can still delay it.

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

Browser-console checks: `web-checks/settings-page-check.js` covers route/focus/draft behavior; `web-checks/forms-check.js` covers metadata field preservation and filtering, manual-list readiness, and account draft guards. Run on an idle library page as an administrator; the forms check needs at least one book and another account. Neither check saves forms or changes server data.

`web-checks/ui-audit-check.js` exercises visible focus, group-preserving sorting, loading/error recovery, metadata filtering, move/menu dialog focus, reader image reuse, unload protection for account/move/organization drafts and pending work, move permissions/preview invalidation, failed-image spread retry/navigation, and scan dismissal across reloads and denied storage. Run on an idle Library page as an administrator with a configured move root and a readable series whose first selected comicbook has at least four pages. The check loads real application documents, intercepts tested writes, and does not save forms or server data. Reload afterward to discard local-only display preferences. For cleanup-failure verification, change its final invocation from `false` to `true` in the console copy; it should throw `Injected UI audit cleanup failure` while restoring the original `window.fetch` and `window.confirm`.

### Verification history

Codebase-audit remediation passed the full C# checks and pure JavaScript reader checks. Coverage includes older-schema relocation, unchanged revision hashes, page-order reads alongside a reserved SQLite writer, competing page-order saves, cancelled page requests, and abrupt process termination after comicbook replacement but before catalog commit. Crash recovery was verified through an explicit backup restore in a fresh process before rescanning, not automatic startup rollback. A disposable 55-book server passed the real-document browser check and injected cleanup-failure restoration check; real HTTP image delivery and custom page-order save/read/reset succeeded, and the offline relocation CLI merged a catalog lacking the optional page-metadata table.

UI/UX audit fixes were verified with the full C# checks, the JavaScript reader checks and both retained browser-console checks. An isolated 55-book server exercised real sparse metadata saves, manual-list creation during a simulated rule-schema failure, account-role updates preserving another account's password draft, move/organization draft guards, stale-route races, cover retry, and save-failure navigation protection. The deployed site was visually checked at 1920, 1440, 768, 390 and 320 CSS-pixel widths; the inspected library layouts had no page-wide horizontal overflow. Physical-device/Safari, real screen-reader and field-performance verification remain outstanding.

Server-administration verification passed the full C#/JavaScript/Python checks, desktop/mobile browser settings saves, secret keep/set/clear and redaction, password/role checks, stale-draft protection and saved-versus-running status. An isolated real restart applied saved settings over prior environment/CLI values and moved the complete database, private data, key ring and archive vault while retaining the source; a conflicting destination remained untouched and the original active state restarted with a reported error. On the Linux host, native systemd validation and real web/API startup toggles, unmanaged-to-managed handoff, managed restart, backup schedule changes/restoration and the migrated backup job succeeded. A unit-generation error found during smoke verification was fixed by passing an explicit content root in the launch command rather than quoting `WorkingDirectory`. The existing 5,541-book catalog and private tables were unchanged during cutover; application autostart remains disabled and backups remain enabled at 03:00 with retention 14.

Comic Vine verification passed the full C#/JavaScript checks and exercised real catalog preview/apply through desktop/mobile Chromium with simulated provider responses: selective fields, empty-field preservation, stale-save rejection, pagination, empty searches, rate-limit feedback, pending-search cancellation and text-safe rendering. Real HTTP checks confirmed missing-key errors and administrator-only access; source archive hashes and unknown catalog XML were preserved. The client checks cover mapping, bounded responses, trusted URLs, caching, request spacing and sanitized failures. Successful live Comic Vine searches remain unverified until an API key is configured.

The following records earlier development verification, not CI status or a guarantee for every platform.

Verified on Linux: build and retained checks; live HTTP pagination/filter bounds, scan authorization/single-flight handling and thumbnail authentication; desktop/mobile browser pagination, sorting, filters, generated covers, reader return state and completion. A real 4,061-archive scan was cancelled after partial import, reconnected after reload and resumed with committed archives skipped. Windows execution has not been verified.

Reader verification additionally exercised desktop and mobile-emulated Chromium: fullscreen entry/exit, zoom overflow/panning, odd-page and RTL spreads, landscape splits, lazy continuous rendering, visible-page progress and reload restoration, account-specific preferences, real browser touch swipes and pinch gestures, and retry behavior for simulated image/preference-save failures. Physical-device and other-browser verification remains outstanding.

Archive/management verification also exercised retained checks with real RAR4/RAR5, solid/non-solid 7z and TAR fixtures, including directory entries, encrypted/multipart rejection and dictionary bounds. Live HTTP/browser checks covered archive scans/pages/thumbnails, extended single-book metadata, sparse 24-book edits and custom values, cross-page list membership/reordering, removal/rename, live unread membership after reading, restart persistence, atomic failures and reader-role/list-ownership restrictions.

The remaining reader batch was checked with an eight-page classified archive in live Chromium: saved rotation/color settings and bookmark reloads, bookmark delete/jumps, embedded bookmark jumps, failed bookmark save/retry and leave protection, advertisement skipping, explicit skipped-page reveal, empty filtered state, rotation-aware spreads, continuous lazy loading, filtered completion and mobile-emulated layout without horizontal overflow. Retained checks cover old preference-schema migration, strict new preference validation, bookmark upserts/ownership/bounds and malformed/unknown/duplicate page metadata. Other browsers and physical devices remain unverified.

Maintenance verification exercised a live 31-book catalog: automatic startup, exact-copy reporting, series/publisher/folder stacks including empty-key drilldown, source disappearance/restoration, preserved metadata/progress/bookmarks/lists, corrupt-source unavailability, explicit confirmed catalog removal without touching surviving source bytes, administrator restrictions and mobile grouping layout. Retained watcher checks exercise real filesystem bursts, changes during scans, cancellation, state-directory exclusion, offline restart changes and recovery of a disconnected root through the real one-minute reconciliation interval; the check suite therefore takes roughly 80–90 seconds.

Account verification exercised real browser/API creation, current-password rejection, password changes, administrator resets, forced-change login without library requests, cross-session revocation, promotions/demotions, protected last-administrator errors, deletion of private rows and memberships, unchanged shared metadata/source bytes, username reuse and password-attempt throttling. Mobile-emulated Chromium checked account/password screens. Retained checks cover legacy account migration, stale/malformed sessions, concurrent password changes and last-administrator mutations, and rejection of private-data writes after account deletion.

Organization verification upgraded a real pre-feature catalog with existing lists/progress, then exercised live API and desktop/mobile browser rating saves/filtering, failure/retry and discard protection, folder creation/rename/deletion without list loss, smart-rule freshness after single/bulk metadata edits, filtered series statistics, account-switch isolation and account deletion cleanup. Source archive bytes remained unchanged. Retained checks cover migration, sorting/pagination, rule all/any semantics, invalid inputs, private ownership, deleted-account writes and catalog cascades.

The nested-folder and comparison batch exercised a 53-archive isolated catalog with five byte-identical copies: parent moves, self/descendant and cross-account rejection, lossless folder deletion, expanded literal/numeric/unknown-year rules, read-only previews and preview/save parity. Browser checks covered hierarchy choices, preview pagination, late-response invalidation, failed-preview retry, smart-save gating, account-switch cleanup, anchored duplicate pages and mobile layouts. Duplicate metadata differences and requester-only progress/ratings were verified; preview/comparison left database rows and source archive bytes unchanged. Retained checks include nested-folder migration and backup/restore preservation. Deployment preserved all existing real catalog/private rows and verified the new preview against 5,541 available books.

Metadata-workflow verification exercised real API/browser reports and book navigation, missing/partial/leap-date handling, name variations, exact previews, draft invalidation, concurrent-edit rejection and successful re-preview/apply on mobile. Reader JSON/CSV downloads were captured and parsed, including formula protection and quotes/newlines; simulated export failures retained selection for retry. Retained checks cover read-only behavior, no-op preservation, concurrent apply, custom-key casing, unknown XML, private-data exclusion and invalid selections. Source archive bytes remained unchanged.

Archive-write verification used only disposable comics. Real API/browser writes and restores checked exact preview XML, unchanged nonmetadata payloads, byte-exact undo, retained catalog edits/private data, confirmation and administrator restrictions, stale/replayed requests, tampered backups, and same-size/mtime external changes. An actual SQLite trigger failure exercised post-replacement rollback and immediate page reading. Linux checks covered read-only/xattr refusal and private backup permissions. Browser checks covered failed-operation history refresh, invalidated confirmations, mobile XML scrolling and account-switch cleanup. Retained archive checks also cover cancellation, unsafe paths, unknown metadata and re-import after scalar removal.

The page/rule/export batch exercised disposable archives through real APIs and desktop/mobile browsers: page draft retention, exact preview/save binding, stale rejection, independent personal bookmarks, page write-back/undo, nested-rule preview/save/reopen and live metadata/private-state matching. Native browser archive downloads were parsed and every original hash checked against its manifest. Corrupt exports failed without partial ZIPs or temporary-file leaks; empty summaries, retry, delayed-response invalidation and account-switch cleanup were exercised. Retained checks also recover an exported vault with the matching catalog and retain orphaned originals.

File-operation verification used disposable archives through real HTTP and desktop/mobile Chromium: exact previews, explicit confirmation, edited-draft invalidation, same-mount inode preservation, cross-filesystem moves/undo, unchanged metadata/private rows, reader restrictions, collisions, unsafe paths and same-size/mtime stale sources. Real SQLite failures restored originals and refreshed browser history. Two actual server kills—during copying and after destination publication before catalog commit—verified restart reservations, duplicate-import prevention and explicit recovery; altered destinations blocked cleanup, and unknown partial copies stayed untouched. Original backups restored byte-exactly after three journaled moves. Retained checks cover every supported archive extension, committed-pending recovery, strict requests and provenance despite clock rollback.

Bulk/root-organization verification exercised a pre-upgrade 30-book disposable catalog with existing private data, page intent, move history and immutable originals. Real offline previews were read-only; injected SQL failure rolled back, repeated relocations preserved undo/restore, and unresolved batch recovery blocked relocation. A real duplicate merge retained the original book ID, previous-owner backups, move history, page intent and private data; authenticated undo/restore worked afterward. Desktop/mobile browser checks covered cross-page selection, template/row invalidation, collision and missing-metadata errors, mixed no-op application, partial failure, unavailable-book recovery, per-book undo and account isolation. A deliberately dropped browser response after a real commit showed an unknown outcome without retrying. Retained checks also cover strict requests, unsafe tokens, namespace-compatible metadata, stale later rows and committed-but-pending batch results.

ACL/NFS verification used disposable archives outside the real library on its actual storage mount. Single and browser-driven bulk moves/undo preserved original inodes, complete archive bytes and exact NFSv4 ACL values. ACL-only changes, external hardlinks, occupied targets and cross-mount ACL inheritance were refused; a real SQLite failure restored the original. Two actual process kills interrupted exclusive linking before unlinking the source and before unlinking the held original. Restart reserved both aliases without duplicate imports; changed ACLs, foreign links and stale link-topology previews blocked recovery. Authenticated API and mobile-browser recovery restored the original inode and ACL without changing private data. Retained checks cover attribute snapshots, legacy journals and owned-alias recovery. NFS server/network power-loss behavior remains unverified.

Page-order verification upgraded an existing two-account disposable catalog on actual NFS storage without changing its prior rows. Desktop Chromium exercised native dragging, keyboard reordering, explicit saves, concurrent-edit rejection and draft retention. Mobile-emulated Chromium exercised move buttons, reset/discard protection and active-reader physical identity. Single/continuous/spread modes, RTL, cover-alone, landscape splitting, skip/reveal, both bookmark kinds, reordered completion, reload resume and reader-role isolation were exercised. Exact source bytes and NFS ACLs stayed unchanged by organization. A same-size/same-timestamp source replacement was refused and its obsolete order reset after rescan. Retained checks also cover strict permutations, reset revision history, move/undo, relocation, source shrinking, metadata write/restore and state-backup restoration.

Comic-conversion verification used real NFS CBZ, RAR4/RAR5, solid/non-solid 7z and TAR fixtures. Authenticated single/batch HTTP downloads and native desktop/mobile Chromium downloads were reopened and checked against every source image byte, saved order, remapped cover/page metadata, unknown XML and custom values. A browser-downloaded CBZ was imported by a separate running application and served every page in the exported order without private bookmarks/progress. Invalid selections, missing batch members, changed sources, non-file/truncated browser responses, preparation cancellation, throttled-transfer cancellation and navigation cancellation were exercised without partial downloads or leaked staging files. An actual HTTP disconnect after server staging began also cleaned up. Original inode/permissions/ACLs and catalog/private rows remained unchanged by export; retained checks cover limits, custom-value roundtrip and legacy reading-state exclusion.

Operations verification exercised real offline preview/apply against duplicate catalog rows, unchanged original IDs/private state and real page serving afterward, active-server maintenance refusal, backup/restore into fresh state and original-login/private-data recovery. HTTPS smoke used a disposable certificate with certificate verification, Secure cookies, HSTS, rejected hosts/plain HTTP, explicitly trusted forwarding, an untrusted loopback peer and forwarded-client rate-limit separation. Retained checks cover conflict refusal, unsafe mappings, corrupt/unsafe backups, exclusive state access and middleware trust boundaries. Public deployment and Windows execution remain unverified.

Scheduled-backup verification ran the installed backup-only user job against the live 5,541-book catalog while 86 catalog requests succeeded. Every database table and signing-key file matched the isolated restore; original credentials, metadata, preferences, all three saved reading positions and real page serving worked without changing live state. Disposable live snapshots rejected concurrent key-content and filename changes without publishing partial backups. Retained checks cover committed WAL recovery, maintenance exclusion, failed-backup preservation, retention counts and manual-directory/symlink boundaries. No application service or application boot startup was installed.

The real root relocation preserved 21 existing book IDs and their saved progress. The subsequent full 5,541-archive scan completed with zero errors, missing entries, unavailable entries or byte-identical duplicates.

## Full-port parity inventory

This inventory is based on the upstream engine, database, provider, display and dialog source. It is a roadmap, not a claim that the missing functionality exists.

| Area | Current implementation | Remaining for full port |
| --- | --- | --- |
| Embedded metadata | ComicInfo/ComicBook metadata import | Legacy desktop database and list import explicitly excluded |
| Archive/image support | CBZ/ZIP, CBR/RAR4/RAR5, CB7/7z, CBT/TAR with PNG/JPEG/GIF/WebP | DjVu, web comics, additional image codecs and large/ZIP64 archives; PDF excluded by request |
| Library organization | Paginated grid/stacks, series/publisher/folder grouping, filtered series reading statistics, search/filters, cached thumbnails, watched folders, missing/unavailable tracking, exact-duplicate reports with read-only comparison and explicit missing-entry removal | Further collection organization and safe duplicate-resolution actions |
| Reader | Single/continuous/spread modes, zoom/pan/fullscreen, RTL/touch, visual adjustments, personal/embedded bookmarks, editable page types, shared non-destructive page order and saved preferences/progress | Further desktop parity, image editing and physical archive page insertion/removal |
| Metadata | 42 scalar fields, custom values, validation, stale-safe previews, page-type/bookmark editing, manual Comic Vine search/review, JSON/CSV export and explicit CBZ/ZIP write-back with verified undo | Further page-structure editing and write-back for other archive formats |
| Lists | Private manual lists, ordering, nested folders and bounded nested AND/OR smart rules, matching previews and additional metadata fields | Further metadata/custom-field coverage; no legacy list import |
| File management | Reviewed Linux single/bulk rename/move with metadata templates, same-mount ACL preservation, no-overwrite NFS fallback, durable recovery/history and verified undo; journal-aware offline root relocation; atomic CBZ/ZIP metadata replacement; verified download-only single/batch CBZ conversion | Cross-mount ACL/xattr transfer, additional output formats and file-operation support beyond Linux |
| Automation | Automatic folder watching and startup/periodic scan reconciliation | Bulk metadata matching, automation rules and persistent background jobs; no legacy plugin-runtime requirement |
| Accounts/operations | Admin/reader roles; account lifecycle and private data; web-managed server configuration and secrets; verified whole-state data relocation; Linux user-service startup/restart; state backups/restore and web-managed backup timer; separate verified archive-original exports; HTTPS-required mode and explicit proxy/host trust | Self-service recovery, library permissions, public-deployment review and encrypted/off-host automation |

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
