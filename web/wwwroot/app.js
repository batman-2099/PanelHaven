import { spreadPages, swipeTurn, pageIncluded, nextPage, rotatedDimensions, pageGeometry } from "./reader-logic.mjs";

const $ = (id) => document.getElementById(id);
let session = null;
let authEpoch = 0;
let currentBook = null;
let detailBook = null;
let currentPage = 0;
let pageNumberDraft = false;
let readerPreferences = null;
let preferenceLoad = null;
let preferenceQueue = Promise.resolve();
let unsavedPreferences = null;
let pages = [];
let displayedPages = [];
let activePages = [];
let revealedPage = null;
let personalBookmarks = [];
let bookmarkEditorPage = null;
let bookmarkQueue = Promise.resolve();
let unsavedBookmark = null;
let bookmarkBusy = false;
let nearPages = new Set();
let pageObserver = null;
let imageLoads = 0;
let viewGeneration = 0;
let settleTimer = null;
let viewAnchor = null;
let lastViewedPage = null;
let swipe = null;
const touchPointers = new Set();
let searchGeneration = 0;
let detailGeneration = 0;
let progressQueue = Promise.resolve();
let unsavedProgress = null;
let routeQueue = Promise.resolve();
let leavingReader = false;
let libraryPage = 1;
let libraryPages = 1;
let libraryQuery = "";
let publisherGeneration = 0;
let groupKey = null;
let groupLabel = "";
let maintenancePage = 1;
let maintenancePages = 1;
let maintenanceGeneration = 0;
let maintenanceBusy = false;
const failedCovers = new Set();
let scanStatus = null;
let scanTimer = null;
let scanRequest = null;
let scanBusy = false;
let libraryItems = [];
const selectedBooks = new Set();
let readingLists = [];
let listFolders = [];
let folderEditingId = "";
let folderDirty = false;
let folderBusy = false;
let ratingDirty = false;
let ratingBusy = false;
let listsGeneration = 0;
let listEditingId = null;
let listDialogGeneration = 0;
let managementBusy = false;
let metadataSchema = null;
let metadataBookIds = [];
let metadataGeneration = 0;
let metadataBulk = false;
let metadataDirty = false;
let metadataBusy = false;
let metadataPreview = null;
let metadataReportPage = 1;
let metadataReportPages = 1;
let metadataReportGeneration = 0;
let metadataExportBusy = false;

function showError(error) {
  if (error.name === "AbortError") return;
  for (const id of ["error", "reader-error"]) {
    $(id).textContent = error.message || String(error);
    $(id).hidden = id === "error" ? !$("reader-view").hidden : $("reader-view").hidden;
  }
}

function clearError() {
  for (const id of ["error", "reader-error"]) {
    $(id).hidden = true;
    $(id).textContent = "";
  }
}

function showView(name) {
  for (const view of ["login", "library", "reader", "password"]) $(view + "-view").hidden = view !== name;
  $("startup").hidden = true;
}

function clearReader() {
  stopReaderView();
  pages = [];
  displayedPages = [];
  activePages = [];
  revealedPage = null;
  personalBookmarks = [];
  bookmarkEditorPage = null;
  $("personal-bookmarks").replaceChildren();
  $("embedded-bookmarks").replaceChildren();
  $("embedded-bookmarks-section").hidden = true;
  $("bookmark-name").value = "";
  $("bookmark-status").textContent = "";
  $("retry-bookmark").hidden = true;
  $("current-page-type").textContent = "";
  $("skip-status").hidden = true;
  lastViewedPage = null;
  pageNumberDraft = false;
  currentBook = null;
  $("reader-pages").replaceChildren();
  const epoch = authEpoch;
  if (document.fullscreenElement === $("reader-view")) document.exitFullscreen().catch((error) => { if (epoch === authEpoch) showError(error); });
}

function showLogin() {
  authEpoch++;
  session = null;
  $("login-form").reset();
  $("password-form").reset();
  $("user-form").reset();
  $("user-list").replaceChildren();
  $("login-message").textContent = "";
  $("password-status").textContent = "";
  $("list-form").reset();
  $("folder-form").reset();
  $("rating-form").reset();
  $("rating-form").hidden = true;
  $("rating-status").textContent = "";
  $("folder-status").textContent = "";
  folderEditingId = "";
  folderDirty = folderBusy = ratingDirty = ratingBusy = false;
  listFolders = [];
  $("folder-choice").replaceChildren(new Option("New folder", ""));
  $("list-folder").replaceChildren(new Option("Unfiled", ""));
  for (const id of ["details-content", "details-bookmarks", "metadata-fields", "custom-values", "list-rules", "maintenance-items"])
    $(id).replaceChildren();
  unsavedProgress = null;
  readerPreferences = null;
  preferenceLoad = null;
  unsavedPreferences = null;
  preferenceQueue = Promise.resolve();
  progressQueue = Promise.resolve();
  bookmarkQueue = Promise.resolve();
  unsavedBookmark = null;
  bookmarkBusy = false;
  $("preferences-status").textContent = "";
  $("retry-preferences").hidden = true;
  resetScan();
  publisherGeneration++;
  libraryPage = 1;
  libraryPages = 1;
  libraryQuery = "";
  $("search").value = "";
  $("book-sort").value = "series";
  $("sort-direction").value = "asc";
  $("sort-direction").disabled = false;
  managementBusy = false;
  $("book-status").value = "all";
  $("book-rating").value = "";
  $("book-availability").value = "all";
  $("group-by").value = "none";
  groupKey = null;
  groupLabel = "";
  maintenanceGeneration++;
  maintenanceBusy = false;
  maintenancePage = 1;
  $("maintenance-kind").value = "missing";
  failedCovers.clear();
  $("page-size").value = "48";
  $("publisher").replaceChildren(new Option("All publishers", ""));
  selectedBooks.clear();
  libraryItems = [];
  readingLists = [];
  listsGeneration++;
  metadataGeneration++;
  metadataReportGeneration++;
  metadataReportPage = metadataReportPages = 1;
  $("metadata-report-kind").value = "all";
  $("metadata-report-items").replaceChildren();
  $("metadata-report-status").textContent = "";
  $("metadata-report-page").textContent = "Page 1 of 1";
  metadataDirty = metadataBusy = metadataBulk = metadataExportBusy = false;
  invalidateMetadataPreview();
  $("metadata-status").textContent = "";
  listDialogGeneration++;
  metadataBookIds = [];
  $("reading-list").replaceChildren(new Option("All books", ""));
  $("add-to-list").replaceChildren(new Option("Choose a list", ""));
  $("management-status").textContent = "";
  updateSelection();
  detailBook = null;
  leavingReader = false;
  clearReader();
  searchGeneration++;
  detailGeneration++;
  for (const dialog of document.querySelectorAll("dialog[open]")) dialog.close();
  $("account").hidden = true;
  $("books").replaceChildren();
  $("books").removeAttribute("aria-busy");
  history.replaceState(null, "", "#library");
  showView("login");
  $("login-form").elements.username.focus();
}

async function api(path, options = {}) {
  if (session?.mustChangePassword && !["/api/session", "/api/logout", "/api/account/password"].includes(path))
    throw new DOMException("Password change required.", "AbortError");
  const epoch = authEpoch;
  const headers = new Headers(options.headers);
  if (options.method && options.method !== "GET") headers.set("X-ComicRack-Request", "1");
  if (options.body) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...options, headers, credentials: "same-origin" });
  if (!response.ok) {
    let message = `Request failed (${response.status}). Please try again.`;
    let mustChangePassword = false;
    try {
      const error = await response.json();
      message = error.error || message;
      mustChangePassword = error.mustChangePassword === true;
    } catch { /* Non-JSON server errors retain the status. */ }
    if (epoch !== authEpoch) throw new DOMException("Session changed.", "AbortError");
    if (response.status === 403 && mustChangePassword) {
      requirePasswordChange({ ...session, mustChangePassword: true });
      throw new DOMException("Password change required.", "AbortError");
    }
    if (response.status === 401) showLogin();
    throw Object.assign(new Error(message), { status: response.status });
  }
  const data = options.blob ? await response.blob() : await response.text();
  if (epoch !== authEpoch) throw new DOMException("Session changed.", "AbortError");
  return options.blob ? data : data ? JSON.parse(data) : null;
}

function signedIn(data) {
  if (data.mustChangePassword) { requirePasswordChange(data); return; }
  authEpoch++;
  session = data;
  readerPreferences = null;
  preferenceLoad = null;
  resetScan();
  $("username").textContent = data.username;
  $("account").hidden = false;
  $("change-password").hidden = false;
  $("admin-actions").hidden = data.role !== "admin";
  $("watch-status").hidden = data.role !== "admin";
  $("login-form").reset();
  clearError();
  $("bulk-metadata").hidden = data.role !== "admin";
  loadLists();
  scheduleRoute();
  if (data.role === "admin") requestScan();
}

function requirePasswordChange(data) {
  showLogin();
  session = data;
  $("username").textContent = data.username || "";
  $("account").hidden = false;
  openPasswordView();
}

function openPasswordView() {
  $("password-form").reset();
  $("password-status").textContent = "";
  $("password-explanation").textContent = session.mustChangePassword
    ? "Your password was reset. You must choose a different password of 12–256 characters before accessing the library. All your sessions will end."
    : "Choose a different password of 12–256 characters. All your sessions will end after this change.";
  $("cancel-password").hidden = session.mustChangePassword;
  $("change-password").hidden = true;
  clearError();
  showView("password");
  $("password-form").elements.currentPassword.focus();
}

function bookTitle(book) {
  return book.title || [book.series, book.number && `#${book.number}`].filter(Boolean).join(" ") || "Untitled comic";
}

function textElement(tag, text, className) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) element.className = className;
  return element;
}

async function loadLibrary() {
  if (!session) return;
  const generation = ++searchGeneration;
  const grouping = $("group-by").value !== "none" && groupKey === null;
  $("group-navigation").hidden = groupKey === null;
  $("group-label").textContent = groupLabel;
  $("books").setAttribute("aria-label", grouping ? "Book groups" : "Books");
  libraryItems = [];
  updateSelection();
  $("library-status").textContent = "Loading your library…";
  $("books").setAttribute("aria-busy", "true");
  $("previous-books").disabled = true;
  $("next-books").disabled = true;
  const query = new URLSearchParams({
    q: libraryQuery, page: libraryPage, pageSize: $("page-size").value,
    sort: $("book-sort").value, direction: $("sort-direction").value,
    status: $("book-status").value, publisher: $("publisher").value,
    availability: $("book-availability").value, groupBy: $("group-by").value
  });
  if ($("reading-list").value) query.set("listId", $("reading-list").value);
  if ($("book-rating").value !== "") query.set("rating", $("book-rating").value);
  if (groupKey !== null) query.set("groupKey", groupKey);
  try {
    const result = await api(`/api/${grouping ? "book-groups" : "books"}?${query}`);
    if (generation !== searchGeneration || !session) return;
    libraryPage = result.page;
    libraryPages = Math.max(1, Math.ceil(result.total / result.pageSize));
    if (libraryPage > libraryPages) { libraryPage = libraryPages; await loadLibrary(); return; }
    libraryItems = grouping ? [] : result.items;
    $("library-page").textContent = `Page ${libraryPage} of ${libraryPages}`;
    $("previous-books").disabled = libraryPage <= 1;
    $("next-books").disabled = libraryPage >= libraryPages;
    let checkedCoverSession = false;
    const cards = result.items.map((book) => {
      const card = textElement("article", "", grouping ? "book-card group-card" : "book-card");
      const title = grouping ? book.label : bookTitle(book);
      const coverId = grouping ? book.coverId : book.availability === "available" ? book.id : null;
      const cover = textElement("button", "", "cover-button");
      cover.type = "button";
      cover.setAttribute("aria-label", `View ${title}`);
      const image = document.createElement("img");
      image.alt = "";
      image.loading = "lazy";
      if (coverId && !failedCovers.has(coverId)) image.src = `/api/books/${encodeURIComponent(coverId)}/thumbnail`;
      image.addEventListener("error", () => {
        failedCovers.add(coverId);
        cover.replaceChildren(textElement("span", "Cover unavailable", "cover-fallback"));
        if (!checkedCoverSession && generation === searchGeneration) {
          checkedCoverSession = true;
          api("/api/session").then((data) => {
            if (generation === searchGeneration && !data.authenticated) showLogin();
            else if (generation === searchGeneration && data.mustChangePassword) requirePasswordChange(data);
          }).catch((error) => { if (generation === searchGeneration) showError(error); });
        }
      }, { once: true });
      cover.append(coverId && !failedCovers.has(coverId) ? image : textElement("span", "Cover unavailable", "cover-fallback"));
      cover.addEventListener("click", () => {
        if (!grouping) { openDetails(book.id); return; }
        groupKey = book.key;
        groupLabel = book.label;
        libraryPage = 1;
        clearSelection();
        loadLibrary();
      });
      card.append(cover, textElement("h2", title));
      if (grouping) {
        card.append(textElement("p", `${book.count} ${book.count === 1 ? "book" : "books"}`, "book-status"));
        if ($("group-by").value === "series") {
          card.append(textElement("p", `${book.unread} unread · ${book.inProgress} in progress · ${book.completed} completed`, "series-statistics"));
          const progress = document.createElement("progress");
          progress.max = 100;
          progress.value = book.completionPercent;
          progress.setAttribute("aria-label", `${title}: ${book.completionPercent}% of books completed`);
          card.append(progress, textElement("p", `${book.completionPercent}% of books completed`, "muted"));
        }
        return card;
      }
      card.append(textElement("p", book.availability, book.availability === "available" ? "muted" : "book-availability"),
        textElement("p", book.location, "muted"));
      const selection = textElement("label", "", "book-selection");
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.dataset.bookId = book.id;
      checkbox.checked = selectedBooks.has(book.id);
      checkbox.setAttribute("aria-label", `Select ${bookTitle(book)}`);
      checkbox.addEventListener("change", () => {
        if (checkbox.checked && selectedBooks.size >= 100) {
          checkbox.checked = false;
          $("management-status").textContent = "Select at most 100 books. Clear some selections first.";
        } else if (checkbox.checked) selectedBooks.add(book.id);
        else selectedBooks.delete(book.id);
        updateSelection();
      });
      selection.append(checkbox, document.createTextNode("Select"));
      card.append(selection);
      card.append(textElement("p", [book.series, book.number && `#${book.number}`].filter(Boolean).join(" · "), "muted"));
      card.append(textElement("p", book.completed ? "Completed" : book.started ? `Page ${Math.min(book.currentPage + 1, book.pageCount)} of ${book.pageCount}` : "Unread", "book-status"));
      if (activeList()?.kind === "manual" && $("book-sort").value === "list") {
        const moves = textElement("div", "", "book-order");
        for (const [direction, label] of [[-1, "Move up"], [1, "Move down"]]) {
          const button = textElement("button", label);
          button.type = "button";
          button.disabled = managementBusy;
          button.setAttribute("aria-label", `${label}: ${bookTitle(book)}`);
          button.addEventListener("click", () => moveListBook(book.id, direction));
          moves.append(button);
        }
        card.append(moves);
      }
      return card;
    });
    $("books").replaceChildren(...cards);
    updateSelection();
    const filtered = libraryQuery || $("book-status").value !== "all" || $("book-rating").value !== "" || $("publisher").value || $("book-availability").value !== "all" || groupKey !== null;
    const unit = grouping ? "group" : "book";
    $("library-status").textContent = result.total
      ? `${result.total} ${unit}${result.total === 1 ? "" : "s"} · Showing ${(libraryPage - 1) * result.pageSize + 1}–${(libraryPage - 1) * result.pageSize + result.items.length}`
      : $("reading-list").value ? filtered ? "No books in this list match your filters." : activeList()?.kind === "smart" ? "No books match this smart list’s rules." : "This reading list is empty. Select books in All books, then add them to this list."
      : filtered ? "No books match. Try another search or change the filters." : "Your library is empty. An administrator can scan the configured library folders to add comics.";
  } catch (error) {
    if (generation !== searchGeneration) return;
    $("books").replaceChildren();
    libraryItems = [];
    updateSelection();
    $("library-page").textContent = "Pages unavailable";
    $("library-status").textContent = "Could not load the library. Search again to retry.";
    showError(error);
  } finally {
    if (generation === searchGeneration) $("books").removeAttribute("aria-busy");
  }
}

async function loadPublishers() {
  const generation = ++publisherGeneration;
  try {
    const result = await api("/api/book-filters");
    if (generation !== publisherGeneration || !session) return;
    const selected = $("publisher").value;
    const publishers = result.publishers;
    if (selected && !publishers.includes(selected)) publishers.push(selected);
    $("publisher").replaceChildren(new Option("All publishers", ""), ...publishers.map((publisher) => new Option(publisher, publisher)));
    $("publisher").value = selected;
  } catch (error) {
    if (generation === publisherGeneration) showError(error);
  }
}

function resetScan() {
  clearTimeout(scanTimer);
  scanRequest?.abort();
  scanRequest = null;
  scanStatus = null;
  scanBusy = false;
  $("scan-panel").hidden = true;
  $("scan-error").hidden = true;
  $("scan-errors").hidden = true;
  $("scan-errors").open = false;
  $("retry-scan-status").hidden = true;
  $("cancel-scan").hidden = true;
  $("scan").disabled = false;
  $("watch-status").textContent = "";
  $("watch-status").hidden = true;
}

function renderScan() {
  const active = scanStatus?.state === "running" || scanStatus?.state === "cancelling";
  $("scan").disabled = scanBusy || active;
  $("cancel-scan").hidden = !active;
  $("cancel-scan").disabled = scanBusy || scanStatus?.state === "cancelling";
  $("retry-scan-status").disabled = scanBusy;
  if (!scanStatus) return;
  $("scan-panel").hidden = scanStatus.state === "idle" && $("scan-error").hidden;
  const label = {
    idle: "No scan has started.", running: "Scanning libraries…", cancelling: "Cancelling scan…",
    completed: "Scan completed.", cancelled: "Scan cancelled. Changes already scanned were kept.", failed: "Scan failed. Review the errors and try scanning again."
  }[scanStatus.state];
  $("scan-result").textContent = `${label}\n${scanStatus.scanned} scanned · ${scanStatus.added} added · ${scanStatus.updated} updated · ${scanStatus.skipped} skipped · ${scanStatus.errorCount} error(s)`;
  $("scan-errors").hidden = scanStatus.errorCount === 0;
  $("scan-error-count").textContent = `${scanStatus.errorCount} scan error(s)${scanStatus.errorCount > scanStatus.errors.length ? ` (showing first ${scanStatus.errors.length})` : ""}`;
  $("scan-error-list").textContent = scanStatus.errors.join("\n");
}

async function requestScan(action = "status") {
  if (session?.role !== "admin" || scanBusy) return;
  const epoch = authEpoch;
  clearTimeout(scanTimer);
  scanBusy = true;
  scanRequest = new AbortController();
  renderScan();
  let retry = false;
  try {
    const result = await api(action === "cancel" ? "/api/scan/cancel" : "/api/scan", {
      method: action === "status" ? "GET" : "POST",
      signal: AbortSignal.any([scanRequest.signal, AbortSignal.timeout(15000)])
    });
    if (epoch !== authEpoch) return;
    const previous = scanStatus;
    scanStatus = result;
    $("scan-error").hidden = true;
    $("retry-scan-status").hidden = true;
    renderScan();
    if (["completed", "cancelled", "failed"].includes(result.state) && (previous?.revision !== result.revision || previous?.state !== result.state || action === "start")) {
      failedCovers.clear();
      loadLibrary();
      loadPublishers();
      if ($("maintenance-dialog").open) loadMaintenance();
      if (detailBook && $("details-dialog").open) refreshDetailAvailability();
    }
    try {
      const watch = await api("/api/watch-status", { signal: AbortSignal.any([scanRequest.signal, AbortSignal.timeout(15000)]) });
      if (epoch !== authEpoch) return;
      $("watch-status").textContent = `Folder watching: ${watch.watchingRoots} active · ${watch.unavailableRoots} unavailable${watch.pending ? " · Scan pending" : ""}.`;
    } catch (error) {
      if (epoch === authEpoch) $("watch-status").textContent = `Could not check folder watching: ${error.message}. Retrying with scan status.`;
    }
  } catch (error) {
    if (epoch !== authEpoch) return;
    retry = true;
    $("scan-panel").hidden = false;
    if (!scanStatus) $("scan-result").textContent = "Scan status is unknown.";
    $("scan-error").textContent = `${action === "status" ? "Could not check scan status" : action === "cancel" ? "Could not confirm cancellation" : "Could not confirm scan start"}: ${error.message} Checking status again shortly; you can also use Check scan status.`;
    $("scan-error").hidden = false;
    $("retry-scan-status").hidden = false;
  } finally {
    if (epoch === authEpoch) {
      scanBusy = false;
      scanRequest = null;
      renderScan();
      // ponytail: schedule only after the request settles, so polling never overlaps.
      scanTimer = setTimeout(() => requestScan(), retry ? 3000 : scanStatus?.state === "running" || scanStatus?.state === "cancelling" ? 1000 : 5000);
    }
  }
}

async function loadMaintenance() {
  if (session?.role !== "admin" || !$("maintenance-dialog").open) return;
  const generation = ++maintenanceGeneration;
  const kind = $("maintenance-kind").value;
  $("maintenance-status").textContent = "Loading maintenance records…";
  $("previous-maintenance").disabled = true;
  $("next-maintenance").disabled = true;
  $("duplicate-note").hidden = kind !== "duplicates";
  try {
    const result = await api(`/api/maintenance?${new URLSearchParams({ kind, page: maintenancePage, pageSize: 48 })}`);
    if (generation !== maintenanceGeneration) return;
    maintenancePages = Math.max(1, Math.ceil(result.total / result.pageSize));
    maintenancePage = result.page;
    if (maintenancePage > maintenancePages) { maintenancePage = maintenancePages; await loadMaintenance(); return; }
    const rows = [];
    let duplicateKey = null;
    for (const book of result.items) {
      if (kind === "duplicates" && book.duplicateKey !== duplicateKey) {
        duplicateKey = book.duplicateKey;
        rows.push(textElement("h3", `SHA-256: ${duplicateKey}`, "duplicate-key"));
      }
      const row = textElement("article", "", "maintenance-row");
      row.append(textElement("h3", bookTitle(book)), textElement("p", book.location),
        textElement("p", book.availability, book.availability === "available" ? "muted" : "book-availability"));
      const actions = textElement("div", "", "actions list-actions");
      const inspect = textElement("button", "Book details");
      inspect.type = "button";
      inspect.setAttribute("aria-label", `Book details: ${bookTitle(book)}`);
      inspect.addEventListener("click", () => openDetails(book.id));
      actions.append(inspect);
      if (kind === "missing" && book.availability === "missing") {
        const remove = textElement("button", "Remove catalog entry");
        remove.type = "button";
        remove.disabled = maintenanceBusy;
        remove.setAttribute("aria-label", `Remove catalog entry: ${bookTitle(book)}`);
        remove.addEventListener("click", () => removeMissingBook(book, remove));
        actions.append(remove);
      }
      row.append(actions);
      rows.push(row);
    }
    $("maintenance-items").replaceChildren(...rows);
    $("maintenance-status").textContent = result.total
      ? `${result.total} records · Showing ${(maintenancePage - 1) * result.pageSize + 1}–${(maintenancePage - 1) * result.pageSize + result.items.length}`
      : "No matching records. Scan the libraries to update availability and duplicate detection.";
    $("maintenance-page").textContent = `Page ${maintenancePage} of ${maintenancePages}`;
    $("previous-maintenance").disabled = maintenancePage <= 1;
    $("next-maintenance").disabled = maintenancePage >= maintenancePages;
  } catch (error) {
    if (generation !== maintenanceGeneration) return;
    $("maintenance-status").textContent = "Could not load maintenance records. Refresh to retry.";
    $("maintenance-error").textContent = error.message;
    $("maintenance-error").hidden = false;
  }
}

async function removeMissingBook(book, button) {
  if (maintenanceBusy || !confirm(`Remove “${bookTitle(book)}” at ${book.location} from the catalog?\n\nThis permanently removes its shared metadata and every user's reading progress, bookmarks, and list memberships. This cannot be undone. Source files are never deleted.\n\nRemoval is allowed only if the file is still genuinely missing from an accessible library.`)) return;
  const epoch = authEpoch;
  maintenanceBusy = true;
  button.disabled = true;
  $("maintenance-error").hidden = true;
  try {
    await api(`/api/books/${encodeURIComponent(book.id)}`, { method: "DELETE" });
    if (epoch !== authEpoch) return;
    clearSelection();
    if (detailBook?.id === book.id) { $("details-dialog").close(); detailBook = null; }
    loadLists();
    loadPublishers();
  } catch (error) {
    if (epoch !== authEpoch) return;
    $("maintenance-error").textContent = `Catalog removal failed: ${error.message}. Records have been refreshed; no source files were deleted.`;
    $("maintenance-error").hidden = false;
  } finally {
    if (epoch === authEpoch) {
      maintenanceBusy = false;
      loadMaintenance();
      loadLibrary();
    }
  }
}

function renderDetailAvailability(book) {
  $("details-availability").textContent = book.availability === "available" ? "Available"
    : `${book.availability === "missing" ? "Missing file" : "Unavailable location"} — reading is disabled. Restore or reconnect the source and ask an administrator to scan again. Catalog metadata, progress, and bookmarks remain available.`;
  $("details-availability").className = book.availability === "available" ? "muted" : "book-availability";
  $("read-book").disabled = book.pageCount < 1 || book.availability !== "available";
}

async function refreshDetailAvailability() {
  const generation = detailGeneration;
  try {
    const book = await api(`/api/books/${encodeURIComponent(detailBook.id)}`);
    if (generation !== detailGeneration || !detailBook) return;
    detailBook = book;
    renderDetailAvailability(book);
  } catch (error) {
    if (generation === detailGeneration) $("details-status").textContent = `Could not refresh availability: ${error.message}. Close and reopen to retry.`;
  }
}

function renderDetails(book, metadata = null, schema = []) {
  $("details-title").textContent = bookTitle(book);
  const content = $("details-content");
  content.replaceChildren(textElement("p", [book.series, book.number && `#${book.number}`, book.publisher].filter(Boolean).join(" · "), "muted"));
  const availability = textElement("p", "", book.availability === "available" ? "muted" : "book-availability");
  availability.id = "details-availability";
  content.append(availability, textElement("p", book.location, "muted"));
  renderDetailAvailability(book);
  if (book.summary) content.append(textElement("p", book.summary, "summary"));
  content.append(textElement("p", `${book.pageCount} pages · ${book.completed ? "Completed" : book.started ? `Continue on page ${book.currentPage + 1}` : "Unread"}`));
  if (metadata) {
    const details = document.createElement("details");
    details.append(textElement("summary", "Full metadata"));
    const values = document.createElement("dl");
    for (const field of schema) {
      if (Object.hasOwn(metadata.fields, field.name)) values.append(textElement("dt", field.label), textElement("dd", metadata.fields[field.name]));
    }
    for (const [name, value] of Object.entries(metadata.custom)) values.append(textElement("dt", `Custom: ${name}`), textElement("dd", value));
    details.append(values.childElementCount ? values : textElement("p", "No metadata values."));
    content.append(details);
  }
  $("read-book").textContent = book.started ? "Continue reading" : "Read book";
  $("edit-metadata").hidden = session?.role !== "admin";
}

function activeList() {
  return readingLists.find((list) => list.id === $("reading-list").value);
}

function updateSelection() {
  const count = selectedBooks.size;
  $("selection-count").textContent = `${count} of 100 selected`;
  $("clear-selection").disabled = !count;
  $("select-page").disabled = !libraryItems.length || count >= 100;
  $("bulk-metadata").disabled = !count;
  $("export-metadata-json").disabled = $("export-metadata-csv").disabled = metadataExportBusy || !count;
  $("add-list-books").disabled = managementBusy || !count || !$("add-to-list").value;
  $("remove-list-books").hidden = activeList()?.kind !== "manual";
  $("remove-list-books").disabled = managementBusy || !count;
  $("edit-list").disabled = managementBusy || !activeList();
  $("delete-list").disabled = managementBusy || !activeList();
  for (const checkbox of $("books").querySelectorAll("[data-book-id]")) checkbox.checked = selectedBooks.has(checkbox.dataset.bookId);
  for (const button of $("books").querySelectorAll(".book-order button")) button.disabled = managementBusy;
}

function clearSelection() {
  selectedBooks.clear();
  updateSelection();
}

async function loadLists() {
  const generation = ++listsGeneration;
  try {
    const [lists, folders] = await Promise.all([api("/api/lists"), api("/api/list-folders")]);
    if (generation !== listsGeneration || !session) return;
    const selected = $("reading-list").value;
    const target = $("add-to-list").value;
    readingLists = lists;
    listFolders = folders;
    for (const [id, manualOnly, placeholder] of [["reading-list", false, "All books"], ["add-to-list", true, "Choose a list"]]) {
      const select = $(id);
      select.replaceChildren(new Option(placeholder, ""));
      for (const folder of [{ id: null, name: "Unfiled" }, ...folders]) {
        const members = lists.filter((list) => (list.folderId ?? null) === folder.id && (!manualOnly || list.kind === "manual"));
        if (!members.length) continue;
        const group = document.createElement("optgroup");
        group.label = folder.name;
        group.append(...members.map((list) => new Option(`${list.name}${list.kind === "smart" ? " (smart)" : ""}`, list.id)));
        select.append(group);
      }
    }
    $("reading-list").value = lists.some((list) => list.id === selected) ? selected : "";
    $("add-to-list").value = lists.some((list) => list.id === target && list.kind === "manual") ? target : "";
    if (selected !== $("reading-list").value) changeLibrary();
    updateSelection();
  } catch (error) { if (generation === listsGeneration) $("management-status").textContent = `Could not load reading lists: ${error.message}`; }
}

function showFolderEditor(id = "") {
  folderEditingId = id;
  folderDirty = false;
  $("folder-choice").replaceChildren(new Option("New folder", ""), ...listFolders.map((folder) => new Option(folder.name, folder.id)));
  $("folder-choice").value = id;
  $("folder-name").value = listFolders.find((folder) => folder.id === id)?.name ?? "";
  $("save-folder").textContent = id ? "Rename folder" : "Create folder";
  $("delete-folder").disabled = folderBusy || !id;
}

async function saveFolder(remove = false) {
  if (folderBusy) return;
  const id = folderEditingId;
  const name = $("folder-name").value.trim();
  if (remove) {
    const folder = listFolders.find((item) => item.id === id);
    if (!folder || !confirm(`Delete folder “${folder.name}”? All lists in it will become unfiled. No lists, books, or reading progress will be deleted.${folderDirty ? "\nUnsaved name changes will be discarded." : ""}`)) return;
  } else if (!name || name.length > 200) {
    $("folder-status").textContent = "Enter a folder name of 1–200 characters.";
    return;
  }
  const epoch = authEpoch;
  folderBusy = true;
  $("folder-controls").disabled = $("save-folder").disabled = $("delete-folder").disabled = true;
  $("folder-status").textContent = remove ? "Deleting folder…" : "Saving folder…";
  try {
    const folder = await api(id ? `/api/list-folders/${encodeURIComponent(id)}` : "/api/list-folders", {
      method: remove ? "DELETE" : id ? "PUT" : "POST",
      ...(remove ? {} : { body: JSON.stringify({ name }) })
    });
    if (epoch !== authEpoch) return;
    listFolders = listFolders.filter((item) => item.id !== id);
    if (!remove) listFolders.push(folder);
    showFolderEditor(remove ? "" : folder.id);
    $("folder-status").textContent = remove ? "Folder deleted. Its lists are now unfiled; no lists or books were deleted." : "Folder saved.";
    await loadLists();
  } catch (error) { if (epoch === authEpoch) $("folder-status").textContent = `Folder not saved: ${error.message}. Your edits are kept; try again.`; }
  finally {
    if (epoch === authEpoch) {
      folderBusy = false;
      $("folder-controls").disabled = $("save-folder").disabled = false;
      $("delete-folder").disabled = !folderEditingId;
    }
  }
}

$("manage-list-folders").addEventListener("click", () => {
  showFolderEditor();
  $("folder-controls").disabled = $("save-folder").disabled = false;
  $("folder-status").textContent = "";
  $("folder-dialog").showModal();
});
$("folder-name").addEventListener("input", () => { folderDirty = true; });
$("folder-choice").addEventListener("change", () => {
  if (folderDirty && !confirm("Discard unsaved folder name changes?")) { $("folder-choice").value = folderEditingId; return; }
  showFolderEditor($("folder-choice").value);
  $("folder-status").textContent = "";
});
$("folder-form").addEventListener("submit", (event) => { event.preventDefault(); saveFolder(); });
$("delete-folder").addEventListener("click", () => saveFolder(true));
$("folder-dialog").addEventListener("close", () => { folderDirty = false; });

$("personal-rating").addEventListener("change", () => {
  ratingDirty = true;
  $("rating-status").textContent = "Rating not saved yet.";
});
$("rating-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!detailBook || ratingBusy) return;
  const rating = Number($("personal-rating").value);
  if (!Number.isInteger(rating) || rating < 0 || rating > 5) { $("rating-status").textContent = "Choose a rating from 0 to 5."; return; }
  const epoch = authEpoch;
  const generation = detailGeneration;
  const id = detailBook.id;
  ratingBusy = true;
  $("personal-rating").disabled = $("save-rating").disabled = true;
  $("rating-status").textContent = "Saving your private rating…";
  try {
    await api(`/api/books/${encodeURIComponent(id)}/rating`, { method: "PUT", body: JSON.stringify({ rating }) });
    if (epoch !== authEpoch) return;
    clearSelection();
    loadLibrary();
    if (generation !== detailGeneration) return;
    detailBook.personalRating = rating;
    ratingDirty = false;
    $("rating-status").textContent = rating === 0 ? "Rating cleared. This book is unrated." : "Your private rating is saved.";
  } catch (error) {
    if (epoch === authEpoch && generation === detailGeneration) $("rating-status").textContent = `Rating not saved: ${error.message}. Your choice is kept; try Save rating again.`;
  } finally {
    if (epoch === authEpoch && generation === detailGeneration) {
      ratingBusy = false;
      $("personal-rating").disabled = $("save-rating").disabled = false;
    }
  }
});

function canCloseDialog(id) {
  const rating = id === "details-dialog";
  const folder = id === "folder-dialog";
  if (id === "metadata-dialog") return !metadataBusy && (!metadataDirty || confirm("Discard unsaved metadata changes?"));
  if ((rating && ratingBusy) || (folder && folderBusy)) return false;
  return !((rating && ratingDirty) || (folder && folderDirty)) || confirm(`Discard unsaved ${rating ? "rating" : "folder name"} changes?`);
}

for (const id of ["details-dialog", "folder-dialog", "metadata-dialog"]) $(id).addEventListener("cancel", (event) => {
  if (!canCloseDialog(id)) event.preventDefault();
});

function labeledControl(text, control) {
  const label = textElement("label", text);
  label.append(control);
  return label;
}

async function loadMetadataSchema() {
  if (!metadataSchema) metadataSchema = await api("/api/metadata-fields");
  return metadataSchema;
}

function metadataChangeRow(name, control, custom = false, value = "") {
  const row = textElement("div", "", custom ? "custom-value metadata-change" : "metadata-change");
  const apply = document.createElement("input");
  apply.type = "checkbox";
  apply.className = "apply-change";
  apply.checked = custom && !name;
  row.append(labeledControl(custom ? "Apply custom value" : `Apply ${name}`, apply));
  if (custom) {
    const key = document.createElement("input");
    key.className = "custom-name";
    key.value = name;
    key.maxLength = 128;
    key.readOnly = Boolean(name);
    row.append(labeledControl("Name", key));
  } else row.dataset.field = name;
  control.classList.add("metadata-value");
  control.value = value;
  const action = document.createElement("select");
  action.className = "change-action";
  action.append(new Option("Set value", "set"), new Option("Remove / reset", "remove"));
  row.append(labeledControl(custom ? "Value" : "New value", control), labeledControl("Action", action));
  const update = () => {
    control.disabled = !apply.checked || action.value === "remove";
    action.disabled = !apply.checked;
    if (custom) row.querySelector(".custom-name").disabled = !apply.checked;
  };
  apply.addEventListener("change", update);
  action.addEventListener("change", update);
  if (custom && !name) {
    const discard = textElement("button", "Discard row");
    discard.type = "button";
    discard.addEventListener("click", () => { row.remove(); metadataChanged(); });
    row.append(discard);
  }
  update();
  return row;
}

function addCustomValue(name = "", value = "") {
  const input = document.createElement("textarea");
  input.rows = 2;
  input.maxLength = 16384;
  $("custom-values").append(metadataChangeRow(name, input, true, value));
}

function invalidateMetadataPreview() {
  metadataPreview = null;
  $("metadata-preview-books").replaceChildren();
  $("metadata-preview").hidden = true;
  $("apply-metadata").disabled = true;
}

function metadataChanged() {
  metadataDirty = true;
  if (metadataPreview) $("metadata-status").textContent = "Draft changed. Preview again before applying.";
  invalidateMetadataPreview();
}

async function loadMetadataReport() {
  if (session?.role !== "admin" || !$("metadata-report-dialog").open) return;
  const generation = ++metadataReportGeneration;
  $("metadata-report-status").textContent = "Checking catalog metadata…";
  $("metadata-report-items").replaceChildren();
  $("previous-metadata-report").disabled = $("next-metadata-report").disabled = true;
  try {
    const result = await api(`/api/metadata-report?${new URLSearchParams({ kind: $("metadata-report-kind").value, page: metadataReportPage, pageSize: 48 })}`);
    if (generation !== metadataReportGeneration) return;
    metadataReportPage = result.page;
    metadataReportPages = Math.max(1, Math.ceil(result.total / result.pageSize));
    if (metadataReportPage > metadataReportPages) { metadataReportPage = metadataReportPages; await loadMetadataReport(); return; }
    for (const book of result.items) {
      const row = textElement("article", "", "maintenance-row");
      row.append(textElement("h3", bookTitle(book)));
      const issues = document.createElement("ul");
      for (const issue of book.issues) issues.append(textElement("li", `${issue.field}: ${issue.message} · Value: ${issue.value == null ? "(absent)" : issue.value === "" ? "(empty string)" : JSON.stringify(issue.value)}`));
      const actions = textElement("div", "", "actions list-actions");
      const inspect = textElement("button", "Book details");
      inspect.type = "button";
      inspect.dataset.reportDetails = book.id;
      inspect.setAttribute("aria-label", `Book details: ${bookTitle(book)}`);
      inspect.addEventListener("click", () => openDetails(book.id));
      const edit = textElement("button", "Edit metadata");
      edit.type = "button";
      edit.dataset.reportEdit = book.id;
      edit.setAttribute("aria-label", `Edit metadata: ${bookTitle(book)}`);
      edit.addEventListener("click", () => openMetadata([book.id]));
      actions.append(inspect, edit);
      row.append(issues, actions);
      $("metadata-report-items").append(row);
    }
    $("metadata-report-status").textContent = result.total
      ? `${result.total} affected books · Showing ${(result.page - 1) * result.pageSize + 1}–${(result.page - 1) * result.pageSize + result.items.length}`
      : "No matching metadata issues.";
    $("metadata-report-page").textContent = `Page ${metadataReportPage} of ${metadataReportPages}`;
    $("previous-metadata-report").disabled = metadataReportPage <= 1;
    $("next-metadata-report").disabled = metadataReportPage >= metadataReportPages;
  } catch (error) {
    if (generation === metadataReportGeneration) $("metadata-report-status").textContent = `Could not load metadata report: ${error.message}. Refresh to retry.`;
  }
}

async function exportMetadata(format) {
  if (!session || session.mustChangePassword || metadataExportBusy || !selectedBooks.size || selectedBooks.size > 100) return;
  const epoch = authEpoch;
  const ids = [...selectedBooks];
  metadataExportBusy = true;
  updateSelection();
  $("management-status").textContent = `Exporting metadata for ${ids.length} selected books…`;
  try {
    const blob = await api(`/api/books/metadata/export?format=${format}`, { method: "POST", body: JSON.stringify({ bookIds: ids }), blob: true });
    if (epoch !== authEpoch) return;
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `comicrack-metadata.${format}`;
    document.body.append(link);
    try { link.click(); }
    finally { link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
    $("management-status").textContent = `Metadata ${format.toUpperCase()} download started for ${ids.length} selected books.`;
  } catch (error) {
    if (epoch === authEpoch) $("management-status").textContent = `Metadata export failed: ${error.message}`;
  } finally {
    if (epoch === authEpoch) { metadataExportBusy = false; updateSelection(); }
  }
}

async function openMetadata(ids, bulk = false) {
  if (session?.role !== "admin" || !ids.length || ids.length > 100) return;
  const generation = ++metadataGeneration;
  metadataBookIds = [...ids];
  metadataBulk = bulk;
  metadataDirty = metadataBusy = false;
  invalidateMetadataPreview();
  $("save-metadata").textContent = bulk ? "Preview checked changes" : "Save checked changes";
  $("apply-metadata").hidden = !bulk;
  $("metadata-title").textContent = ids.length === 1 ? "Edit book metadata" : `Edit metadata for ${ids.length} books`;
  $("metadata-status").textContent = "Loading metadata…";
  $("metadata-fields").replaceChildren();
  $("custom-values").replaceChildren();
  $("metadata-controls").disabled = true;
  $("save-metadata").disabled = true;
  $("metadata-dialog").showModal();
  try {
    const [schema, metadata] = await Promise.all([loadMetadataSchema(), ids.length === 1 ? api(`/api/books/${encodeURIComponent(ids[0])}/metadata`) : Promise.resolve({ fields: {}, custom: {} })]);
    if (generation !== metadataGeneration) return;
    for (const field of schema) {
      const input = document.createElement(field.options ? "select" : field.type === "textarea" ? "textarea" : "input");
      if (field.options) input.append(new Option("(blank)", ""), ...field.options.map((value) => new Option(value, value)));
      else if (field.type === "integer" || field.type === "number") {
        input.type = "number";
        input.step = field.type === "integer" ? "1" : "any";
        if (field.min != null) input.min = field.min;
        if (field.max != null) input.max = field.max;
      } else { input.maxLength = 16384; if (field.type === "textarea") input.rows = 3; }
      const value = metadata.fields[field.name] ?? "";
      if (field.options && value && !field.options.includes(value)) input.append(new Option(value, value));
      const row = metadataChangeRow(field.name, input, false, value);
      row.prepend(textElement("h3", field.label));
      $("metadata-fields").append(row);
    }
    for (const [name, value] of Object.entries(metadata.custom)) addCustomValue(name, value);
    $("metadata-controls").disabled = false;
    $("save-metadata").disabled = false;
    $("metadata-status").textContent = bulk ? "Unchecked fields and custom keys remain unchanged. Preview your checked changes before applying them." : "Check each field or custom value you want to change.";
  } catch (error) { if (generation === metadataGeneration) $("metadata-status").textContent = `Could not load metadata: ${error.message}. Close and try again.`; }
}

function collectMetadataChanges() {
  const fields = Object.create(null);
  const custom = Object.create(null);
  for (const row of $("metadata-controls").querySelectorAll(".metadata-change")) {
    if (!row.querySelector(".apply-change").checked) continue;
    const isCustom = row.classList.contains("custom-value");
    const name = isCustom ? row.querySelector(".custom-name").value : row.dataset.field;
    if (isCustom && (!name.trim() || /[\u0000-\u001f\u007f]/u.test(name) || name.includes("&#61;") || name.includes("&#44;"))) throw new Error("Custom names cannot be blank, contain control characters, or contain &#61; or &#44;.");
    const values = isCustom ? custom : fields;
    if (Object.hasOwn(values, name)) throw new Error(`Custom value “${name}” is selected more than once.`);
    values[name] = row.querySelector(".change-action").value === "remove" ? null : row.querySelector(".metadata-value").value;
  }
  return { fields, custom };
}

const ruleFields = ["Title", "Series", "Number", "Publisher", "Summary", "Writer", "Penciller", "Genre", "Year", "PersonalRating", "PageCount", "ReadStatus"];
const numericRuleLimits = { PageCount: 2147483647, Year: 9999, PersonalRating: 5 };
const ruleOperatorLabels = { contains: "Contains", equals: "Equals", notEquals: "Does not equal", greaterThan: "Greater than", lessThan: "Less than" };

function addListRule(rule = { field: "Series", operator: "contains", value: "" }) {
  if ($("list-rules").children.length >= 20) { $("list-status").textContent = "A smart list supports at most 20 rules."; return; }
  const row = textElement("div", "", "list-rule");
  const field = document.createElement("select");
  field.className = "rule-field";
  field.append(...ruleFields.map((name) => new Option(({ ReadStatus: "Reading status", PageCount: "Page count", Penciller: "Artist", PersonalRating: "My rating" })[name] ?? name, name)));
  field.value = rule.field;
  const operator = document.createElement("select");
  operator.className = "rule-operator";
  const valueLabel = textElement("label", "Value");
  const update = (initial = false) => {
    const numeric = Object.hasOwn(numericRuleLimits, field.value);
    const operators = numeric ? ["equals", "greaterThan", "lessThan"] : field.value === "ReadStatus" ? ["equals", "notEquals"] : ["contains", "equals", "notEquals"];
    operator.replaceChildren(...operators.map((name) => new Option(ruleOperatorLabels[name], name)));
    if (initial) operator.value = rule.operator;
    const value = document.createElement(field.value === "ReadStatus" ? "select" : "input");
    value.className = "rule-value";
    if (field.value === "ReadStatus") value.append(new Option("Unread", "unread"), new Option("In progress", "in-progress"), new Option("Completed", "completed"));
    else if (numeric) { value.type = "number"; value.min = "0"; value.max = String(numericRuleLimits[field.value]); value.step = "1"; }
    else value.maxLength = 2000;
    value.required = numeric || field.value === "ReadStatus";
    if (initial) value.value = rule.value;
    valueLabel.replaceChildren(document.createTextNode("Value"), value);
  };
  field.addEventListener("change", () => update());
  const remove = textElement("button", "Remove rule");
  remove.type = "button";
  remove.addEventListener("click", () => { row.remove(); $("add-list-rule").disabled = false; });
  row.append(labeledControl("Field", field), labeledControl("Operator", operator), valueLabel, remove);
  update(true);
  $("list-rules").append(row);
  $("add-list-rule").disabled = $("list-rules").children.length >= 20;
}

function showListEditor(list = null) {
  listDialogGeneration++;
  listEditingId = list?.id ?? null;
  $("list-title").textContent = list ? "Edit reading list" : "New reading list";
  $("list-form").reset();
  $("list-controls").disabled = false;
  $("save-list").disabled = false;
  $("list-name").value = list?.name ?? "";
  $("list-kind").value = list?.kind ?? "manual";
  $("list-kind").disabled = Boolean(list);
  $("list-match").value = list?.match ?? "all";
  $("list-folder").replaceChildren(new Option("Unfiled", ""), ...listFolders.map((folder) => new Option(folder.name, folder.id)));
  $("list-folder").value = list?.folderId ?? "";
  $("list-rules").replaceChildren();
  $("add-list-rule").disabled = false;
  for (const rule of list?.rules ?? []) addListRule(rule);
  $("smart-list-controls").hidden = $("list-kind").value !== "smart";
  $("smart-list-controls").disabled = $("list-kind").value !== "smart";
  $("list-status").textContent = "";
  $("list-dialog").showModal();
}

async function changeListMembership(remove) {
  if (managementBusy || !selectedBooks.size) return;
  const listId = remove ? activeList()?.id : $("add-to-list").value;
  if (!listId) return;
  const epoch = authEpoch;
  managementBusy = true;
  updateSelection();
  $("management-status").textContent = remove ? "Removing selected books…" : "Adding selected books…";
  try {
    await api(`/api/lists/${encodeURIComponent(listId)}/books`, { method: remove ? "DELETE" : "POST", body: JSON.stringify({ bookIds: [...selectedBooks] }) });
    if (epoch !== authEpoch) return;
    clearSelection();
    $("management-status").textContent = remove ? "Selected books removed from the list." : "Selected books added to the list.";
    await loadLists();
    await loadLibrary();
  } catch (error) { if (epoch === authEpoch) $("management-status").textContent = error.message; }
  finally { if (epoch === authEpoch) { managementBusy = false; updateSelection(); } }
}

async function moveListBook(bookId, direction) {
  if (managementBusy) return;
  const listId = activeList()?.id;
  if (!listId) return;
  const epoch = authEpoch;
  managementBusy = true;
  updateSelection();
  try {
    const list = await api(`/api/lists/${encodeURIComponent(listId)}`);
    const index = list.bookIds.indexOf(bookId);
    if (index < 0) throw new Error("This book is no longer in the list. Refresh the list.");
    if ((direction < 0 && index === 0) || (direction > 0 && index === list.bookIds.length - 1)) {
      $("management-status").textContent = direction < 0 ? "This book is already first in the full list." : "This book is already last in the full list.";
      return;
    }
    const beforeId = direction < 0 ? list.bookIds[index - 1] : list.bookIds[index + 2] ?? null;
    await api(`/api/lists/${encodeURIComponent(listId)}/order`, { method: "PUT", body: JSON.stringify({ bookId, beforeId }) });
    if (epoch !== authEpoch) return;
    clearSelection();
    $("management-status").textContent = "List order saved. Moves use the full list, including books on other pages or hidden by filters.";
    await loadLibrary();
  } catch (error) { if (epoch === authEpoch) $("management-status").textContent = error.message; }
  finally { if (epoch === authEpoch) { managementBusy = false; updateSelection(); } }
}

$("select-page").addEventListener("click", () => {
  for (const book of libraryItems) {
    if (selectedBooks.size >= 100) break;
    selectedBooks.add(book.id);
  }
  $("management-status").textContent = selectedBooks.size === 100 ? "Selection limit reached (100 books)." : "This page selected. Selections remain when changing pages.";
  updateSelection();
});
$("clear-selection").addEventListener("click", clearSelection);
$("add-to-list").addEventListener("change", updateSelection);
$("reading-list").addEventListener("change", () => {
  if (activeList()?.kind === "manual") $("book-sort").value = "list";
  changeLibrary();
});
$("add-list-books").addEventListener("click", () => changeListMembership(false));
$("remove-list-books").addEventListener("click", () => changeListMembership(true));
$("bulk-metadata").addEventListener("click", () => openMetadata([...selectedBooks], true));
$("edit-metadata").addEventListener("click", () => { if (detailBook) openMetadata([detailBook.id]); });
$("add-custom-value").addEventListener("click", () => { addCustomValue(); metadataChanged(); });
$("metadata-controls").addEventListener("input", metadataChanged);
$("metadata-controls").addEventListener("change", metadataChanged);
$("metadata-dialog").addEventListener("close", () => {
  metadataGeneration++;
  metadataDirty = false;
  metadataBookIds = [];
  invalidateMetadataPreview();
});
$("export-metadata-json").addEventListener("click", () => exportMetadata("json"));
$("export-metadata-csv").addEventListener("click", () => exportMetadata("csv"));
$("metadata-report").addEventListener("click", () => { $("metadata-report-dialog").showModal(); loadMetadataReport(); });
$("metadata-report-dialog").addEventListener("close", () => { metadataReportGeneration++; });
$("metadata-report-kind").addEventListener("change", () => { metadataReportPage = 1; loadMetadataReport(); });
$("refresh-metadata-report").addEventListener("click", loadMetadataReport);
$("previous-metadata-report").addEventListener("click", () => { if (metadataReportPage > 1) { metadataReportPage--; loadMetadataReport(); } });
$("next-metadata-report").addEventListener("click", () => { if (metadataReportPage < metadataReportPages) { metadataReportPage++; loadMetadataReport(); } });
$("metadata-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (session?.role !== "admin" || !metadataBookIds.length || metadataBusy) return;
  const generation = metadataGeneration;
  const detailsGeneration = detailGeneration;
  const epoch = authEpoch;
  const ids = [...metadataBookIds];
  try {
    const changes = collectMetadataChanges();
    if (!Object.keys(changes.fields).length && !Object.keys(changes.custom).length) throw new Error("Check at least one field or custom value to apply.");
    const payload = { bookIds: ids, ...changes };
    const applying = metadataBulk && event.submitter === $("apply-metadata");
    if (applying && (!metadataPreview?.hasChanges || JSON.stringify(payload) !== JSON.stringify(metadataPreview.payload))) {
      invalidateMetadataPreview();
      throw new Error("Preview the current draft before applying changes.");
    }
    metadataBusy = true;
    $("metadata-controls").disabled = true;
    $("save-metadata").disabled = $("apply-metadata").disabled = true;
    if (metadataBulk && !applying) {
      invalidateMetadataPreview();
      $("metadata-status").textContent = "Preparing exact changes. Nothing is being saved…";
      const preview = await api("/api/books/metadata/preview", { method: "POST", body: JSON.stringify(payload) });
      if (epoch !== authEpoch || generation !== metadataGeneration) return;
      metadataPreview = { payload, revision: preview.revision, hasChanges: preview.books.some((book) => book.changes.length > 0) };
      for (const book of preview.books) {
        const row = textElement("article", "", "maintenance-row");
        row.dataset.previewBook = book.id;
        row.append(textElement("h4", bookTitle(book)));
        if (!book.changes.length) row.append(textElement("p", "Unchanged — no actual changes.", "muted"));
        for (const change of book.changes) {
          row.append(textElement("h5", `${change.custom ? "Custom: " : ""}${change.field}`));
          const values = document.createElement("dl");
          values.append(
            textElement("dt", "Before"), textElement("dd", change.before == null ? "(absent)" : change.before === "" ? "(empty string)" : JSON.stringify(change.before)),
            textElement("dt", "After"), textElement("dd", change.after == null ? "(removed / absent)" : change.after === "" ? "(empty string)" : JSON.stringify(change.after)));
          row.append(values);
        }
        $("metadata-preview-books").append(row);
      }
      $("metadata-preview").hidden = false;
      $("metadata-preview").focus();
      $("metadata-status").textContent = metadataPreview.hasChanges ? "Review every book below, then choose Apply reviewed changes. Nothing has been saved yet." : "No actual changes. Every selected book is unchanged.";
      return;
    }
    $("metadata-status").textContent = `Saving metadata for ${ids.length} ${ids.length === 1 ? "book" : "books"}…`;
    await api(metadataBulk ? "/api/books/metadata" : `/api/books/${encodeURIComponent(ids[0])}/metadata`, {
      method: metadataBulk ? "POST" : "PUT", body: JSON.stringify(metadataBulk ? { ...metadataPreview.payload, expectedRevision: metadataPreview.revision } : changes)
    });
    if (epoch !== authEpoch) return;
    clearSelection();
    loadLists();
    loadLibrary();
    loadPublishers();
    if ($("maintenance-dialog").open) loadMaintenance();
    if ($("metadata-report-dialog").open) loadMetadataReport();
    if (generation !== metadataGeneration) return;
    for (const checkbox of $("metadata-controls").querySelectorAll(".apply-change")) {
      checkbox.checked = false;
      checkbox.dispatchEvent(new Event("change"));
    }
    metadataDirty = false;
    invalidateMetadataPreview();
    $("metadata-status").textContent = "Metadata saved in the library database. Source archives unchanged.";
    if ($("details-dialog").open && detailBook && ids.includes(detailBook.id)) {
      try {
        const id = detailBook.id;
        const [book, metadata] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}`), api(`/api/books/${encodeURIComponent(id)}/metadata`)]);
        if (detailsGeneration === detailGeneration) { detailBook = book; renderDetails(book, metadata, metadataSchema); }
      } catch (error) { if (detailsGeneration === detailGeneration) $("details-status").textContent = `Metadata was saved, but details could not refresh: ${error.message}`; }
    }
  } catch (error) {
    if (generation === metadataGeneration) {
      if (error.status === 409) invalidateMetadataPreview();
      $("metadata-status").textContent = error.status === 409 ? `${error.message} Your draft is retained. Preview again before applying.` : error.message;
    }
  } finally {
    if (generation === metadataGeneration) {
      metadataBusy = false;
      $("metadata-controls").disabled = $("save-metadata").disabled = false;
      $("apply-metadata").disabled = !metadataPreview?.hasChanges;
    }
  }
});

$("create-list").addEventListener("click", () => showListEditor());
$("edit-list").addEventListener("click", () => { if (activeList()) showListEditor(activeList()); });
$("list-dialog").addEventListener("close", () => { listDialogGeneration++; });
$("list-kind").addEventListener("change", () => {
  const smart = $("list-kind").value === "smart";
  $("smart-list-controls").hidden = !smart;
  $("smart-list-controls").disabled = !smart;
  if (smart && !$("list-rules").children.length) addListRule();
});
$("add-list-rule").addEventListener("click", () => addListRule());
$("list-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!$("list-form").reportValidity()) return;
  const generation = listDialogGeneration;
  const epoch = authEpoch;
  const id = listEditingId;
  const kind = $("list-kind").value;
  const rules = kind === "smart" ? [...$("list-rules").children].map((row) => {
    const value = row.querySelector(".rule-value");
    return { field: row.querySelector(".rule-field").value, operator: row.querySelector(".rule-operator").value,
      value: value.type === "number" ? String(value.valueAsNumber) : value.value };
  }) : [];
  if (kind === "smart" && (!rules.length || rules.length > 20)) { $("list-status").textContent = "Add between 1 and 20 rules."; return; }
  const name = $("list-name").value.trim();
  if (!name) { $("list-status").textContent = "Enter a list name."; return; }
  $("list-controls").disabled = true;
  $("save-list").disabled = true;
  $("list-status").textContent = "Saving reading list…";
  try {
    const list = await api(id ? `/api/lists/${encodeURIComponent(id)}` : "/api/lists", {
      method: id ? "PUT" : "POST", body: JSON.stringify({ name, kind, match: $("list-match").value, rules, folderId: $("list-folder").value || null })
    });
    if (epoch !== authEpoch) return;
    await loadLists();
    if (generation !== listDialogGeneration) return;
    $("reading-list").value = list.id;
    if (kind === "manual") $("book-sort").value = "list";
    changeLibrary();
    $("list-dialog").close();
    $("management-status").textContent = "Reading list saved.";
  } catch (error) { if (generation === listDialogGeneration) $("list-status").textContent = error.message; }
  finally { if (generation === listDialogGeneration) { $("list-controls").disabled = false; $("save-list").disabled = false; } }
});
$("delete-list").addEventListener("click", async () => {
  const list = activeList();
  if (!list || managementBusy || !confirm(`Delete reading list “${list.name}”? Books and reading progress will not be deleted.`)) return;
  const epoch = authEpoch;
  managementBusy = true;
  updateSelection();
  try {
    await api(`/api/lists/${encodeURIComponent(list.id)}`, { method: "DELETE" });
    if (epoch !== authEpoch) return;
    clearSelection();
    await loadLists();
    $("management-status").textContent = "Reading list deleted. Books and reading progress were kept.";
  } catch (error) { if (epoch === authEpoch) $("management-status").textContent = error.message; }
  finally { if (epoch === authEpoch) { managementBusy = false; updateSelection(); } }
});

async function openDetails(id) {
  const generation = ++detailGeneration;
  detailBook = null;
  ratingDirty = ratingBusy = false;
  $("rating-form").hidden = true;
  $("personal-rating").disabled = $("save-rating").disabled = false;
  $("rating-status").textContent = "";
  $("details-title").textContent = "Book details";
  $("details-content").textContent = "Loading book details…";
  $("details-bookmarks").textContent = "Loading your bookmarks…";
  $("details-status").textContent = "";
  $("read-book").disabled = true;
  $("edit-metadata").hidden = true;
  $("details-dialog").showModal();
  api(`/api/books/${encodeURIComponent(id)}/bookmarks`).then((bookmarks) => {
    if (generation !== detailGeneration) return;
    const section = document.createElement("details");
    section.append(textElement("summary", `My bookmarks (${bookmarks.length})`));
    const list = document.createElement("ul");
    for (const bookmark of bookmarks) list.append(textElement("li", `Page ${bookmark.page + 1}: ${bookmark.name}`));
    section.append(bookmarks.length ? list : textElement("p", "No personal bookmarks."));
    $("details-bookmarks").replaceChildren(section);
  }).catch((error) => {
    if (generation === detailGeneration) $("details-bookmarks").textContent = `Could not load bookmarks: ${error.message}. Close and reopen to retry.`;
  });
  try {
    const book = await api(`/api/books/${encodeURIComponent(id)}`);
    if (generation !== detailGeneration || !session) return;
    detailBook = book;
    $("personal-rating").value = String(book.personalRating);
    $("rating-form").hidden = false;
    renderDetails(book);
    $("details-status").textContent = "Loading full metadata…";
    const [metadata, schema] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}/metadata`), loadMetadataSchema()]);
    if (generation !== detailGeneration || !session) return;
    renderDetails(detailBook, metadata, schema);
    $("details-status").textContent = "";
  } catch (error) {
    if (generation === detailGeneration) {
      if (!detailBook) $("details-content").textContent = "Could not load book details. Close this dialog and try again.";
      $("details-status").textContent = detailBook ? `Could not load full metadata: ${error.message}. Close and reopen to retry.` : error.message;
    }
  }
}

function queueProgress(record) {
  const epoch = authEpoch;
  unsavedProgress = record;
  $("save-status").textContent = "Saving progress…";
  $("retry-save").hidden = true;
  // ponytail: one promise queue preserves write order; no background sync or storage layer.
  progressQueue = progressQueue.then(async () => {
    if (epoch !== authEpoch) return;
    try {
      await api(`/api/books/${encodeURIComponent(record.id)}/progress`, {
        method: "POST", body: JSON.stringify({ page: record.page, completed: record.completed })
      });
      if (epoch !== authEpoch) return;
      if (unsavedProgress === record) {
        unsavedProgress = null;
        $("save-status").textContent = record.completed ? "Completed · Progress saved" : "Progress saved";
        $("retry-save").hidden = true;
      }
    } catch (error) {
      if (epoch !== authEpoch) return;
      $("save-status").textContent = "Progress not saved";
      $("retry-save").hidden = false;
      showError(error);
    }
  });
}

async function flushProgress() {
  await progressQueue;
  if (unsavedProgress) throw new Error("Your reading progress could not be saved. Retry save before leaving the reader.");
}

async function loadReaderPreferences() {
  if (readerPreferences) return;
  const epoch = authEpoch;
  if (!preferenceLoad) {
    preferenceLoad = api("/api/reader-preferences").then((preferences) => {
      if (epoch === authEpoch) readerPreferences = preferences;
    }).catch((error) => {
      if (epoch === authEpoch) preferenceLoad = null;
      if (error.name === "AbortError") throw error;
      throw new Error(`Could not load reader preferences. Open the book again to retry. ${error.message}`);
    });
  }
  await preferenceLoad;
}

function queuePreferences() {
  const epoch = authEpoch;
  const record = { ...readerPreferences };
  unsavedPreferences = record;
  $("preferences-status").textContent = "Saving preferences…";
  $("retry-preferences").hidden = true;
  preferenceQueue = preferenceQueue.then(async () => {
    if (epoch !== authEpoch) return;
    try {
      await api("/api/reader-preferences", { method: "PUT", body: JSON.stringify(record) });
      if (epoch !== authEpoch) return;
      if (unsavedPreferences === record) {
        unsavedPreferences = null;
        $("preferences-status").textContent = "Preferences saved";
        $("retry-preferences").hidden = true;
      }
    } catch (error) {
      if (epoch !== authEpoch) return;
      $("preferences-status").textContent = "Preferences not saved";
      $("retry-preferences").hidden = false;
      showError(error);
    }
  });
}

function isPageIncluded(page) {
  return page === revealedPage || pageIncluded(pages[page]?.typeMask, readerPreferences.skipPageTypes);
}

function isPageWide(page) {
  const entry = pages[page];
  if (!entry?.width) return false;
  const [width, height] = rotatedDimensions(entry.width, entry.height, readerPreferences.rotation);
  return width > height;
}

function pageLabel(page) {
  return `Page ${page + 1} · ${pages[page]?.type || "Unclassified"}`;
}

function renderBookmarks() {
  for (const [id, bookmarks, personal] of [
    ["personal-bookmarks", personalBookmarks, true],
    ["embedded-bookmarks", pages.filter((entry) => entry.bookmark).map((entry) => ({ page: entry.page, name: entry.bookmark })), false]
  ]) {
    const items = [...bookmarks].sort((a, b) => a.page - b.page).map((bookmark) => {
      const item = textElement("li", "");
      const jump = textElement("button", `${bookmark.name} — ${pageLabel(bookmark.page)}`);
      jump.type = "button";
      jump.dataset.page = String(bookmark.page);
      jump.addEventListener("click", () => showPage(bookmark.page));
      item.append(jump);
      if (personal) {
        const remove = textElement("button", "Delete");
        remove.type = "button";
        remove.dataset.deleteBookmark = String(bookmark.page);
        remove.setAttribute("aria-label", `Delete bookmark ${bookmark.name}, page ${bookmark.page + 1}`);
        remove.addEventListener("click", () => queueBookmark({ page: bookmark.page, name: null }));
        item.append(remove);
      }
      return item;
    });
    $(id).replaceChildren(...items);
    if (personal && !items.length) $(id).append(textElement("li", "No personal bookmarks yet."));
    if (!personal) $("embedded-bookmarks-section").hidden = !items.length;
  }
  updatePageControls();
}

function queueBookmark(record) {
  if (!currentBook || leavingReader || bookmarkBusy || (unsavedBookmark && unsavedBookmark !== record)) return;
  const book = currentBook;
  const epoch = authEpoch;
  unsavedBookmark = record;
  bookmarkBusy = true;
  $("bookmark-status").textContent = record.name === null ? "Deleting bookmark…" : "Saving bookmark…";
  $("retry-bookmark").hidden = true;
  updatePageControls();
  bookmarkQueue = bookmarkQueue.then(async () => {
    try {
      if (epoch !== authEpoch || book !== currentBook) return;
      await api(`/api/books/${encodeURIComponent(book.id)}/bookmarks/${record.page}`, {
        method: record.name === null ? "DELETE" : "PUT",
        ...(record.name === null ? {} : { body: JSON.stringify({ name: record.name }) })
      });
      if (epoch !== authEpoch || book !== currentBook) return;
      personalBookmarks = personalBookmarks.filter((bookmark) => bookmark.page !== record.page);
      if (record.name !== null) personalBookmarks.push(record);
      unsavedBookmark = null;
      bookmarkEditorPage = null;
      $("bookmark-status").textContent = record.name === null ? "Bookmark deleted" : "Bookmark saved";
      renderBookmarks();
    } catch (error) {
      if (epoch !== authEpoch || book !== currentBook) return;
      $("bookmark-status").textContent = `Bookmark change not saved: ${error.message}`;
      $("retry-bookmark").hidden = false;
      showError(error);
    } finally {
      if (epoch === authEpoch && book === currentBook) {
        bookmarkBusy = false;
        updatePageControls();
      }
    }
  });
}

function applyReaderControls() {
  if (!readerPreferences) return;
  $("reader-mode").value = readerPreferences.mode;
  $("fit").value = readerPreferences.fit;
  $("zoom").value = String(readerPreferences.zoom);
  $("rtl").checked = readerPreferences.rtl;
  $("cover-alone").checked = readerPreferences.coverAlone;
  for (const id of ["rotation", "brightness", "contrast"]) $(id).value = String(readerPreferences[id]);
  for (const input of $("skip-type-options").querySelectorAll("input")) input.checked = Boolean(readerPreferences.skipPageTypes & Number(input.value));
  $("reader-pages").style.setProperty("--rotation", `${readerPreferences.rotation}deg`);
  $("reader-pages").style.setProperty("--brightness", `${readerPreferences.brightness}%`);
  $("reader-pages").style.setProperty("--contrast", `${readerPreferences.contrast}%`);
  $("previous-page").textContent = readerPreferences.rtl ? "Previous →" : "← Previous";
  $("next-page").textContent = readerPreferences.rtl ? "← Next" : "Next →";
  $("spread-hint").hidden = readerPreferences.mode !== "spread";
  $("reader-pages").className = `reader-pages ${readerPreferences.mode === "continuous" ? "continuous" : ""} ${readerPreferences.rtl ? "rtl" : ""}`;
  updateTouchMode();
  updatePageControls();
}

function spreadCandidates() {
  return spreadPages(currentPage, currentBook.pageCount, readerPreferences.coverAlone, () => false, isPageIncluded);
}

function spreadResolved() {
  const pair = spreadCandidates();
  return pair.some(isPageWide) || pair.every((page) => pages[page]?.width);
}

function updatePageControls() {
  const unavailable = leavingReader || !currentBook || !readerPreferences;
  const empty = !activePages.length;
  const resolving = !unavailable && !empty && readerPreferences.mode === "spread" && !spreadResolved();
  const first = readerPreferences?.mode === "continuous" ? currentPage : displayedPages[0] ?? currentPage;
  const last = readerPreferences?.mode === "continuous" ? currentPage : displayedPages.at(-1) ?? currentPage;
  if (!pageNumberDraft) $("page-number").value = String(currentPage + 1);
  $("previous-page").disabled = unavailable || empty || resolving || nextPage(first, currentBook.pageCount, -1, (page) => pageIncluded(pages[page].typeMask, readerPreferences.skipPageTypes)) === null;
  $("next-page").disabled = unavailable || empty || resolving || nextPage(last, currentBook.pageCount, 1, (page) => pageIncluded(pages[page].typeMask, readerPreferences.skipPageTypes)) === null;
  for (const element of document.querySelectorAll(".display-controls input, .display-controls select, .display-controls button, #page-form input, #page-form button, #skip-types input, #skip-types button, #personal-bookmarks button, #embedded-bookmarks button")) element.disabled = unavailable;
  $("bookmark-name").disabled = unavailable || empty || bookmarkBusy || Boolean(unsavedBookmark);
  $("save-bookmark").disabled = $("bookmark-name").disabled;
  $("retry-bookmark").disabled = unavailable || bookmarkBusy;
  for (const button of $("personal-bookmarks").querySelectorAll("[data-delete-bookmark]")) button.disabled = unavailable || bookmarkBusy || Boolean(unsavedBookmark);
  if (!unavailable) {
    $("current-page-type").textContent = empty ? "No visible pages" : (readerPreferences.mode === "continuous" ? [currentPage] : displayedPages.length ? displayedPages : [currentPage]).map(pageLabel).join(" · ");
    $("bookmark-page").textContent = `page ${currentPage + 1}`;
    if (bookmarkEditorPage !== currentPage) {
      bookmarkEditorPage = currentPage;
      $("bookmark-name").value = personalBookmarks.find((bookmark) => bookmark.page === currentPage)?.name || "";
      $("bookmark-name").setCustomValidity("");
    }
  }
  $("cover-alone").disabled = unavailable || readerPreferences?.mode !== "spread";
  updateFullscreen();
}

function createReaderPage(page, info) {
  const node = textElement("div", "", "reader-page");
  node.dataset.page = String(page);
  node.setAttribute("role", "group");
  node.setAttribute("aria-label", `Page ${page + 1} · ${info?.type || "Unclassified"}`);
  const message = textElement("div", "", "page-message");
  node.append(message);
  const entry = { page, type: info?.type || "", typeMask: info?.typeMask || 0, bookmark: info?.bookmark || "", node, message, width: 0, height: 0, state: "idle", url: null, image: null, controller: null };
  pageMessage(entry);
  return entry;
}

function pageMessage(entry, error = null) {
  entry.node.classList.toggle("failed", Boolean(error));
  entry.message.hidden = entry.state === "ready";
  if (error) entry.message.setAttribute("role", "alert");
  else entry.message.removeAttribute("role");
  entry.message.replaceChildren(textElement("p", error ? `Page ${entry.page + 1} could not load: ${error.message}` : `Loading page ${entry.page + 1}…`));
  if (error) {
    const retry = textElement("button", `Retry page ${entry.page + 1}`);
    retry.type = "button";
    retry.addEventListener("click", () => {
      if (!currentBook || leavingReader) return;
      entry.state = "idle";
      pageMessage(entry);
      pumpPageLoads();
    });
    entry.message.append(retry);
  }
}

function releasePage(entry) {
  entry.controller?.abort();
  entry.controller = null;
  if (entry.url) URL.revokeObjectURL(entry.url);
  entry.url = null;
  entry.image?.removeAttribute("src");
  entry.image?.remove();
  entry.image = null;
  if (entry.state === "ready" || entry.state === "loading") {
    entry.state = "idle";
    pageMessage(entry);
  }
}

function stopReaderView() {
  viewGeneration++;
  clearTimeout(settleTimer);
  pageObserver?.disconnect();
  pageObserver = null;
  nearPages.clear();
  viewAnchor = null;
  swipe = null;
  touchPointers.clear();
  for (const entry of pages) releasePage(entry);
}

function captureViewAnchor() {
  const stage = $("page-stage");
  let page = currentPage;
  if (readerPreferences?.mode === "continuous" && activePages.length) {
    let low = 0;
    let high = activePages.length - 1;
    const position = stage.scrollTop + stage.clientHeight * .35;
    while (low < high) {
      const middle = Math.ceil((low + high) / 2);
      if (pages[activePages[middle]].node.offsetTop <= position) low = middle;
      else high = middle - 1;
    }
    page = activePages[low];
  }
  const entry = pages[page];
  return { page, offset: entry?.node.isConnected ? (stage.scrollTop - entry.node.offsetTop) / entry.node.offsetHeight : 0,
    x: stage.scrollWidth > stage.clientWidth ? stage.scrollLeft / (stage.scrollWidth - stage.clientWidth) : 0 };
}

function restoreViewAnchor(anchor) {
  const entry = pages[anchor.page];
  if (!entry?.node.isConnected) return;
  const stage = $("page-stage");
  stage.scrollTop = entry.node.offsetTop + anchor.offset * entry.node.offsetHeight;
  stage.scrollLeft = anchor.x * (stage.scrollWidth - stage.clientWidth);
}

function layoutReader(anchor = viewAnchor || captureViewAnchor(), changed = null) {
  if (!currentBook || !readerPreferences) return;
  const stage = $("page-stage");
  const width = Math.max(1, stage.clientWidth);
  const height = Math.max(1, stage.clientHeight);
  const continuous = readerPreferences.mode === "continuous";
  const entries = continuous ? changed ? [changed] : activePages.map((page) => pages[page]) : displayedPages.map((page) => pages[page]);
  const ratio = (entry) => {
    const [w, h] = rotatedDimensions(entry.width || 2, entry.height || 3, readerPreferences.rotation);
    return w / h;
  };
  const spreadWidth = Math.max(1, width - Math.max(0, entries.length - 1) * 8);
  const spreadHeight = continuous || !entries.length ? 0 : spreadWidth / entries.reduce((sum, entry) => sum + ratio(entry), 0);
  for (const entry of entries) {
    const geometry = pageGeometry(entry.width || 2, entry.height || 3, readerPreferences.rotation,
      continuous ? width : spreadHeight * ratio(entry), height, readerPreferences.fit, readerPreferences.zoom);
    entry.node.style.width = `${geometry.width}px`;
    entry.node.style.height = `${geometry.height}px`;
    if (entry.image) {
      entry.image.style.width = `${geometry.imageWidth}px`;
      entry.image.style.height = `${geometry.imageHeight}px`;
    }
  }
  restoreViewAnchor(anchor);
  scheduleVisibleProgress();
}

function refreshPagedView() {
  const next = !activePages.length ? [] : readerPreferences.mode === "spread"
    ? spreadPages(currentPage, currentBook.pageCount, readerPreferences.coverAlone, isPageWide, isPageIncluded)
    : [currentPage];
  if (next.join() !== displayedPages.join()) {
    displayedPages = next;
    $("reader-pages").replaceChildren(...next.map((page) => pages[page].node));
  }
  layoutReader();
  updatePageControls();
}

function renderReader(anchor = { page: currentPage, offset: 0, x: readerPreferences.rtl ? 1 : 0 }) {
  stopReaderView();
  activePages = pages.filter((entry) => isPageIncluded(entry.page)).map((entry) => entry.page);
  currentPage = isPageIncluded(anchor.page) ? anchor.page
    : nextPage(anchor.page, pages.length, 1, isPageIncluded) ?? nextPage(anchor.page, pages.length, -1, isPageIncluded) ?? anchor.page;
  if (currentPage !== anchor.page) anchor = { ...anchor, page: currentPage, offset: 0 };
  viewAnchor = activePages.length ? anchor : null;
  displayedPages = [];
  applyReaderControls();
  $("page-status").hidden = Boolean(activePages.length);
  $("page-status").textContent = "All pages are skipped. Choose Show all page types, change the skip checkboxes, or jump to a page or bookmark to reveal it.";
  $("skip-status").hidden = revealedPage === null;
  $("skip-status").textContent = revealedPage === null ? "" : `Page ${revealedPage + 1} is temporarily revealed despite its skipped type. Previous/Next still follow your skip filters.`;
  if (!activePages.length) {
    $("reader-pages").replaceChildren();
    updatePageControls();
    return;
  }
  if (readerPreferences.mode === "continuous") {
    const fragment = document.createDocumentFragment();
    for (const page of activePages) fragment.append(pages[page].node);
    $("reader-pages").replaceChildren(fragment);
    layoutReader(anchor);
    const generation = viewGeneration;
    pageObserver = new IntersectionObserver((observations) => {
      if (generation !== viewGeneration) return;
      for (const observation of observations) {
        const page = Number(observation.target.dataset.page);
        if (observation.isIntersecting) nearPages.add(page);
        else if (page !== viewAnchor?.page) {
          nearPages.delete(page);
          releasePage(pages[page]);
        }
      }
      pumpPageLoads();
      scheduleVisibleProgress();
    }, { root: $("page-stage"), rootMargin: "100% 100%" });
    for (const page of activePages) pageObserver.observe(pages[page].node);
    nearPages.add(currentPage);
  } else {
    displayedPages = [];
    nearPages = new Set(readerPreferences.mode === "spread" ? spreadCandidates() : [currentPage]);
    refreshPagedView();
  }
  pumpPageLoads();
}

function pumpPageLoads() {
  if (!currentBook || leavingReader || !activePages.length) return;
  const wanted = [...nearPages].sort((a, b) => Math.abs(a - currentPage) - Math.abs(b - currentPage));
  // ponytail: three in-flight decodes and viewport-near images, not an all-book cache.
  for (const page of wanted) {
    if (imageLoads >= 3) break;
    const entry = pages[page];
    if (entry?.state === "idle") loadReaderImage(entry);
  }
}

async function loadReaderImage(entry) {
  const book = currentBook;
  const epoch = authEpoch;
  const controller = new AbortController();
  entry.controller = controller;
  entry.state = "loading";
  imageLoads++;
  let url = null;
  try {
    const blob = await api(`/api/books/${encodeURIComponent(book.id)}/pages/${entry.page}`, { blob: true, signal: controller.signal });
    if (entry.controller !== controller || book !== currentBook) return;
    url = URL.createObjectURL(blob);
    const image = new Image();
    image.alt = `${bookTitle(book)}, ${pageLabel(entry.page)} of ${book.pageCount}`;
    image.draggable = false;
    image.src = url;
    await image.decode();
    if (epoch !== authEpoch || entry.controller !== controller || book !== currentBook) return;
    const anchor = viewAnchor || captureViewAnchor();
    entry.width = image.naturalWidth;
    entry.height = image.naturalHeight;
    entry.image = image;
    entry.url = url;
    url = null;
    entry.state = "ready";
    entry.node.append(image);
    pageMessage(entry);
    if (readerPreferences.mode === "continuous") layoutReader(anchor, entry);
    else refreshPagedView();
  } catch (error) {
    if (epoch !== authEpoch || entry.controller !== controller || book !== currentBook) return;
    entry.state = "error";
    pageMessage(entry, error);
    updatePageControls();
  } finally {
    if (url) URL.revokeObjectURL(url);
    if (entry.controller === controller) entry.controller = null;
    imageLoads--;
    pumpPageLoads();
    scheduleVisibleProgress();
  }
}

function scheduleVisibleProgress() {
  clearTimeout(settleTimer);
  if (!currentBook || leavingReader) return;
  const generation = viewGeneration;
  settleTimer = setTimeout(() => {
    if (generation !== viewGeneration || leavingReader || document.hidden) return;
    if (viewAnchor) {
      if (pages[viewAnchor.page]?.state !== "ready") return;
      restoreViewAnchor(viewAnchor);
      viewAnchor = null;
    }
    recordVisibleProgress();
  }, 180);
}

function recordVisibleProgress() {
  if (!currentBook || leavingReader || !activePages.length || viewAnchor || document.hidden) return;
  if (readerPreferences.mode === "spread" && !spreadResolved()) return;
  const stage = $("page-stage");
  const stageBounds = stage.getBoundingClientRect();
  const viewport = window.visualViewport;
  const top = viewport?.offsetTop ?? 0, left = viewport?.offsetLeft ?? 0;
  const viewportHeight = viewport?.height ?? window.innerHeight, viewportWidth = viewport?.width ?? window.innerWidth;
  const bounds = { top: Math.max(stageBounds.top, top), bottom: Math.min(stageBounds.bottom, top + viewportHeight),
    left: Math.max(stageBounds.left, left), right: Math.min(stageBounds.right, left + viewportWidth) };
  if (bounds.bottom <= bounds.top || bounds.right <= bounds.left) return;
  const continuous = readerPreferences.mode === "continuous";
  const visible = [];
  for (const page of continuous ? nearPages : displayedPages) {
    const entry = pages[page];
    if (!entry.node.isConnected) continue;
    const rect = entry.node.getBoundingClientRect();
    if (continuous && (rect.bottom < stageBounds.top - stage.clientWidth || rect.top > stageBounds.bottom + stage.clientWidth)) {
      nearPages.delete(page);
      releasePage(entry);
      continue;
    }
    if (entry.state !== "ready") continue;
    const height = Math.min(rect.bottom, bounds.bottom) - Math.max(rect.top, bounds.top);
    const width = Math.min(rect.right, bounds.right) - Math.max(rect.left, bounds.left);
    if (height >= Math.min(rect.height, stage.clientHeight, viewportHeight) * .2 && width >= Math.min(rect.width, stage.clientWidth, viewportWidth) * .2) {
      visible.push({ page, distance: Math.abs((rect.top + rect.bottom - bounds.top - bounds.bottom) / 2) });
    }
  }
  if (!visible.length) return;
  const atEnd = stage.scrollTop + stage.clientHeight >= stage.scrollHeight - 2;
  visible.sort((a, b) => continuous && !atEnd ? a.distance - b.distance : b.page - a.page);
  currentPage = visible[0].page;
  updatePageControls();
  const completed = currentPage === activePages.at(-1);
  if (lastViewedPage === currentPage && currentBook.completed === completed) return;
  lastViewedPage = currentPage;
  currentBook.currentPage = currentPage;
  currentBook.completed = completed;
  queueProgress({ id: currentBook.id, page: currentPage, completed: currentBook.completed });
}

function showPage(page, reveal = true) {
  if (!currentBook || leavingReader || !Number.isInteger(page) || page < 0 || page >= currentBook.pageCount) return;
  const previousReveal = revealedPage;
  revealedPage = reveal && !pageIncluded(pages[page].typeMask, readerPreferences.skipPageTypes) ? page : null;
  pageNumberDraft = false;
  currentPage = page;
  updatePageControls();
  if (readerPreferences.mode !== "continuous" || previousReveal !== revealedPage) {
    renderReader();
    return;
  }
  viewAnchor = { page, offset: 0, x: 0 };
  if (pages[page].state === "error") {
    pages[page].state = "idle";
    pageMessage(pages[page]);
  }
  nearPages.add(page);
  restoreViewAnchor(viewAnchor);
  pumpPageLoads();
  scheduleVisibleProgress();
}

function turnPage(direction) {
  const button = $(direction > 0 ? "next-page" : "previous-page");
  if (button.disabled) return;
  const page = readerPreferences.mode === "continuous" ? currentPage : direction > 0 ? displayedPages.at(-1) : displayedPages[0];
  const target = nextPage(page, currentBook.pageCount, direction, (next) => pageIncluded(pages[next].typeMask, readerPreferences.skipPageTypes));
  if (target !== null) showPage(target, false);
}

async function leaveReader() {
  if (!currentBook) return;
  const epoch = authEpoch;
  recordVisibleProgress();
  leavingReader = true;
  clearTimeout(settleTimer);
  updatePageControls();
  try {
    await flushProgress();
    await preferenceQueue;
    await bookmarkQueue;
    if (epoch !== authEpoch) return;
    if (unsavedPreferences) throw new Error("Your reader preferences could not be saved. Retry preference save before leaving the reader.");
    if (unsavedBookmark) throw new Error("Your bookmark change could not be saved. Retry bookmark save before leaving the reader.");
    if (document.fullscreenElement === $("reader-view")) await document.exitFullscreen();
    if (epoch === authEpoch) clearReader();
  } finally {
    if (epoch === authEpoch) {
      leavingReader = false;
      updatePageControls();
      pumpPageLoads();
      scheduleVisibleProgress();
    }
  }
}

async function route(hash) {
  if (!session || session.mustChangePassword) return;
  const epoch = authEpoch;
  const match = /^#reader\/([^/]+)$/.exec(hash);
  const id = match ? decodeURIComponent(match[1]) : null;
  if (currentBook && String(currentBook.id) === id) return;
  try {
    await leaveReader();
    if (epoch !== authEpoch || !session) return;
    clearError();
    $("password-form").reset();
    $("change-password").hidden = false;
    if (!id) {
      showView("library");
      if (activeList()?.kind === "smart") clearSelection();
      loadLists();
      loadPublishers();
      await loadLibrary();
      return;
    }
    showView("reader");
    $("reader-title").textContent = "Loading book…";
    updatePageControls();
    $("page-status").hidden = false;
    $("page-status").textContent = "Loading book and reader preferences…";
    $("save-status").textContent = "";
    $("retry-save").hidden = true;
    const [book, , pageInfo, bookmarks] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}`), loadReaderPreferences(),
      api(`/api/books/${encodeURIComponent(id)}/page-info`), api(`/api/books/${encodeURIComponent(id)}/bookmarks`)]);
    if (epoch !== authEpoch || !session) return;
    if (book.availability !== "available") throw new Error("This source is missing or unavailable. Restore or reconnect it and scan again. Metadata and bookmarks remain accessible in Book details.");
    if (book.pageCount < 1) throw new Error("This book has no readable pages.");
    currentBook = book;
    const info = new Map(pageInfo.map((entry) => [entry.page, entry]));
    pages = Array.from({ length: book.pageCount }, (_, page) => createReaderPage(page, info.get(page)));
    personalBookmarks = bookmarks;
    currentPage = Math.max(0, Math.min(book.currentPage, book.pageCount - 1));
    $("reader-title").textContent = bookTitle(book);
    $("page-count").textContent = `/ ${book.pageCount}`;
    $("page-number").max = String(book.pageCount);
    $("page-stage").focus({ preventScroll: true });
    renderReader();
    renderBookmarks();
    $("reader-view").scrollIntoView({ block: "start" });
  } catch (error) {
    if (epoch !== authEpoch) return;
    if (currentBook) history.replaceState(null, "", `#reader/${encodeURIComponent(currentBook.id)}`);
    else if (session) {
      history.replaceState(null, "", "#library");
      showView("library");
      loadPublishers();
      await loadLibrary();
    }
    showError(error);
  }
}

function scheduleRoute() {
  const hash = location.hash;
  const epoch = authEpoch;
  routeQueue = routeQueue.then(() => { if (epoch === authEpoch) return route(hash); })
    .catch((error) => { if (epoch === authEpoch) showError(error); });
}

$("login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = event.submitter;
  button.disabled = true;
  clearError();
  try {
    const values = Object.fromEntries(new FormData(event.currentTarget));
    signedIn(await api("/api/login", { method: "POST", body: JSON.stringify(values) }));
  } catch (error) { showError(error); }
  finally { button.disabled = false; }
});

$("logout").addEventListener("click", async () => {
  const epoch = authEpoch;
  $("logout").disabled = true;
  try {
    await leaveReader();
    if (epoch !== authEpoch) return;
    await api("/api/logout", { method: "POST" });
    clearError();
    showLogin();
  } catch (error) { if (epoch === authEpoch) showError(error); }
  finally { $("logout").disabled = false; }
});

function changeLibrary() {
  clearSelection();
  groupKey = null;
  groupLabel = "";
  $("sort-direction").disabled = $("book-sort").value === "list";
  if ($("book-sort").value === "list") $("sort-direction").value = "asc";
  libraryPage = 1;
  libraryQuery = $("search").value.trim();
  clearError();
  loadLibrary();
}

$("search-form").addEventListener("submit", (event) => { event.preventDefault(); changeLibrary(); });
$("clear-search").addEventListener("click", () => { $("search").value = ""; changeLibrary(); });
$("search").addEventListener("search", () => { if (!$("search").value) changeLibrary(); });
for (const id of ["book-sort", "sort-direction", "book-status", "book-rating", "publisher", "page-size", "book-availability", "group-by"]) $(id).addEventListener("change", changeLibrary);
$("back-to-groups").addEventListener("click", changeLibrary);
$("previous-books").addEventListener("click", () => { if (libraryPage > 1) { libraryPage--; loadLibrary(); } });
$("next-books").addEventListener("click", () => { if (libraryPage < libraryPages) { libraryPage++; loadLibrary(); } });
$("scan").addEventListener("click", () => requestScan("start"));
$("cancel-scan").addEventListener("click", () => requestScan("cancel"));
$("retry-scan-status").addEventListener("click", () => requestScan());
$("maintenance").addEventListener("click", () => {
  $("maintenance-error").hidden = true;
  $("maintenance-dialog").showModal();
  loadMaintenance();
});
$("maintenance-dialog").addEventListener("close", () => { maintenanceGeneration++; });
$("maintenance-kind").addEventListener("change", () => {
  maintenancePage = 1;
  $("maintenance-items").replaceChildren();
  $("maintenance-error").hidden = true;
  loadMaintenance();
});
$("refresh-maintenance").addEventListener("click", () => { $("maintenance-error").hidden = true; loadMaintenance(); });
$("previous-maintenance").addEventListener("click", () => { if (maintenancePage > 1) { maintenancePage--; loadMaintenance(); } });
$("next-maintenance").addEventListener("click", () => { if (maintenancePage < maintenancePages) { maintenancePage++; loadMaintenance(); } });

for (const button of document.querySelectorAll("[data-close]")) button.addEventListener("click", () => {
  if (canCloseDialog(button.dataset.close)) $(button.dataset.close).close();
});
$("details-dialog").addEventListener("close", () => { detailGeneration++; ratingDirty = false; });
$("read-book").addEventListener("click", () => {
  if (!detailBook || detailBook.availability !== "available" || !canCloseDialog("details-dialog")) return;
  $("details-dialog").close();
  $("maintenance-dialog").close();
  $("metadata-report-dialog").close();
  location.hash = `reader/${encodeURIComponent(detailBook.id)}`;
});


$("change-password").addEventListener("click", async () => {
  const epoch = authEpoch;
  try {
    await leaveReader();
    if (epoch !== authEpoch) return;
    history.replaceState(null, "", "#library");
    openPasswordView();
  } catch (error) { if (epoch === authEpoch) showError(error); }
});
$("cancel-password").addEventListener("click", () => {
  if (!session?.mustChangePassword) scheduleRoute();
});
$("password-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const { currentPassword, newPassword, confirmation } = form.elements;
  if (newPassword.value !== confirmation.value) {
    $("password-status").textContent = "The new passwords do not match.";
    confirmation.focus();
    return;
  }
  if (currentPassword.value === newPassword.value) {
    $("password-status").textContent = "Choose a password different from your current password.";
    newPassword.focus();
    return;
  }
  const epoch = authEpoch;
  event.submitter.disabled = true;
  $("password-status").textContent = "";
  try {
    await api("/api/account/password", { method: "POST", body: JSON.stringify({ currentPassword: currentPassword.value, newPassword: newPassword.value }) });
    showLogin();
    $("login-message").textContent = "Password changed. All your sessions have ended. Sign in with your new password.";
  } catch (error) { if (epoch === authEpoch) $("password-status").textContent = error.message; }
  finally { event.submitter.disabled = false; }
});

async function loadUsers() {
  const users = await api("/api/users");
  if (!$("user-dialog").open) return;
  $("user-list").replaceChildren();
  for (const user of users) {
    const row = textElement("section", "", "user-row");
    row.dataset.userId = user.id;
    row.append(textElement("h3", `${user.username}${user.id === session.id ? " (you)" : ""}`),
      textElement("p", user.mustChangePassword ? "Password change required at next sign-in." : "Password change not required.", "muted"));
    const roleForm = document.createElement("form");
    roleForm.className = "actions";
    roleForm.innerHTML = '<label>Role <select name="role"><option value="reader">Reader</option><option value="admin">Administrator</option></select></label><button type="submit">Save role</button>';
    roleForm.elements.role.value = user.role;
    roleForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const role = roleForm.elements.role.value;
      if (!confirm(`Change “${user.username}” to ${role}? Changed roles end all sessions for this account.${user.id === session.id && role !== user.role ? " You will be signed out." : ""}`)) return;
      mutateAccount(`/api/users/${encodeURIComponent(user.id)}/role`, "PUT", { role }, "Role saved.", user.id === session.id && role !== user.role);
    });
    row.append(roleForm);
    if (user.id === session.id) {
      const ownPassword = textElement("button", "Change my password");
      ownPassword.type = "button";
      ownPassword.addEventListener("click", () => { $("user-dialog").close(); $("change-password").click(); });
      row.append(ownPassword);
    } else {
      const reset = document.createElement("details");
      reset.append(textElement("summary", "Reset password"));
      const form = document.createElement("form");
      form.className = "form-stack";
      form.innerHTML = '<label>New password<input name="password" type="password" autocomplete="new-password" required minlength="12" maxlength="256"></label><label>Confirm new password<input name="confirmation" type="password" autocomplete="new-password" required minlength="12" maxlength="256"></label><button type="submit">Reset password</button>';
      reset.addEventListener("toggle", () => { if (!reset.open) form.reset(); });
      form.addEventListener("submit", (event) => {
        event.preventDefault();
        if (form.elements.password.value !== form.elements.confirmation.value) {
          $("user-status").textContent = "The new passwords do not match.";
          form.elements.confirmation.focus();
          return;
        }
        if (!confirm(`Reset the password for “${user.username}”? All their sessions will end and they must change this password at next sign-in.`)) return;
        mutateAccount(`/api/users/${encodeURIComponent(user.id)}/password`, "PUT", { password: form.elements.password.value }, "Password reset. The account must change it at next sign-in.", false, form);
      });
      reset.append(form);
      row.append(reset);
    }
    const remove = textElement("button", "Delete account");
    remove.type = "button";
    remove.addEventListener("click", () => {
      if (!confirm(`Permanently delete “${user.username}”? Their private reading history, ratings, bookmarks, lists, list folders, and reader preferences will be lost. Shared books and files are kept. This cannot be undone. The last administrator is protected.${user.id === session.id ? " You will be signed out." : ""}`)) return;
      mutateAccount(`/api/users/${encodeURIComponent(user.id)}`, "DELETE", null, "Account deleted.", user.id === session.id);
    });
    row.append(remove);
    $("user-list").append(row);
  }
}

async function mutateAccount(path, method, body, message, endSession = false, form = null) {
  const epoch = authEpoch;
  $("user-controls").disabled = true;
  $("user-status").textContent = "Saving…";
  try {
    if (endSession) await leaveReader();
    if (epoch !== authEpoch) return;
    await api(path, { method, ...(body ? { body: JSON.stringify(body) } : {}) });
    form?.reset();
    if (endSession) {
      showLogin();
      $("login-message").textContent = "Your account changed. You have been signed out.";
      return;
    }
    $("user-status").textContent = message;
    await loadUsers();
  } catch (error) { if (epoch === authEpoch) $("user-status").textContent = error.message; }
  finally { if (epoch === authEpoch) $("user-controls").disabled = false; }
}

$("manage-users").addEventListener("click", async () => {
  $("user-status").textContent = "Loading accounts…";
  $("user-form").reset();
  $("user-list").replaceChildren();
  $("user-controls").disabled = false;
  $("user-dialog").showModal();
  const epoch = authEpoch;
  try { await loadUsers(); if (epoch === authEpoch) $("user-status").textContent = ""; }
  catch (error) { if (epoch === authEpoch) $("user-status").textContent = error.message; }
});
$("user-dialog").addEventListener("close", () => {
  $("user-form").reset();
  $("user-list").replaceChildren();
});
$("user-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const values = Object.fromEntries(new FormData(form));
  mutateAccount("/api/users", "POST", values, `Account “${values.username}” created.`, false, form);
});

$("close-reader").addEventListener("click", () => { location.hash = "library"; });
$("previous-page").addEventListener("click", () => turnPage(-1));
$("next-page").addEventListener("click", () => turnPage(1));
for (const event of ["focus", "input"]) $("page-number").addEventListener(event, () => { pageNumberDraft = true; });
$("page-form").addEventListener("submit", (event) => { event.preventDefault(); showPage(Number($("page-number").value) - 1); });
$("bookmark-form").addEventListener("submit", (event) => {
  event.preventDefault();
  if (!currentBook || !activePages.length) return;
  const name = $("bookmark-name").value.trim();
  if (!name || name.length > 200 || /[\p{Cc}]/u.test(name)) {
    $("bookmark-name").setCustomValidity("Enter a name of 1–200 characters without control characters.");
    $("bookmark-name").reportValidity();
    return;
  }
  queueBookmark({ page: currentPage, name });
});
$("bookmark-name").addEventListener("input", () => $("bookmark-name").setCustomValidity(""));
$("retry-bookmark").addEventListener("click", () => {
  if (!unsavedBookmark) return;
  clearError();
  queueBookmark(unsavedBookmark);
});
for (const [index, name] of ["Front cover", "Inner cover", "Roundup", "Story", "Advertisement", "Editorial", "Letters", "Preview", "Back cover", "Other", "Deleted"].entries()) {
  const label = textElement("label", "", "checkbox");
  const input = document.createElement("input");
  input.type = "checkbox";
  input.value = String(1 << index);
  input.id = `skip-type-${1 << index}`;
  input.addEventListener("change", changeReaderPreferences);
  label.append(input, document.createTextNode(name));
  $("skip-type-options").append(label);
}
$("reset-skip-types").addEventListener("click", () => {
  for (const input of $("skip-type-options").querySelectorAll("input")) input.checked = false;
  changeReaderPreferences();
});
function changeReaderPreferences() {
  if (!currentBook || leavingReader || !readerPreferences) return;
  const values = {};
  for (const [id, min, max] of [["zoom", 50, 300], ["brightness", 50, 200], ["contrast", 50, 200]]) {
    values[id] = Number($(id).value);
    if (!Number.isInteger(values[id]) || values[id] < min || values[id] > max) {
      $(id).setCustomValidity(`Enter a whole number from ${min} to ${max}.`);
      $(id).reportValidity();
      return;
    }
    $(id).setCustomValidity("");
  }
  const anchor = viewAnchor || captureViewAnchor();
  const previous = readerPreferences;
  readerPreferences = { mode: $("reader-mode").value, fit: $("fit").value, ...values, rtl: $("rtl").checked,
    coverAlone: $("cover-alone").checked, rotation: Number($("rotation").value),
    skipPageTypes: [...$("skip-type-options").querySelectorAll("input:checked")].reduce((mask, input) => mask | Number(input.value), 0) };
  if (previous.rtl !== readerPreferences.rtl && readerPreferences.mode !== "continuous") anchor.x = 1 - anchor.x;
  queuePreferences();
  if (previous.skipPageTypes !== readerPreferences.skipPageTypes) revealedPage = null;
  if (previous.mode !== readerPreferences.mode || previous.coverAlone !== readerPreferences.coverAlone || previous.skipPageTypes !== readerPreferences.skipPageTypes) renderReader(anchor);
  else {
    viewAnchor = activePages.length ? anchor : null;
    applyReaderControls();
    if (readerPreferences.mode === "spread" && previous.rotation !== readerPreferences.rotation) refreshPagedView();
    else layoutReader(anchor);
  }
}
for (const id of ["reader-mode", "fit", "zoom", "rtl", "cover-alone", "rotation", "brightness", "contrast"]) $(id).addEventListener("change", changeReaderPreferences);
for (const id of ["zoom", "brightness", "contrast"]) $(id).addEventListener("input", () => $(id).setCustomValidity(""));
$("reset-visuals").addEventListener("click", () => {
  $("rotation").value = "0";
  $("brightness").value = $("contrast").value = "100";
  changeReaderPreferences();
});
$("reset-zoom").addEventListener("click", () => { $("zoom").value = "100"; changeReaderPreferences(); });
$("retry-preferences").addEventListener("click", () => {
  if (!unsavedPreferences) return;
  clearError();
  queuePreferences();
});
$("retry-save").addEventListener("click", async () => {
  if (!unsavedProgress) return;
  clearError();
  queueProgress(unsavedProgress);
  await progressQueue;
});
document.addEventListener("keydown", (event) => {
  if (!currentBook || leavingReader || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.target.closest("input, textarea, select, [contenteditable], dialog")) return;
  if (readerPreferences.mode !== "continuous" && (event.key === "PageDown" || event.key === "PageUp")) {
    event.preventDefault();
    turnPage(event.key === "PageDown" ? 1 : -1);
  } else if (readerPreferences.mode !== "continuous" && readerPreferences.zoom === 100 && (window.visualViewport?.scale ?? 1) <= 1.01 && ["ArrowLeft", "ArrowRight"].includes(event.key)) {
    event.preventDefault();
    turnPage((event.key === "ArrowRight" ? 1 : -1) * (readerPreferences.rtl ? -1 : 1));
  } else if (["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "].includes(event.key)) viewAnchor = null;
});
function updateTouchMode() {
  $("page-stage").dataset.swipe = String(readerPreferences?.mode !== "continuous" && readerPreferences?.zoom === 100 && (window.visualViewport?.scale ?? 1) <= 1.01);
}
window.visualViewport?.addEventListener("resize", () => { swipe = null; updateTouchMode(); scheduleVisibleProgress(); });
window.visualViewport?.addEventListener("scroll", scheduleVisibleProgress, { passive: true });
window.addEventListener("pointerdown", (event) => {
  if (!currentBook || leavingReader) return;
  const stageTarget = event.target.closest("#page-stage") && !event.target.closest("button, input, select, a");
  if (stageTarget) viewAnchor = null;
  if (event.pointerType !== "touch") return;
  touchPointers.add(event.pointerId);
  if (touchPointers.size !== 1) { swipe = null; return; }
  if (!stageTarget) return;
  swipe = { id: event.pointerId, x: event.clientX, y: event.clientY, time: performance.now(),
    left: $("page-stage").scrollLeft, top: $("page-stage").scrollTop, scale: window.visualViewport?.scale ?? 1 };
}, { passive: true });
window.addEventListener("pointerup", (event) => {
  touchPointers.delete(event.pointerId);
  const gesture = swipe;
  swipe = null;
  if (!gesture || gesture.id !== event.pointerId || touchPointers.size || !currentBook || leavingReader) return;
  const stage = $("page-stage");
  if (Math.abs(stage.scrollLeft - gesture.left) > 2 || Math.abs(stage.scrollTop - gesture.top) > 2) return;
  const direction = swipeTurn(event.clientX - gesture.x, event.clientY - gesture.y, performance.now() - gesture.time,
    readerPreferences, Math.max(gesture.scale, window.visualViewport?.scale ?? 1));
  if (direction) turnPage(direction);
}, { passive: true });
window.addEventListener("pointercancel", (event) => { touchPointers.delete(event.pointerId); swipe = null; }, { passive: true });
$("page-stage").addEventListener("wheel", () => { viewAnchor = null; swipe = null; }, { passive: true });
$("page-stage").addEventListener("scroll", scheduleVisibleProgress, { passive: true });
window.addEventListener("scroll", scheduleVisibleProgress, { passive: true });
document.addEventListener("visibilitychange", () => {
  swipe = null;
  touchPointers.clear();
  if (!document.hidden) scheduleVisibleProgress();
});
new ResizeObserver(() => {
  if (!currentBook || leavingReader) return;
  viewAnchor ||= captureViewAnchor();
  layoutReader(viewAnchor);
}).observe($("page-stage"));
function updateFullscreen() {
  const active = document.fullscreenElement === $("reader-view");
  const supported = document.fullscreenEnabled && $("reader-view").requestFullscreen;
  $("fullscreen").disabled = !supported || !currentBook || leavingReader;
  $("fullscreen").textContent = active ? "Exit fullscreen" : "Fullscreen";
  $("fullscreen").setAttribute("aria-pressed", String(active));
  $("fullscreen").title = supported ? "" : "Fullscreen is unavailable in this browser.";
}
$("fullscreen").addEventListener("click", async () => {
  const epoch = authEpoch;
  const book = currentBook;
  try {
    if (document.fullscreenElement === $("reader-view")) await document.exitFullscreen();
    else await $("reader-view").requestFullscreen();
    if ((epoch !== authEpoch || book !== currentBook) && document.fullscreenElement === $("reader-view")) await document.exitFullscreen();
  } catch (error) { if (epoch === authEpoch) showError(error); }
});
document.addEventListener("fullscreenchange", updateFullscreen);
updateFullscreen();
window.addEventListener("hashchange", scheduleRoute);
window.addEventListener("beforeunload", (event) => {
  if (unsavedProgress || unsavedPreferences || unsavedBookmark || ratingDirty || ratingBusy || folderDirty || folderBusy || metadataDirty || metadataBusy) { event.preventDefault(); event.returnValue = ""; }
});

(async () => {
  try {
    const data = await api("/api/session");
    if (data.authenticated) signedIn(data);
    else showLogin();
  } catch (error) { showLogin(); showError(error); }
})();
