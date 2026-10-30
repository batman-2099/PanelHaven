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
let readingOrder = [];
let readingPositions = [];
let pageOrder = null;
let pageOrderDraft = [];
let pageOrderBusy = false;
let pageEditConflict = false;
let pageOrderGeneration = 0;
let draggedPage = null;
$("reader-details").addEventListener("click", () => { if (currentBook && !leavingReader) openDetails(currentBook.id); });

function validatePageOrder(order, id, count) {
  if (order?.bookId !== id || typeof order.revision !== "string" || !order.revision ||
      !Array.isArray(order.pages) || order.pages.length !== count || new Set(order.pages).size !== count ||
      order.pages.some((page) => !Number.isInteger(page) || page < 0 || page >= count))
    throw new Error("Page order does not match this book. Reload after scanning the source.");
}

function setReadingOrder(order) {
  readingOrder = [...order];
  readingPositions = [];
  readingOrder.forEach((page, position) => { readingPositions[page] = position; });
}

function pageOrderDirty() {
  return Boolean(pageOrder && pageOrderDraft.some((page, position) => page !== pageOrder.pages[position]));
}

function resetPageOrder() {
  pageOrderGeneration++;
  pageOrder = null;
  pageOrderDraft = [];
  pageOrderBusy = pageEditConflict = false;
  draggedPage = null;
  $("page-order-pages").replaceChildren();
}

function updatePageEditorDraftState() {
  const busy = pageOrderBusy || pageMetadataBusy;
  const loaded = pageOrder && pageMetadata && pageOrder.bookId === pageMetadata.bookId && pageOrder.pages.length === pageMetadata.pages.length;
  const dirty = pageOrderDirty() || pageMetadataDirty;
  for (const id of ["save-page-changes", "save-page-changes-bottom"])
    $(id).disabled = busy || !loaded || pageEditConflict || !dirty;
  $("page-order-draft-state").textContent = !loaded ? "" : dirty ? "Unsaved changes." : "All changes saved.";
}

function updatePageEditorControls() {
  const busy = pageOrderBusy || pageMetadataBusy;
  updatePageEditorDraftState();
  $("reset-page-changes").disabled = busy;
  $("page-metadata-dialog").querySelector("[data-close]").disabled = busy;
  $("page-order-pages").setAttribute("aria-busy", String(busy));
  for (const [position, node] of [...$("page-order-pages").children].entries()) {
    node.querySelector("img").draggable = !busy;
    node.querySelector("[data-retry-image]").disabled = busy;
    node.querySelector("[data-move='-1']").disabled = busy || position === 0;
    node.querySelector("[data-move='1']").disabled = busy || position === pageOrderDraft.length - 1;
    node.querySelector("fieldset").disabled = busy || !pageMetadataDraft[Number(node.dataset.page)];
  }
}

function moveOrganizedPage(page, position, direction = null) {
  if (pageOrderBusy || pageMetadataBusy || !pageOrder || position < 0 || position >= pageOrderDraft.length) return;
  const from = pageOrderDraft.indexOf(page);
  if (from < 0 || from === position) return;
  pageOrderDraft.splice(from, 1);
  pageOrderDraft.splice(position, 0, page);
  renderPageOrder();
  $("page-order-status").textContent = `Physical page ${page + 1} moved to position ${position + 1}. Not saved yet.`;
  const node = $("page-order-pages").querySelector(`[data-page="${page}"]`);
  const button = direction === null ? null : node.querySelector(`[data-move="${direction}"]`);
  (button && !button.disabled ? button : node).focus({ preventScroll: true });
}

function renderPageOrder() {
  const existing = new Map([...$("page-order-pages").children].map((node) => [Number(node.dataset.page), node]));
  const nodes = pageOrderDraft.map((page, position) => {
    let node = existing.get(page);
    if (!node) {
      node = textElement("li", "", "page-order-card");
      node.dataset.page = String(page);
      node.tabIndex = -1;
      const image = new Image();
      image.loading = "lazy";
      image.decoding = "async";
      image.width = 140;
      image.height = 180;
      image.alt = `Physical page ${page + 1}`;
      const imageError = textElement("div", "", "page-order-image-error");
      imageError.hidden = true;
      const imageStatus = textElement("p", "");
      imageStatus.setAttribute("role", "status");
      const retry = textElement("button", "Retry thumbnail");
      retry.type = "button";
      retry.dataset.retryImage = "";
      retry.setAttribute("aria-label", `Retry thumbnail: physical page ${page + 1}`);
      let retrying = false;
      image.addEventListener("error", () => {
        retrying = false;
        retry.removeAttribute("aria-disabled");
        image.hidden = true;
        imageError.hidden = false;
        imageStatus.textContent = "Thumbnail unavailable. Retry without changing your draft.";
      });
      image.addEventListener("load", () => {
        retrying = false;
        retry.removeAttribute("aria-disabled");
        if (document.activeElement === retry) node.focus({ preventScroll: true });
        image.hidden = false;
        imageError.hidden = true;
      });
      retry.addEventListener("click", () => {
        if (pageOrderBusy || pageMetadataBusy || retrying) return;
        retrying = true;
        retry.setAttribute("aria-disabled", "true");
        imageStatus.textContent = "Retrying thumbnail…";
        image.loading = "eager";
        const url = new URL(image.src);
        url.searchParams.set("retry", String(Date.now()));
        image.src = url.href;
      });
      image.src = `/api/books/${encodeURIComponent(pageOrder.bookId)}/pages/${page}`;
      imageError.append(imageStatus, retry);
      node.append(image, imageError, textElement("p", "", "page-order-label"));
      for (const [direction, label] of [[-1, "Move earlier"], [1, "Move later"]]) {
        const button = textElement("button", label);
        button.type = "button";
        button.dataset.move = String(direction);
        button.setAttribute("aria-label", `${label}: physical page ${page + 1}`);
        button.addEventListener("click", () => moveOrganizedPage(page, pageOrderDraft.indexOf(page) + direction, direction));
        node.append(button);
      }
      const fields = document.createElement("fieldset");
      fields.className = "page-order-details";
      fields.append(textElement("legend", `Physical page ${page + 1} details`, "sr-only"));
      for (const [field, title] of [["type", "Page type"], ["bookmark", "Embedded bookmark"]]) {
        const label = textElement("label", title);
        const control = document.createElement(field === "type" ? "select" : "input");
        control.dataset.pageField = field;
        if (field === "bookmark") control.maxLength = 200;
        label.append(control);
        fields.append(label);
      }
      node.append(fields);
      renderPageMetadata(node);
      node.addEventListener("dragstart", (event) => {
        if (pageOrderBusy || pageMetadataBusy) { event.preventDefault(); return; }
        draggedPage = page;
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", String(page));
      });
      node.addEventListener("dragover", (event) => {
        if (!pageOrderBusy && !pageMetadataBusy && draggedPage !== null) { event.preventDefault(); event.dataTransfer.dropEffect = "move"; }
      });
      node.addEventListener("drop", (event) => {
        event.preventDefault();
        if (draggedPage !== null) moveOrganizedPage(draggedPage, pageOrderDraft.indexOf(page));
        draggedPage = null;
      });
      node.addEventListener("dragend", () => { draggedPage = null; });
    }
    node.querySelector(".page-order-label").textContent = `Position ${position + 1}, Physical page ${page + 1}`;
    return node;
  });
  $("page-order-pages").replaceChildren(...nodes);
  updatePageEditorControls();
}

async function loadPageOrder() {
  if (!detailBook || pageOrderBusy || session?.role !== "admin") return;
  const id = detailBook.id;
  const epoch = authEpoch;
  const generation = ++pageOrderGeneration;
  pageOrderBusy = true;
  updatePageEditorControls();
  $("page-order-status").textContent = "Loading saved reading order…";
  try {
    const result = await api(`/api/books/${encodeURIComponent(id)}/page-order`);
    if (epoch !== authEpoch || generation !== pageOrderGeneration) return;
    validatePageOrder(result, id, result.pages?.length);
    pageOrder = result;
    pageOrderDraft = [...result.pages];
    $("page-order-pages").replaceChildren();
    renderPageOrder();
    $("page-order-status").textContent = "";
  } catch (error) {
    if (epoch === authEpoch && generation === pageOrderGeneration) {
      pageEditConflict = true;
      $("page-order-status").textContent = `Could not load page order: ${error.message}. Use Reset changes to retry; scan changed sources first.`;
    }
  } finally {
    if (epoch === authEpoch && generation === pageOrderGeneration) { pageOrderBusy = false; updatePageEditorControls(); }
  }
}

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
let routeGeneration = 0;
let leavingReader = false;
let nextIssue = { state: "idle", id: null };
let libraryPage = 1;
let libraryPages = 1;
let libraryQuery = "";
let catalogLocation = "library";
let publisherGeneration = 0;
let groupKey = null;
let sectionKey = null;
let closedSections = new Set();
let groupLabel = "";
let groupOrigin = null;
let libraryReturnFocus = null;
let catalogReturn = null;
let discoveryReturn = null;
let advancedFilter = null;
let listEditorMode = "list";
let ruleManualLists = [];
const catalogControls = ["reading-list", "group-by", "stack-by", "book-sort", "sort-direction", "book-status", "book-rating", "publisher", "page-size", "book-availability"];
let maintenancePage = 1;
let maintenancePages = 1;
let maintenanceGeneration = 0;
let maintenanceBusy = false;
let duplicateId = null;
let duplicatePage = 1;
let duplicatePages = 1;
let duplicateGeneration = 0;
let duplicateBusy = false;
let serverSettings = null;
let serverSettingsBaseline = "";
let serverSettingsGeneration = 0;
let serverSettingsBusy = false;
let serverSettingsStale = false;
const failedCovers = new Set();
let scanStatus = null;
let dismissedScanResult = null;
let scanTimer = null;
let scanRequest = null;
let scanLogRequest = null;
let scanBusy = false;
let libraryItems = [];
const selectedBooks = new Set();
let readingStatusBusy = false;
let comicExport = null;
let kavitaImport = null;
let readingLists = [];
let listFolders = [];
let folderEditingId = "";
let folderDirty = false;
let folderBusy = false;
let accountBusy = false;
let passwordBusy = false;
let ratingDirty = false;
let ratingBusy = false;
let listsGeneration = 0;
let listEditingId = null;
let listDialogGeneration = 0;
let listDirty = false;
let listBusy = false;
let listPreviewGeneration = 0;
let listPreviewBusy = false;
let listPreviewKey = null;
let listPreviewPage = 1;
let listPreviewPages = 1;
let managementBusy = false;
let seriesList = null;
let metadataSchema = null;
let metadataBookIds = [];
let metadataGeneration = 0;
let metadataBulk = false;
let metadataDirty = false;
let metadataBusy = false;
let metadataPage = null;
let metadataReturn = null;
let metadataPreview = null;
let metadataReportPage = 1;
let metadataReportPages = 1;
let metadataReportGeneration = 0;
let intake = null;
let archiveBookId = null;
let archiveGeneration = 0;
let archiveBusy = false;
let archivePreview = null;
let fileOperationBookId = null;
let fileOperationGeneration = 0;
let fileOperationDraftGeneration = 0;
let fileOperationBusy = false;
let fileOperationPreview = null;
let fileOperationExtension = "";
let fileOperationInitialDraft = "";
let organizationBookIds = [];
let organizationRows = [];
let organizationGeneration = 0;
let organizationDraftGeneration = 0;
let organizationBusy = false;
let organizationPreview = null;
let organizationInitialDraft = "";
let deletion = null;
let deletionBusy = false;
let pageMetadata = null;
let pageMetadataDraft = [];
let pageMetadataGeneration = 0;
let pageMetadataBusy = false;
let pageMetadataDirty = false;

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
  if (name !== "library") {
    searchGeneration++;
    finishLibraryLoading();
  }
  $("account-menu").open = false;
  $("account-menu").hidden = !session || session.mustChangePassword || name === "password";
  $("admin-actions").hidden = session?.role !== "admin" || !!session?.mustChangePassword;
  if (name !== "settings" && !$("settings-view").hidden) resetServerSettings();
  if (name !== "metadata" && !$("metadata-view").hidden) resetMetadataEditor();
  for (const view of ["login", "library", "reader", "password", "settings", "metadata"]) $(view + "-view").hidden = view !== name;
  document.title = `${{ login: "Sign in", password: "Change password", library: $("library-title").textContent, settings: "Server settings", metadata: $("metadata-title").textContent, reader: currentBook ? bookTitle(currentBook) : "Loading book" }[name]} — Panel Haven`;
  $("startup").hidden = true;
  renderScan();
}

const narrowCatalog = matchMedia("(max-width: 900px)");
function updateCatalogArrangement() {
  const arrangement = $("catalog-arrangement");
  const summary = arrangement.querySelector("summary");
  const focused = document.activeElement;
  summary.hidden = false;
  if (narrowCatalog.matches && arrangement.contains(focused)) summary.focus();
  arrangement.open = !narrowCatalog.matches;
  if (!narrowCatalog.matches && focused === summary) $("book-status").focus();
  summary.hidden = !narrowCatalog.matches;
}
narrowCatalog.addEventListener("change", updateCatalogArrangement);
updateCatalogArrangement();

$("account-menu").addEventListener("click", (event) => {
  if (!event.target.closest("button") || event.target.closest("#scan-panel, #scan-log-controls")) return;
  $("account-menu").open = false;
  $("account-menu").querySelector("summary").focus();
}, true);
$("account-menu").addEventListener("keydown", (event) => {
  if (event.key !== "Escape" || !$("account-menu").open) return;
  event.preventDefault();
  event.stopPropagation();
  $("account-menu").open = false;
  $("account-menu").querySelector("summary").focus();
});
document.addEventListener("click", (event) => {
  if (!$("account-menu").contains(event.target)) $("account-menu").open = false;
});

const readerDisplay = document.querySelector(".reader-display");
readerDisplay.addEventListener("focusout", (event) => {
  if (!readerDisplay.contains(event.relatedTarget)) readerDisplay.open = false;
});
readerDisplay.addEventListener("keydown", (event) => {
  if (event.key !== "Escape" || !readerDisplay.open) return;
  event.preventDefault();
  event.stopPropagation();
  readerDisplay.open = false;
  readerDisplay.querySelector("summary").focus();
});

function clearReader() {
  stopReaderView();
  for (const entry of pages) releasePage(entry);
  pages = [];
  readingOrder = [];
  readingPositions = [];
  displayedPages = [];
  activePages = [];
  revealedPage = null;
  personalBookmarks = [];
  bookmarkEditorPage = null;
  bookmarkDirty = false;
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
  nextIssue = { state: "idle", id: null };
  $("reader-pages").replaceChildren();
  const epoch = authEpoch;
  if (document.fullscreenElement === $("reader-view")) document.exitFullscreen().catch((error) => { if (epoch === authEpoch) showError(error); });
}

function showLogin() {
  resetIntake();
  resetKavitaImport();
  authEpoch++;
  session = null;
  accountBusy = false;
  setPasswordBusy(false);
  readingStatusBusy = false;
  resetComicExport();
  resetPageOrder();
  resetServerSettings();
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
  listDirty = listBusy = false;
  invalidateListPreview();
  duplicateGeneration++;
  duplicateId = null;
  duplicateBusy = false;
  duplicatePage = duplicatePages = 1;
  $("duplicate-table").replaceChildren();
  $("duplicate-status").textContent = $("duplicate-hash").textContent = "";
  $("folder-parent").replaceChildren(new Option("Top level", ""));
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
  setCatalogLocation("library");
  $("search").value = "";
  $("book-sort").value = "number";
  $("sort-direction").value = "asc";
  $("sort-direction").disabled = false;
  managementBusy = false;
  seriesList = null;
  $("series-list-form").reset();
  $("series-list-target").replaceChildren(new Option("Choose a list", ""));
  $("series-list-status").textContent = $("series-list-scope").textContent = "";
  $("book-status").value = "all";
  $("book-rating").value = "";
  $("book-availability").value = "all";
  $("group-by").value = "publisher";
  $("stack-by").value = "series";
  groupKey = sectionKey = null;
  closedSections.clear();
  catalogReturn = null;
  discoveryReturn = null;
  advancedFilter = null;
  groupLabel = "";
  maintenanceGeneration++;
  maintenanceBusy = false;
  maintenancePage = 1;
  $("maintenance-kind").value = "missing";
  $("maintenance-ignored").checked = false;
  failedCovers.clear();
  $("page-size").value = "48";
  $("publisher").replaceChildren(new Option("All publishers", ""));
  selectedBooks.clear();
  libraryItems = [];
  readingLists = [];
  listsGeneration++;
  metadataGeneration++;
  metadataPage = metadataReturn = null;
  metadataReportGeneration++;
  metadataReportPage = metadataReportPages = 1;
  $("metadata-report-kind").value = "all";
  $("metadata-report-items").replaceChildren();
  $("metadata-report-status").textContent = "";
  $("metadata-report-page").textContent = "Page 1 of 1";
  metadataDirty = metadataBusy = metadataBulk = false;
  invalidateMetadataPreview();
  $("metadata-status").textContent = "";
  resetArchiveWrite();
  resetFileOperation();
  resetOrganization();
  $("organize-selected").hidden = $("archive-selected").hidden = true;
  deletion = null;
  deletionBusy = false;
  $("delete-selected").hidden = true;
  $("move-archive").hidden = true;
  resetPageMetadata();
  $("edit-page-metadata").hidden = true;
  $("write-archive").hidden = true;
  $("convert-cbz").hidden = true;
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
  groupOrigin = null;
  libraryReturnFocus = null;
  updateReadingStatusControls();
  history.replaceState(null, "", ["#settings", "#unsorted"].includes(location.hash) ? location.hash : "#library");
  showView("login");
  $("login-form").elements.username.focus();
}

async function api(path, options = {}) {
  if (session?.mustChangePassword && !["/api/session", "/api/logout", "/api/account/password"].includes(path))
    throw new DOMException("Password change required.", "AbortError");
  const epoch = authEpoch;
  const headers = new Headers(options.headers);
  if (options.method && options.method !== "GET") headers.set("X-PanelHaven-Request", "1");
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
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
  if (options.rawResponse) {
    if (epoch !== authEpoch) {
      await response.body?.cancel();
      throw new DOMException("Session changed.", "AbortError");
    }
    return response;
  }
  const data = options.blob ? await response.blob() : await response.text();
  if (epoch !== authEpoch) throw new DOMException("Session changed.", "AbortError");
  return options.blob ? data : data ? JSON.parse(data) : null;
}

function signedIn(data) {
  if (data.mustChangePassword) { requirePasswordChange(data); return; }
  authEpoch++;
  setPasswordBusy(false);
  resetServerSettings();
  resetIntake();
  resetKavitaImport();
  session = data;
  resetComicExport();
  resetPageOrder();
  resetOrganization();
  if ($("organization-dialog").open) $("organization-dialog").close();
  readerPreferences = null;
  preferenceLoad = null;
  resetScan();
  $("username").textContent = data.username;
  $("account").hidden = false;
  $("change-password").hidden = false;
  $("admin-actions").hidden = data.role !== "admin";
  $("login-form").reset();
  clearError();
  $("bulk-metadata").hidden = data.role !== "admin";
  $("organize-selected").hidden = $("archive-selected").hidden = data.role !== "admin";
  $("delete-selected").hidden = data.role !== "admin";
  deletion = null;
  deletionBusy = false;
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

function setPasswordBusy(busy) {
  passwordBusy = busy;
  for (const control of $("password-form").elements) control.disabled = busy;
  $("password-form").setAttribute("aria-busy", String(busy));
  $("logout").disabled = busy;
  $("catalog-home").inert = busy;
}

function openPasswordView() {
  if (passwordBusy) return;
  routeGeneration++;
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
function setCatalogLocation(value) {
  if (value !== catalogLocation) {
    searchGeneration++;
    finishLibraryLoading();
    publisherGeneration++;
    catalogLocation = value;
    if (value !== "library" && seriesView()) $("book-status").value = "all";
    if (value === "unsorted") $("stack-by").value = "none";
    libraryPage = 1;
    groupKey = sectionKey = null;
    closedSections.clear();
    groupOrigin = null;
    libraryReturnFocus = null;
    groupLabel = "";
    libraryItems = [];
    $("books").replaceChildren();
    $("publisher").replaceChildren(new Option("All publishers", ""));
    clearSelection();
  }
  const intake = value === "unsorted";
  $("process-unsorted").hidden = !intake || session?.role !== "admin";
  const label = intake ? "Unsorted" : value === "archived" ? "Archived" : "Library";
  $("library-location").value = value;
  $("library-title").textContent = intake ? "Unsorted intake" : value === "archived" ? "Archived comicbooks" : "Your library";
  $("catalog-home").href = $("settings-catalog-back").href = intake ? "#unsorted" : "#library";
  $("catalog-home").setAttribute("aria-label", `Panel Haven ${label}`);
  $("settings-catalog-back").textContent = `Back to ${label}`;
  $("close-reader").textContent = label;
  updateReadingStatusControls();
}

function finishLibraryLoading() {
  $("books").inert = false;
  $("books").removeAttribute("aria-busy");
  $("refresh-library").disabled = false;
}
for (const eventName of ["focusin", "click"]) $("library-view").addEventListener(eventName, (event) => {
  const target = event.target.closest("[data-library-focus]");
  if (target && libraryReturnFocus !== target.dataset.libraryFocus) {
    libraryReturnFocus = target.dataset.libraryFocus;
    rememberCatalogView();
  }
});

function restoreLibraryFocus(key, generation) {
  if (generation !== searchGeneration || $("library-view").hidden) return;
  const target = key && $("library-view").querySelector(`[data-library-focus="${CSS.escape(key)}"]`);
  const section = target?.closest("details.book-section");
  if (section && target !== section.querySelector("summary") && !section.open) {
    section.open = true;
    closedSections.delete(section.dataset.sectionKey);
    rememberCatalogView();
  }
  (target && !target.closest("[hidden]") ? target : $("library-page")).focus();
}


function seriesProgress(book) {
  const progress = document.createElement("progress");
  progress.max = 100;
  progress.value = book.completionPercent;
  progress.setAttribute("aria-label", `${book.label}: ${book.completionPercent}% of books completed`);
  const meter = textElement("div", "", "series-progress");
  const percentage = textElement("span", `${book.completionPercent}%`);
  percentage.setAttribute("aria-hidden", "true");
  meter.append(progress, percentage);
  return meter;
}

function rememberCatalogView() {
  if (!session || $("library-view").hidden || !["#library", "#unsorted"].includes(location.hash)) return;
  catalogReturn = {
    version: 2, username: session.username, hash: location.hash, location: catalogLocation,
    page: libraryPage, query: libraryQuery, groupKey, sectionKey, groupLabel, groupOrigin, focus: libraryReturnFocus,
    closedSections: [...closedSections], advancedFilter, discoveryReturn: seriesView() ? discoveryReturn : null,
    filters: Object.fromEntries(catalogControls.map(id => [id, $(id).value]))
  };
  history.replaceState({ catalog: catalogReturn }, "", location.href);
}

function openBookGroup(book, key, section) {
  libraryReturnFocus = key;
  rememberCatalogView();
  history.pushState(null, "", location.href);
  groupOrigin = { key, page: libraryPage, closedSections: [...closedSections], advancedFilter,
    filters: Object.fromEntries(catalogControls.map(id => [id, $(id).value])) };
  groupKey = book.key;
  sectionKey = section;
  groupLabel = book.label;
  libraryPage = 1;
  clearSelection();
  loadLibrary(true);
}

function seriesView() {
  return $("book-status").selectedOptions[0]?.hasAttribute("data-series-view") ? $("book-status").value : null;
}

function updateReadingStatusControls() {
  const series = Boolean(seriesView());
  const seriesRoot = series && groupKey === null;
  for (const option of $("book-status").querySelectorAll("[data-series-view]")) option.disabled = catalogLocation !== "library";
  for (const control of $("search-form").querySelectorAll("input, button:not(#refresh-library)")) control.disabled = series;
  for (const id of ["book-rating", "publisher", "book-availability"]) $(id).disabled = series;
  $("book-sort").disabled = seriesRoot;
  for (const id of ["group-by", "stack-by"]) $(id).disabled = series;
  $("sort-direction").disabled = seriesRoot || $("book-sort").value === "list";
  for (const id of ["book-sort", "sort-direction"]) $(id).closest("label").hidden = seriesRoot;
  $("reading-status-hint").hidden = !series;
  $("return-to-browse").hidden = !series || !discoveryReturn;
  $("edit-advanced-filters").disabled = series;
  const countRules = rules => rules.reduce((count, rule) => count + (rule.rules ? countRules(rule.rules) : 1), 0);
  const advancedCount = advancedFilter ? countRules(advancedFilter.rules) : 0;
  const count = advancedCount + [libraryQuery, $("reading-list").value, $("publisher").value,
    $("book-rating").value, $("book-status").value !== "all" && !series, $("book-availability").value !== "all"].filter(value => value !== "" && Boolean(value)).length;
  $("active-filter-count").textContent = count ? `(${count} active filter${count === 1 ? "" : "s"})` : "(No active filters)";
  $("advanced-filter-summary").textContent = series ? "Advanced filters are unavailable in fixed series discovery views."
    : advancedCount ? `${advancedCount} advanced rule${advancedCount === 1 ? "" : "s"} applied (${advancedFilter.match === "all" ? "all" : "any"} must match), together with your basic filters.`
    : "No advanced rules applied.";
}

async function loadLibrary(focusResults = false) {
  if (!session) return;
  updateReadingStatusControls();
  rememberCatalogView();
  const generation = ++searchGeneration;
  const hash = location.hash;
  const series = seriesView();
  const seriesRoot = series && groupKey === null;
  const grouping = seriesRoot || ($("stack-by").value !== "none" && groupKey === null);
  $("group-navigation").hidden = groupKey === null;
  $("group-label").textContent = groupLabel;
  $("add-series-to-list").hidden = !groupKey?.trim() || (!series && $("stack-by").value !== "series");
  $("books").setAttribute("aria-label", grouping ? "Book stacks" : "Books");
  libraryItems = [];
  updateSelection();
  $("library-status").textContent = `Loading ${catalogLocation === "unsorted" ? "Unsorted intake" : catalogLocation === "archived" ? "Archived comicbooks" : "your library"}…`;
  $("books").setAttribute("aria-busy", "true");
  if (focusResults && $("books").contains(document.activeElement)) $("library-status").focus({ preventScroll: true });
  $("books").inert = true;
  for (const id of ["previous-books", "next-books", "previous-books-bottom", "next-books-bottom", "refresh-library"]) $(id).disabled = true;
  const query = new URLSearchParams({
    location: catalogLocation, q: libraryQuery, page: libraryPage, pageSize: $("page-size").value,
    sort: $("book-sort").value, direction: $("sort-direction").value,
    status: series ? "all" : $("book-status").value, publisher: $("publisher").value,
    availability: $("book-availability").value, groupBy: $("stack-by").value, sectionBy: $("group-by").value
  });
  if ($("reading-list").value) query.set("listId", $("reading-list").value);
  if ($("book-rating").value !== "") query.set("rating", $("book-rating").value);
  if (advancedFilter && !series) query.set("filter", JSON.stringify(advancedFilter));
  if (groupKey !== null) query.set("groupKey", groupKey);
  if (sectionKey !== null) query.set("sectionKey", sectionKey);
  try {
    const url = seriesRoot
      ? `/api/series-shelf?${new URLSearchParams({ kind: series, page: libraryPage, pageSize: $("page-size").value })}`
      : `/api/${grouping ? "book-groups" : "books"}?${query}`;
    if (url.length > 7500) throw new Error("The combined search, advanced rules and stack path exceed the request limit. Shorten the search or rule values, or leave the stack, then retry. Your filters are kept.");
    const result = await api(url);
    if (generation !== searchGeneration || hash !== location.hash || !session) return;
    libraryPage = result.page;
    libraryPages = Math.max(1, Math.ceil(result.total / result.pageSize));
    if (libraryPage > libraryPages) { libraryPage = libraryPages; return await loadLibrary(focusResults); }
    libraryItems = grouping ? [] : result.items;
    for (const suffix of ["", "-bottom"]) {
      $("library-page" + suffix).textContent = `Page ${libraryPage} of ${libraryPages}`;
      $("previous-books" + suffix).disabled = libraryPage <= 1;
      $("next-books" + suffix).disabled = libraryPage >= libraryPages;
    }
    let checkedCoverSession = false;
    const sections = groupKey === null ? result.sections || [] : [];
    const sectionKeys = [];
    for (const section of sections)
      for (let index = section.start; index < section.start + section.count; index++) sectionKeys[index] = section.key;
    const cards = result.items.map((book, index) => {
      const card = textElement("article", "", grouping ? "book-card group-card" : "book-card");
      const title = grouping ? book.label : [book.series, book.number && `#${book.number}`].filter(Boolean).join(" ") || "Unknown series";
      const coverId = grouping ? book.coverId : book.availability === "available" ? book.id : null;
      const cover = textElement("button", "", "cover-button");
      cover.type = "button";
      const itemSection = sectionKeys[index] ?? null;
      cover.dataset.libraryFocus = grouping
        ? itemSection === null ? `group:${book.key}` : `group:${JSON.stringify([itemSection, book.key])}`
        : `book:${book.id}`;
      cover.setAttribute("aria-label", `${grouping ? "View" : "Open"} ${title}`);
      const image = document.createElement("img");
      image.alt = "";
      image.loading = "lazy";
      if (coverId && !failedCovers.has(coverId)) image.src = `/api/books/${encodeURIComponent(coverId)}/thumbnail`;
      image.addEventListener("error", () => {
        if (generation !== searchGeneration) return;
        failedCovers.add(coverId);
        cover.replaceChildren(textElement("span", "Cover unavailable", "cover-fallback"));
        if (!checkedCoverSession) {
          checkedCoverSession = true;
          api("/api/session").then((data) => {
            if (generation === searchGeneration && !data.authenticated) showLogin();
            else if (generation === searchGeneration && data.mustChangePassword) requirePasswordChange(data);
          }).catch((error) => { if (generation === searchGeneration) showError(error); });
        }
      }, { once: true });
      cover.append(coverId && !failedCovers.has(coverId) ? image : textElement("span", "Cover unavailable", "cover-fallback"));
      cover.addEventListener("click", () => {
        if (!grouping) {
          libraryReturnFocus = cover.dataset.libraryFocus;
          rememberCatalogView();
          if (selectedBooks.size) card.querySelector("[data-book-id]").click();
          else if (book.availability === "available" && book.pageCount > 0) location.hash = `reader/${encodeURIComponent(book.id)}?location=${catalogLocation}`;
          else openDetails(book.id);
          return;
        }
        openBookGroup(book, cover.dataset.libraryFocus, itemSection);
      });
      card.append(cover, textElement(sections.length ? "h3" : "h2", title));
      if (grouping) {
        card.append(textElement("p", `${book.count} ${book.count === 1 ? "book" : "books"}`, "book-status"));
        if ($("stack-by").value === "series") card.append(seriesProgress(book));
        return card;
      }
      if (book.availability !== "available") card.append(textElement("p", book.availability, "book-availability"));
      const selection = textElement("label", "", "book-selection");
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.dataset.bookId = book.id;
      checkbox.checked = selectedBooks.has(book.id);
      checkbox.setAttribute("aria-label", `Select ${title}`);
      checkbox.addEventListener("change", () => {
        if (deletionBusy) { checkbox.checked = selectedBooks.has(book.id); return; }
        if (checkbox.checked && selectedBooks.size >= 100) {
          checkbox.checked = false;
          $("management-status").textContent = "Select at most 100 books. Clear some selections first.";
        } else if (checkbox.checked) selectedBooks.add(book.id);
        else selectedBooks.delete(book.id);
        updateSelection();
      });
      selection.append(checkbox);
      const coverFrame = textElement("div", "", "book-cover");
      cover.before(coverFrame);
      coverFrame.append(cover, selection);
      card.append(textElement("p", book.completed ? "Completed" : book.started ? `Page ${Math.min(book.currentPage + 1, book.pageCount)} of ${book.pageCount}` : "Unread", "book-status"));
      const details = textElement("button", "Details", "quiet book-details");
      details.type = "button";
      details.dataset.libraryFocus = `details:${book.id}`;
      details.setAttribute("aria-label", `Details: ${title}`);
      details.setAttribute("aria-haspopup", "dialog");
      details.addEventListener("click", () => openDetails(book.id));
      card.append(details);
      if (activeList()?.kind === "manual" && $("book-sort").value === "list") {
        const moves = textElement("div", "", "book-order");
        for (const [direction, label] of [[-1, "Move up"], [1, "Move down"]]) {
          const button = textElement("button", label);
          button.type = "button";
          button.disabled = managementBusy;
          button.dataset.move = direction;
          button.setAttribute("aria-label", `${label}: ${title}`);
          button.addEventListener("click", () => moveListBook(book.id, direction));
          moves.append(button);
        }
        card.append(moves);
      }
      return card;
    });
    $("books").replaceChildren(...(sections.length ? sections.map((section) => {
      const details = textElement("details", "", "book-section");
      details.dataset.sectionKey = section.key;
      details.open = !closedSections.has(section.key);
      const summary = document.createElement("summary");
      summary.dataset.libraryFocus = `section:${JSON.stringify(section.key)}`;
      summary.append(textElement("h2", section.label));
      const grid = textElement("div", "", "book-grid");
      grid.append(...cards.slice(section.start, section.start + section.count));
      details.append(summary, grid);
      details.addEventListener("toggle", () => {
        if (generation !== searchGeneration || hash !== location.hash || !details.isConnected || $("library-view").hidden) return;
        if (details.open === !closedSections.has(section.key)) return;
        if (details.open) closedSections.delete(section.key);
        else {
          closedSections.add(section.key);
          if (grid.contains(document.activeElement)) summary.focus();
        }
        rememberCatalogView();
      });
      return details;
    }) : cards));
    for (const gap of result.missingIssues || []) {
      const anchor = result.items.findIndex(book => book.id === gap.beforeBookId);
      if (anchor < 0) continue;
      const placeholders = [];
      // ponytail: large numbering jumps use one range card instead of an unbounded DOM.
      const range = gap.last - gap.first >= 100;
      for (let number = gap.first; number <= gap.last; number++) {
        const label = range ? `#${gap.first}–#${gap.last}` : `#${number}`;
        const card = textElement("article", "", "book-card missing-issue");
        const cover = textElement("div", "", "missing-issue-cover");
        cover.append(textElement("strong", label), textElement("span", range ? "Missing issues" : "Missing issue"));
        card.append(cover, textElement(sections.length ? "h3" : "h2", `${groupLabel}, ${gap.volume === null ? "Volume unknown" : `Vol. ${gap.volume}`} ${label}`),
          textElement("p", "Not in this location", "muted"), missingIssueLink(result.items[anchor].series, number));
        placeholders.push(card);
        if (range) break;
      }
      if ($("book-sort").value === "number" && $("sort-direction").value === "desc") placeholders.reverse();
      cards[anchor].before(...placeholders);
    }
    updateSelection();
    const filtered = advancedFilter || libraryQuery || $("book-status").value !== "all" || $("book-rating").value !== "" || $("publisher").value || $("book-availability").value !== "all" || groupKey !== null;
    $("library-status").textContent = result.total
      ? ""
      : seriesRoot ? series === "on-deck" ? "No series in Next Issue. Start reading a series from the library to continue it here."
        : series === "recently-updated" ? "No series received new comicbooks in the last 12 days. Choose All books to browse your library."
        : "No named series yet. Scan library comicbooks with Series metadata to see them here."
      : $("reading-list").value ? "No comicbooks in this reading list match the current area and filters. Choose All books or change the filters."
      : filtered ? "No comicbooks in this area match. Try another search or change the filters."
      : catalogLocation === "unsorted" ? "Unsorted intake is empty. An administrator can configure an Unsorted path in Server settings, restart, and scan incoming comicbooks. Nothing is moved automatically."
      : catalogLocation === "archived" ? "No Archived comicbooks. An administrator can configure an Archived path in Server settings, restart, and scan, or explicitly move comicbooks there. Regular library browsing keeps them hidden."
      : "Your library is empty. An administrator can scan the configured library folders to add comicbooks. Check Unsorted for comicbooks awaiting organization.";
    rememberCatalogView();
  } catch (error) {
    if (generation !== searchGeneration || hash !== location.hash) return;
    $("books").replaceChildren();
    libraryItems = [];
    updateSelection();
    for (const suffix of ["", "-bottom"]) $("library-page" + suffix).textContent = "Pages unavailable";
    $("library-status").textContent = "Could not load this area. Use Refresh to retry.";
    showError(error);
  } finally {
    if (generation === searchGeneration && hash === location.hash) {
      finishLibraryLoading();
      if (focusResults && !$("library-view").hidden) {
        const target = $("library-status").textContent ? $("library-status") : $("library-page");
        target.focus({ preventScroll: true });
        target.scrollIntoView({ block: "start" });
      }
    }
  }
  return generation;
}

async function loadPublishers() {
  const generation = ++publisherGeneration;
  try {
    const result = await api(`/api/book-filters?${new URLSearchParams({ location: catalogLocation })}`);
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
  scanLogRequest?.abort();
  scanLogRequest = null;
  $("scan-log-status").textContent = "";
  $("scan-log-error").textContent = "";
  $("scan-log-error").hidden = true;
  $("export-scan-log").disabled = true;
  scanStatus = null;
  dismissedScanResult = null;
  try {
    if (session) dismissedScanResult = localStorage.getItem(`panelhaven.dismissed-scan:${session.username}`);
  } catch { /* Keep scan controls usable when browser storage is blocked. */ }
  scanBusy = false;
  $("scan-panel").hidden = true;
  $("scan-panel").open = false;
  $("scan-summary").textContent = "Library scan";
  $("scan-error").hidden = true;
  $("scan-errors").hidden = true;
  $("scan-errors").open = false;
  $("retry-scan-status").hidden = true;
  $("cancel-scan").hidden = true;
  $("scan").disabled = false;
}

function scanResultSignature() {
  return JSON.stringify([scanStatus.state, scanStatus.scanned, scanStatus.added, scanStatus.updated, scanStatus.skipped, scanStatus.errorCount, scanStatus.errors]);
}

function rememberScanDismissal(result) {
  dismissedScanResult = result;
  try {
    localStorage.setItem(`panelhaven.dismissed-scan:${session.username}`, result || "");
  } catch { /* The dismissal still applies until this page is reloaded. */ }
}

function renderScan() {
  const active = scanStatus?.state === "running" || scanStatus?.state === "cancelling";
  $("scan").disabled = scanBusy || active;
  $("cancel-scan").hidden = !active;
  $("cancel-scan").disabled = scanBusy || scanStatus?.state === "cancelling";
  $("retry-scan-status").disabled = scanBusy;
  $("export-scan-log").disabled = !!scanLogRequest || !scanStatus?.logAvailable;
  $("scan-log-error").textContent = scanStatus?.logError || "";
  $("scan-log-error").hidden = !scanStatus?.logError;
  const dismissible = scanStatus && ["completed", "cancelled", "failed"].includes(scanStatus.state) && $("scan-error").hidden;
  $("dismiss-scan").hidden = !dismissible;
  const summary = !$("scan-error").hidden ? "Scan status needs attention" : {
    running: "Scanning libraries…", cancelling: "Cancelling scan…", completed: "Scan completed",
    cancelled: "Scan cancelled", failed: "Scan failed"
  }[scanStatus?.state] || "Library scan";
  $("scan-summary").textContent = `${summary}${scanStatus?.errorCount ? `, ${scanStatus.errorCount} error(s)` : ""}`;
  if (!scanStatus) return;
  $("scan-panel").hidden = (scanStatus.state === "idle" && $("scan-error").hidden) ||
    (dismissible && dismissedScanResult === scanResultSignature());
  const label = {
    idle: "No scan has started.", running: "Scanning libraries…", cancelling: "Cancelling scan…",
    completed: "Scan completed.", cancelled: "Scan cancelled. Changes already scanned were kept.", failed: "Scan failed. Review the errors and try scanning again."
  }[scanStatus.state];
  $("scan-result").textContent = `${label}\n${scanStatus.scanned} scanned, ${scanStatus.added} added, ${scanStatus.updated} updated, ${scanStatus.skipped} skipped, ${scanStatus.errorCount} error(s)`;
  $("scan-errors").hidden = scanStatus.errorCount === 0;
  $("scan-error-count").textContent = `${scanStatus.errorCount} scan error(s)${scanStatus.errorCount > scanStatus.errors.length ? ` (showing first ${scanStatus.errors.length})` : ""}`;
  $("scan-error-list").textContent = scanStatus.errors.join("\n");
}

async function requestScan(action = "status") {
  if (session?.role !== "admin" || scanBusy) return;
  if (action === "start") rememberScanDismissal(null);
  const epoch = authEpoch;
  clearTimeout(scanTimer);
  const focused = document.activeElement;
  scanBusy = true;
  scanRequest = new AbortController();
  renderScan();
  if ($("account-menu").open && $("scan-panel").contains(focused) && focused.disabled)
    $("scan-summary").focus();
  let retry = false;
  try {
    const result = await api(action === "cancel" ? "/api/scan/cancel" : "/api/scan", {
      method: action === "status" ? "GET" : "POST",
      signal: AbortSignal.any([scanRequest.signal, AbortSignal.timeout(15000)])
    });
    if (epoch !== authEpoch) return;
    const previous = scanStatus;
    scanStatus = result;
    if ((result.errorCount > 0 && result.errorCount !== previous?.errorCount)
      || (result.state === "failed" && (previous?.state !== result.state || previous?.revision !== result.revision)))
      $("scan-panel").open = true;
    $("scan-error").hidden = true;
    $("retry-scan-status").hidden = true;
    renderScan();
    if (["completed", "cancelled", "failed"].includes(result.state) && (previous?.revision !== result.revision || previous?.state !== result.state || action === "start")) {
      if (archivePreview) {
        invalidateArchivePreview();
        $("archive-status").textContent = "A library scan finished. Source checks must run again before the next operation.";
      }
      failedCovers.clear();
      loadLibrary();
      loadPublishers();
      if ($("maintenance-dialog").open) loadMaintenance();
      if (detailBook && $("details-dialog").open) refreshDetailAvailability();
    }
  } catch (error) {
    if (epoch !== authEpoch) return;
    retry = true;
    $("scan-panel").hidden = false;
    if ($("scan-error").hidden) $("scan-panel").open = true;
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
  if (session?.role !== "admin" || !$("maintenance-dialog").open || maintenanceBusy) return;
  const generation = ++maintenanceGeneration;
  const kind = $("maintenance-kind").value;
  const ignored = kind === "missing-issues" && $("maintenance-ignored").checked;
  $("maintenance-status").textContent = "Loading maintenance records…";
  $("previous-maintenance").disabled = true;
  $("next-maintenance").disabled = true;
  $("duplicate-note").hidden = kind !== "duplicates";
  $("missing-issues-note").hidden = kind !== "missing-issues";
  $("maintenance-ignored-control").hidden = kind !== "missing-issues";
  $("maintenance-file-note").hidden = kind === "missing-issues";
  $("remove-all-missing").hidden = kind !== "missing";
  $("remove-all-missing").disabled = true;
  try {
    const result = await api(`/api/maintenance?${new URLSearchParams({ kind, page: maintenancePage, pageSize: 48, ignored })}`);
    if (generation !== maintenanceGeneration) return;
    maintenancePages = Math.max(1, Math.ceil(result.total / result.pageSize));
    maintenancePage = result.page;
    if (maintenancePage > maintenancePages) { maintenancePage = maintenancePages; await loadMaintenance(); return; }
    const rows = [];
    let duplicateKey = null;
    for (const book of result.items) {
      if (kind === "missing-issues") {
        const row = textElement("article", "", "maintenance-row");
        const issues = book.first === book.last ? `Missing issue #${book.first}` : `Missing issues #${book.first}–#${book.last}`;
        const location = { library: "Library", archived: "Archived", unsorted: "Unsorted" }[book.location];
        const runLabel = `${book.series}, ${book.volume === null ? "Volume unknown" : `Vol. ${book.volume}`}`;
        row.append(textElement("h3", runLabel), textElement("p", issues), textElement("p", location, "muted"));
        const view = textElement("button", "View series");
        view.type = "button";
        view.setAttribute("aria-label", `View series: ${runLabel} (${location})`);
        view.addEventListener("click", async () => {
          if (!leaveServerSettings()) return;
          const epoch = authEpoch;
          view.disabled = true;
          try {
            await leaveReader();
            if (epoch !== authEpoch || generation !== maintenanceGeneration || !$("maintenance-dialog").open) return;
            routeGeneration++;
            setCatalogLocation(book.location);
            advancedFilter = { match: "all", rules: [{
              field: "Volume", operator: book.volume === null ? "isEmpty" : "equals",
              value: book.volume === null ? "" : String(book.volume)
            }] };
            $("search").value = libraryQuery = "";
            $("reading-list").value = $("publisher").value = $("book-rating").value = "";
            $("book-status").value = $("book-availability").value = "all";
            $("group-by").value = "none";
            $("stack-by").value = "series";
            sectionKey = null;
            closedSections.clear();
            $("book-sort").value = "number";
            $("sort-direction").value = "asc";
            $("sort-direction").disabled = false;
            groupKey = groupLabel = book.series;
            groupOrigin = null;
            libraryPage = 1;
            clearSelection();
            history.pushState(null, "", book.location === "unsorted" ? "#unsorted" : "#library");
            $("maintenance-dialog").close();
            showView("library");
            loadPublishers();
            await loadLibrary(true);
          } catch (error) {
            if (epoch === authEpoch) {
              $("maintenance-error").textContent = error.message;
              $("maintenance-error").hidden = false;
            }
          } finally { view.disabled = false; }
        });
        const actions = textElement("div", "", "actions");
        const ignore = textElement("button", ignored ? "Unignore" : "Ignore", "quiet missing-series-ignore");
        ignore.type = "button";
        ignore.disabled = maintenanceBusy;
        ignore.setAttribute("aria-label", `${ignore.textContent} series: ${book.series}`);
        ignore.addEventListener("click", () => setMissingIssueIgnored(book.series, !ignored, generation));
        actions.append(view, missingIssueLink(book.series, book.first), ignore);
        row.append(actions);
        rows.push(row);
        continue;
      }
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
      if (kind === "duplicates") {
        const compare = textElement("button", "Compare copies");
        compare.type = "button";
        compare.setAttribute("aria-label", `Compare copies: ${bookTitle(book)}`);
        compare.addEventListener("click", () => openDuplicateComparison(book.id));
        actions.append(compare);
      }
      if (kind === "missing" && book.availability === "missing") {
        const remove = textElement("button", "Remove catalog entry", "danger");
        remove.type = "button";
        remove.disabled = maintenanceBusy;
        remove.setAttribute("aria-label", `Remove catalog entry: ${bookTitle(book)}`);
        remove.addEventListener("click", () => removeMissingBooks(book));
        actions.append(remove);
      }
      row.append(actions);
      rows.push(row);
    }
    $("maintenance-items").replaceChildren(...rows);
    $("maintenance-status").textContent = result.total
      ? `${result.total} ${kind === "missing-issues" ? ignored ? "ignored issue gaps" : "issue gaps" : "records"}. Showing ${(maintenancePage - 1) * result.pageSize + 1}–${(maintenancePage - 1) * result.pageSize + result.items.length}`
      : kind === "missing-issues" ? ignored ? "No ignored series currently have missing issue gaps." : "No missing issue gaps found. Use Ignored to view hidden series."
      : "No matching records. Scan the libraries to update availability and duplicate detection.";
    $("maintenance-page").textContent = `Page ${maintenancePage} of ${maintenancePages}`;
    $("previous-maintenance").disabled = maintenancePage <= 1;
    $("next-maintenance").disabled = maintenancePage >= maintenancePages;
    $("remove-all-missing").disabled = kind !== "missing" || !result.total;
  } catch (error) {
    if (generation !== maintenanceGeneration) return;
    $("maintenance-status").textContent = "Could not load maintenance records. Refresh to retry.";
    $("maintenance-error").textContent = error.message;
    $("maintenance-error").hidden = false;
  }
}

async function setMissingIssueIgnored(series, ignored, generation) {
  if (maintenanceBusy || generation !== maintenanceGeneration) return;
  const epoch = authEpoch;
  maintenanceBusy = true;
  for (const button of $("maintenance-items").querySelectorAll(".missing-series-ignore")) button.disabled = true;
  $("maintenance-error").hidden = true;
  $("maintenance-status").textContent = ignored ? "Ignoring series…" : "Restoring series…";
  try {
    await api("/api/maintenance/missing-issues/ignored-series", {
      method: "PUT", body: JSON.stringify({ series, ignored })
    });
  } catch (error) {
    if (epoch === authEpoch && generation === maintenanceGeneration) {
      $("maintenance-error").textContent = `Could not confirm the change: ${error.message}. Refresh to check the series status before trying again.`;
      $("maintenance-error").hidden = false;
    }
  } finally {
    if (epoch === authEpoch) {
      maintenanceBusy = false;
      for (const button of $("maintenance-items").querySelectorAll(".missing-series-ignore")) button.disabled = false;
      if (generation === maintenanceGeneration && $("maintenance-dialog").open) {
        await loadMaintenance();
        if (epoch === authEpoch && generation + 1 === maintenanceGeneration && $("maintenance-dialog").open)
          $("maintenance-status").focus();
      }
    }
  }
}

async function removeMissingBooks(book = null) {
  if (maintenanceBusy || session?.role !== "admin") return;
  const epoch = authEpoch;
  const books = book ? [book] : [];
  let removed = 0;
  maintenanceBusy = true;
  maintenanceGeneration++;
  for (const control of $("maintenance-dialog").querySelectorAll("button, select, input")) control.disabled = true;
  $("maintenance-error").hidden = true;
  $("maintenance-status").textContent = "Checking missing catalog entries…";
  try {
    if (!book) {
      for (let page = 1; ; page++) {
        const result = await api(`/api/maintenance?${new URLSearchParams({ kind: "missing", page, pageSize: 100 })}`);
        if (epoch !== authEpoch) return;
        books.push(...result.items);
        if (page * result.pageSize >= result.total) break;
      }
    }
    if (!books.length || !confirm(`Remove ${book ? `“${bookTitle(book)}” at ${book.location}` : `all ${books.length} missing comicbooks across every page`} from the catalog?\n\nThis permanently removes their shared metadata and every user's reading progress, bookmarks, and list memberships. This cannot be undone. Source files are never deleted.\n\nEach file must still be genuinely missing from an accessible library. Cleanup stops on the first failure; earlier removals are kept.`)) return;
    for (const item of books) {
      $("maintenance-status").textContent = `Removing missing catalog entries: ${removed} of ${books.length}…`;
      await api(`/api/books/${encodeURIComponent(item.id)}`, { method: "DELETE" });
      if (epoch !== authEpoch) return;
      removed++;
      selectedBooks.delete(item.id);
      if (detailBook?.id === item.id) { $("details-dialog").close(); detailBook = null; }
    }
  } catch (error) {
    if (epoch !== authEpoch) return;
    $("maintenance-error").textContent = `Cleanup stopped. Confirmed removals: ${removed}. Could not confirm the next removal: ${error.message}. Refresh and inspect the remaining records before trying again. Nothing is retried automatically; no source files were deleted.`;
    $("maintenance-error").hidden = false;
  } finally {
    if (epoch === authEpoch) {
      maintenanceBusy = false;
      for (const control of $("maintenance-dialog").querySelectorAll("button, select, input")) control.disabled = false;
      loadLists();
      loadPublishers();
      loadLibrary();
      await loadMaintenance();
      if (epoch === authEpoch && $("maintenance-dialog").open) $("maintenance-status").focus();
    }
  }
}

function openDuplicateComparison(id) {
  if (session?.role !== "admin" || duplicateBusy) return;
  duplicateId = id;
  duplicatePage = duplicatePages = 1;
  $("duplicate-table").replaceChildren();
  $("duplicate-hash").textContent = "";
  $("duplicate-page").textContent = "Page 1 of 1";
  $("duplicate-dialog").showModal();
  loadDuplicateComparison();
}

async function loadDuplicateComparison(page = duplicatePage) {
  if (duplicateBusy || !duplicateId || !$("duplicate-dialog").open || session?.role !== "admin") return;
  const generation = ++duplicateGeneration;
  const epoch = authEpoch;
  duplicateBusy = true;
  $("refresh-duplicates").disabled = $("previous-duplicates").disabled = $("next-duplicates").disabled = true;
  $("duplicate-status").textContent = "Loading read-only comparison…";
  try {
    const [result, schema] = await Promise.all([
      api(`/api/duplicates/${encodeURIComponent(duplicateId)}?${new URLSearchParams({ page, pageSize: 3 })}`), loadMetadataSchema()
    ]);
    if (epoch !== authEpoch || generation !== duplicateGeneration) return;
    duplicatePages = Math.max(1, Math.ceil(result.total / result.pageSize));
    if (result.page > duplicatePages) { duplicateBusy = false; await loadDuplicateComparison(duplicatePages); return; }
    duplicatePage = result.page;
    const copies = [result.anchor, ...result.items];
    const heading = document.createElement("thead");
    const titles = document.createElement("tr");
    const fieldHeading = textElement("th", "Field");
    fieldHeading.scope = "col";
    titles.append(fieldHeading);
    for (const [index, copy] of copies.entries()) {
      const title = textElement("th", `${index === 0 ? "Selected copy" : "Other copy"}: ${bookTitle(copy.book)}`);
      title.scope = "col";
      titles.append(title);
    }
    heading.append(titles);
    const body = document.createElement("tbody");
    const addRow = (label, values) => {
      const row = document.createElement("tr");
      const name = textElement("th", label);
      name.scope = "row";
      row.append(name);
      for (const [index, value] of values.entries()) {
        const different = index > 0 && value !== values[0];
        const cell = textElement("td", value == null ? "— Not set" : value === "" ? '"" (empty)' : String(value), different ? "comparison-different" : "");
        if (different) cell.append(textElement("span", " (differs from selected copy)", "sr-only"));
        row.append(cell);
      }
      body.append(row);
    };
    addRow("Catalog ID", copies.map(({ book }) => book.id));
    addRow("Location", copies.map(({ book }) => book.location));
    addRow("Availability", copies.map(({ book }) => book.availability));
    addRow("Physical page count", copies.map(({ book }) => book.pageCount));
    addRow("My reading status", copies.map(({ book }) => book.completed ? "Completed" : book.started ? "In progress" : "Unread"));
    addRow("My current page", copies.map(({ book }) => book.started ? book.currentPage + 1 : null));
    addRow("My rating", copies.map(({ book }) => book.personalRating ? `${book.personalRating} / 5` : "Unrated"));
    const fields = new Set([...schema.map((field) => field.name), ...copies.flatMap(({ metadata }) => Object.keys(metadata.fields))]);
    for (const field of fields) addRow(schema.find((item) => item.name === field)?.label ?? field, copies.map(({ metadata }) => Object.hasOwn(metadata.fields, field) ? metadata.fields[field] : null));
    const custom = [...new Set(copies.flatMap(({ metadata }) => Object.keys(metadata.custom)))].sort();
    for (const key of custom) addRow(`Custom: ${key}`, copies.map(({ metadata }) => Object.hasOwn(metadata.custom, key) ? metadata.custom[key] : null));
    $("duplicate-table").replaceChildren(textElement("caption", "Selected copy and up to three other copies; the selected copy stays in the first book column."), heading, body);
    $("duplicate-hash").textContent = `Last-scan SHA-256: ${result.duplicateKey}`;
    $("duplicate-page").textContent = `Page ${duplicatePage} of ${duplicatePages}`;
    $("duplicate-status").textContent = `${result.total} other copies. Showing ${(duplicatePage - 1) * result.pageSize + 1}–${(duplicatePage - 1) * result.pageSize + result.items.length}. Highlighted cells differ from the selected copy.`;
  } catch (error) {
    if (epoch === authEpoch && generation === duplicateGeneration)
      $("duplicate-status").textContent = `Comparison failed: ${error.message}. Any displayed comparison is unchanged. Refresh to retry; scan again if the group no longer exists.`;
  } finally {
    if (epoch === authEpoch && generation === duplicateGeneration) {
      duplicateBusy = false;
      $("refresh-duplicates").disabled = false;
      $("previous-duplicates").disabled = duplicatePage <= 1;
      $("next-duplicates").disabled = duplicatePage >= duplicatePages;
    }
  }
}

$("refresh-duplicates").addEventListener("click", () => loadDuplicateComparison());
$("previous-duplicates").addEventListener("click", () => { if (duplicatePage > 1) loadDuplicateComparison(duplicatePage - 1); });
$("next-duplicates").addEventListener("click", () => { if (duplicatePage < duplicatePages) loadDuplicateComparison(duplicatePage + 1); });
$("duplicate-dialog").addEventListener("close", () => { duplicateGeneration++; duplicateBusy = false; duplicateId = null; });

function renderDetailAvailability(book) {
  $("details-availability").textContent = book.availability === "available" ? "Available"
    : `${book.availability === "missing" ? "Missing file" : "Unavailable location"} — reading is disabled. Restore or reconnect the source and ask an administrator to scan again. Catalog metadata, progress, and bookmarks remain available.`;
  $("details-availability").className = book.availability === "available" ? "muted" : "book-availability";
  $("read-book").disabled = book.pageCount < 1 || book.availability !== "available";
  $("export-comic").disabled = !session || session.mustChangePassword || book.pageCount < 1 || book.availability !== "available";
  $("convert-cbz").hidden = session?.role !== "admin" || book.availability !== "available" || !/\.(cbr|rar|cb7|7z|cbt|tar)$/i.test(book.location);
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
  content.replaceChildren(textElement("p", [book.series, book.number && `#${book.number}`, book.publisher].filter(Boolean).join(", "), "muted"));
  const availability = textElement("p", "", book.availability === "available" ? "muted" : "book-availability");
  availability.id = "details-availability";
  content.append(availability, textElement("p", book.location, "muted"));
  renderDetailAvailability(book);
  if (book.summary) content.append(textElement("p", book.summary, "summary"));
  content.append(textElement("p", `${book.pageCount} pages. ${book.completed ? "Completed" : book.started ? `Continue on physical page ${book.currentPage + 1}` : "Unread"}`));
  if (metadata) {
    const details = document.createElement("details");
    details.append(textElement("summary", "Full metadata"));
    const values = document.createElement("dl");
    for (const field of schema) {
      if (!Object.hasOwn(metadata.fields, field.name)) continue;
      const value = textElement("dd", metadata.fields[field.name]);
      if (field.name === "Web") value.replaceChildren(comicVineLink(metadata.fields[field.name], metadata.fields[field.name]));
      values.append(textElement("dt", field.label), value);
    }
    for (const [name, value] of Object.entries(metadata.custom)) values.append(textElement("dt", `Custom: ${name}`), textElement("dd", value));
    details.append(values.childElementCount ? values : textElement("p", "No metadata values."));
    content.append(details);
  }
  $("read-book").textContent = book.started ? "Continue reading" : "Read book";
  $("edit-metadata").hidden = session?.role !== "admin";
  $("find-metadata").hidden = session?.role !== "admin";
  $("write-archive").hidden = session?.role !== "admin";
  $("move-archive").hidden = session?.role !== "admin";
  $("edit-page-metadata").hidden = session?.role !== "admin";
}

function activeList() {
  return readingLists.find((list) => list.id === $("reading-list").value);
}

function pageSelectionFull() {
  return libraryItems.length > 0 && (selectedBooks.size >= 100 || libraryItems.every(book => selectedBooks.has(book.id)));
}

function updateSelection() {
  const count = selectedBooks.size;
  document.querySelector(".selection-toolbar").hidden = !count;
  document.querySelector(".selection-toolbar").inert = deletionBusy;
  $("selection-count").textContent = `${count} of 100 selected`;
  $("clear-selection").disabled = deletionBusy || !count;
  $("select-page").disabled = deletionBusy || !libraryItems.length;
  $("select-page").textContent = pageSelectionFull() ? "Unselect all" : "Select page";
  $("process-selected").hidden = session?.role !== "admin";
  $("process-selected").disabled = deletionBusy || !count || count > 100;
  $("bulk-metadata").disabled = !count;
  $("export-selected-comics").disabled = !session || session.mustChangePassword || !count || count > 100;
  $("mark-selected-read").disabled = $("mark-selected-unread").disabled = readingStatusBusy || !session || session.mustChangePassword || !count || count > 100;
  if (comicExport?.selection && !comicExportCurrent(comicExport)) resetComicExport();
  $("organize-selected").disabled = $("archive-selected").disabled = organizationBusy || session?.role !== "admin" || !count || count > 100;
  $("delete-selected").disabled = deletionBusy || session?.role !== "admin" || !count || count > 100;
  if ($("organization-dialog").open && (organizationBookIds.length !== count || organizationBookIds.some((id) => !selectedBooks.has(id)))) {
    invalidateOrganizationPreview(true);
    $("organization-status").textContent = "Selection changed. Close and reopen Organize selected to use the current selection.";
  }
  $("add-list-books").disabled = managementBusy || !count || !$("add-to-list").value;
  $("add-series-to-list").disabled = managementBusy || !session || session.mustChangePassword;
  $("remove-list-books").hidden = activeList()?.kind !== "manual";
  $("remove-list-books").disabled = managementBusy || !count;
  $("edit-list").disabled = managementBusy || !activeList();
  $("delete-list").disabled = managementBusy || !activeList();
  for (const checkbox of $("books").querySelectorAll("[data-book-id]")) {
    checkbox.checked = selectedBooks.has(checkbox.dataset.bookId);
    checkbox.disabled = deletionBusy;
    const card = checkbox.closest(".book-card");
    const cover = card.querySelector(".cover-button");
    const action = count ? (checkbox.checked ? "Deselect" : "Select") : "Open";
    cover.setAttribute("aria-label", `${action} ${card.querySelector("h2, h3").textContent}`);
    if (count) cover.setAttribute("aria-pressed", String(checkbox.checked));
    else cover.removeAttribute("aria-pressed");
  }
  for (const button of $("books").querySelectorAll(".book-order button")) button.disabled = managementBusy;
}

function clearSelection(announce = false) {
  if (deletionBusy) return;
  selectedBooks.clear();
  updateSelection();
  if (announce) {
    $("management-status").textContent = "All selections cleared.";
    $("library-page").focus();
  }
}
function folderPath(folder) {
  const names = [];
  for (let current = folder; current; current = listFolders.find((item) => item.id === current.parentId)) names.unshift(current.name);
  return names.join(" / ");
}

function folderOptions(excludedId = "") {
  return listFolders.filter((folder) => {
    for (let current = folder; current; current = listFolders.find((item) => item.id === current.parentId))
      if (current.id === excludedId) return false;
    return true;
  }).map((folder) => ({ id: folder.id, path: folderPath(folder) }))
    .sort((a, b) => a.path.localeCompare(b.path))
    .map((folder) => new Option(folder.path, folder.id));
}

async function loadLists() {
  const generation = ++listsGeneration;
  try {
    const [lists, folders] = await Promise.all([api("/api/lists"), api("/api/list-folders")]);
    if (generation !== listsGeneration || !session) return;
    const selected = $("reading-list").value;
    const target = $("add-to-list").value;
    const seriesTarget = $("series-list-target").value;
    readingLists = lists;
    listFolders = folders;
    for (const [id, manualOnly, placeholder] of [["reading-list", false, "All books"], ["add-to-list", true, "Choose a list"], ["series-list-target", true, "Choose a list"]]) {
      const select = $(id);
      select.replaceChildren(new Option(placeholder, ""));
      for (const folder of [{ id: null, name: "Unfiled" }, ...folders.slice().sort((a, b) => folderPath(a).localeCompare(folderPath(b)))]) {
        const members = lists.filter((list) => (list.folderId ?? null) === folder.id && (!manualOnly || list.kind === "manual"));
        if (!members.length) continue;
        const group = document.createElement("optgroup");
        group.label = folder.id ? folderPath(folder) : folder.name;
        group.append(...members.map((list) => new Option(`${list.name}${list.kind === "smart" ? " (smart)" : ""}`, list.id)));
        select.append(group);
      }
    }
    $("reading-list").value = lists.some((list) => list.id === selected) ? selected : "";
    $("add-to-list").value = lists.some((list) => list.id === target && list.kind === "manual") ? target : "";
    $("series-list-target").value = lists.some((list) => list.id === seriesTarget && list.kind === "manual") ? seriesTarget : "";
    if (selected !== $("reading-list").value) changeLibrary();
    updateSelection();
    return true;
  } catch (error) { if (generation === listsGeneration) $("management-status").textContent = `Could not load reading lists: ${error.message}`; }
  return false;
}

function showFolderEditor(id = "") {
  folderEditingId = id;
  folderDirty = false;
  $("folder-choice").replaceChildren(new Option("New folder", ""), ...folderOptions());
  $("folder-choice").value = id;
  const folder = listFolders.find((item) => item.id === id);
  $("folder-name").value = folder?.name ?? "";
  $("folder-name").setCustomValidity("");
  $("folder-parent").replaceChildren(new Option("Top level", ""), ...folderOptions(id));
  $("folder-parent").value = folder?.parentId ?? "";
  $("save-folder").textContent = id ? "Save folder" : "Create folder";
  $("delete-folder").disabled = folderBusy || !id;
}

async function saveFolder(remove = false) {
  if (folderBusy) return;
  const id = folderEditingId;
  const name = $("folder-name").value.trim();
  if (remove) {
    const folder = listFolders.find((item) => item.id === id);
    if (!folder || !confirm(`Delete folder “${folderPath(folder)}”? Its direct lists will become unfiled. Its immediate child folders will move to the top level, keeping their lists and nested folders. No lists, books, progress, ratings, or bookmarks will be deleted.${folderDirty ? "\nUnsaved folder changes will be discarded." : ""}`)) return;
  } else if (!name || name.length > 200) {
    $("folder-status").textContent = "Enter a folder name of 1–200 characters.";
    $("folder-name").setCustomValidity($("folder-status").textContent);
    $("folder-name").reportValidity();
    return;
  }
  const epoch = authEpoch;
  folderBusy = true;
  $("folder-controls").disabled = $("save-folder").disabled = $("delete-folder").disabled = true;
  $("folder-status").textContent = remove ? "Deleting folder…" : "Saving folder…";
  try {
    const folder = await api(id ? `/api/list-folders/${encodeURIComponent(id)}` : "/api/list-folders", {
      method: remove ? "DELETE" : id ? "PUT" : "POST",
      ...(remove ? {} : { body: JSON.stringify({ name, parentId: $("folder-parent").value || null }) })
    });
    if (epoch !== authEpoch) return;
    listFolders = listFolders.filter((item) => item.id !== id);
    if (remove) listFolders = listFolders.map((item) => item.parentId === id ? { ...item, parentId: null } : item);
    if (!remove) listFolders.push(folder);
    showFolderEditor(remove ? "" : folder.id);
    $("folder-status").textContent = remove ? "Folder deleted. Direct lists are unfiled; child folders are now top level. All contents and private reading data were kept." : "Folder saved.";
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
$("folder-name").addEventListener("input", () => { folderDirty = true; clearFieldError($("folder-name"), $("folder-status")); });
$("folder-parent").addEventListener("change", () => { folderDirty = true; });
$("folder-choice").addEventListener("change", () => {
  if (folderDirty && !confirm("Discard unsaved folder changes?")) { $("folder-choice").value = folderEditingId; return; }
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

function readServerSettings() {
  const lines = (id) => $(id).value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  return {
    dataPath: serverSettings.settings.dataPath,
    libraries: lines("settings-libraries"),
    archivePath: $("settings-archive-path").value.trim() || null,
    unsortedPath: $("settings-unsorted-path").value.trim() || null,
    urls: lines("settings-urls"),
    requireHttps: $("settings-require-https").checked,
    allowedHosts: lines("settings-allowed-hosts"),
    knownProxies: lines("settings-known-proxies"),
    certificatePath: $("settings-certificate-path").value.trim() || null,
    certificateKeyPath: $("settings-certificate-key-path").value.trim() || null,
    backup: { enabled: $("settings-backup-enabled").checked, destination: $("settings-backup-destination").value.trim(),
      time: $("settings-backup-time").value, keep: Number($("settings-backup-keep").value) },
  };
}

function serverSettingsDirty() {
  return !!serverSettings && (JSON.stringify(readServerSettings()) !== serverSettingsBaseline
    || !!$("settings-comic-vine-key").value || $("settings-clear-comic-vine").checked
    || !!$("settings-certificate-password").value || $("settings-clear-certificate-password").checked);
}

function dataRelocationDirty() {
  return !!$("data-relocation-destination").value || $("data-relocation-confirm").checked || !!$("data-relocation-password").value;
}

function resetServerSettings() {
  serverSettingsGeneration++;
  serverSettings = null;
  serverSettingsBaseline = "";
  serverSettingsBusy = serverSettingsStale = false;
  $("server-settings-form").reset();
  $("data-relocation-form").reset();
  if ($("data-relocation-dialog").open) $("data-relocation-dialog").close();
  for (const id of ["server-settings-running", "server-settings-restart", "server-settings-draft", "server-settings-status",
    "settings-service-status", "settings-backup-status", "settings-data-path", "settings-relocation-status",
    "data-relocation-source", "data-relocation-status", "settings-comic-vine-state", "settings-certificate-state"]) $(id).replaceChildren();
  $("settings-reconnect").textContent = "";
}

function leaveServerSettings() {
  if ($("settings-view").hidden) return true;
  if (serverSettingsBusy || ($("data-relocation-dialog").open && !canCloseDialog("data-relocation-dialog"))
    || ((serverSettingsDirty() || $("settings-current-password").value) && !confirm("Discard unsaved server settings and credentials?"))) return false;
  return true;
}

function updateServerSettingsControls() {
  const unavailable = !serverSettings || serverSettingsBusy || serverSettingsStale;
  const dirty = serverSettingsDirty();
  const hostUnavailable = unavailable || dirty || !serverSettings?.service.supported;
  $("server-settings-controls").disabled = !serverSettings || serverSettingsBusy;
  $("settings-current-password").disabled = unavailable;
  $("save-server-settings").disabled = unavailable || !dirty;
  $("reload-server-settings").disabled = serverSettingsBusy;
  $("settings-enable-startup").disabled = hostUnavailable || serverSettings?.service.enabled;
  $("settings-disable-startup").disabled = hostUnavailable || !serverSettings?.service.installed || !serverSettings?.service.enabled;
  $("settings-restart-service").disabled = hostUnavailable || !serverSettings?.service.installed;
  $("open-data-relocation").disabled = unavailable || dirty || !!serverSettings?.pendingDataPath;
  $("data-relocation-controls").disabled = unavailable;
  $("queue-data-relocation").disabled = unavailable;
  $("data-relocation-dialog").querySelector("[data-close]").disabled = serverSettingsBusy;
  $("settings-comic-vine-key").disabled = $("settings-clear-comic-vine").checked;
  $("settings-certificate-password").disabled = $("settings-clear-certificate-password").checked;
  $("settings-backup-destination").required = $("settings-backup-enabled").checked;
  $("settings-allowed-hosts").required = $("settings-require-https").checked;
  $("settings-view").setAttribute("aria-busy", String(serverSettingsBusy));
  $("server-settings-draft").classList.toggle("notice", dirty || serverSettingsStale);
  $("server-settings-draft").classList.toggle("muted", !dirty && !serverSettingsStale);
  $("server-settings-draft").textContent = !serverSettings ? "" : serverSettingsStale
    ? "Reload saved settings before another action. Your unsaved draft has been retained; copy it before reloading."
    : dirty ? "Unsaved changes. Save or reload before using service or relocation actions." : "No unsaved configuration changes.";
}

function renderServerSettings(snapshot) {
  serverSettings = snapshot;
  serverSettingsStale = false;
  $("server-settings-form").reset();
  const settings = snapshot.settings;
  for (const [id, key] of [["settings-libraries", "libraries"], ["settings-urls", "urls"],
    ["settings-allowed-hosts", "allowedHosts"], ["settings-known-proxies", "knownProxies"]]) $(id).value = settings[key].join("\n");
  $("settings-archive-path").value = settings.archivePath || "";
  $("settings-unsorted-path").value = settings.unsortedPath || "";
  $("settings-require-https").checked = settings.requireHttps;
  $("settings-certificate-path").value = settings.certificatePath || "";
  $("settings-certificate-key-path").value = settings.certificateKeyPath || "";
  $("settings-backup-enabled").checked = settings.backup.enabled;
  $("settings-backup-destination").value = settings.backup.destination || "";
  $("settings-backup-time").value = settings.backup.time;
  $("settings-backup-keep").value = settings.backup.keep;
  serverSettingsBaseline = JSON.stringify(readServerSettings());
  $("settings-comic-vine-state").textContent = `Saved API key: ${snapshot.secrets.comicVineApiKey ? "configured" : "not configured"}.`;
  $("settings-certificate-state").textContent = `Saved certificate password: ${snapshot.secrets.certificatePassword ? "configured" : "not configured"}.`;
  $("server-settings-restart").textContent = snapshot.restartRequired
    ? "Restart required: saved application settings or a queued relocation differ from the running server. Saving alone does not apply them."
    : "No application restart is currently required.";
  $("server-settings-restart").classList.toggle("notice", snapshot.restartRequired);
  $("server-settings-restart").classList.toggle("muted", !snapshot.restartRequired);
  const active = snapshot.activeSettings;
  const rows = [["Settings file", snapshot.settingsPath], ["Prior settings recovery file", snapshot.recoveryPath],
    ["Revision", snapshot.revision]];
  if (active) rows.push(["Running data path", active.dataPath], ["Running library roots", active.libraries.join("\n")],
    ["Running Archived path", active.archivePath || "Not configured"], ["Running Unsorted path", active.unsortedPath || "Not configured"],
    ["Running listen URLs", active.urls.join("\n")], ["Running HTTPS requirement", active.requireHttps ? "Required" : "Not required"],
    ["Running allowed hosts", active.allowedHosts.join("\n")], ["Running trusted proxies", active.knownProxies.join("\n") || "None"],
    ["Running certificate file", active.certificatePath || "None"], ["Running certificate key file", active.certificateKeyPath || "None"]);
  $("server-settings-running").replaceChildren();
  for (const [label, value] of rows) $("server-settings-running").append(textElement("dt", label), textElement("dd", String(value ?? "Unavailable")));
  const service = snapshot.service;
  $("settings-service-status").textContent = service.supported
    ? `Service: ${service.installed ? "installed" : "not installed"}; ${service.running ? "running" : "not running"}; startup ${service.enabled ? "enabled" : "disabled"}. ${service.message || ""}`
    : `Service controls unavailable. ${service.message || "Linux user systemd is required."}`;
  $("settings-backup-status").textContent = `Backup timer: ${service.backupEnabled === true ? "enabled" : service.backupEnabled === false ? "disabled" : "status unavailable"}. ${service.backupMessage || ""}`;
  $("settings-data-path").textContent = `Saved data path: ${settings.dataPath}`;
  $("settings-relocation-status").textContent = [snapshot.pendingDataPath ? `Queued destination: ${snapshot.pendingDataPath}. Copy will run on the next startup.` : "No data relocation queued.",
    snapshot.relocationError ? `Last relocation failed: ${snapshot.relocationError}` : ""].filter(Boolean).join("\n");
  $("settings-reconnect").textContent = `After restarting, reconnect manually using the saved bindings: ${settings.urls.join(", ")} (or your reverse proxy address). Replace wildcard bindings such as 0.0.0.0 or * with the server's reachable hostname. This page will not redirect or retry a restart automatically.`;
  updateServerSettingsControls();
}

async function loadServerSettings() {
  if (serverSettingsBusy || session?.role !== "admin") return;
  const generation = ++serverSettingsGeneration;
  const epoch = authEpoch;
  const focus = document.activeElement;
  let failed = false;
  serverSettingsBusy = true;
  updateServerSettingsControls();
  $("server-settings-status").textContent = "Loading saved settings and actual service status…";
  try {
    const snapshot = await api("/api/server-settings");
    if (epoch !== authEpoch || generation !== serverSettingsGeneration) return;
    renderServerSettings(snapshot);
    $("server-settings-status").textContent = "Saved settings loaded.";
  } catch (error) {
    if (epoch !== authEpoch || generation !== serverSettingsGeneration) return;
    if (error.status === 403) { resetServerSettings(); showError(error); return; }
    serverSettingsStale = true;
    failed = true;
    $("server-settings-status").textContent = `Could not load server settings: ${error.message} Reload settings to try again.`;
  } finally {
    if (epoch === authEpoch && generation === serverSettingsGeneration) {
      serverSettingsBusy = false;
      updateServerSettingsControls();
      if (!$("settings-view").hidden && (failed || $("settings-view").contains(focus)))
        (failed || !focus.isConnected || focus.matches(":disabled") ? $("server-settings-status") : focus).focus();
    }
  }
}

async function mutateServerSettings(action, payload = {}) {
  if (!serverSettings || serverSettingsBusy || serverSettingsStale || session?.role !== "admin") return;
  const relocating = action === "relocate";
  const password = $(relocating ? "data-relocation-password" : "settings-current-password");
  if (!password.reportValidity()) return;
  if (action && serverSettingsDirty()) return;
  const generation = serverSettingsGeneration;
  const epoch = authEpoch;
  const status = $(relocating ? "data-relocation-status" : "server-settings-status");
  const focus = document.activeElement;
  let failed = false;
  const body = { expectedRevision: serverSettings.revision, currentPassword: password.value, ...payload };
  serverSettingsBusy = true;
  updateServerSettingsControls();
  status.textContent = action === "restart" ? "Requesting restart…" : relocating ? "Validating and queuing relocation…" : "Saving…";
  try {
    const result = await api(`/api/server-settings${action ? `/${action}` : ""}`, {
      method: action ? "POST" : "PUT", body: JSON.stringify(body),
    });
    if (epoch !== authEpoch || generation !== serverSettingsGeneration) return;
    if (action === "restart") {
      serverSettingsStale = true;
      status.textContent = `${result.message} Wait for the server to start, then reconnect manually or reload settings to check its status.`;
    } else {
      renderServerSettings(result);
      if (relocating) {
        $("data-relocation-form").reset();
        $("data-relocation-dialog").close();
      }
      $("server-settings-status").textContent = relocating ? "Destination validated and relocation queued, not yet performed. Restart when ready."
        : action === "service" ? "Startup request saved. Check the actual service status below; the server has not been restarted."
        : "Settings saved. Check restart and backup timer status before assuming changes are active.";
    }
  } catch (error) {
    if (epoch !== authEpoch || generation !== serverSettingsGeneration) return;
    if (error.status === 403) { resetServerSettings(); showError(error); return; }
    if (error.status === 409 || !error.status || error.status >= 500) serverSettingsStale = true;
    failed = true;
    const context = relocating ? "Data relocation" : action === "restart" ? "Server restart" : action === "service" ? "Startup change" : "Server settings";
    status.textContent = `${context}: ${error.message}${error.status === 409 ? " Settings changed elsewhere. Your draft is retained; copy it before reloading and reviewing."
      : !error.status || error.status >= 500 ? " The outcome may be unknown. Nothing will be retried automatically; copy your draft before reloading status."
      : " Review the message and your entries before submitting again. Your draft is retained; re-enter your current password."}`;
  } finally {
    if (epoch === authEpoch && generation === serverSettingsGeneration) {
      password.value = "";
      serverSettingsBusy = false;
      updateServerSettingsControls();
      if (!$("settings-view").hidden) {
        const summary = relocating && $("data-relocation-dialog").open ? status : $("server-settings-status");
        (failed || !focus.isConnected || focus.matches(":disabled") || !focus.checkVisibility() ? summary : focus).focus();
      }
    }
  }
}

$("server-settings").addEventListener("click", () => {
  if (session?.role === "admin") location.hash = "settings";
});
$("data-relocation-dialog").addEventListener("close", () => { $("data-relocation-form").reset(); $("data-relocation-status").textContent = ""; });
$("reload-server-settings").addEventListener("click", () => {
  if (!serverSettingsBusy && (!serverSettingsDirty() || confirm("Discard this draft and reload the saved settings?"))) loadServerSettings();
});
for (const name of ["input", "change"]) $("server-settings-controls").addEventListener(name, updateServerSettingsControls);
for (const [clear, secret] of [["settings-clear-comic-vine", "settings-comic-vine-key"], ["settings-clear-certificate-password", "settings-certificate-password"]])
  $(clear).addEventListener("change", () => { if ($(clear).checked) $(secret).value = ""; updateServerSettingsControls(); });
$("server-settings-form").addEventListener("submit", (event) => {
  event.preventDefault();
  if (!serverSettings || !serverSettingsDirty() || !$("server-settings-form").reportValidity()) return;
  mutateServerSettings("", { settings: readServerSettings(), comicVineApiKey: $("settings-comic-vine-key").value || null,
    clearComicVineApiKey: $("settings-clear-comic-vine").checked, certificatePassword: $("settings-certificate-password").value || null,
    clearCertificatePassword: $("settings-clear-certificate-password").checked });
});
$("settings-enable-startup").addEventListener("click", () => mutateServerSettings("service", { enabled: true }));
$("settings-disable-startup").addEventListener("click", () => mutateServerSettings("service", { enabled: false }));
$("settings-restart-service").addEventListener("click", () => {
  if ($("settings-current-password").reportValidity() && confirm("Restart the server now? All readers will be disconnected. Saved application settings and any queued data relocation will be applied on startup. Keep the saved address and recovery paths before continuing."))
    mutateServerSettings("restart");
});
$("open-data-relocation").addEventListener("click", () => {
  if (!serverSettings || serverSettingsBusy || serverSettingsStale || serverSettingsDirty() || serverSettings.pendingDataPath) return;
  $("data-relocation-form").reset();
  $("data-relocation-source").textContent = `Current source: ${serverSettings.settings.dataPath}`;
  $("data-relocation-status").textContent = "";
  $("data-relocation-dialog").showModal();
});
$("data-relocation-form").addEventListener("submit", (event) => {
  event.preventDefault();
  if (!$("data-relocation-form").reportValidity()) return;
  mutateServerSettings("relocate", { destination: $("data-relocation-destination").value.trim() });
});

$("delete-selected").addEventListener("click", async () => {
  if (session?.role !== "admin" || session.mustChangePassword || deletionBusy || !selectedBooks.size || selectedBooks.size > 100) return;
  const state = deletion = { ids: [...selectedBooks], hash: location.hash };
  const status = $("management-status");
  let submitted = false;
  deletionBusy = true;
  status.textContent = "Checking selected files and catalog records before permanent deletion…";
  status.focus({ preventScroll: true });
  updateSelection();
  try {
    const preview = await api("/api/books/delete-preview", { method: "POST", body: JSON.stringify({ bookIds: state.ids }) });
    if (deletion !== state) return;
    if (!preview.canDelete) {
      status.textContent = ["Nothing was deleted. Resolve the listed problems before trying again.",
        ...preview.items.filter(item => !item.canDelete).map(item => `${item.title} (${item.source}): ${item.error || "Cannot delete this comicbook."}`)].join("\n");
      return;
    }
    const dialog = $("delete-dialog");
    dialog.returnValue = "";
    $("delete-count").textContent = `${preview.items.length} selected comicbook${preview.items.length === 1 ? "" : "s"}:`;
    $("delete-items").replaceChildren(...preview.items.map(item => textElement("li", `${item.title} — ${item.source}`)));
    status.textContent = "Review the selected comicbooks before permanent deletion.";
    dialog.showModal();
    await new Promise(resolve => dialog.addEventListener("close", resolve, { once: true }));
    $("delete-items").replaceChildren();
    if (deletion !== state) return;
    if (dialog.returnValue !== "delete") {
      status.textContent = "Deletion cancelled. Nothing was deleted.";
      return;
    }
    status.textContent = "Deleting selected files and catalog records. Do not close this page. Completed deletions cannot be undone…";
    submitted = true;
    const result = await api("/api/books/delete", { method: "POST", body: JSON.stringify({
      bookIds: state.ids, expectedRevision: preview.revision, confirmed: true
    }) });
    if (deletion !== state) return;
    for (const id of result.deletedIds) { selectedBooks.delete(id); failedCovers.delete(id); }
    const failures = result.failures.map(item => `${preview.items.find(book => book.bookId === item.bookId)?.title || item.bookId}: ${item.error}`);
    status.textContent = failures.length
      ? `Deleted: ${result.deletedIds.length}. Failed: ${failures.length}.\n${failures.join("\n")}\nFailed comicbooks remain selected. Inspect them before trying again.`
      : "";
  } catch (error) {
    if (deletion !== state) return;
    status.textContent = !submitted
      ? `Safety check failed: ${error.message} Nothing was deleted. Resolve the problem before trying again.`
      : error.status && error.status < 500
        ? `Deletion rejected: ${error.message} Resolve the problem before trying again. Nothing will be retried automatically.`
        : `Could not confirm deletion: ${error.message} Some files or catalog records may already be deleted. Refresh and inspect the library and files before another action. Missing-book cleanup can remove a catalog record left after its file was deleted. Nothing will be retried automatically.`;
  } finally {
    if (deletion === state) {
      deletionBusy = false;
      deletion = null;
      updateSelection();
      (status.textContent ? status : $("library-page")).focus({ preventScroll: true });
      loadLists();
      loadPublishers();
      loadLibrary();
    }
  }
});

function canCloseDialog(id) {
  if (id === "series-list-dialog") return !seriesList?.busy;
  if (id === "kavita-import-dialog") return !kavitaImport?.applying;
  if (id === "maintenance-dialog") return !maintenanceBusy;
  if (id === "details-dialog" && intake?.replacement && intake.saving) return false;
  if (id === "intake-dialog") return !intake?.saving && !metadataBusy && !organizationBusy && !archiveBusy && !fileOperationBusy;
  if (id === "data-relocation-dialog") return !serverSettingsBusy && (!dataRelocationDirty() || confirm("Discard this unsubmitted relocation request?"));
  if (id === "user-dialog") return !accountBusy && (!accountDirty() || confirm("Discard unsaved account changes?"));
  const rating = id === "details-dialog";
  const folder = id === "folder-dialog";
  if (id === "page-metadata-dialog") return !pageOrderBusy && !pageMetadataBusy
    && (!(pageOrderDirty() || pageMetadataDirty) || confirm("Discard unsaved page changes?"));
  if (id === "list-dialog") return !listBusy && !listPreviewBusy && (!listDirty || confirm(listEditorMode === "list" ? "Discard unsaved reading list changes?" : "Discard unapplied advanced filter changes?"));
  if (id === "archive-dialog") return !archiveBusy;
  if (id === "file-operation-dialog") return !fileOperationBusy && (!fileOperationDirty() || confirm("Discard this move draft? Nothing is applied by closing."));
  if (id === "organization-dialog") return !organizationBusy && (!organizationDirty() || confirm("Discard this batch organization draft? Nothing is applied by closing."));
  if ((rating && ratingBusy) || (folder && folderBusy)) return false;
  return !((rating && ratingDirty) || (folder && folderDirty)) || confirm(`Discard unsaved ${rating ? "rating" : "folder"} changes?`);
}

for (const id of ["series-list-dialog", "maintenance-dialog", "kavita-import-dialog", "user-dialog", "data-relocation-dialog", "details-dialog", "folder-dialog", "page-metadata-dialog", "list-dialog", "duplicate-dialog", "archive-dialog", "file-operation-dialog", "organization-dialog"]) $(id).addEventListener("cancel", (event) => {
  if (!canCloseDialog(id)) event.preventDefault();
});

function resetKavitaImport() {
  kavitaImport?.controller?.abort();
  kavitaImport = null;
  $("kavita-import-form").reset();
  $("kavita-import-user").replaceChildren(new Option("Preview the backup to load users", ""));
  $("kavita-import-entries").replaceChildren();
  $("kavita-import-preview").hidden = true;
  $("kavita-import-summary").textContent = $("kavita-import-account").textContent = $("kavita-import-status").textContent = "";
  if ($("kavita-import-dialog").open) $("kavita-import-dialog").close();
}

function kavitaImportCurrent(state) {
  return kavitaImport === state && state.epoch === authEpoch && session?.role === "admin"
    && !session.mustChangePassword && $("kavita-import-dialog").open;
}

function updateKavitaImportControls() {
  const state = kavitaImport;
  $("kavita-import-file").disabled = !!state?.applying;
  $("kavita-import-user").disabled = !!state?.applying || $("kavita-import-user").options.length < 2;
  $("preview-kavita-import").disabled = !!state?.busy;
  $("apply-kavita-import").disabled = !!state?.busy || !state?.preview?.revision || state.preview.readyBooks < 1;
  $("kavita-import-dialog").querySelector("[data-close]").disabled = !!state?.applying;
}

function invalidateKavitaImport() {
  if (!kavitaImport || kavitaImport.applying) return;
  kavitaImport.controller?.abort();
  kavitaImport.controller = null;
  kavitaImport.preview = null;
  kavitaImport.file = null;
  kavitaImport.busy = false;
  $("kavita-import-preview").hidden = true;
  $("kavita-import-entries").replaceChildren();
  $("kavita-import-status").textContent = "Selection changed. Preview again before marking books read.";
  updateKavitaImportControls();
}

function renderKavitaImportEntries() {
  const state = kavitaImport;
  if (!state?.preview) return;
  const filter = $("kavita-import-filter").value;
  const entries = filter ? state.preview.entries.filter(entry => entry.status === filter) : state.preview.entries;
  const pages = Math.max(1, Math.ceil(entries.length / 50));
  state.page = Math.max(1, Math.min(state.page, pages));
  $("kavita-import-page").textContent = `Page ${state.page} of ${pages}, ${entries.length} files`;
  $("previous-kavita-import").disabled = state.page === 1;
  $("next-kavita-import").disabled = state.page === pages;
  $("kavita-import-entries").replaceChildren(...entries.slice((state.page - 1) * 50, state.page * 50).map(entry => {
    const row = textElement("article", "", "maintenance-row");
    const source = textElement("p", "");
    source.append(textElement("strong", entry.source));
    row.append(source, textElement("p", `${{ ready: "Ready", "already-read": "Already read", unmatched: "Unmatched", ambiguous: "Ambiguous" }[entry.status]}${entry.target ? `: ${entry.target}` : ""}`));
    return row;
  }));
  if (!entries.length) $("kavita-import-entries").append(textElement("p", "No files in this result filter."));
}

$("import-kavita").addEventListener("click", () => {
  if (session?.role !== "admin" || session.mustChangePassword || kavitaImport || leavingReader || readingStatusBusy) return;
  resetKavitaImport();
  kavitaImport = { epoch: authEpoch, hash: location.hash, preview: null, busy: false, applying: false, controller: null, page: 1 };
  $("kavita-import-account").textContent = `Target Panel Haven account: ${session.username} (signed in).`;
  $("kavita-import-status").textContent = "Choose a Kavita backup ZIP, then preview. Nothing is imported until you mark matched books read.";
  updateKavitaImportControls();
  $("kavita-import-dialog").showModal();
});
$("kavita-import-file").addEventListener("change", () => {
  if (kavitaImport?.applying) return;
  $("kavita-import-user").replaceChildren(new Option("Preview the backup to load users", ""));
  invalidateKavitaImport();
});
$("kavita-import-user").addEventListener("change", invalidateKavitaImport);
$("kavita-import-filter").addEventListener("change", () => {
  if (kavitaImport) { kavitaImport.page = 1; renderKavitaImportEntries(); }
});
for (const [id, direction] of [["previous-kavita-import", -1], ["next-kavita-import", 1]])
  $(id).addEventListener("click", () => {
    if (kavitaImport?.preview) { kavitaImport.page += direction; renderKavitaImportEntries(); }
  });
$("kavita-import-form").addEventListener("submit", async event => {
  event.preventDefault();
  const state = kavitaImport;
  if (!state || !kavitaImportCurrent(state) || state.busy) return;
  const file = $("kavita-import-file").files[0];
  if (!file) return;
  invalidateKavitaImport();
  if (file.size > 256 * 1024 * 1024) {
    $("kavita-import-status").textContent = "Choose a backup ZIP no larger than 256 MiB.";
    $("kavita-import-file").focus();
    return;
  }
  const sourceUserId = $("kavita-import-user").value;
  const controller = state.controller = new AbortController();
  state.busy = true;
  updateKavitaImportControls();
  $("kavita-import-status").textContent = "Uploading and checking completed chapters. No reading status is being changed…";
  try {
    recordVisibleProgress();
    await flushProgress();
    if (!kavitaImportCurrent(state) || controller.signal.aborted) return;
    const preview = await api(`/api/reading-import/kavita/preview${sourceUserId ? `?${new URLSearchParams({ sourceUserId })}` : ""}`, {
      method: "POST", headers: { "Content-Type": "application/zip" }, body: file, signal: controller.signal
    });
    if (!kavitaImportCurrent(state) || controller.signal.aborted) return;
    $("kavita-import-user").replaceChildren(new Option("Choose a Kavita user", ""),
      ...preview.users.map(user => new Option(user.name, String(user.id))));
    $("kavita-import-user").value = preview.sourceUserId === null ? "" : String(preview.sourceUserId);
    state.preview = preview;
    state.file = file;
    state.page = 1;
    $("kavita-import-filter").value = "";
    $("kavita-import-preview").hidden = preview.sourceUserId === null;
    if (preview.sourceUserId === null) {
      $("kavita-import-status").textContent = "Choose the Kavita source user, then preview again. No Panel Haven account other than your signed-in account can be targeted.";
    } else {
      const user = preview.users.find(user => user.id === preview.sourceUserId);
      $("kavita-import-summary").textContent = `Kavita user: ${user.name}. Target Panel Haven account: ${session.username}. ${preview.completedFiles} completed files; ${preview.partialChapters} partial chapters skipped; ${preview.readyBooks} matched books ready; ${preview.alreadyReadBooks} matched books already read; ${preview.unmatchedFiles} unmatched files; ${preview.ambiguousFiles} ambiguous files. Book counts are deduplicated; results list each completed source file.`;
      renderKavitaImportEntries();
      $("kavita-import-status").textContent = preview.readyBooks
        ? "Review the matches and source user, then mark matched books read. Unmatched and ambiguous files will be skipped."
        : "No matched books need marking read. Review all result filters or choose another source user.";
    }
  } catch (error) {
    if (kavitaImportCurrent(state) && !controller.signal.aborted)
      $("kavita-import-status").textContent = `Preview failed: ${error.message} Nothing was imported. Resolve the problem and preview again.`;
  } finally {
    if (kavitaImportCurrent(state) && state.controller === controller) {
      state.busy = false;
      state.controller = null;
      updateKavitaImportControls();
      (state.preview?.sourceUserId === null ? $("kavita-import-user") : $("kavita-import-status")).focus({ preventScroll: true });
    }
  }
});
$("apply-kavita-import").addEventListener("click", async () => {
  const state = kavitaImport;
  if (!state || !kavitaImportCurrent(state) || state.busy || !state.preview?.revision || state.preview.readyBooks < 1) return;
  const preview = state.preview;
  if (state.file !== $("kavita-import-file").files[0] || String(preview.sourceUserId) !== $("kavita-import-user").value) {
    invalidateKavitaImport();
    return;
  }
  const controller = state.controller = new AbortController();
  state.busy = state.applying = true;
  state.preview = null;
  $("kavita-import-preview").hidden = true;
  $("kavita-import-entries").replaceChildren();
  updateKavitaImportControls();
  $("kavita-import-status").textContent = "Saving current reader progress, then marking matched books read. Keep this page open…";
  try {
    state.readerCleared ||= !!currentBook;
    await leaveReader();
    if (!kavitaImportCurrent(state) || controller.signal.aborted) return;
    const result = await api(`/api/reading-import/kavita/apply?${new URLSearchParams({ sourceUserId: preview.sourceUserId, revision: preview.revision })}`, {
      method: "POST", headers: { "Content-Type": "application/zip" }, body: state.file, signal: controller.signal
    });
    if (!kavitaImportCurrent(state) || controller.signal.aborted) return;
    $("kavita-import-status").textContent = `${result.markedRead} books marked read for ${session.username}; ${result.alreadyRead} already-read books kept unchanged. Preview again to see the updated status.`;
    await Promise.all([loadLists(), loadLibrary()]);
  } catch (error) {
    if (kavitaImportCurrent(state) && !controller.signal.aborted)
      $("kavita-import-status").textContent = error.status === 409
        ? `${error.message} The preview is stale. Preview again and review the new matches before applying.`
        : `Cannot confirm the import: ${error.message} It may have completed. Refresh by previewing again before any further apply. Nothing will be retried automatically.`;
  } finally {
    if (kavitaImportCurrent(state) && state.controller === controller) {
      state.busy = state.applying = false;
      state.controller = null;
      updateKavitaImportControls();
      $("kavita-import-status").focus({ preventScroll: true });
    }
  }
});
$("kavita-import-dialog").addEventListener("close", () => {
  if ($("kavita-import-dialog").open) return;
  const state = kavitaImport;
  resetKavitaImport();
  if (state?.epoch === authEpoch && session) {
    if (state.readerCleared && !currentBook) scheduleRoute();
    else $("account-menu").querySelector("summary").focus({ preventScroll: true });
  }
});
window.addEventListener("pagehide", () => {
  if (!kavitaImport) return;
  const applying = kavitaImport.applying;
  kavitaImport.controller?.abort();
  kavitaImport.controller = null;
  kavitaImport.busy = kavitaImport.applying = false;
  kavitaImport.preview = null;
  $("kavita-import-preview").hidden = true;
  $("kavita-import-entries").replaceChildren();
  $("kavita-import-status").textContent = applying
    ? "Cannot confirm the import after leaving this page. It may have completed. Preview again before applying; nothing is retried automatically."
    : "Preview cleared after leaving this page. Preview again before marking books read.";
  updateKavitaImportControls();
});

function labeledControl(text, control) {
  const label = textElement("label", text);
  label.append(control);
  return label;
}

function clearFieldError(control, status) {
  if (control.validity.customError && status.textContent === control.validationMessage) status.textContent = "";
  control.setCustomValidity("");
}

async function loadMetadataSchema() {
  if (!metadataSchema) metadataSchema = await api("/api/metadata-fields");
  return metadataSchema;
}

let customValueNumber = 0;

function metadataChangeRow(name, control, custom = false, value = "", label = name) {
  const row = textElement("fieldset", "", custom ? "custom-value metadata-change" : "metadata-change");
  const context = custom ? name || `New custom value ${++customValueNumber}` : label;
  row.dataset.label = context;
  row.append(textElement("legend", context));
  const apply = document.createElement("input");
  apply.type = "checkbox";
  apply.className = "apply-change";
  apply.checked = custom && !name;
  row.append(labeledControl(`Apply ${context}`, apply));
  if (custom) {
    const key = document.createElement("input");
    key.className = "custom-name";
    key.value = name;
    key.maxLength = 128;
    key.readOnly = Boolean(name);
    row.append(labeledControl(`Name for ${context}`, key));
    if (!name) {
      const updateContext = () => {
        const current = key.value.trim() || context;
        row.dataset.label = current;
        row.querySelector("legend").textContent = current;
        for (const [selector, prefix] of [[".apply-change", "Apply"], [".custom-name", "Name for"], [".metadata-value", "New"], [".change-action", "Action for"]])
          row.querySelector(selector).parentElement.firstChild.textContent = `${prefix} ${current}`;
        row.querySelector("button").setAttribute("aria-label", `Discard row: ${current}`);
      };
      key.addEventListener("input", updateContext);
      key.addEventListener("change", updateContext);
    }
  } else row.dataset.field = name;
  control.classList.add("metadata-value");
  control.value = value;
  const action = document.createElement("select");
  action.className = "change-action";
  action.append(new Option("Set value", "set"), new Option("Remove / reset", "remove"));
  row.append(labeledControl(`New ${context}`, control), labeledControl(`Action for ${context}`, action));
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
    discard.setAttribute("aria-label", `Discard row: ${context}`);
    discard.addEventListener("click", () => {
      row.remove();
      metadataChanged();
      $("add-custom-value").focus();
    });
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

function filterMetadataFields() {
  const query = $("metadata-field-search").value.trim().toLocaleLowerCase();
  const checkedOnly = $("metadata-changed-only").checked;
  const selected = [];
  for (const row of $("metadata-controls").querySelectorAll(".metadata-change")) {
    const checked = row.querySelector(".apply-change").checked;
    const label = row.querySelector(".custom-name")?.value || row.dataset.label;
    if (checked) selected.push(label);
    const hidden = (checkedOnly && !checked) || !label.toLocaleLowerCase().includes(query);
    const active = row.contains(document.activeElement);
    if (hidden && active && document.activeElement.matches(".custom-name") && checked) continue;
    if (hidden && active) $(checkedOnly && !checked ? "metadata-changed-only" : "metadata-field-search").focus();
    row.hidden = hidden;
  }
  for (const group of $("metadata-controls").querySelectorAll(".metadata-field-group, .custom-values-group")) {
    const visible = [...group.querySelectorAll(".metadata-change")].some((row) => !row.hidden);
    group.hidden = group.classList.contains("metadata-field-group") && !visible;
  }
  $("metadata-change-summary").textContent = selected.length ? `${selected.length} checked: ${selected.join(", ")}` : "No fields checked. Unchecked values remain unchanged.";
}

function renderMetadataPreview(books, target) {
  for (const book of books) {
    const row = textElement("article", "", "maintenance-row");
    row.dataset.previewBook = book.id;
    row.append(textElement("h3", bookTitle(book)));
    if (!book.changes.length) row.append(textElement("p", "Unchanged — no actual changes.", "muted"));
    for (const change of book.changes) {
      row.append(textElement("h4", `${change.custom ? "Custom: " : ""}${change.field}`));
      const values = document.createElement("dl");
      values.append(
        textElement("dt", "Before"), textElement("dd", change.before == null ? "(absent)" : change.before === "" ? "(empty string)" : JSON.stringify(change.before)),
        textElement("dt", "After"), textElement("dd", change.after == null ? "(removed / absent)" : change.after === "" ? "(empty string)" : JSON.stringify(change.after)));
      row.append(values);
    }
    target.append(row);
  }
}

function invalidateMetadataPreview() {
  metadataPreview = null;
  $("metadata-preview-books").replaceChildren();
  $("metadata-preview").hidden = true;
  $("apply-metadata").disabled = true;
}

function metadataChanged() {
  metadataDirty = true;
  for (const input of $("custom-values").querySelectorAll(".custom-name")) clearFieldError(input, $("metadata-status"));
  filterMetadataFields();
  if (metadataPreview) $("metadata-status").textContent = "Draft changed. Preview again before applying.";
  invalidateMetadataPreview();
  if (archivePreview && metadataBookIds.includes(archiveBookId)) {
    invalidateArchivePreview();
    $("archive-status").textContent = "Catalog draft changed. Save or discard it before trying the comicbook operation again.";
  }
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
      for (const issue of book.issues) issues.append(textElement("li", `${issue.field}: ${issue.message}; Value: ${issue.value == null ? "(absent)" : issue.value === "" ? "(empty string)" : JSON.stringify(issue.value)}`));
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
      ? `${result.total} affected books. Showing ${(result.page - 1) * result.pageSize + 1}–${(result.page - 1) * result.pageSize + result.items.length}`
      : "No matching metadata issues.";
    $("metadata-report-page").textContent = `Page ${metadataReportPage} of ${metadataReportPages}`;
    $("previous-metadata-report").disabled = metadataReportPage <= 1;
    $("next-metadata-report").disabled = metadataReportPage >= metadataReportPages;
  } catch (error) {
    if (generation === metadataReportGeneration) $("metadata-report-status").textContent = `Could not load metadata report: ${error.message}. Refresh to retry.`;
  }
}

function comicExportCurrent(state) {
  return comicExport === state && session && !session.mustChangePassword && state.epoch === authEpoch &&
    state.hash === location.hash && (state.selection
      ? state.ids.length === selectedBooks.size && state.ids.every((id) => selectedBooks.has(id))
      : state.details === detailGeneration && state.ids[0] === detailBook?.id && $("details-dialog").open);
}

function resetComicExport() {
  comicExport?.controller?.abort();
  if (comicExport?.url) URL.revokeObjectURL(comicExport.url);
  comicExport = null;
  if ($("comic-export-dialog").open) $("comic-export-dialog").close();
  $("comic-export-selection").textContent = $("comic-export-status").textContent = "";
  $("comic-export-progress").hidden = true;
}

function openComicExport(selection) {
  if (!session || session.mustChangePassword || (!selection && (!detailBook || $("export-comic").disabled))) return;
  const ids = selection ? [...selectedBooks] : [detailBook.id];
  if (!ids.length || ids.length > 100) return;
  resetComicExport();
  comicExport = { ids, selection, epoch: authEpoch, details: detailGeneration, hash: location.hash, controller: null, url: null };
  $("comic-export-selection").textContent = `${ids.length} ${ids.length === 1 ? "book selected — one CBZ" : "books selected — ZIP containing CBZs"}${selection ? "." : `: ${bookTitle(detailBook)}`}`;
  $("download-comics").textContent = ids.length === 1 ? "Download CBZ" : "Download ZIP";
  $("download-comics").disabled = false;
  $("cancel-comic-export").hidden = true;
  $("close-comic-export").textContent = "Close";
  $("comic-export-dialog").showModal();
}

$("export-comic").addEventListener("click", () => openComicExport(false));
$("export-selected-comics").addEventListener("click", () => openComicExport(true));
$("close-comic-export").addEventListener("click", resetComicExport);
$("comic-export-dialog").addEventListener("cancel", (event) => { event.preventDefault(); resetComicExport(); });
$("comic-export-dialog").addEventListener("close", () => { if (!$("comic-export-dialog").open) resetComicExport(); });
$("cancel-comic-export").addEventListener("click", () => {
  comicExport?.controller?.abort();
  $("cancel-comic-export").disabled = true;
  $("comic-export-status").textContent = "Cancelling download…";
});
$("download-comics").addEventListener("click", async () => {
  const state = comicExport;
  if (!state || state.controller) return;
  if (!comicExportCurrent(state)) { resetComicExport(); return; }
  const controller = state.controller = new AbortController();
  let downloaded = false;
  const progress = $("comic-export-progress");
  $("download-comics").disabled = true;
  $("cancel-comic-export").hidden = $("cancel-comic-export").disabled = false;
  $("close-comic-export").textContent = "Cancel and close";
  $("comic-export-status").textContent = "Preparing and verifying the complete export on the server…";
  progress.hidden = false;
  progress.removeAttribute("value");
  try {
    const response = await api("/api/comics/export", {
      method: "POST", body: JSON.stringify({ bookIds: state.ids }), signal: controller.signal, rawResponse: true
    });
    if (!comicExportCurrent(state)) throw new DOMException("Export scope changed.", "AbortError");
    controller.signal.throwIfAborted();
    const mime = state.ids.length === 1 ? "application/vnd.comicbook+zip" : "application/zip";
    const disposition = response.headers.get("Content-Disposition") || "";
    const length = response.headers.get("Content-Length") || "";
    const total = Number(length);
    if (response.status !== 200 || !response.body ||
        response.headers.get("Content-Type")?.split(";")[0].trim().toLowerCase() !== mime ||
        !/^attachment(?:;|$)/i.test(disposition) || !/^\d+$/.test(length) ||
        !Number.isSafeInteger(total) || total <= 0 || total > 1024 ** 3)
      throw new Error("The server did not return a valid comic download (maximum 1 GiB).");
    const extension = state.ids.length === 1 ? ".cbz" : ".zip";
    let filename = "";
    const encodedName = /(?:^|;)\s*filename\*=UTF-8''([^;]+)/i.exec(disposition);
    const plainName = /(?:^|;)\s*filename=(?:"([^"]*)"|([^;\s]+))/i.exec(disposition);
    try { filename = encodedName ? decodeURIComponent(encodedName[1]) : plainName?.[1] || plainName?.[2] || ""; }
    catch { /* Malformed filename encoding uses the safe local name below. */ }
    if (!filename || /[\\/:*?"<>|\u0000-\u001f\u007f\u202a-\u202e\u2066-\u2069]/.test(filename) ||
        !filename.toLowerCase().endsWith(extension))
      filename = `panelhaven-comics${extension}`;
    let received = 0;
    progress.max = total;
    progress.value = 0;
    $("comic-export-status").textContent = `Receiving 0 of ${total.toLocaleString()} bytes…`;
    // ponytail: native Blob storage handles chunks; no in-memory chunk array or download job framework.
    const stream = response.body.pipeThrough(new TransformStream({
      transform(chunk, output) {
        controller.signal.throwIfAborted();
        if (!comicExportCurrent(state)) throw new DOMException("Export scope changed.", "AbortError");
        received += chunk.byteLength;
        if (received > total) throw new Error("The download size did not match the prepared export.");
        progress.value = received;
        $("comic-export-status").textContent = `Receiving ${received.toLocaleString()} of ${total.toLocaleString()} bytes…`;
        output.enqueue(chunk);
      }
    }), { signal: controller.signal });
    const blob = await new Response(stream, { headers: { "Content-Type": mime } }).blob();
    controller.signal.throwIfAborted();
    if (!comicExportCurrent(state)) throw new DOMException("Export scope changed.", "AbortError");
    if (received !== total || blob.size !== total) throw new Error("The download was incomplete. No file was saved.");
    const url = state.url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.append(link);
    try { link.click(); }
    finally {
      link.remove();
      setTimeout(() => { URL.revokeObjectURL(url); if (state.url === url) state.url = null; }, 1000);
    }
    downloaded = true;
    $("comic-export-status").textContent = `Complete export received (${total.toLocaleString()} bytes). Download handed to your browser.`;
  } catch (error) {
    if (comicExportCurrent(state))
      $("comic-export-status").textContent = controller.signal.aborted
        ? "Download cancelled. No file was saved. You can retry."
        : `Export failed: ${error.message} No partial file was saved. Retry download when ready.`;
  } finally {
    controller.abort();
    state.controller = null;
    if (comicExport === state) {
      if (!comicExportCurrent(state)) resetComicExport();
      else {
        progress.hidden = true;
        $("download-comics").disabled = downloaded;
        $("download-comics").textContent = downloaded ? "Download received" : "Retry download";
        $("cancel-comic-export").hidden = true;
        $("close-comic-export").textContent = "Close";
      }
    }
  }
});

function comicVineLink(url, label = "Comic Vine") {
  const link = textElement("a", label);
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== "https:" || parsed.hostname !== "comicvine.gamespot.com" || parsed.port || parsed.username || parsed.password) throw new Error("Invalid provider link");
    link.href = parsed.href;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
  } catch { return textElement("span", label); }
  return link;
}

function missingIssueLink(series, number) {
  const link = textElement("a", "Find", "button-link");
  link.href = `https://getcomics.org/?${new URLSearchParams({ s: `${series} ${number}` })}`;
  link.target = "_blank";
  link.rel = "noopener noreferrer";
  link.setAttribute("aria-label", `Find ${series} #${number} on GetComics (opens in a new tab)`);
  return link;
}

function intakeCurrent(state, row = state?.row) {
  return !!state && intake === state && state.epoch === authEpoch && state.hash === location.hash
    && session?.role === "admin" && $("intake-dialog").open && state.row === row
    && (!state.replacement || state.details === detailGeneration && detailBook?.id === row?.id && $("details-dialog").open);
}

function cancelIntakeRequest() {
  if (intake?.saving) return;
  intake?.controller?.abort();
  if (!intake) return;
  intake.controller = null;
  intake.busy = false;
  invalidateIntakePreview();
  updateIntakeControls();
}

function resetIntake() {
  cancelIntakeRequest();
  intake = null;
  if ($("intake-dialog").open) $("intake-dialog").close();
  $("intake-queue").replaceChildren();
  $("intake-root").replaceChildren();
  $("intake-candidates").replaceChildren();
  $("intake-current-cover").replaceChildren();
  $("intake-form").reset();
  invalidateIntakePreview();
  for (const id of ["intake-source", "intake-outcome", "intake-status", "intake-scope"]) $(id).textContent = "";
  updateIntakeControls();
}

function invalidateIntakePreview() {
  if (intake) intake.preview = null;
  $("intake-preview").hidden = true;
  $("intake-preview-details").replaceChildren();
  $("intake-confirm").disabled = true;
}

function showIntakePreview(preview) {
  intake.preview = preview;
  $("intake-preview-title").textContent = "Review source and destination";
  $("intake-preview-summary").textContent = `${preview.converted ? "Converts to CBZ" : "Preserves the comicbook format"} and removes the source after verification. This cannot be undone.`;
  const details = $("intake-preview-details");
  const paths = document.createElement("dl");
  paths.append(textElement("dt", "Source"), textElement("dd", preview.source),
    textElement("dt", "Destination"), textElement("dd", preview.destination));
  details.append(paths);
  for (const warning of preview.warnings) details.append(textElement("p", warning, "notice"));
  $("intake-confirm").textContent = "Convert to CBZ";
  $("intake-preview").hidden = false;
  $("intake-status").textContent = "Nothing has been saved. Review the proposed changes, then confirm or cancel.";
}

function updateIntakeControls() {
  const row = intake?.row;
  const busy = !!intake?.busy;
  const showProgress = busy && !!intake?.processingStage;
  $("intake-progress").hidden = !showProgress;
  $("intake-feedback").classList.toggle("processing", showProgress);
  if (showProgress) {
    $("intake-progress-label").textContent = intake.processingStage;
    $("intake-progress-book").textContent = row.title + (intake.rows.length > 1 ? `, Queue ${intake.rows.indexOf(row) + 1} of ${intake.rows.length}` : "");
  }
  const processing = row?.processing;
  const blocked = !intake?.metadataOnly && (intake?.historyOnly || !processing || processing.state === "pending" || row.completed || processing.canRecover);
  $("intake-hints").disabled = $("intake-match").disabled = busy || !row?.hints || blocked;
  $("intake-cancel").hidden = !busy || !!intake?.saving;
  $("intake-queue").disabled = !!intake?.saving;
  $("intake-root").disabled = busy || !$("intake-root").options.length;
  $("intake-dialog").querySelector("[data-close]").disabled = !!intake?.saving;
  $("intake-refresh").disabled = busy;
  $("apply-cbz-replacement").disabled = busy || !processing || processing.state === "pending" || processing.canRecover || !!intake?.replacementAttempt;
  $("intake-confirm").disabled = busy || !intake?.preview;
  $("intake-discard").disabled = !!intake?.saving;
  $("intake-recover").hidden = !processing?.canRecover;
  $("intake-recover").disabled = busy;
  $("intake-outcome").textContent = row?.outcome || "Processing status has not been checked. Refresh status before proceeding.";
  for (const option of $("intake-queue").options) {
    const item = intake?.rows.find((entry) => entry.id === option.value);
    if (item) option.textContent = `${item.source || item.title} — ${item.processing?.state === "none" || item.processing?.state === "completed" && !item.completed ? "needs review" : item.processing?.state || "status unknown"}`;
  }
  for (const button of $("intake-candidates").querySelectorAll("button")) button.disabled = busy || blocked || !intake?.metadataOnly && !$("intake-root").value;
}

function setIntakeProcessing(row, result) {
  if (!result || !["none", "pending", "completed", "failed"].includes(result.state)) throw new Error("The server returned an unknown processing status");
  row.processing = result;
  row.completed = result.state === "completed" && result.revision !== row.initialRevision;
  if (intake?.replacement) $("intake-history").hidden = result.state === "none";
  row.outcome = result.state === "none" ? intake?.historyOnly ? "No recorded processing operation." : "Not processed. Accept a candidate to save metadata and organize the comicbook."
    : [result.message, result.destination ? `Destination: ${result.destination}` : "",
      result.state === "completed" ? result.converted ? "Converted to CBZ; original source removed after verification." : "Comicbook format preserved." : "",
      result.canRecover ? "Use Recover interrupted processing to finish committed cleanup or roll back uncommitted changes safely." : ""].filter(Boolean).join("\n");
  if (result.state === "completed" && !row.completed) row.outcome = "Previous operation: " + row.outcome;
  if (row.completed) {
    selectedBooks.delete(row.id);
    updateSelection();
  }
}

async function intakeRequest(message, work, saving = false, checkProcessing = false) {
  const state = intake;
  const row = state?.row;
  if (!intakeCurrent(state, row) || state.busy) return;
  invalidateIntakePreview();
  const controller = state.controller = new AbortController();
  state.busy = true;
  state.saving = saving;
  state.processingStage = checkProcessing ? "Preparing comicbook…" : "";
  $("intake-status").textContent = message;
  updateIntakeControls();
  if (checkProcessing) {
    $("intake-feedback").scrollIntoView({ block: "center" });
    $("intake-status").focus({ preventScroll: true });
  }
  const current = () => intakeCurrent(state, row) && state.controller === controller && !controller.signal.aborted;
  try {
    await work({ signal: AbortSignal.any([controller.signal, AbortSignal.timeout(60000)]) }, current, state, row);
  } catch (error) {
    if (!current()) return;
    if (state.metadataOnly && state.saving) {
      $("intake-status").textContent = error.status === 409
        ? `${error.message}. Nothing was saved. Check the current metadata before accepting again.`
        : `${error.message}. Could not confirm the save. Close and check the book's metadata before trying again. No comicbook file operation was requested.`;
    } else if (state.saving || checkProcessing) {
      row.processing = null;
      row.outcome = "Processing outcome unknown. Refresh status before taking any further action; do not retry processing.";
      if (state.processingStage) {
        state.processingStage = "Checking saved outcome…";
        $("intake-status").textContent = row.outcome;
        updateIntakeControls();
      }
      try {
        const result = await api(`/api/books/${encodeURIComponent(row.id)}/intake-processing`, { signal: AbortSignal.timeout(30000) });
        if (!current()) return;
        setIntakeProcessing(row, result);
        loadLibrary();
        loadPublishers();
        loadLists();
        $("intake-status").textContent = row.completed ? row.outcome
          : `${error.message}. Persisted status checked; processing was not retried. ${state.replacement ? "Review the status below; refresh status or use recovery before converting again." : result.state === "none" || result.state === "completed" || result.state === "failed" && !result.canRecover ? "You may explicitly accept again after resolving the error." : "Use the status and recovery controls below."}`;
      } catch {
        if (current()) $("intake-status").textContent = row.outcome;
      }
    } else $("intake-status").textContent = `${error.message}. Nothing was saved by this request. ${state.replacement ? "Refresh status or convert again after resolving the problem." : "Refresh or retry the search."}`;
  } finally {
    if (current()) {
      if ((state.replacement || state.saving && state.historyOnly && state.details === detailGeneration && detailBook?.id === row.id) && row.processing) {
        const attempted = state.replacementAttempt;
        const completed = row.processing.state === "completed" && row.processing.converted
          && (!state.replacement || attempted?.source === row.processing.source && attempted.destination === row.processing.destination);
        if (row.processing.state === "none" || row.processing.state === "failed" && !row.processing.canRecover
          || row.processing.state === "completed" && !completed)
          state.replacementAttempt = null;
        if (attempted && row.processing.state === "completed" && !completed)
          $("intake-status").textContent = "The recorded completed operation does not match this conversion. No automatic retry was made. Review processing history before converting again.";
        if (completed) {
          invalidateReaderNextIssue(row.id);
          failedCovers.delete(row.id);
          loadLibrary();
          loadPublishers();
          loadLists();
          try {
            const [book, metadata, schema] = await Promise.all([api(`/api/books/${encodeURIComponent(row.id)}`),
              api(`/api/books/${encodeURIComponent(row.id)}/metadata`), loadMetadataSchema()]);
            if (!current()) return;
            if (state.details === detailGeneration && detailBook?.id === row.id && $("details-dialog").open) {
              detailBook = book;
              renderDetails(book, metadata, schema);
              $("intake-history").hidden = false;
            }
            await refreshReaderSource(row.id);
          } catch (error) {
            if (current()) $("intake-status").textContent += ` Conversion completed, but the view could not refresh: ${error.message}. Close and reopen Book details and the reader.`;
          }
          if (!current()) return;
        }
      }
      const focusStatus = state.saving || checkProcessing || document.activeElement?.disabled;
      state.busy = state.saving = false;
      state.processingStage = "";
      state.controller = null;
      updateIntakeControls();
      if (checkProcessing && row.completed) {
        const index = state.rows.indexOf(row);
        const next = state.rows.find((item, position) => position > index && !item.completed)
          || state.rows.find(item => !item.completed);
        if (next) {
          $("intake-queue").value = next.id;
          await showIntakeRow();
          if (intakeCurrent(state, next)) $("intake-queue").focus({ preventScroll: true });
        } else {
          $("intake-dialog").close();
          $("library-page").focus({ preventScroll: true });
        }
      } else if (state.preview) $("intake-preview").focus();
      else if (focusStatus) $("intake-status").focus({ preventScroll: true });
    }
  }
}

async function acceptMatchedMetadata(candidate) {
  const state = intake, row = state?.row;
  let accepted = false;
  await intakeRequest("Accepting catalog metadata…", async (options, current) => {
    const proposal = await api(`/api/comic-vine/issues/${candidate.issueId}/metadata?volumeId=${candidate.volumeId}&intake=true`, options);
    if (!current()) return;
    const fields = Object.fromEntries(Object.entries(proposal.fields).filter(([, value]) => typeof value === "string" && value.trim()));
    if (!Object.keys(fields).length) throw new Error("The candidate returned no metadata");
    const payload = { bookIds: [row.id], fields };
    const preview = await api("/api/books/metadata/preview", { ...options, method: "POST", body: JSON.stringify(payload) });
    if (!current()) return;
    if (!preview.revision) throw new Error("The server did not return a metadata revision");
    await api("/api/books/metadata", { method: "POST", body: JSON.stringify({ ...payload, expectedRevision: preview.revision }) });
    if (!current()) return;
    accepted = true;
    invalidateReaderNextIssue(row.id);
    loadLibrary();
    loadPublishers();
    loadLists();
  }, true);
  if (!accepted || !intakeCurrent(state, row)) return;
  $("intake-dialog").close();
  await openDetails(row.id);
  if (state.epoch !== authEpoch || state.hash !== location.hash || !$("details-dialog").open || detailBook?.id !== row.id) return;
  $("details-status").textContent = "Metadata accepted. Source comicbook unchanged.";
  $("find-metadata").focus({ preventScroll: true });
}

function renderIntakeCandidates() {
  $("intake-candidates").replaceChildren();
  for (const candidate of intake?.row?.candidates || []) {
    const article = textElement("article", "", "maintenance-row intake-candidate");
    const cover = textElement("div", "Cover unavailable", "intake-cover");
    if (candidate.coverUrl) {
      const image = document.createElement("img");
      image.alt = `Cover of ${candidate.series} #${candidate.number}, run ${candidate.startYear || "unknown"}`;
      image.width = 120;
      image.height = 180;
      image.loading = "lazy";
      image.decoding = "async";
      image.referrerPolicy = "no-referrer";
      image.addEventListener("error", () => cover.replaceChildren("Cover unavailable"), { once: true });
      image.src = candidate.coverUrl;
      cover.replaceChildren(image);
    }
    const details = document.createElement("div");
    details.append(textElement("h3", `${candidate.series} #${candidate.number} — ${candidate.title || "Untitled"}`),
      textElement("p", `${candidate.publisher || "Unknown publisher"}, Run ${candidate.startYear || "unknown"}, Cover ${candidate.coverDate || "unknown"}`),
      textElement("p", `Rank score ${candidate.score}/100`));
    const accept = textElement("button", "Accept", "success");
    accept.type = "button";
    accept.setAttribute("aria-label", `${accept.textContent}: ${candidate.series} #${candidate.number}, run ${candidate.startYear || "unknown"}`);
    accept.addEventListener("click", () => {
      if (accept.disabled) return;
      if (intake.metadataOnly) { acceptMatchedMetadata(candidate); return; }
      intakeRequest("Preparing metadata and destination. Nothing has changed yet…", async (options, current, state, row) => {
        const payload = { issueId: candidate.issueId, volumeId: candidate.volumeId, rootIndex: Number($("intake-root").value) };
        const prepared = await api(`/api/books/${encodeURIComponent(row.id)}/intake/prepare`, { ...options, method: "POST", body: JSON.stringify(payload) });
        if (!current()) return;
        if (!prepared?.revision || !prepared.source || !prepared.destination || !Array.isArray(prepared.warnings))
          throw new Error("The server did not return a complete processing preview");
        state.saving = true;
        state.processingStage = "Processing comicbook…";
        $("intake-status").textContent = "Processing comicbook. Do not close this page; this cannot be cancelled or undone…";
        updateIntakeControls();
        const result = await api(`/api/books/${encodeURIComponent(row.id)}/intake/process`, {
          method: "POST", body: JSON.stringify({ ...payload, expectedRevision: prepared.revision, confirmed: true })
        });
        if (!current()) return;
        if (result?.state !== "completed") throw new Error("Processing completion was not confirmed");
        setIntakeProcessing(row, result);
        $("intake-status").textContent = row.outcome;
        loadLibrary();
        loadPublishers();
        loadLists();
      }, false, true);
    });
    const actions = textElement("div", "", "actions");
    const view = comicVineLink(candidate.url, "View candidate");
    if (view.tagName === "A") view.className = "button-link";
    actions.append(accept, view);
    details.append(actions);
    article.append(cover, details);
    $("intake-candidates").append(article);
  }
}

async function showIntakeRow() {
  cancelIntakeRequest();
  const state = intake;
  if (!state) return;
  state.row = state.rows.find((row) => row.id === $("intake-queue").value);
  const row = state.row;
  if (row.processing?.state !== "completed" && row.processing?.state !== "pending" && !row.processing?.canRecover) {
    row.processing = null;
    row.outcome = "";
  }
  $("intake-source").textContent = row.source || row.title;
  const cover = $("intake-current-cover");
  const image = document.createElement("img");
  image.alt = `Cover of ${row.source || row.title}`;
  image.width = 120;
  image.height = 180;
  image.decoding = "async";
  image.addEventListener("error", () => {
    if (image.parentNode === cover) cover.replaceChildren(textElement("span", "Cover unavailable"));
  }, { once: true });
  image.src = `/api/books/${encodeURIComponent(row.id)}/thumbnail`;
  cover.replaceChildren(image);
  $("intake-status").textContent = "";
  for (const name of ["series", "number", "year", "volume", "publisher"]) $(`intake-${name}`).value = row.hints?.[name] ?? "";
  renderIntakeCandidates();
  updateIntakeControls();
  await intakeRequest(state.metadataOnly ? "Loading search hints…" : state.historyOnly ? "Checking processing status…" : "Checking processing status and filename hints…", async (options, current) => {
    if (!state.metadataOnly) {
      const result = await api(`/api/books/${encodeURIComponent(row.id)}/intake-processing`, options);
      if (!current()) return;
      if (!state.historyOnly && row.initialRevision === undefined) row.initialRevision = result.revision;
      setIntakeProcessing(row, result);
      if (state.historyOnly) {
        $("intake-status").textContent = row.outcome;
        return;
      }
      if (!$("intake-root").options.length) {
        const roots = await api("/api/file-operation-roots", options);
        if (!current()) return;
        $("intake-root").replaceChildren(...roots.filter((root) => root.label !== "Unsorted").map((root) => new Option(root.label, root.index)));
      }
      if (row.completed || result.state === "pending" || result.canRecover) {
        $("intake-status").textContent = row.outcome;
        return;
      }
    }
    if (!row.hints) {
      const hints = await api(`/api/books/${encodeURIComponent(row.id)}/${state.metadataOnly ? "metadata-matches" : "intake"}`, options);
      if (!current()) return;
      row.hints = hints.hints;
      row.source = hints.source;
      $("intake-source").textContent = row.source;
      for (const name of ["series", "number", "year", "volume", "publisher"]) $(`intake-${name}`).value = row.hints[name] ?? "";
    }
    $("intake-status").textContent = state.metadataOnly ? "Check the hints, then match and choose a candidate."
      : $("intake-root").options.length
      ? "Check the hints and destination, then match. Scores rank suggestions; they are not approval."
      : "No destination is configured. Configure a writable Library or Archived path in Server settings before accepting.";
  });
}

function openIntake(rows, scope, mode = "process") {
  if (session?.role !== "admin" || intake?.saving) return;
  const replacement = mode === "replacement", historyOnly = mode === "history" || replacement, metadataOnly = mode === "metadata";
  resetIntake();
  intake = { epoch: authEpoch, hash: location.hash, rows, row: null, busy: false, historyOnly, replacement, metadataOnly, details: detailGeneration };
  $("intake-title").textContent = metadataOnly ? "Find metadata" : replacement ? "Convert to CBZ" : historyOnly ? "Processing status" : "Process comicbooks";
  $("cbz-replacement").hidden = !replacement;
  $("intake-queue").hidden = document.querySelector('label[for="intake-queue"]').hidden = replacement || metadataOnly;
  for (const id of ["intake-form", "intake-candidates"]) $(id).hidden = historyOnly;
  $("intake-root").hidden = document.querySelector('label[for="intake-root"]').hidden = historyOnly || metadataOnly;
  $("intake-outcome").hidden = metadataOnly;
  $("intake-refresh").textContent = metadataOnly ? "Refresh hints" : "Refresh processing status";
  $("intake-scope").textContent = scope + (historyOnly || metadataOnly ? "" : " Accept saves metadata and organizes the comicbook in the selected destination. Processing removes the source after verified success and cannot be undone.");
  $("intake-queue").replaceChildren(...rows.map((row) => new Option(row.title, row.id)));
  $("intake-dialog").showModal();
  showIntakeRow();
}

$("intake-history").addEventListener("click", () => {
  if (session?.role !== "admin" || !detailBook || !$("details-dialog").open || $("intake-history").hidden || intake) return;
  openIntake([{ id: detailBook.id, title: bookTitle(detailBook) }],
    "Review the last processing operation or recover interrupted work. This view never starts new processing.", "history");
});

$("convert-cbz").addEventListener("click", () => {
  if (session?.role !== "admin" || !detailBook || !$("details-dialog").open || $("convert-cbz").hidden || intake) return;
  openIntake([{ id: detailBook.id, title: bookTitle(detailBook) }],
    "Creates a CBZ in the same folder using saved metadata.", "replacement");
});
$("cbz-replacement-form").addEventListener("submit", (event) => {
  event.preventDefault();
  if (!intake?.replacement || $("apply-cbz-replacement").disabled) return;
  intakeRequest("Checking the source, saved metadata, and destination before conversion…", async (options, current, state, row) => {
    const preview = await api(`/api/books/${encodeURIComponent(row.id)}/cbz-replacement/preview`, { ...options, method: "POST" });
    if (!current()) return;
    if (!preview?.revision || !preview.source || !preview.destination || preview.converted !== true || !Array.isArray(preview.warnings))
      throw new Error("The server did not return a complete conversion preview");
    showIntakePreview(preview);
  });
});

$("intake-confirm").addEventListener("click", () => {
  const preview = intake?.preview;
  if (!preview || $("intake-confirm").disabled) return;
  intakeRequest("Processing comicbook. Do not close this page; this cannot be cancelled or undone…", async (options, current, state, row) => {
    state.replacementAttempt = preview;
    state.processingStage = "Processing comicbook…";
    updateIntakeControls();
    const result = await api(`/api/books/${encodeURIComponent(row.id)}/cbz-replacement`, {
      method: "POST", body: JSON.stringify({ expectedRevision: preview.revision, confirmed: true })
    });
    if (!current()) return;
    if (result?.state !== "completed" || !result.converted || result.source !== preview.source || result.destination !== preview.destination)
      throw new Error("Processing completion was not confirmed");
    setIntakeProcessing(row, result);
    $("intake-status").textContent = row.outcome;
  }, true);
});
$("intake-discard").addEventListener("click", () => {
  if (intake?.saving) return;
  cancelIntakeRequest();
  $("intake-status").textContent = "Review cancelled. Nothing was saved.";
  ($("intake-form").hidden ? $("apply-cbz-replacement") : $("intake-match")).focus();
});

for (const trigger of ["process-unsorted", "process-selected"]) $(trigger).addEventListener("click", () => {
  if (session?.role !== "admin" || deletionBusy || (trigger === "process-selected" ? !selectedBooks.size : catalogLocation !== "unsorted")) return;
  const selected = selectedBooks.size > 0;
  const ids = selected ? [...selectedBooks] : libraryItems.map((book) => book.id);
  if (!ids.length || ids.length > 100) {
    $("library-status").textContent = "Choose Stack by: None or open a stack, then select up to 100 comicbooks. With no selection, Process Unsorted uses only the current page.";
    $("library-status").focus();
    return;
  }
  openIntake(ids.map((id) => ({ id, title: bookTitle(libraryItems.find((book) => book.id === id) || { title: id }) })), selected
    ? `${ids.length} selected comicbook(s), across library pages. Processing status is checked even when a scan marks a source unavailable.`
    : `No selection: ${ids.length} comicbook(s) on the current page only (page ${libraryPage}); not the entire Unsorted intake.`);
});
$("intake-queue").addEventListener("change", showIntakeRow);
$("intake-refresh").addEventListener("click", () => {
  if (intake?.metadataOnly) { intake.row.hints = null; intake.row.candidates = []; }
  showIntakeRow();
});
$("intake-root").addEventListener("change", () => {
  cancelIntakeRequest();
  $("intake-status").textContent = "Destination changed. Accept a candidate to process it in the selected destination.";
});
$("intake-recover").addEventListener("click", () => {
  if (!intake?.row?.processing?.canRecover) return;
  intakeRequest("Recovering interrupted processing. Do not close this page…", async (options, current, state, row) => {
    const result = await api(`/api/books/${encodeURIComponent(row.id)}/intake-processing/recover`, {
      method: "POST", body: JSON.stringify({ expectedRevision: row.processing.revision, confirmed: true })
    });
    if (!current()) return;
    setIntakeProcessing(row, result);
    $("intake-status").textContent = row.outcome;
    loadLibrary();
    loadPublishers();
    loadLists();
  }, true);
});
$("intake-cancel").addEventListener("click", () => {
  cancelIntakeRequest();
  $("intake-status").textContent = "Request cancelled. Nothing saved. Match again to retry.";
  ($("intake-match").disabled ? $("intake-refresh") : $("intake-match")).focus();
});
$("intake-hints").addEventListener("input", () => {
  const row = intake?.row;
  if (!row) return;
  invalidateIntakePreview();
  row.hints = Object.fromEntries(["series", "number", "year", "volume", "publisher"].map((name) => {
    const value = $(`intake-${name}`).value.trim();
    return [name, name === "year" || name === "volume" ? value ? Number(value) : null : value];
  }));
  row.candidates = [];
  renderIntakeCandidates();
  $("intake-status").textContent = "Hints changed. Match again; saved catalog metadata is unchanged.";
});
$("intake-form").addEventListener("submit", (event) => {
  event.preventDefault();
  if (!$("intake-form").reportValidity()) return;
  intakeRequest("Searching and ranking Comic Vine issues…", async (options, current, state, row) => {
    const result = await api(`/api/books/${encodeURIComponent(row.id)}/${state.metadataOnly ? "metadata-matches" : "intake"}/matches`, { ...options, method: "POST", body: JSON.stringify(row.hints) });
    if (!current()) return;
    row.candidates = result.candidates;
    renderIntakeCandidates();
    $("intake-status").textContent = [result.candidates.length ? `${result.candidates.length} candidate(s). Compare the run and issue; nothing is selected automatically.`
      : "No candidates. Edit the hints and retry.", ...(result.warnings || [])].join("\n");
  });
});
$("intake-dialog").addEventListener("cancel", (event) => { if (!canCloseDialog("intake-dialog")) event.preventDefault(); });
$("intake-dialog").addEventListener("close", () => {
  if ($("intake-dialog").open) return;
  const replacement = intake?.replacement && intake.epoch === authEpoch && intake.details === detailGeneration;
  resetIntake();
  if (replacement && $("details-dialog").open)
    ($("convert-cbz").hidden ? $("intake-history").hidden ? $("read-book") : $("intake-history") : $("convert-cbz")).focus({ preventScroll: true });
});
for (const id of ["details-dialog", "organization-dialog", "file-operation-dialog", "archive-dialog"]) $(id).addEventListener("close", () => {
  if (!intakeCurrent(intake)) return;
  updateIntakeControls();
  if (![...document.querySelectorAll("dialog[open]")].some((dialog) => dialog.id !== "intake-dialog"))
    (intake.metadataOnly ? $("intake-title") : $("intake-queue")).focus();
});

$("find-metadata").addEventListener("click", () => {
  if (session?.role !== "admin" || !detailBook || !$("details-dialog").open || intake) return;
  openIntake([{ id: detailBook.id, title: bookTitle(detailBook) }],
    "Accept metadata updates the catalog only. It does not write, convert or move the comicbook.", "metadata");
});

function leaveMetadata() {
  return $("metadata-view").hidden || (!metadataBusy && (!metadataDirty || confirm("Discard unsaved metadata changes?")));
}

function resetMetadataEditor() {
  metadataGeneration++;
  metadataDirty = metadataBusy = false;
  metadataBookIds = [];
  metadataPage = null;
  $("metadata-fields").replaceChildren();
  $("custom-values").replaceChildren();
  invalidateMetadataPreview();
}

function openMetadata(ids, bulk = false) {
  if (session?.role !== "admin" || !ids.length || ids.length > 100 || !leaveMetadata()) return;
  const dialogs = [...document.querySelectorAll("dialog[open]")];
  if (dialogs.some(dialog => !canCloseDialog(dialog.id))) return;
  const origin = { hash: location.hash, bookId: $("details-dialog").open ? detailBook?.id : null,
    report: $("metadata-report-dialog").open, selection: [...selectedBooks] };
  const hash = `#metadata/${ids.map(encodeURIComponent).join(",")}${bulk ? "?bulk=1" : ""}`;
  cancelIntakeRequest();
  metadataPage = { hash, origin };
  for (const dialog of dialogs) dialog.close();
  history.pushState({ returnCatalog: catalogReturn, metadataOrigin: origin }, "", hash);
  scheduleRoute();
}

async function restoreMetadataOrigin(hash, generation) {
  const context = metadataReturn;
  metadataReturn = null;
  if (!context || context.origin.hash !== hash || generation !== routeGeneration) return;
  if (context.origin.report) { $("metadata-report-dialog").showModal(); loadMetadataReport(); }
  if (context.origin.bookId) {
    await openDetails(context.origin.bookId);
    if (generation === routeGeneration && $("details-dialog").open) $("edit-metadata").focus();
  } else if (!$("library-view").hidden && context.origin.selection?.length) {
    for (const id of context.origin.selection) selectedBooks.add(id);
    updateSelection();
    $("bulk-metadata").focus();
  }
}

$("metadata-back").addEventListener("click", () => {
  location.hash = metadataPage?.origin.hash || (catalogLocation === "unsorted" ? "#unsorted" : "#library");
});

async function loadMetadataEditor(ids, bulk = false) {
  const epoch = authEpoch;
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
  $("metadata-field-search").value = "";
  $("metadata-changed-only").checked = false;
  $("metadata-change-summary").textContent = "";
  $("metadata-controls").disabled = true;
  $("save-metadata").disabled = true;
  showView("metadata");
  $("metadata-title").focus();
  window.scrollTo(0, 0);
  try {
    const [schema, metadata] = await Promise.all([loadMetadataSchema(), ids.length === 1 ? api(`/api/books/${encodeURIComponent(ids[0])}/metadata`) : Promise.resolve({ fields: {}, custom: {} })]);
    if (epoch !== authEpoch || generation !== metadataGeneration || $("metadata-view").hidden) return;
    const groups = [
      ["Common fields", ["Title", "Series", "Number", "Year", "Publisher", "Summary"]],
      ["Series and issue", ["Count", "Volume", "AlternateSeries", "AlternateNumber", "AlternateCount", "StoryArc", "SeriesGroup"]],
      ["Creators", ["Writer", "Penciller", "Inker", "Colorist", "Letterer", "CoverArtist", "Editor", "Translator"]],
      ["Publication and format", ["Month", "Day", "Imprint", "Web", "PageCount", "LanguageISO", "Format", "AgeRating", "BlackAndWhite", "Manga", "PreferredFrontCover"]],
      ["Story and classification", ["Genre", "Characters", "Teams", "MainCharacterOrTeam", "Locations", "Tags"]],
      ["Notes and ratings", []]
    ].map(([title, names]) => {
      const section = textElement("section", "", "metadata-field-group");
      section.append(textElement("h2", title));
      $("metadata-fields").append(section);
      return { names, section };
    });
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
      const row = metadataChangeRow(field.name, input, false, value, field.label);
      (groups.find((group) => group.names.includes(field.name)) || groups[groups.length - 1]).section.append(row);
    }
    for (const [name, value] of Object.entries(metadata.custom)) addCustomValue(name, value);
    $("metadata-controls").disabled = false;
    $("save-metadata").disabled = false;
    $("metadata-status").textContent = bulk ? "Unchecked fields and custom keys remain unchanged. Preview your checked changes before applying them." : "Check each field or custom value you want to change.";
    filterMetadataFields();
  } catch (error) { if (generation === metadataGeneration) $("metadata-status").textContent = `Could not load metadata: ${error.message}. Go back and try again.`; }
}

function collectMetadataChanges() {
  const fields = Object.create(null);
  const custom = Object.create(null);
  for (const row of $("metadata-controls").querySelectorAll(".metadata-change")) {
    if (!row.querySelector(".apply-change").checked) continue;
    const isCustom = row.classList.contains("custom-value");
    const name = isCustom ? row.querySelector(".custom-name").value : row.dataset.field;
    const values = isCustom ? custom : fields;
    const error = isCustom && (!name.trim() || /[\u0000-\u001f\u007f]/u.test(name) || name.includes("&#61;") || name.includes("&#44;"))
      ? "Custom names cannot be blank, contain control characters, or contain &#61; or &#44;."
      : Object.hasOwn(values, name) ? `Custom value “${name}” is selected more than once.` : "";
    if (error) {
      row.hidden = false;
      const group = row.closest(".metadata-field-group, .custom-values-group");
      if (group) group.hidden = false;
      const input = row.querySelector(".custom-name");
      input.setCustomValidity(error);
      input.reportValidity();
      throw new Error(error);
    }
    values[name] = row.querySelector(".change-action").value === "remove" ? null : row.querySelector(".metadata-value").value;
  }
  return { fields, custom };
}

function updateArchiveControls() {
  const available = session?.role === "admin" && archiveBookId !== null;
  $("apply-archive").disabled = !available || archiveBusy;
  $("archive-dialog").querySelector("[data-close]").disabled = archiveBusy;
}

function invalidateArchivePreview() {
  archivePreview = null;
  updateArchiveControls();
}

function resetArchiveWrite() {
  archiveGeneration++;
  archiveBookId = null;
  archiveBusy = false;
  invalidateArchivePreview();
  for (const id of ["archive-book", "archive-status"]) $(id).textContent = "";
}

async function previewArchiveWrite() {
  if (session?.role !== "admin" || !archiveBookId || archiveBusy) return;
  invalidateArchivePreview();
  if ((metadataBookIds.includes(archiveBookId) && (metadataDirty || metadataBusy)) || (pageMetadata?.bookId === archiveBookId && (pageMetadataDirty || pageMetadataBusy))) {
    $("archive-status").textContent = "Save or discard the unsaved catalog metadata draft first. Comicbook write-back uses saved metadata only.";
    return;
  }
  const generation = archiveGeneration;
  const epoch = authEpoch;
  archiveBusy = true;
  updateArchiveControls();
  $("archive-status").textContent = "Checking the source and saved metadata. Nothing is being written…";
  try {
    const preview = await api(`/api/books/${encodeURIComponent(archiveBookId)}/archive-write/preview`, { method: "POST" });
    if (epoch !== authEpoch || generation !== archiveGeneration) return;
    if ((metadataBookIds.includes(archiveBookId) && (metadataDirty || metadataBusy)) || (pageMetadata?.bookId === archiveBookId && (pageMetadataDirty || pageMetadataBusy)))
      throw new Error("Catalog draft changed. Save or discard it before trying again.");
    archivePreview = preview;
    $("archive-status").textContent = preview.canApply ? "Source checked. Writing saved metadata…" : "No comicbook changes to apply.";
  } catch (error) {
    if (epoch === authEpoch && generation === archiveGeneration) {
      invalidateArchivePreview();
      $("archive-status").textContent = `Source check failed: ${error.message}. No comicbook operation was applied. Resolve the problem, then try again.`;
    }
  } finally {
    if (epoch === authEpoch && generation === archiveGeneration) {
      archiveBusy = false;
      updateArchiveControls();
    }
  }
}

$("write-archive").addEventListener("click", () => {
  if (session?.role !== "admin" || !detailBook || archiveBusy) return;
  resetArchiveWrite();
  archiveBookId = detailBook.id;
  $("archive-book").textContent = `${bookTitle(detailBook)}: ${detailBook.location}`;
  updateArchiveControls();
  $("archive-dialog").showModal();
});
$("archive-dialog").addEventListener("close", () => { if (!$("archive-dialog").open) resetArchiveWrite(); });
$("archive-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (session?.role !== "admin" || !archiveBookId || archiveBusy) return;
  const generation = archiveGeneration;
  const epoch = authEpoch;
  const id = archiveBookId;
  await previewArchiveWrite();
  if (epoch !== authEpoch || generation !== archiveGeneration || id !== archiveBookId || archiveBusy) return;
  if (!archivePreview?.canApply || archivePreview.bookId !== id) return;
  if ((metadataBookIds.includes(archiveBookId) && (metadataDirty || metadataBusy)) || (pageMetadata?.bookId === archiveBookId && (pageMetadataDirty || pageMetadataBusy))) {
    invalidateArchivePreview();
    $("archive-status").textContent = "Catalog draft changed. Save or discard it before trying again.";
    return;
  }
  const detailsGeneration = detailGeneration;
  const preview = archivePreview;
  archiveBusy = true;
  invalidateArchivePreview();
  $("archive-status").textContent = "Writing saved metadata. Do not close this page…";
  try {
    await api(`/api/books/${encodeURIComponent(id)}/archive-write`, { method: "POST", body: JSON.stringify({ expectedRevision: preview.revision, confirmed: true }) });
    if (epoch !== authEpoch || generation !== archiveGeneration) return;
    $("archive-status").textContent = "Metadata written to the comicbook. Private reading data is unchanged.";
    invalidateReaderNextIssue(id);
    try {
      await refreshReaderSource(id);
    } catch (error) {
      if (epoch === authEpoch && generation === archiveGeneration)
        $("archive-status").textContent += ` Reader refresh failed: ${error.message}. Close and reopen the reader before continuing.`;
    }
    if (epoch !== authEpoch || generation !== archiveGeneration) return;
    failedCovers.delete(id);
    loadLibrary();
    loadLists();
    loadPublishers();
    if ($("maintenance-dialog").open) loadMaintenance();
    if ($("metadata-report-dialog").open) loadMetadataReport();
    if ($("duplicate-dialog").open) loadDuplicateComparison();
    if ($("details-dialog").open && detailBook?.id === id) {
      try {
        const [book, metadata, schema] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}`), api(`/api/books/${encodeURIComponent(id)}/metadata`), loadMetadataSchema()]);
        if (epoch === authEpoch && detailsGeneration === detailGeneration) { detailBook = book; renderDetails(book, metadata, schema); }
      } catch (error) {
        if (epoch === authEpoch && detailsGeneration === detailGeneration)
          $("details-status").textContent = `Comicbook operation succeeded, but details could not refresh: ${error.message}. Close and reopen Book details.`;
      }
    }
  } catch (error) {
    if (epoch === authEpoch && generation === archiveGeneration)
      $("archive-status").textContent = error.status === 409
        ? `${error.message} Source checks are no longer valid. Check the source and catalog status before trying again.`
        : `Could not confirm the comicbook operation: ${error.message}. It may have completed; inspect the source and rescan before trying again. Nothing will be retried automatically.`;
  } finally {
    if (epoch === authEpoch && generation === archiveGeneration) {
      archiveBusy = false;
      updateArchiveControls();
    }
  }
});

function updateFileOperationControls() {
  const available = session?.role === "admin" && fileOperationBookId !== null;
  $("file-operation-controls").disabled = $("preview-file-operation").disabled = !available || fileOperationBusy || !$("file-operation-root").options.length;
  $("refresh-file-operation-recovery").disabled = !available || fileOperationBusy;
  $("apply-file-operation").disabled = !available || fileOperationBusy || !fileOperationPreview?.canApply;
  for (const button of $("file-operation-recovery").querySelectorAll("button")) button.disabled = !available || fileOperationBusy;
  $("file-operation-dialog").querySelector("[data-close]").disabled = fileOperationBusy;
}

function invalidateFileOperationPreview() {
  fileOperationDraftGeneration++;
  fileOperationPreview = null;
  $("file-operation-preview").hidden = true;
  $("file-operation-conflicts").replaceChildren();
  for (const id of ["file-operation-source", "file-operation-destination", "file-operation-hash"]) $(id).textContent = "";
  $("apply-file-operation").textContent = "Apply reviewed move";
  updateFileOperationControls();
}

function resetFileOperation() {
  fileOperationGeneration++;
  fileOperationBookId = null;
  fileOperationBusy = false;
  fileOperationExtension = "";
  $("file-operation-draft").reset();
  $("file-operation-name").setCustomValidity("");
  $("file-operation-root").replaceChildren();
  $("file-operation-recovery").replaceChildren();
  for (const id of ["file-operation-book", "file-operation-status", "file-operation-recovery-status", "file-operation-extension"]) $(id).textContent = "";
  invalidateFileOperationPreview();
}

function fileOperationDraft() {
  return { rootIndex: Number($("file-operation-root").value), folder: "", fileName: $("file-operation-name").value };
}

function organizationDraft() {
  return JSON.stringify({ root: $("organization-root").value, folder: $("organization-folder").value,
    name: $("organization-name").value, rows: organizationRows });
}

function fileOperationDirty() {
  return JSON.stringify(fileOperationDraft()) !== fileOperationInitialDraft;
}

function organizationDirty() {
  return organizationDraft() !== organizationInitialDraft;
}

async function refreshFileOperationRecovery() {
  if (session?.role !== "admin" || !fileOperationBookId || fileOperationBusy) return;
  const generation = fileOperationGeneration;
  const epoch = authEpoch;
  fileOperationBusy = true;
  if (fileOperationPreview) $("file-operation-status").textContent = "Checking pending recovery cleared the confirmation. Preview again before applying.";
  invalidateFileOperationPreview();
  $("file-operation-recovery").replaceChildren();
  $("file-operation-recovery-status").textContent = "Checking for interrupted moves…";
  try {
    const operations = await api(`/api/books/${encodeURIComponent(fileOperationBookId)}/file-operations`);
    if (epoch !== authEpoch || generation !== fileOperationGeneration) return;
    for (const operation of operations) {
      const row = textElement("article", "", "maintenance-row");
      row.append(textElement("h4", `${operation.state}, ${new Date(operation.createdAt).toLocaleString()}`),
        textElement("p", `Operation ${operation.id}`),
        textElement("p", `From: ${operation.source}`), textElement("p", `To: ${operation.destination}`),
        textElement("p", `SHA-256: ${operation.hash}`, "duplicate-key"));
      if (operation.message) row.append(textElement("p", operation.message));
      row.append(textElement("p", "Pending recovery: do not move, remove, or overwrite either path yourself. Review recovery below; if blocked, preserve all files and resolve the reported conflict before trying a new preview.", "book-availability"));
      if (operation.canRecover) {
        const button = textElement("button", "Preview pending recovery");
        button.type = "button";
        button.dataset.operationId = operation.id;
        button.dataset.operation = "recover";
        button.setAttribute("aria-label", `Preview recovery for operation ${operation.id}`);
        button.addEventListener("click", () => previewFileOperation("recover", operation.id));
        row.append(button);
      }
      $("file-operation-recovery").append(row);
    }
    $("file-operation-recovery-status").textContent = operations.length
      ? `${operations.length} interrupted move(s) need recovery. Preview and confirm each recovery separately.`
      : "No pending move recovery. Completed moves are not retained; check the comicbook's current location if an outcome was uncertain.";
  } catch (error) {
    if (epoch === authEpoch && generation === fileOperationGeneration)
      $("file-operation-recovery-status").textContent = `Could not check pending recovery: ${error.message}. Use Check pending recovery to try again. Do not assume an uncertain operation failed.`;
  } finally {
    if (epoch === authEpoch && generation === fileOperationGeneration) {
      fileOperationBusy = false;
      updateFileOperationControls();
    }
  }
}

async function previewFileOperation(operation = "move", operationId = null) {
  if (session?.role !== "admin" || !fileOperationBookId || fileOperationBusy) return;
  invalidateFileOperationPreview();
  const draft = fileOperationDraft();
  if (operation === "move") {
    const invalidName = /[/\\\u0000-\u001f\u007f]/u.test(draft.fileName) || (fileOperationExtension && !draft.fileName.endsWith(fileOperationExtension));
    $("file-operation-name").setCustomValidity(invalidName ? `Use a single filename with the unchanged ${fileOperationExtension || "comicbook"} extension, without path separators or control characters.` : "");
    if (!$("file-operation-draft").reportValidity()) return;
  }
  const generation = fileOperationGeneration;
  const draftGeneration = fileOperationDraftGeneration;
  const epoch = authEpoch;
  fileOperationBusy = true;
  updateFileOperationControls();
  $("file-operation-status").textContent = "Checking paths and comicbook bytes; move previews also create missing destination folders. No comicbook is being moved…";
  try {
    const path = `/api/books/${encodeURIComponent(fileOperationBookId)}/file-operations${operation === "move" ? "" : `/${encodeURIComponent(operationId)}/${operation}`}/preview`;
    const preview = await api(path, { method: "POST", ...(operation === "move" ? { body: JSON.stringify(draft) } : {}) });
    if (epoch !== authEpoch || generation !== fileOperationGeneration || draftGeneration !== fileOperationDraftGeneration) return;
    if (preview.bookId !== fileOperationBookId || preview.operation !== operation || (operationId !== null && preview.operationId !== operationId) || !preview.revision)
      throw new Error("The preview did not match this book and operation.");
    fileOperationPreview = { ...preview, draft, canApply: preview.canApply === true && !preview.conflicts.length };
    $("file-operation-preview-title").textContent = operation === "recover" ? "Review pending recovery" : "Review exact move";
    $("file-operation-source").textContent = preview.source;
    $("file-operation-destination").textContent = preview.destination;
    $("file-operation-hash").textContent = preview.sourceHash;
    for (const conflict of preview.conflicts) $("file-operation-conflicts").append(textElement("li", conflict));
    $("apply-file-operation").textContent = operation === "recover" ? "Apply reviewed recovery" : "Apply reviewed move";
    $("file-operation-preview").hidden = false;
    $("file-operation-preview").focus();
    $("file-operation-status").textContent = [preview.message, fileOperationPreview.canApply
      ? "No comicbook has moved. Review the paths and hash, then use the Apply button to confirm. A changed source, catalog, or destination requires a new preview."
      : preview.conflicts.length ? "This operation is blocked. Resolve the conflicts and preview again; apply is disabled." : "No change to apply. The comicbook remains where it is; apply is disabled."].filter(Boolean).join("\n");
  } catch (error) {
    if (epoch === authEpoch && generation === fileOperationGeneration && draftGeneration === fileOperationDraftGeneration) {
      invalidateFileOperationPreview();
      $("file-operation-status").textContent = `Preview unavailable: ${error.message}. No operation was applied. Resolve the problem, then preview again.`;
    }
  } finally {
    if (epoch === authEpoch && generation === fileOperationGeneration) {
      fileOperationBusy = false;
      updateFileOperationControls();
    }
  }
}

function updateOrganizationControls() {
  const selected = organizationBookIds.length > 0 && organizationBookIds.length <= 100 &&
    organizationBookIds.length === selectedBooks.size && organizationBookIds.every((id) => selectedBooks.has(id));
  const available = session?.role === "admin" && selected && !organizationBusy;
  $("organization-controls").disabled = $("generate-organization").disabled = !available || !$("organization-root").options.length;
  $("apply-organization").disabled = !available || !organizationPreview?.canApply;
  for (const button of $("organization-recovery").querySelectorAll("button")) button.disabled = organizationBusy;
  $("organization-dialog").querySelector("[data-close]").disabled = organizationBusy;
}

function invalidateOrganizationPreview(clearRows = false) {
  organizationDraftGeneration++;
  organizationPreview = null;
  if (clearRows) {
    organizationRows = [];
    $("organization-rows").replaceChildren();
    $("organization-review").hidden = true;
  }
  updateOrganizationControls();
}

function resetOrganization() {
  organizationGeneration++;
  organizationBusy = false;
  organizationBookIds = [];
  $("organization-draft").reset();
  $("organization-root").replaceChildren();
  $("organization-recovery").replaceChildren();
  $("organization-outcome").replaceChildren();
  $("organization-result").hidden = true;
  $("organization-selection").textContent = $("organization-status").textContent = "";
  invalidateOrganizationPreview(true);
}

async function previewOrganization() {
  if (session?.role !== "admin" || organizationBusy || !organizationBookIds.length ||
      organizationBookIds.length !== selectedBooks.size || organizationBookIds.some((id) => !selectedBooks.has(id))) return;
  if (!$("organization-draft").reportValidity()) return;
  invalidateOrganizationPreview();
  const generation = organizationGeneration;
  const draftGeneration = organizationDraftGeneration;
  const epoch = authEpoch;
  const body = {
    bookIds: [...organizationBookIds], rootIndex: Number($("organization-root").value),
    folderTemplate: $("organization-folder").value, fileNameTemplate: $("organization-name").value
  };
  organizationBusy = true;
  updateOrganizationControls();
  $("organization-status").textContent = "Creating missing destination folders and checking every source hash. No comicbooks are being moved…";
  try {
    const preview = await api("/api/file-operations/template-preview", { method: "POST", body: JSON.stringify(body) });
    if (epoch !== authEpoch || generation !== organizationGeneration || draftGeneration !== organizationDraftGeneration) return;
    if (!preview.revision || !Array.isArray(preview.items) || preview.items.length !== organizationBookIds.length ||
        new Set(preview.items.map((row) => row.bookId)).size !== organizationBookIds.length ||
        preview.items.some((row) => !organizationBookIds.includes(row.bookId) || !Array.isArray(row.conflicts)))
      throw new Error("The preview did not match every selected book.");
    organizationRows = preview.items.map(({ bookId, rootIndex, folder, fileName }) => ({ bookId, rootIndex, folder, fileName }));
    organizationPreview = { ...preview, canApply: preview.canApply === true &&
      preview.items.every((row) => !row.conflicts.length && (row.canApply || row.noOp)) && preview.items.some((row) => !row.noOp) };
    $("organization-rows").replaceChildren();
    preview.items.forEach((item) => {
      const row = textElement("article", "", "maintenance-row organization-row");
      row.append(textElement("h4", item.source || `Book ${item.bookId}`));
      const recoveryButton = $("organization-recovery").querySelector(`[data-book-id="${CSS.escape(item.bookId)}"]`);
      if (recoveryButton) recoveryButton.textContent = `Check pending recovery: ${item.source || item.bookId}`;
      const paths = document.createElement("dl");
      for (const [label, value] of [["From", item.source], ["To", item.destination], ["Source SHA-256", item.sourceHash]])
        paths.append(textElement("dt", label), textElement("dd", value || "Unavailable — see conflicts"));
      row.append(paths);
      const conflicts = document.createElement("ul");
      conflicts.className = "scan-error";
      for (const conflict of item.conflicts) conflicts.append(textElement("li", conflict));
      row.append(conflicts);
      if (item.conflicts.length || item.noOp)
        row.append(textElement("p", item.conflicts.length ? "Blocked — resolve conflicts and preview again." : "No change — this book will be skipped."));
      $("organization-rows").append(row);
    });
    $("organization-review").hidden = false;
    $("organization-review").focus();
    $("organization-status").textContent = organizationPreview.canApply
      ? "Review every destination, then choose Move. Changing the destination or templates requires a new preview."
      : preview.items.some((row) => row.conflicts.length)
        ? "The entire batch is blocked by row conflicts. Edit the destination or templates and preview again."
        : "There are no applicable moves. All unchanged rows are skipped; apply is disabled.";
  } catch (error) {
    if (epoch === authEpoch && generation === organizationGeneration && draftGeneration === organizationDraftGeneration) {
      invalidateOrganizationPreview();
      $("organization-status").textContent = `Preview unavailable: ${error.message}. Nothing was applied. Resolve the problem and preview again.`;
    }
  } finally {
    if (epoch === authEpoch && generation === organizationGeneration) {
      organizationBusy = false;
      updateOrganizationControls();
    }
  }
}

$("organize-selected").addEventListener("click", async () => {
  if (session?.role !== "admin" || organizationBusy) return;
  if (!selectedBooks.size || selectedBooks.size > 100) {
    $("management-status").textContent = "Select between 1 and 100 books across pages to organize.";
    return;
  }
  resetOrganization();
  organizationBookIds = [...selectedBooks];
  $("organization-selection").textContent = `${organizationBookIds.length} of at most 100 books selected across library pages.`;
  for (const id of organizationBookIds) {
    const button = textElement("button", `Check pending recovery: ${id}`);
    button.type = "button";
    button.dataset.bookId = id;
    button.addEventListener("click", () => {
      invalidateOrganizationPreview();
      $("organization-status").textContent = "Checking pending recovery cleared this batch confirmation. Preview the batch again after recovery.";
      openFileOperation({ id, title: `Book ${id}`, location: "" });
    });
    $("organization-recovery").append(button);
  }
  organizationBusy = true;
  updateOrganizationControls();
  $("organization-status").textContent = "Loading configured destination roots…";
  $("organization-dialog").showModal();
  const generation = organizationGeneration;
  const epoch = authEpoch;
  try {
    const roots = await api("/api/file-operation-roots");
    if (epoch !== authEpoch || generation !== organizationGeneration) return;
    $("organization-root").replaceChildren(...roots.map((root) => new Option(root.label, root.index)));
    $("organization-status").textContent = roots.length ? "Choose a root and templates, then generate the preview. No files have changed."
      : "No configured destination roots are available. Pending recovery is still accessible below.";
  } catch (error) {
    if (epoch === authEpoch && generation === organizationGeneration)
      $("organization-status").textContent = `Could not load roots: ${error.message}. Close and reopen to try again. Pending recovery is still accessible below.`;
    return;
  } finally {
    if (epoch === authEpoch && generation === organizationGeneration) {
      organizationInitialDraft = organizationDraft();
      organizationBusy = false;
      updateOrganizationControls();
    }
  }
});
$("archive-selected").addEventListener("click", async () => {
  if (session?.role !== "admin" || session.mustChangePassword || organizationBusy || !selectedBooks.size || selectedBooks.size > 100) return;
  const bookIds = [...selectedBooks];
  const epoch = authEpoch;
  let applying = false;
  organizationBusy = true;
  updateSelection();
  $("management-status").textContent = `Preparing to archive ${bookIds.length} selected comicbooks…`;
  try {
    const roots = await api("/api/file-operation-roots");
    if (epoch !== authEpoch) return;
    const archived = roots.find((root) => root.label === "Archived");
    if (!archived) throw new Error("Configure an Archived folder in Server settings and restart before archiving.");
    const preview = await api("/api/file-operations/template-preview", { method: "POST", body: JSON.stringify({
      bookIds, rootIndex: archived.index, folderTemplate: $("organization-folder").defaultValue,
      fileNameTemplate: $("organization-name").defaultValue
    }) });
    if (epoch !== authEpoch) return;
    if (!preview.revision || !Array.isArray(preview.items) || preview.items.length !== bookIds.length ||
        new Set(preview.items.map((row) => row.bookId)).size !== bookIds.length ||
        preview.items.some((row) => !bookIds.includes(row.bookId) || row.rootIndex !== archived.index || !Array.isArray(row.conflicts)))
      throw new Error("The archive preview did not match the selected comicbooks and destination.");
    if (preview.canApply !== true || preview.items.some((row) => row.conflicts.length || (!row.canApply && !row.noOp)) ||
        preview.items.every((row) => row.noOp)) {
      const conflicts = preview.items.flatMap((row) => row.conflicts.map((message) => `${row.source}: ${message}`));
      $("management-status").textContent = conflicts.length
        ? `Archive blocked. ${conflicts.join(" ")} No comicbooks were moved.`
        : "No applicable archive moves. Unchanged comicbooks were skipped.";
      return;
    }
    const items = preview.items.map(({ bookId, rootIndex, folder, fileName }) => ({ bookId, rootIndex, folder, fileName }));
    applying = true;
    $("management-status").textContent = `Archiving ${bookIds.length} selected comicbooks…`;
    const result = await api("/api/file-operations", { method: "POST",
      body: JSON.stringify({ items, expectedRevision: preview.revision, confirmed: true }) });
    if (epoch !== authEpoch) return;
    $("management-status").textContent = `${result.completed.length} archived; ${result.skipped.length} unchanged.` +
      (result.failedBookId
        ? ` Stopped at ${preview.items.find((row) => row.bookId === result.failedBookId)?.source || result.failedBookId}: ${result.error} ${result.notAttempted.length} not attempted. Completed moves were kept. Check pending recovery in Book details → Move / reorganize before trying again.`
        : "");
  } catch (error) {
    if (epoch === authEpoch) $("management-status").textContent = !applying || error.status === 409
      ? `Archive stopped: ${error.message} No comicbooks were moved.`
      : `Archive outcome unconfirmed: ${error.message} Some moves may have completed. Check current locations and pending recovery in Book details → Move / reorganize. Nothing will be retried automatically.`;
  } finally {
    if (epoch === authEpoch) {
      organizationBusy = false;
      updateSelection();
      if (applying) {
        for (const id of bookIds) { failedCovers.delete(id); invalidateReaderNextIssue(id); }
        loadLists();
        loadPublishers();
        loadLibrary();
      }
    }
  }
});
$("organization-dialog").addEventListener("close", () => { if (!$("organization-dialog").open) resetOrganization(); });
for (const name of ["input", "change"]) $("organization-controls").addEventListener(name, () => {
  invalidateOrganizationPreview(true);
  $("organization-status").textContent = "Destination or template changed. Generate a new template preview.";
});
$("organization-draft").addEventListener("submit", (event) => { event.preventDefault(); previewOrganization(); });
$("organization-apply-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (session?.role !== "admin" || organizationBusy) return;
  const preview = organizationPreview;
  const items = organizationRows.map((row) => ({ ...row }));
  if (!preview?.canApply || organizationBookIds.length !== selectedBooks.size ||
      organizationBookIds.some((id) => !selectedBooks.has(id)) || JSON.stringify(items) !== JSON.stringify(
        preview.items.map(({ bookId, rootIndex, folder, fileName }) => ({ bookId, rootIndex, folder, fileName })))) {
    invalidateOrganizationPreview();
    $("organization-status").textContent = "Preview every destination before choosing Move.";
    return;
  }
  const generation = organizationGeneration;
  const detailsGeneration = detailGeneration;
  const epoch = authEpoch;
  organizationBusy = true;
  invalidateOrganizationPreview();
  $("organization-outcome").replaceChildren();
  $("organization-result").hidden = true;
  $("organization-status").textContent = "Applying sequential moves. Do not close this page or change these files. Earlier moves remain completed if a later one fails…";
  try {
    const result = await api("/api/file-operations", { method: "POST", body: JSON.stringify({ items, expectedRevision: preview.revision, confirmed: true }) });
    if (epoch !== authEpoch || generation !== organizationGeneration) return;
    for (const [label, entries] of [
      ["Completed", result.completed.map((item) => `${item.bookId}, operation ${item.operationId}`)],
      ["Skipped (unchanged)", result.skipped],
      ["Failed", result.failedBookId ? [`${result.failedBookId}: ${result.error || "Execution failed"}`] : []],
      ["Not attempted", result.notAttempted]
    ]) {
      $("organization-outcome").append(textElement("h4", `${label} (${entries.length})`));
      const list = document.createElement("ul");
      for (const entry of entries) list.append(textElement("li", entry));
      $("organization-outcome").append(entries.length ? list : textElement("p", "None"));
    }
    $("organization-status").textContent = result.failedBookId
      ? "Batch stopped at the first failure. Completed moves were kept. A book may appear as both completed and failed if cleanup needs recovery. Check pending recovery below before a new preview; nothing will be retried automatically."
      : "Batch completed. Unchanged books were skipped. Book identity and private reading progress were kept.";
    $("organization-result").hidden = false;
    $("organization-result").focus();
    if (!result.failedBookId) organizationInitialDraft = organizationDraft();
  } catch (error) {
    if (epoch === authEpoch && generation === organizationGeneration) {
      const message = error.status === 409
        ? `${error.message} Revalidation rejected this batch before execution. Resolve conflicts and preview again.`
        : `Could not confirm the batch outcome: ${error.message}. Some moves may have completed. Check pending recovery for every book below, and check each comicbook's current location before proceeding.`;
      $("organization-status").textContent = `${message} Nothing will be retried automatically.`;
      $("organization-outcome").append(textElement("p", error.status === 409
        ? "No moves were executed by this request. The preview is no longer valid."
        : "Completed, skipped, failed, and not-attempted counts are unknown. Only interrupted moves are retained for recovery; completed moves are not retained."));
      $("organization-result").hidden = false;
      $("organization-result").focus();
    }
  } finally {
    if (epoch === authEpoch && generation === organizationGeneration) {
      organizationBusy = false;
      updateOrganizationControls();
      for (const item of items) failedCovers.delete(item.bookId);
      for (const item of items) invalidateReaderNextIssue(item.bookId);
      loadLists();
      loadPublishers();
      loadLibrary();
      if ($("maintenance-dialog").open) loadMaintenance();
      if ($("duplicate-dialog").open) loadDuplicateComparison();
      const id = detailBook?.id;
      if ($("details-dialog").open && items.some((item) => item.bookId === id)) {
        try {
          const [book, metadata, schema] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}`), api(`/api/books/${encodeURIComponent(id)}/metadata`), loadMetadataSchema()]);
          if (epoch === authEpoch && detailsGeneration === detailGeneration) {
            detailBook = book;
            renderDetails(book, metadata, schema);
            if (currentBook?.id === id) currentBook.location = book.location;
          }
        } catch (error) {
          if (epoch === authEpoch && detailsGeneration === detailGeneration)
            $("details-status").textContent = `Could not refresh details: ${error.message}. Close and reopen Book details; check pending recovery and the comicbook's current location before proceeding.`;
        }
      }
    }
  }
});

async function openFileOperation(book) {
  if (session?.role !== "admin" || !book || fileOperationBusy) return;
  resetFileOperation();
  fileOperationBookId = book.id;
  const location = book.location || "";
  const relative = location.slice(location.indexOf("/") + 1);
  const slash = relative.lastIndexOf("/");
  const name = relative.slice(slash + 1);
  fileOperationExtension = name.match(/\.[^.]+$/u)?.[0] || "";
  $("file-operation-name").value = name;
  $("file-operation-extension").textContent = `Keep the source extension${fileOperationExtension ? `: ${fileOperationExtension}` : ""}. This is a rename / move, not a comicbook conversion.`;
  $("file-operation-book").textContent = `${bookTitle(book)}: ${location || "Check pending recovery below; source availability is not required."}`;
  $("file-operation-status").textContent = "Loading destinations…";
  fileOperationBusy = true;
  updateFileOperationControls();
  $("file-operation-dialog").showModal();
  const generation = fileOperationGeneration;
  const epoch = authEpoch;
  try {
    const roots = await api("/api/file-operation-roots");
    if (epoch !== authEpoch || generation !== fileOperationGeneration) return;
    $("file-operation-root").replaceChildren(...roots.map((root) => new Option(root.label, root.index)));
    const sourceRoot = roots.find((root) => location.startsWith(`${root.label}/`));
    if (sourceRoot) $("file-operation-root").value = String(sourceRoot.index);
    $("file-operation-status").textContent = roots.length ? "Choose a destination, then preview the exact paths before moving." : "No destinations are configured. Pending recovery is available below.";
  } catch (error) {
    if (epoch === authEpoch && generation === fileOperationGeneration)
      $("file-operation-status").textContent = `Could not load destination roots: ${error.message}. Close and reopen to try again. Pending recovery remains available below.`;
  } finally {
    if (epoch === authEpoch && generation === fileOperationGeneration) {
      fileOperationInitialDraft = JSON.stringify(fileOperationDraft());
      fileOperationBusy = false;
      await refreshFileOperationRecovery();
    }
  }
}
$("move-archive").addEventListener("click", () => openFileOperation(detailBook));
$("file-operation-dialog").addEventListener("close", () => { if (!$("file-operation-dialog").open) resetFileOperation(); });
for (const name of ["input", "change"]) $("file-operation-controls").addEventListener(name, () => {
  $("file-operation-name").setCustomValidity("");
  invalidateFileOperationPreview();
  $("file-operation-status").textContent = "Destination changed. Preview again before confirming; nothing has been applied.";
});
$("file-operation-draft").addEventListener("submit", (event) => { event.preventDefault(); previewFileOperation(); });
$("refresh-file-operation-recovery").addEventListener("click", refreshFileOperationRecovery);
$("file-operation-apply-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (session?.role !== "admin" || !fileOperationBookId || fileOperationBusy) return;
  const preview = fileOperationPreview;
  if (!preview?.canApply || preview.bookId !== fileOperationBookId || (preview.operation === "move" && JSON.stringify(preview.draft) !== JSON.stringify(fileOperationDraft()))) {
    invalidateFileOperationPreview();
    $("file-operation-status").textContent = "Preview this operation and explicitly confirm it before applying.";
    return;
  }
  const generation = fileOperationGeneration;
  const detailsGeneration = detailGeneration;
  const epoch = authEpoch;
  const id = fileOperationBookId;
  fileOperationBusy = true;
  invalidateFileOperationPreview();
  $("file-operation-status").textContent = "Applying the reviewed operation. Do not close this page or change either comicbook path…";
  try {
    const path = `/api/books/${encodeURIComponent(id)}/file-operations${preview.operation === "move" ? "" : `/${encodeURIComponent(preview.operationId)}/${preview.operation}`}`;
    await api(path, { method: "POST", body: JSON.stringify({ ...(preview.operation === "move" ? preview.draft : {}), expectedRevision: preview.revision, confirmed: true }) });
    if (epoch !== authEpoch || generation !== fileOperationGeneration) return;
    invalidateReaderNextIssue(id);
    fileOperationInitialDraft = JSON.stringify(fileOperationDraft());
    $("file-operation-status").textContent = "Reviewed operation completed. Book identity, metadata, and private reading data were kept.";
    failedCovers.delete(id);
    loadLists();
    loadPublishers();
    loadLibrary();
    if ($("maintenance-dialog").open) loadMaintenance();
    if ($("duplicate-dialog").open) loadDuplicateComparison();
    if ($("details-dialog").open && detailBook?.id === id) {
      try {
        const [book, metadata, schema] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}`), api(`/api/books/${encodeURIComponent(id)}/metadata`), loadMetadataSchema()]);
        if (epoch !== authEpoch || generation !== fileOperationGeneration || detailsGeneration !== detailGeneration) return;
        detailBook = book;
        renderDetails(book, metadata, schema);
        $("file-operation-book").textContent = `${bookTitle(book)}: ${book.location}`;
        if (currentBook?.id === id) currentBook.location = book.location;
      } catch (error) {
        if (epoch === authEpoch && detailsGeneration === detailGeneration)
          $("details-status").textContent = `File operation succeeded, but details could not refresh: ${error.message}. Close and reopen Book details.`;
      }
    }
  } catch (error) {
    if (epoch === authEpoch && generation === fileOperationGeneration)
      $("file-operation-status").textContent = error.status === 409
        ? `${error.message} The preview is invalid. Check pending recovery below, resolve conflicts, and preview again before confirming. Nothing will be retried automatically.`
        : `Could not confirm the operation: ${error.message}. It may have completed. Check pending recovery below and the comicbook's current location before proceeding. Nothing will be retried automatically.`;
  } finally {
    if (epoch === authEpoch && generation === fileOperationGeneration) {
      fileOperationBusy = false;
      await refreshFileOperationRecovery();
    }
  }
});


function resetPageMetadata() {
  pageMetadataGeneration++;
  pageMetadata = null;
  pageMetadataDraft = [];
  pageMetadataBusy = pageMetadataDirty = false;
  $("page-metadata-status").textContent = "";
  updatePageEditorControls();
}

function renderPageMetadata(node) {
  const row = pageMetadataDraft[Number(node.dataset.page)];
  const type = node.querySelector('[data-page-field="type"]');
  type.replaceChildren(new Option("Default (Story)", ""),
    ...(pageMetadata?.types ?? []).map((value) => new Option(pageTypeLabel(value), value)));
  if (row?.type && !pageMetadata.types.includes(row.type)) type.append(new Option(pageTypeLabel(row.type), row.type));
  type.value = row?.type ?? "";
  node.querySelector('[data-page-field="bookmark"]').value = row?.bookmark ?? "";
}

function setPageMetadata(result) {
  pageMetadata = result;
  pageMetadataDraft = result.pages.map((row) => ({ ...row }));
  pageMetadataDirty = false;
  for (const node of $("page-order-pages").children) renderPageMetadata(node);
  updatePageEditorControls();
}

async function refreshPageEditorReader(id) {
  if (currentBook?.id !== id) return;
  const epoch = authEpoch, generation = pageMetadataGeneration;
  try {
    const info = await api(`/api/books/${encodeURIComponent(id)}/page-info`);
    if (epoch !== authEpoch || generation !== pageMetadataGeneration || currentBook?.id !== id) return;
    if (pages.length !== pageOrder.pages.length) {
      $("page-metadata-status").textContent += " Reopen the reader to load the changed source.";
      return;
    }
    setReadingOrder(pageOrder.pages);
    pageNumberDraft = false;
    for (const entry of info) if (pages[entry.page]) Object.assign(pages[entry.page], entry);
    renderReader();
    renderBookmarks();
  } catch (error) {
    if (epoch === authEpoch && generation === pageMetadataGeneration)
      $("page-metadata-status").textContent += ` Reader refresh failed: ${error.message}. Reopen the reader to reload saved pages.`;
  }
}

async function loadPageMetadata(id) {
  const generation = ++pageMetadataGeneration;
  const epoch = authEpoch;
  pageMetadataBusy = true;
  updatePageEditorControls();
  $("page-metadata-status").textContent = "Loading saved page metadata…";
  try {
    const result = await api(`/api/books/${encodeURIComponent(id)}/page-metadata`);
    if (epoch !== authEpoch || generation !== pageMetadataGeneration) return;
    setPageMetadata(result);
    $("page-metadata-status").textContent = result.pages.length ? "" : "This book has no pages.";
  } catch (error) {
    if (epoch === authEpoch && generation === pageMetadataGeneration) {
      pageEditConflict = true;
      $("page-metadata-status").textContent = `Could not load page details: ${error.message}. Use Reset changes to retry.`;
    }
  } finally {
    if (epoch === authEpoch && generation === pageMetadataGeneration) { pageMetadataBusy = false; updatePageEditorControls(); }
  }
}

$("edit-page-metadata").addEventListener("click", () => {
  if (session?.role !== "admin" || !detailBook || pageMetadataBusy || pageOrderBusy) return;
  resetPageOrder();
  resetPageMetadata();
  $("page-metadata-dialog").showModal();
  loadPageOrder();
  loadPageMetadata(detailBook.id);
});
$("page-metadata-dialog").addEventListener("close", () => { resetPageOrder(); resetPageMetadata(); });
$("reset-page-changes").addEventListener("click", async () => {
  if (session?.role !== "admin" || pageOrderBusy || pageMetadataBusy || !detailBook
      || ((pageOrderDirty() || pageMetadataDirty) && !confirm("Discard unsaved page changes and reload saved values?"))) return;
  const id = detailBook.id, epoch = authEpoch;
  pageEditConflict = false;
  await Promise.all([loadPageOrder(), loadPageMetadata(id)]);
  if (epoch !== authEpoch || pageEditConflict || pageMetadata?.bookId !== id || pageOrder?.bookId !== id) return;
  const generation = pageMetadataGeneration;
  pageMetadataBusy = true;
  updatePageEditorControls();
  try {
    await refreshPageEditorReader(id);
  } finally {
    if (epoch === authEpoch && generation === pageMetadataGeneration) {
      pageMetadataBusy = false;
      updatePageEditorControls();
    }
  }
});
$("page-order-pages").addEventListener("input", (event) => {
  const field = event.target.dataset.pageField;
  if (!pageMetadata || pageOrderBusy || pageMetadataBusy || !field) return;
  const row = pageMetadataDraft[Number(event.target.closest("[data-page]").dataset.page)];
  if (!row) return;
  row[field] = event.target.value || null;
  pageMetadataDirty = pageMetadataDraft.some((draft, i) => draft.type !== pageMetadata.pages[i].type || draft.bookmark !== pageMetadata.pages[i].bookmark);
  updatePageEditorDraftState();
  if (!pageEditConflict) $("page-metadata-status").textContent = "";
});
$("page-metadata-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (session?.role !== "admin" || pageOrderBusy || pageMetadataBusy || pageEditConflict
      || !pageOrder || !pageMetadata || pageOrder.bookId !== pageMetadata.bookId
      || pageOrder.pages.length !== pageMetadata.pages.length || !$("page-metadata-form").reportValidity()) return;
  const changed = pageMetadataDraft.filter((draft, i) => draft.type !== pageMetadata.pages[i].type || draft.bookmark !== pageMetadata.pages[i].bookmark);
  const orderChanged = pageOrderDirty();
  if (!changed.length && !orderChanged) return;
  const id = pageMetadata.bookId;
  const generation = pageMetadataGeneration;
  const epoch = authEpoch;
  let metadataSaved = false, orderSaved = false;
  pageOrderBusy = pageMetadataBusy = true;
  updatePageEditorControls();
  $("page-order-status").textContent = "";
  $("page-metadata-status").textContent = "Saving changes…";
  try {
    try {
      if (changed.length) {
        const preview = await api(`/api/books/${encodeURIComponent(id)}/page-metadata/preview`, {
          method: "POST", body: JSON.stringify({ expectedRevision: pageMetadata.revision, pages: changed })
        });
        if (epoch !== authEpoch || generation !== pageMetadataGeneration) return;
        const result = await api(`/api/books/${encodeURIComponent(id)}/page-metadata`, {
          method: "PUT", body: JSON.stringify({ expectedRevision: preview.revision, pages: changed })
        });
        if (epoch !== authEpoch || generation !== pageMetadataGeneration) return;
        setPageMetadata(result);
        metadataSaved = true;
      }
      if (orderChanged) {
        const result = await api(`/api/books/${encodeURIComponent(id)}/page-order`, {
          method: "PUT", body: JSON.stringify({ expectedRevision: pageOrder.revision, pages: pageOrderDraft })
        });
        if (epoch !== authEpoch || generation !== pageMetadataGeneration) return;
        validatePageOrder(result, id, pageOrderDraft.length);
        pageOrder = result;
        pageOrderDraft = [...result.pages];
        orderSaved = true;
        renderPageOrder();
      }
      $("page-metadata-status").textContent = "Changes saved.";
    } catch (error) {
      if (epoch !== authEpoch || generation !== pageMetadataGeneration) return;
      pageEditConflict = error.status !== 400;
      const reason = error.status === 409 ? "Saved page data or the comicbook source changed." : error.message;
      $("page-metadata-status").textContent = `${metadataSaved ? "Page details saved; order save could not be confirmed." : "Save could not be confirmed."} ${reason} Unsaved draft retained. ${pageEditConflict ? "Use Reset changes to reload saved values before retrying." : "Correct the values and save again."}`;
    }
    if (metadataSaved) {
      if (archiveBookId === id) invalidateArchivePreview();
      loadLists();
    }
    if (metadataSaved || orderSaved) await refreshPageEditorReader(id);
  } finally {
    if (epoch === authEpoch && generation === pageMetadataGeneration) {
      pageOrderBusy = pageMetadataBusy = false;
      updatePageEditorControls();
    }
  }
});

let ruleFields = [];
const ruleOperatorLabels = { contains: "Contains", notContains: "Does not contain", equals: "Equals", notEquals: "Does not equal", startsWith: "Starts with", endsWith: "Ends with", isEmpty: "Not set", isNotEmpty: "Is set", greaterThan: "Greater than", lessThan: "Less than", greaterOrEqual: "Greater than or equal", lessOrEqual: "Less than or equal" };

function addListRule(rule = { field: "Series", operator: "contains", value: "" }, container = $("list-rules")) {
  if ($("list-rules").querySelectorAll(".list-rule").length >= 20) { $("list-status").textContent = "A smart list supports at most 20 total rules."; return; }
  const row = textElement("div", "", "list-rule");
  const field = document.createElement("select");
  field.className = "rule-field";
  field.append(...ruleFields.map(({ name, label }) => new Option(label, name)));
  field.value = rule.field;
  const operator = document.createElement("select");
  operator.className = "rule-operator";
  const valueLabel = textElement("label", "Value");
  const updateValue = () => {
    const empty = operator.value === "isEmpty" || operator.value === "isNotEmpty";
    valueLabel.hidden = empty;
    valueLabel.querySelector(".rule-value").disabled = empty;
  };
  const update = (initial = false) => {
    const schema = ruleFields.find((item) => item.name === field.value);
    const numeric = schema?.type === "integer" || schema?.type === "number";
    const date = schema?.type === "date";
    const membership = schema?.type === "listMembership";
    const enumeration = ["readStatus", "availability", "boolean", "listMembership"].includes(schema?.type);
    const missing = !["PageCount", "PersonalRating", "FileSizeMB"].includes(field.value) ? ["isEmpty", "isNotEmpty"] : [];
    const comparisons = ["greaterThan", "lessThan", "greaterOrEqual", "lessOrEqual"];
    const operators = numeric || date ? ["equals", "notEquals", ...comparisons, ...missing]
      : enumeration ? ["equals", "notEquals", ...(membership ? missing : [])]
      : ["contains", "notContains", "equals", "notEquals", "startsWith", "endsWith", ...(field.value === "Number" ? comparisons : []), ...missing];
    operator.replaceChildren(...operators.map(name => new Option(membership && name === "isEmpty" ? "In no manual list" : membership && name === "isNotEmpty" ? "In any manual list" : ruleOperatorLabels[name], name)));
    if (initial) operator.value = rule.operator;
    const value = document.createElement(enumeration ? "select" : "input");
    value.className = "rule-value";
    value.setAttribute("aria-describedby", "list-status");
    if (membership) {
      value.append(new Option("Choose a manual list", ""), ...ruleManualLists.map(list => new Option(list.name, list.id)));
      if (initial && rule.value && !ruleManualLists.some(list => list.id === rule.value)) {
        const unavailable = new Option("List no longer available — choose another", rule.value);
        unavailable.disabled = true;
        value.append(unavailable);
      }
    } else if (schema?.type === "readStatus") value.append(new Option("Unread", "unread"), new Option("In progress", "in-progress"), new Option("Completed", "completed"));
    else if (schema?.type === "availability") value.append(new Option("Available", "available"), new Option("Missing", "missing"), new Option("Unavailable", "unavailable"));
    else if (schema?.type === "boolean") value.append(new Option("Yes", "yes"), new Option("No", "no"));
    else if (date) { value.type = "date"; value.min = "0001-01-01"; value.max = "9999-12-31"; }
    else if (numeric) { value.type = "number"; value.min = String(schema.min ?? 0); if (schema.max != null) value.max = String(schema.max); value.step = schema.type === "integer" ? "1" : "any"; }
    else value.maxLength = 2000;
    value.required = numeric || enumeration || date;
    if (initial) value.value = rule.value;
    valueLabel.replaceChildren(document.createTextNode("Value"), value);
    updateValue();
  };
  field.addEventListener("change", () => update());
  operator.addEventListener("change", updateValue);
  const remove = textElement("button", "Remove rule", "danger");
  remove.type = "button";
  remove.addEventListener("click", () => {
    row.remove();
    listDirty = true;
    updateRuleBounds();
    invalidateListPreview();
    (container.closest(".rule-group")?.querySelector(":scope > [data-add-rule]") || $("add-list-rule")).focus();
  });
  row.append(labeledControl("Field", field), labeledControl("Operator", operator), valueLabel, remove);
  update(true);
  container.append(row);
  updateRuleBounds();
  invalidateListPreview();
}

function updateRuleBounds() {
  const leaves = $("list-rules").querySelectorAll(".list-rule").length;
  const groups = $("list-rules").querySelectorAll(".rule-group").length + 1;
  $("add-list-rule").disabled = leaves >= 20;
  $("add-list-group").disabled = groups >= 20 || leaves >= 20;
  for (const button of $("list-rules").querySelectorAll("[data-add-rule]")) button.disabled = leaves >= 20;
  for (const button of $("list-rules").querySelectorAll("[data-add-group]"))
    button.disabled = groups >= 20 || leaves >= 20 || Number(button.dataset.addGroup) >= 4;
}

function addListGroup(group = { match: "all", rules: [{ field: "Series", operator: "contains", value: "" }] }, container = $("list-rules"), depth = 2) {
  if (depth > 4 || $("list-rules").querySelectorAll(".rule-group").length >= 19 || $("list-rules").querySelectorAll(".list-rule").length >= 20) return;
  const section = textElement("fieldset", "", "rule-group");
  section.append(textElement("legend", `Rule group (level ${depth})`));
  const match = document.createElement("select");
  match.className = "group-match";
  match.append(new Option("All rules (AND)", "all"), new Option("Any rule (OR)", "any"));
  match.value = group.match;
  const children = textElement("div", "", "group-rules");
  const addRule = textElement("button", "Add rule");
  addRule.type = "button";
  addRule.dataset.addRule = "";
  addRule.addEventListener("click", () => { listDirty = true; addListRule(undefined, children); });
  const addGroup = textElement("button", "Add group");
  addGroup.type = "button";
  addGroup.dataset.addGroup = String(depth);
  addGroup.addEventListener("click", () => { listDirty = true; addListGroup(undefined, children, depth + 1); });
  const remove = textElement("button", "Remove group", "danger");
  remove.type = "button";
  remove.addEventListener("click", () => {
    section.remove();
    listDirty = true;
    updateRuleBounds();
    invalidateListPreview();
    const target = container.closest(".rule-group")?.querySelector(":scope > [data-add-group]") || $("add-list-group");
    (target.disabled ? container.closest(".rule-group")?.querySelector(".group-match") || $("list-match") : target).focus();
  });
  section.append(labeledControl("Match", match), children, addRule, addGroup, remove);
  container.append(section);
  for (const child of group.rules) {
    if (child.rules) addListGroup(child, children, depth + 1);
    else addListRule(child, children);
  }
  updateRuleBounds();
  invalidateListPreview();
}

function collectListRules(container = $("list-rules")) {
  return [...container.children].map((row) => {
    if (row.classList.contains("rule-group"))
      return { match: row.querySelector(".group-match").value, rules: collectListRules(row.querySelector(".group-rules")) };
    const value = row.querySelector(".rule-value");
    return { field: row.querySelector(".rule-field").value, operator: row.querySelector(".rule-operator").value,
      value: value.disabled ? "" : value.type === "number" ? String(value.valueAsNumber) : value.value };
  });
}

function validateRuleDraft() {
  for (const control of $("list-rules").querySelectorAll("input, select"))
    if (!control.reportValidity()) return null;
  const payload = { match: $("list-match").value, rules: collectListRules() };
  if (!payload.rules.length || [...$("list-rules").querySelectorAll(".group-rules")].some(group => !group.children.length)) {
    $("list-status").textContent = "Add at least one rule to every group, or remove empty groups.";
    ($("add-list-rule").disabled ? $("list-match") : $("add-list-rule")).focus();
    return null;
  }
  if (listEditorMode !== "list" && encodeURIComponent(JSON.stringify(payload)).replace(/[!'()*]/g, character => `%${character.charCodeAt(0).toString(16)}`).length > 6000) {
    $("list-status").textContent = "These advanced filters exceed the 6,000-character encoded request limit. Shorten values or remove rules, then try again. Your draft is kept.";
    $("list-match").focus();
    return null;
  }
  return payload;
}

function setListEditorMode(mode) {
  listEditorMode = mode;
  const filter = mode === "filter";
  if (mode !== "list") $("list-title").textContent = filter ? "Advanced filters" : "Save advanced rules as smart list";
  $("list-controls-legend").textContent = filter ? "Filter current catalog" : "Private reading list";
  for (const id of ["list-name", "list-folder"]) {
    $(id).closest("label").hidden = filter;
    $(id).disabled = filter;
  }
  $("list-kind").closest("label").hidden = mode !== "list";
  $("list-kind").disabled = mode !== "list" || Boolean(listEditingId);
  $("filter-save-help").hidden = mode !== "filter-save";
  $("save-filter-list").hidden = !filter;
  $("back-to-filter").hidden = mode !== "filter-save";
  $("save-list").textContent = filter ? "Apply filters" : mode === "filter-save" ? "Save advanced rules" : "Save list";
  $("save-list").disabled = listBusy || (filter ? !ruleFields.length : $("list-kind").value === "smart" && !listPreviewKey);
}

function invalidateListPreview() {
  listPreviewGeneration++;
  listPreviewBusy = false;
  listPreviewKey = null;
  listPreviewPage = listPreviewPages = 1;
  $("list-preview-items").replaceChildren();
  $("list-preview-status").textContent = "Preview the current rules before saving. No list is saved by previewing.";
  $("list-preview-page").textContent = "Page 1 of 1";
  $("previous-list-preview").disabled = $("next-list-preview").disabled = true;
  $("preview-list").disabled = false;
  $("save-list").disabled = listBusy || (listEditorMode === "filter" ? !ruleFields.length : $("list-kind").value === "smart");
}

async function previewList(page = 1) {
  if (listBusy || listPreviewBusy || $("list-kind").value !== "smart") return;
  const payload = validateRuleDraft();
  if (!payload) return;
  const key = JSON.stringify(payload);
  const generation = ++listPreviewGeneration;
  const epoch = authEpoch;
  listPreviewBusy = true;
  $("preview-list").disabled = $("previous-list-preview").disabled = $("next-list-preview").disabled = $("save-list").disabled = true;
  $("list-preview-status").textContent = "Previewing unsaved rules…";
  try {
    const result = await api(`/api/lists/preview?${new URLSearchParams({ page, pageSize: 12 })}`, { method: "POST", body: key });
    if (epoch !== authEpoch || generation !== listPreviewGeneration) return;
    listPreviewPages = Math.max(1, Math.ceil(result.total / result.pageSize));
    if (result.page > listPreviewPages) { listPreviewBusy = false; await previewList(listPreviewPages); return; }
    listPreviewPage = result.page;
    listPreviewKey = key;
    $("list-preview-items").replaceChildren(...result.items.map((book) => {
      const row = textElement("article", "", "maintenance-row");
      const details = textElement("button", "Book details");
      details.type = "button";
      details.setAttribute("aria-label", `Book details: ${bookTitle(book)}`);
      details.addEventListener("click", () => openDetails(book.id));
      row.append(textElement("h3", bookTitle(book)), textElement("p", book.location),
        textElement("p", `${book.pageCount} pages, ${book.availability}`), details);
      return row;
    }));
    $("list-preview-page").textContent = `Page ${listPreviewPage} of ${listPreviewPages}`;
    $("list-preview-status").textContent = result.total
      ? `${result.total} matching books. Showing ${(result.page - 1) * result.pageSize + 1}–${(result.page - 1) * result.pageSize + result.items.length}. Preview only; matches remain live.`
      : "0 matching books. These rules are valid; you can save this empty smart list.";
  } catch (error) {
    if (epoch === authEpoch && generation === listPreviewGeneration) {
      listPreviewKey = null;
      $("list-preview-status").textContent = `Preview failed: ${error.message}. Your draft and previous displayed page are kept. Preview again before saving.`;
    }
  } finally {
    if (epoch === authEpoch && generation === listPreviewGeneration) {
      listPreviewBusy = false;
      $("preview-list").disabled = false;
      $("save-list").disabled = listEditorMode !== "filter" && !listPreviewKey;
      $("previous-list-preview").disabled = !listPreviewKey || listPreviewPage <= 1;
      $("next-list-preview").disabled = !listPreviewKey || listPreviewPage >= listPreviewPages;
    }
  }
}

function showListEditor(list = null, mode = "list") {
  listDialogGeneration++;
  ruleFields = [];
  listDirty = listBusy = false;
  listEditingId = list?.id ?? null;
  $("list-title").textContent = list ? "Edit reading list" : "New reading list";
  $("list-form").reset();
  $("list-controls").disabled = false;
  $("save-list").disabled = false;
  $("list-name").value = list?.name ?? "";
  $("list-name").setCustomValidity("");
  $("list-kind").value = list?.kind ?? "manual";
  $("list-kind").disabled = Boolean(list);
  $("list-match").value = list?.match ?? "all";
  $("list-folder").replaceChildren(new Option("Unfiled", ""), ...folderOptions());
  $("list-folder").value = list?.folderId ?? "";
  $("list-rules").replaceChildren();
  $("add-list-rule").disabled = false;
  $("add-list-group").disabled = false;
  $("smart-list-controls").hidden = $("list-kind").value !== "smart";
  $("smart-list-controls").disabled = true;
  $("list-status").textContent = "";
  setListEditorMode(mode);
  invalidateListPreview();
  $("list-dialog").showModal();
  $("retry-list-rules").onclick = () => loadListRuleFields(list?.rules ?? []);
  loadListRuleFields(list?.rules ?? []);
}

async function loadListRuleFields(rules) {
  const generation = listDialogGeneration;
  const epoch = authEpoch;
  $("retry-list-rules").hidden = true;
  $("list-rule-status").textContent = "Loading rule fields and your manual lists…";
  try {
    const [fields, lists, folders] = await Promise.all([api("/api/list-rule-fields"), api("/api/lists"), api("/api/list-folders")]);
    if (epoch !== authEpoch || generation !== listDialogGeneration) return;
    ruleFields = fields;
    ruleManualLists = lists.filter(list => list.kind === "manual");
    listFolders = folders;
    const folder = $("list-folder").value;
    $("list-folder").replaceChildren(new Option("Unfiled", ""), ...folderOptions());
    $("list-folder").value = folder;
    for (const rule of rules) {
      if (rule.rules) addListGroup(rule);
      else addListRule(rule);
    }
    if ($("list-kind").value === "smart" && !$("list-rules").children.length) addListRule();
    $("smart-list-controls").disabled = $("list-kind").value !== "smart";
    $("list-rule-status").textContent = "";
    invalidateListPreview();
  } catch (error) {
    if (epoch === authEpoch && generation === listDialogGeneration) {
      $("list-rule-status").textContent = `Rule fields or lists unavailable: ${error.message}. Retry loading to edit rules. Manual lists can still be saved.`;
      $("retry-list-rules").hidden = false;
    }
  }
}

function updateSeriesListControls() {
  const busy = !!seriesList?.busy;
  const unavailable = !seriesList?.loaded || !readingLists.some(list => list.kind === "manual");
  $("series-list-target").disabled = $("submit-series-list").disabled = busy || unavailable;
  $("close-series-list").disabled = busy;
  $("series-list-empty").hidden = !seriesList?.loaded || !unavailable;
  $("series-list-form").setAttribute("aria-busy", String(busy || seriesList?.loaded === null));
}

$("add-series-to-list").addEventListener("click", async () => {
  if (managementBusy || !session || session.mustChangePassword || !groupKey?.trim() ||
      (!seriesView() && $("stack-by").value !== "series") || $("series-list-dialog").open) return;
  const publisher = seriesView() ? null : $("group-by").value === "publisher" && sectionKey !== null
    ? sectionKey : $("publisher").value || null;
  const state = seriesList = { epoch: authEpoch, scope: { series: groupKey, location: catalogLocation, publisher }, loaded: null, busy: false };
  $("series-list-form").reset();
  $("close-series-list").textContent = "Cancel";
  $("series-list-scope").textContent = `${groupKey} — ${catalogLocation === "library" ? "Library" : catalogLocation === "archived" ? "Archived" : "Unsorted"}; ${publisher === null ? "all publishers" : publisher === "" ? "no publisher" : publisher}.`;
  $("series-list-status").textContent = "Loading manual reading lists…";
  updateSeriesListControls();
  $("series-list-dialog").showModal();
  const loaded = await loadLists();
  if (seriesList !== state || state.epoch !== authEpoch || !$("series-list-dialog").open) return;
  state.loaded = !!loaded;
  $("series-list-status").textContent = loaded ? "" : "Could not load reading lists. Cancel and reopen to retry.";
  updateSeriesListControls();
  (!$("series-list-target").disabled ? $("series-list-target") : $("close-series-list")).focus();
});
$("series-list-dialog").addEventListener("close", () => { if (!$("series-list-dialog").open) seriesList = null; });
$("series-list-form").addEventListener("submit", async event => {
  event.preventDefault();
  const state = seriesList;
  const list = readingLists.find(list => list.id === $("series-list-target").value && list.kind === "manual");
  if (!state?.loaded || state.epoch !== authEpoch || managementBusy || !list || !$("series-list-dialog").open) return;
  const generation = searchGeneration;
  state.busy = managementBusy = true;
  updateSelection();
  updateSeriesListControls();
  $("series-list-status").textContent = "Adding series…";
  try {
    const result = await api(`/api/lists/${encodeURIComponent(list.id)}/series`, { method: "POST", body: JSON.stringify(state.scope) });
    if (state.epoch !== authEpoch || seriesList !== state) return;
    const message = result.added ? `Added ${result.added} comicbook${result.added === 1 ? "" : "s"} to “${list.name}”.` : `No new comicbooks to add to “${list.name}”. Existing entries were kept.`;
    $("series-list-status").textContent = $("management-status").textContent = message;
    $("close-series-list").textContent = "Close";
    const refreshed = await loadLists();
    if (state.epoch !== authEpoch || seriesList !== state) return;
    if (!refreshed) $("series-list-status").textContent += " Added entries are saved, but lists could not refresh. Close and refresh the library.";
    if (generation === searchGeneration && $("reading-list").value && !$("library-view").hidden) await loadLibrary();
  } catch (error) {
    if (state.epoch === authEpoch && seriesList === state)
      $("series-list-status").textContent = !error.status || error.status >= 500
        ? `Add outcome unknown: ${error.message} Entries may have been added. No automatic retry was made. You can explicitly retry Add series; existing entries will not be duplicated.`
        : `Series not added: ${error.message} Review the message, then retry or cancel.`;
  } finally {
    if (state.epoch === authEpoch) {
      managementBusy = false;
      updateSelection();
      if (seriesList === state) {
        state.busy = false;
        updateSeriesListControls();
        if ($("series-list-dialog").open) $("series-list-status").focus();
      }
    }
  }
});

async function changeListMembership(remove) {
  if (managementBusy || !selectedBooks.size) return;
  const listId = remove ? activeList()?.id : $("add-to-list").value;
  if (!listId) return;
  const epoch = authEpoch;
  const bookIds = [...selectedBooks];
  const generation = searchGeneration;
  managementBusy = true;
  updateSelection();
  $("management-status").textContent = remove ? "Removing selected books…" : "Adding selected books…";
  try {
    await api(`/api/lists/${encodeURIComponent(listId)}/books`, { method: remove ? "DELETE" : "POST", body: JSON.stringify({ bookIds }) });
    if (epoch !== authEpoch) return;
    if (generation === searchGeneration && bookIds.length === selectedBooks.size && bookIds.every(id => selectedBooks.has(id)))
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
  let generation = searchGeneration;
  const trigger = document.activeElement;
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
    $("management-status").textContent = "List order saved. Moves use the full list, including books on other pages or hidden by filters.";
    generation = await loadLibrary();
  } catch (error) { if (epoch === authEpoch) $("management-status").textContent = error.message; }
  finally {
    if (epoch === authEpoch) {
      managementBusy = false;
      updateSelection();
      if (generation === searchGeneration && !$("library-view").hidden && $("reading-list").value === listId &&
          (document.activeElement === trigger || document.activeElement === document.body)) {
        const card = $("books").querySelector(`[data-book-id="${CSS.escape(bookId)}"]`)?.closest(".book-card");
        (card?.querySelector(`[data-move="${direction}"]`) || $("management-status")).focus({ preventScroll: true });
      }
    }
  }
}

async function changeReadingStatus(completed) {
  if (readingStatusBusy || !session || session.mustChangePassword || !selectedBooks.size || selectedBooks.size > 100) return;
  const bookIds = [...selectedBooks];
  const epoch = authEpoch;
  const generation = searchGeneration;
  readingStatusBusy = true;
  updateSelection();
  $("management-status").textContent = `Marking ${bookIds.length} selected comicbooks ${completed ? "read" : "unread"}…`;
  try {
    await api("/api/reading-status", { method: "POST", body: JSON.stringify({ bookIds, completed }) });
    if (epoch !== authEpoch) return;
    if (generation === searchGeneration)
      $("management-status").textContent = `${bookIds.length} selected comicbooks marked ${completed ? "100% read" : "unread (0%)"} for your account.`;
    await loadLibrary();
  } catch (error) {
    if (epoch === authEpoch && generation === searchGeneration && error.name !== "AbortError")
      $("management-status").textContent = `Could not confirm reading status: ${error.message} Refresh to check before trying again.`;
  } finally {
    if (epoch === authEpoch) { readingStatusBusy = false; updateSelection(); }
  }
}

$("mark-selected-read").addEventListener("click", () => changeReadingStatus(true));
$("mark-selected-unread").addEventListener("click", () => changeReadingStatus(false));

$("select-page").addEventListener("click", () => {
  if (deletionBusy) return;
  if (pageSelectionFull()) {
    clearSelection(true);
    return;
  }
  for (const book of libraryItems) {
    if (selectedBooks.size >= 100) break;
    selectedBooks.add(book.id);
  }
  $("management-status").textContent = selectedBooks.size === 100 ? "Selection limit reached (100 books)." : "This page selected. Selections remain when changing pages.";
  updateSelection();
});
$("clear-selection").addEventListener("click", () => clearSelection(true));
$("add-to-list").addEventListener("change", updateSelection);
$("reading-list").addEventListener("change", () => {
  if (seriesView()) $("book-status").value = "all";
  if (activeList()?.kind === "manual") $("book-sort").value = "list";
  changeLibrary();
});
$("add-list-books").addEventListener("click", () => changeListMembership(false));
$("remove-list-books").addEventListener("click", () => changeListMembership(true));
$("bulk-metadata").addEventListener("click", () => openMetadata([...selectedBooks], true));
$("edit-metadata").addEventListener("click", () => { if (detailBook) openMetadata([detailBook.id]); });
$("add-custom-value").addEventListener("click", () => {
  $("metadata-field-search").value = "";
  addCustomValue();
  const row = $("custom-values").lastElementChild;
  const group = row.closest(".custom-values-group");
  if (group) group.hidden = false;
  metadataChanged();
  row.querySelector(".custom-name").focus();
});
$("metadata-field-search").addEventListener("input", filterMetadataFields);
$("metadata-changed-only").addEventListener("change", filterMetadataFields);
$("metadata-form").addEventListener("invalid", (event) => {
  const row = event.target.closest(".metadata-change");
  if (!row) return;
  row.hidden = false;
  const group = row.closest(".metadata-field-group, .custom-values-group");
  if (group) group.hidden = false;
}, true);
$("metadata-controls").addEventListener("input", metadataChanged);
$("metadata-controls").addEventListener("change", metadataChanged);
$("custom-values").addEventListener("focusout", (event) => {
  if (event.target.matches(".custom-name")) queueMicrotask(filterMetadataFields);
});
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
  let focus = event.submitter || document.activeElement;
  let submitted = false;
  let saved = false;
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
      renderMetadataPreview(preview.books, $("metadata-preview-books"));
      $("metadata-preview").hidden = false;
      focus = $("metadata-preview");
      $("metadata-status").textContent = metadataPreview.hasChanges ? "Review every book below, then choose Apply reviewed changes. Nothing has been saved yet." : "No actual changes. Every selected book is unchanged.";
      return;
    }
    $("metadata-status").textContent = `Saving metadata for ${ids.length} ${ids.length === 1 ? "book" : "books"}…`;
    submitted = true;
    await api(metadataBulk ? "/api/books/metadata" : `/api/books/${encodeURIComponent(ids[0])}/metadata`, {
      method: metadataBulk ? "POST" : "PUT", body: JSON.stringify(metadataBulk ? { ...metadataPreview.payload, expectedRevision: metadataPreview.revision } : changes)
    });
    saved = true;
    if (epoch !== authEpoch) return;
    if (Object.hasOwn(changes.fields, "Series") || Object.hasOwn(changes.fields, "Number"))
      for (const id of ids) invalidateReaderNextIssue(id);
    if (archivePreview && ids.includes(archiveBookId)) {
      invalidateArchivePreview();
      $("archive-status").textContent = "Saved catalog metadata changed. Source checks must run again before writing.";
    }
    clearSelection();
    if (metadataPage) metadataPage.origin.selection = [];
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
    filterMetadataFields();
    invalidateMetadataPreview();
    $("metadata-status").textContent = "Metadata saved in the library database. Source comicbooks unchanged.";
    if ($("details-dialog").open && detailBook && ids.includes(detailBook.id)) {
      try {
        const id = detailBook.id;
        const [book, metadata] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}`), api(`/api/books/${encodeURIComponent(id)}/metadata`)]);
        if (detailsGeneration === detailGeneration) { detailBook = book; renderDetails(book, metadata, metadataSchema); }
      } catch (error) { if (detailsGeneration === detailGeneration) $("details-status").textContent = `Metadata was saved, but details could not refresh: ${error.message}`; }
    }
  } catch (error) {
    if (epoch === authEpoch && generation === metadataGeneration) {
      const uncertain = submitted && !saved && (!error.status || error.status >= 500);
      if (error.status === 409 || uncertain) invalidateMetadataPreview();
      focus = $("metadata-status");
      focus.textContent = saved ? `Metadata was saved, but the view could not refresh: ${error.message} Reopen metadata to inspect the saved values.`
        : uncertain ? `Metadata save outcome unknown: ${error.message} Changes may have been saved. No automatic retry was made. Your draft is retained; copy it, then reopen metadata to inspect the saved values before submitting again.`
        : `Metadata ${submitted ? "save rejected" : "not saved"}: ${error.message} Your draft is retained.${error.status === 409 && metadataBulk ? " Preview again before applying." : " Review the message and your entries before submitting again."}`;
    }
  } finally {
    if (epoch === authEpoch && generation === metadataGeneration) {
      metadataBusy = false;
      $("metadata-controls").disabled = $("save-metadata").disabled = false;
      $("apply-metadata").disabled = !metadataPreview?.hasChanges;
      if (!$("metadata-view").hidden)
        (focus.isConnected && !focus.matches(":disabled") && focus.checkVisibility() ? focus : $("metadata-status")).focus();
    }
  }
});

$("edit-advanced-filters").addEventListener("click", () => showListEditor({ kind: "smart", ...advancedFilter }, "filter"));
$("save-filter-list").addEventListener("click", () => {
  if (listBusy || listPreviewBusy || !ruleFields.length) return;
  setListEditorMode("filter-save");
  $("list-name").focus();
});
$("back-to-filter").addEventListener("click", () => {
  if (listBusy || listPreviewBusy) return;
  setListEditorMode("filter");
  $("list-match").focus();
});
$("list-form").addEventListener("invalid", event => {
  event.target.setAttribute("aria-invalid", "true");
  $("list-status").textContent = `${event.target.closest("label")?.firstChild.textContent.trim() || "Value"}: ${event.target.validationMessage}`;
}, true);
$("create-list").addEventListener("click", () => showListEditor());
$("edit-list").addEventListener("click", () => { if (activeList()) showListEditor(activeList()); });
$("list-dialog").addEventListener("close", () => { listDialogGeneration++; listDirty = listBusy = false; invalidateListPreview(); });
$("list-name").addEventListener("input", () => clearFieldError($("list-name"), $("list-status")));
for (const event of ["input", "change"]) $("list-form").addEventListener(event, (event) => {
  listDirty = true;
  event.target.removeAttribute("aria-invalid");
  if (event.target.closest("#list-rules")) $("list-status").textContent = "";
  if (event.target.closest("#list-rules") || event.target.id === "list-match" || event.target.id === "list-kind") invalidateListPreview();
});
$("preview-list").addEventListener("click", () => previewList());
$("previous-list-preview").addEventListener("click", () => { if (listPreviewPage > 1) previewList(listPreviewPage - 1); });
$("next-list-preview").addEventListener("click", () => { if (listPreviewPage < listPreviewPages) previewList(listPreviewPage + 1); });
$("list-kind").addEventListener("change", () => {
  const smart = $("list-kind").value === "smart";
  $("smart-list-controls").hidden = !smart;
  $("smart-list-controls").disabled = !smart || !ruleFields.length;
  if (smart && ruleFields.length && !$("list-rules").children.length) addListRule();
});
$("add-list-rule").addEventListener("click", () => { listDirty = true; addListRule(); });
$("add-list-group").addEventListener("click", () => { listDirty = true; addListGroup(); });
$("list-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (listBusy || listPreviewBusy || !$("list-form").reportValidity()) return;
  const generation = listDialogGeneration;
  const epoch = authEpoch;
  const id = listEditingId;
  const kind = $("list-kind").value;
  if (kind === "smart" && !ruleFields.length) return;
  const payload = kind === "smart" ? validateRuleDraft() : null;
  if (kind === "smart" && !payload) return;
  const rules = payload?.rules ?? [];
  if (listEditorMode === "filter") {
    if (listPreviewKey !== JSON.stringify(payload)) await previewList();
    if (epoch !== authEpoch || generation !== listDialogGeneration || listPreviewKey !== JSON.stringify(payload)) return;
    rememberCatalogView();
    history.pushState(null, "", location.href);
    advancedFilter = payload;
    listDirty = false;
    $("list-dialog").close();
    changeLibrary(false, true);
    return;
  }
  if (kind === "smart" && listPreviewKey !== JSON.stringify({ match: $("list-match").value, rules })) {
    $("list-status").textContent = "Preview the current rules successfully before saving.";
    return;
  }
  const name = $("list-name").value.trim();
  if (!name) {
    $("list-status").textContent = "Enter a list name.";
    $("list-name").setCustomValidity($("list-status").textContent);
    $("list-name").reportValidity();
    return;
  }
  listBusy = true;
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
    if (listEditorMode === "list") {
      $("reading-list").value = list.id;
      if (kind === "manual") $("book-sort").value = "list";
      changeLibrary();
    }
    $("list-dialog").close();
    $("management-status").textContent = listEditorMode === "filter-save" ? "Advanced rules saved as a smart list. Current catalog filters are unchanged." : "Reading list saved.";
  } catch (error) { if (generation === listDialogGeneration) $("list-status").textContent = error.message; }
  finally { if (generation === listDialogGeneration) { listBusy = false; $("list-controls").disabled = false; $("save-list").disabled = kind === "smart" && !listPreviewKey; } }
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
    await loadLists();
    $("management-status").textContent = "Reading list deleted. Books and reading progress were kept.";
  } catch (error) { if (epoch === authEpoch) $("management-status").textContent = error.message; }
  finally { if (epoch === authEpoch) { managementBusy = false; updateSelection(); } }
});

async function openDetails(id) {
  if (intake?.replacement) {
    if (intake.saving) return;
    resetIntake();
  }
  if (comicExport && !comicExport.selection) resetComicExport();
  const generation = ++detailGeneration;
  const epoch = authEpoch, hash = location.hash;
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
  $("export-comic").disabled = true;
  $("edit-metadata").hidden = true;
  $("find-metadata").hidden = true;
  $("write-archive").hidden = true;
  $("convert-cbz").hidden = true;
  $("intake-history").hidden = true;
  $("move-archive").hidden = true;
  $("edit-page-metadata").hidden = true;
  $("details-dialog").showModal();
  api(`/api/books/${encodeURIComponent(id)}/bookmarks`).then((bookmarks) => {
    if (generation !== detailGeneration) return;
    const section = document.createElement("details");
    section.append(textElement("summary", `My bookmarks (${bookmarks.length})`));
    const list = document.createElement("ul");
    for (const bookmark of bookmarks) list.append(textElement("li", `Physical page ${bookmark.page + 1}: ${bookmark.name}`));
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
    if (session.role === "admin" && !intake) {
      try {
        const status = await api(`/api/books/${encodeURIComponent(id)}/intake-processing`, { signal: AbortSignal.timeout(30000) });
        if (generation !== detailGeneration || epoch !== authEpoch || hash !== location.hash || !session || !$("details-dialog").open || detailBook?.id !== id) return;
        $("intake-history").hidden = session.role !== "admin" || status.state === "none";
      } catch (error) {
        if (generation === detailGeneration && epoch === authEpoch && hash === location.hash && $("details-dialog").open)
          $("details-status").textContent = `Could not load processing status: ${error.message}. Close and reopen Book details to retry.`;
      }
    }
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
  if (document.activeElement === $("retry-save")) $("page-stage").focus({ preventScroll: true });
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
        $("save-status").textContent = "";
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
  if (document.activeElement === $("retry-preferences")) $("page-stage").focus({ preventScroll: true });
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
function pageTypeLabel(type) {
  return (type || "Unclassified").replace(/([a-z])([A-Z])/g, (_, before, after) => `${before} ${after.toLowerCase()}`);
}

function pageLabel(page) {
  return `Position ${readingPositions[page] + 1}, Physical page ${page + 1}, ${pageTypeLabel(pages[page]?.type)}`;
}

let bookmarkDirty = false;

function renderBookmarks() {
  for (const [id, bookmarks, personal] of [
    ["personal-bookmarks", personalBookmarks, true],
    ["embedded-bookmarks", pages.filter((entry) => entry.bookmark).map((entry) => ({ page: entry.page, name: entry.bookmark })), false]
  ]) {
    const items = [...bookmarks].sort((a, b) => readingPositions[a.page] - readingPositions[b.page]).map((bookmark) => {
      const item = textElement("li", "");
      const jump = textElement("button", `${bookmark.name} — ${pageLabel(bookmark.page)}`);
      jump.type = "button";
      jump.dataset.page = String(bookmark.page);
      jump.addEventListener("click", () => showPage(bookmark.page));
      item.append(jump);
      if (personal) {
        const remove = textElement("button", "Delete", "danger");
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
  if (document.activeElement === $("retry-bookmark")) $("page-stage").focus({ preventScroll: true });
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
      if (record.name !== null) bookmarkDirty = false;
      if (!bookmarkDirty) bookmarkEditorPage = null;
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
  $("previous-page").dataset.direction = readerPreferences.rtl ? "right" : "left";
  $("reader-pages").className = `reader-pages ${readerPreferences.mode === "continuous" ? "continuous" : ""} ${readerPreferences.rtl ? "rtl" : ""}`;
  updateTouchMode();
  updatePageControls();
}

function spreadCandidates() {
  return spreadPages(currentPage, currentBook.pageCount, readerPreferences.coverAlone, () => false, isPageIncluded, readingOrder);
}

function spreadResolved() {
  const pair = spreadCandidates();
  return pair.some(isPageWide) || pair.every((page) => pages[page]?.width || pages[page]?.state === "error");
}

function updatePageControls() {
  const unavailable = leavingReader || !currentBook || !readerPreferences;
  $("reader-details").disabled = unavailable;
  const empty = !activePages.length;
  const resolving = !unavailable && !empty && readerPreferences.mode === "spread" && !spreadResolved();
  const first = readerPreferences?.mode === "continuous" ? currentPage : displayedPages[0] ?? currentPage;
  const last = readerPreferences?.mode === "continuous" ? currentPage : displayedPages.at(-1) ?? currentPage;
  if (!pageNumberDraft) $("page-number").value = String((readingPositions[currentPage] ?? 0) + 1);
  $("previous-page").disabled = unavailable || empty || resolving || nextPage(first, currentBook.pageCount, -1, (page) => pageIncluded(pages[page].typeMask, readerPreferences.skipPageTypes), readingOrder) === null;
  const atEnd = !unavailable && !empty && !resolving &&
    nextPage(last, currentBook.pageCount, 1, (page) => pageIncluded(pages[page].typeMask, readerPreferences.skipPageTypes), readingOrder) === null;
  if (atEnd && nextIssue.state === "idle") loadNextIssue();
  const nextLabel = !atEnd ? "Next page" : nextIssue.state === "loading" ? "Finding next issue…"
    : nextIssue.state === "error" ? "Retry next issue" : nextIssue.id ? "Next issue" : "No more issues";
  $("next-page").disabled = unavailable || empty || resolving ||
    (atEnd && (nextIssue.state === "loading" || (nextIssue.state === "ready" && !nextIssue.id)));
  $("next-page").setAttribute("aria-label", nextLabel);
  $("next-page").textContent = atEnd ? nextLabel : "Next";
  $("next-page").dataset.direction = atEnd && nextLabel !== "Next issue" ? "none" : readerPreferences?.rtl ? "left" : "right";
  for (const element of document.querySelectorAll(".display-controls input, .display-controls select, .display-controls button, #page-form input, #page-form button, #skip-types input, #skip-types button, #personal-bookmarks button, #embedded-bookmarks button")) element.disabled = unavailable;
  $("bookmark-name").disabled = unavailable || empty || bookmarkBusy || Boolean(unsavedBookmark);
  $("save-bookmark").disabled = $("bookmark-name").disabled;
  $("retry-bookmark").disabled = unavailable || bookmarkBusy;
  $("discard-bookmark").hidden = !bookmarkDirty;
  $("discard-bookmark").disabled = unavailable || bookmarkBusy || Boolean(unsavedBookmark);
  $("bookmark-draft").hidden = !bookmarkDirty;
  for (const button of $("personal-bookmarks").querySelectorAll("[data-delete-bookmark]")) button.disabled = unavailable || bookmarkBusy || Boolean(unsavedBookmark);
  if (!unavailable) {
    $("current-page-type").textContent = empty ? "No visible pages" : (readerPreferences.mode === "continuous" ? [currentPage] : displayedPages.length ? displayedPages : [currentPage]).map(pageLabel).join("; ");
    if (!bookmarkDirty && !unsavedBookmark && bookmarkEditorPage !== currentPage) {
      bookmarkEditorPage = currentPage;
      $("bookmark-name").value = personalBookmarks.find((bookmark) => bookmark.page === currentPage)?.name || "";
      $("bookmark-name").setCustomValidity("");
    }
    $("bookmark-page").textContent = `physical page ${bookmarkEditorPage + 1}`;
  }
  $("cover-alone").disabled = unavailable || readerPreferences?.mode !== "spread";
  updateFullscreen();
}

function createReaderPage(page, info) {
  const node = textElement("div", "", "reader-page");
  node.dataset.page = String(page);
  node.setAttribute("role", "group");
  node.setAttribute("aria-label", `Physical page ${page + 1}, ${pageTypeLabel(info?.type)}`);
  const message = textElement("div", "", "page-message");
  node.append(message);
  const entry = { page, type: info?.type || "", typeMask: info?.typeMask || 0, bookmark: info?.bookmark || "", node, message, width: 0, height: 0, state: "idle", url: null, image: null, controller: null };
  pageMessage(entry);
  return entry;
}

function pageMessage(entry, error = null) {
  if (entry.message.contains(document.activeElement)) $("page-stage").focus({ preventScroll: true });
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
      updatePageControls();
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
    ? spreadPages(currentPage, currentBook.pageCount, readerPreferences.coverAlone, isPageWide, isPageIncluded, readingOrder)
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
  activePages = readingOrder.filter(isPageIncluded);
  currentPage = isPageIncluded(anchor.page) ? anchor.page
    : nextPage(anchor.page, pages.length, 1, isPageIncluded, readingOrder) ?? nextPage(anchor.page, pages.length, -1, isPageIncluded, readingOrder) ?? anchor.page;
  if (currentPage !== anchor.page) anchor = { ...anchor, page: currentPage, offset: 0 };
  if (activePages.length) nearPages = new Set(readerPreferences.mode === "spread" ? spreadCandidates() : [currentPage]);
  for (const entry of pages) {
    if (readerPreferences.mode === "continuous" ? !isPageIncluded(entry.page) : !nearPages.has(entry.page)) releasePage(entry);
    entry.node.setAttribute("aria-label", `Physical page ${entry.page + 1}, ${pageTypeLabel(entry.type)}`);
    if (entry.image) entry.image.alt = `${bookTitle(currentBook)}, ${pageLabel(entry.page)} of ${currentBook.pageCount}`;
  }
  viewAnchor = activePages.length ? anchor : null;
  displayedPages = [];
  applyReaderControls();
  $("page-status").hidden = Boolean(activePages.length);
  $("page-status").textContent = "All pages are skipped. Choose Show all page types, change the skip checkboxes, or jump to a page or bookmark to reveal it.";
  $("skip-status").hidden = revealedPage === null;
  $("skip-status").textContent = revealedPage === null ? "" : `Physical page ${revealedPage + 1} is temporarily revealed despite its skipped type. Previous/Next still follow your skip filters.`;
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
  } else {
    refreshPagedView();
  }
  pumpPageLoads();
}

function pumpPageLoads() {
  if (!currentBook || leavingReader || !activePages.length) return;
  const wanted = [...nearPages].sort((a, b) => Math.abs(readingPositions[a] - readingPositions[currentPage]) - Math.abs(readingPositions[b] - readingPositions[currentPage]));
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
    image.draggable = false;
    image.src = url;
    await image.decode();
    if (epoch !== authEpoch || entry.controller !== controller || book !== currentBook) return;
    image.alt = `${bookTitle(book)}, ${pageLabel(entry.page)} of ${book.pageCount}`;
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
  visible.sort((a, b) => continuous && !atEnd ? a.distance - b.distance : readingPositions[b.page] - readingPositions[a.page]);
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

function invalidateReaderNextIssue(id) {
  if (currentBook?.id !== id) return;
  nextIssue = { state: "idle", id: null };
  updatePageControls();
}

async function refreshReaderSource(id) {
  const book = currentBook;
  if (book?.id !== id) return;
  const epoch = authEpoch;
  const path = `/api/books/${encodeURIComponent(id)}`;
  const [source, info, order] = await Promise.all([api(path), api(`${path}/page-info`), api(`${path}/page-order`)]);
  if (epoch !== authEpoch || book !== currentBook || leavingReader) return;
  validatePageOrder(order, id, source.pageCount);
  if (source.pageCount < 1) throw new Error("This book has no readable pages.");
  stopReaderView();
  for (const entry of pages) releasePage(entry);
  const pageInfo = new Map(info.map(entry => [entry.page, entry]));
  pages = Array.from({ length: source.pageCount }, (_, page) => createReaderPage(page, pageInfo.get(page)));
  book.pageCount = source.pageCount;
  setReadingOrder(order.pages);
  currentPage = Math.min(currentPage, source.pageCount - 1);
  revealedPage = pageIncluded(pages[currentPage].typeMask, readerPreferences.skipPageTypes) ? null : currentPage;
  pageNumberDraft = false;
  $("page-count").textContent = `/ ${book.pageCount}`;
  $("page-number").max = String(book.pageCount);
  renderReader();
  renderBookmarks();
}

async function loadNextIssue() {
  const book = currentBook;
  const request = nextIssue;
  request.state = "loading";
  try {
    const result = await api(`/api/books/${encodeURIComponent(book.id)}/next-issue`);
    if (request !== nextIssue || book !== currentBook) return;
    if (!result || !(result.id === null || (typeof result.id === "string" && result.id && result.id !== book.id)))
      throw new Error("Invalid next-issue response.");
    request.id = result.id;
    request.state = "ready";
  } catch (error) {
    if (request !== nextIssue || book !== currentBook) return;
    request.state = "error";
    showError(new Error(`Could not find the next issue. ${error.message} Use Retry next issue to try again.`));
  } finally {
    if (request === nextIssue && book === currentBook) updatePageControls();
  }
}

function turnPage(direction) {
  const button = $(direction > 0 ? "next-page" : "previous-page");
  if (button.disabled) return;
  const page = readerPreferences.mode === "continuous" ? currentPage : direction > 0 ? displayedPages.at(-1) : displayedPages[0];
  const target = nextPage(page, currentBook.pageCount, direction, (next) => pageIncluded(pages[next].typeMask, readerPreferences.skipPageTypes), readingOrder);
  if (target !== null) showPage(target, false);
  else if (direction > 0 && nextIssue.state === "error") {
    clearError();
    loadNextIssue();
    updatePageControls();
  } else if (direction > 0 && nextIssue.state === "ready" && nextIssue.id) {
    location.hash = `reader/${encodeURIComponent(nextIssue.id)}?location=${catalogLocation}`;
  }
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
    if (bookmarkDirty && !confirm("Discard the unsubmitted bookmark name?"))
      throw new Error("Bookmark name kept. Save or discard it before leaving the reader.");
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

async function route(hash, generation) {
  if (!session || session.mustChangePassword) return;
  const epoch = authEpoch;
  const returned = history.state?.returnCatalog;
  if (returned?.version === 2 && returned.username === session.username) catalogReturn = returned;
  const saved = history.state?.catalog ?? catalogReturn;
  const catalog = saved?.version === 2 && saved.username === session.username && saved.hash === hash ? saved : null;
  const match = /^#reader\/([^/?]+)(?:\?location=(library|archived|unsorted))?$/.exec(hash);
  const metadataMatch = /^#metadata\/([a-f0-9]{32}(?:,[a-f0-9]{32}){0,99})(\?bulk=1)?$/i.exec(hash);
  const id = match ? decodeURIComponent(match[1]) : null;
  if (currentBook && String(currentBook.id) === id && !leavingReader) {
    if (match[2]) setCatalogLocation(match[2]);
    return;
  }
  try {
    // ponytail: serialize save gates, not obsolete read requests.
    await (routeQueue = routeQueue.catch(() => {}).then(() => {
      if (epoch === authEpoch && generation === routeGeneration) return leaveReader();
    }));
    if (epoch !== authEpoch || generation !== routeGeneration || hash !== location.hash || !session || passwordBusy) return;
    if ((id || hash === "#settings") && catalogReturn)
      history.replaceState({ returnCatalog: catalogReturn }, "", location.href);
    clearError();
    $("password-form").reset();
    $("change-password").hidden = false;
    if (metadataMatch && session.role === "admin") {
      metadataPage ||= { hash, origin: history.state?.metadataOrigin ?? { hash: catalogLocation === "unsorted" ? "#unsorted" : "#library" } };
      await loadMetadataEditor(metadataMatch[1].split(","), !!metadataMatch[2]);
      return;
    }
    if (hash.startsWith("#metadata/")) history.replaceState(null, "", "#library");
    if (hash === "#settings") {
      if (session.role === "admin") {
        if (catalogReturn) setCatalogLocation(catalogReturn.location);
        resetServerSettings();
        showView("settings");
        $("server-settings-title").focus();
        window.scrollTo(0, 0);
        await loadServerSettings();
        return;
      }
      history.replaceState(null, "", "#library");
    }
    if (!id) {
      setCatalogLocation(catalog?.location ?? (hash === "#unsorted" ? "unsorted" : $("library-location").value === "archived" ? "archived" : "library"));
      if (catalog) {
        await Promise.all([loadLists(), loadPublishers()]);
        if (epoch !== authEpoch || generation !== routeGeneration || hash !== location.hash) return;
        for (const id of catalogControls) {
          $(id).value = catalog.filters[id];
          if ($(id).selectedIndex < 0) $(id).selectedIndex = 0;
        }
        $("search").value = libraryQuery = catalog.query;
        discoveryReturn = catalog.discoveryReturn ?? null;
        advancedFilter = catalog.advancedFilter ?? null;
        libraryPage = catalog.page;
        groupKey = catalog.groupKey;
        sectionKey = catalog.sectionKey;
        closedSections = new Set(catalog.closedSections);
        groupLabel = catalog.groupLabel;
        groupOrigin = catalog.groupOrigin;
        libraryReturnFocus = catalog.focus;
        clearSelection();
      } else {
        loadLists();
        loadPublishers();
      }
      showView("library");
      if (activeList()?.kind === "smart") clearSelection();
      const search = await loadLibrary();
      if (epoch === authEpoch && generation === routeGeneration) await restoreLibraryFocus(libraryReturnFocus, search);
      if (epoch === authEpoch && generation === routeGeneration) await restoreMetadataOrigin(hash, generation);
      return;
    }
    if (match[2]) setCatalogLocation(match[2]);
    showView("reader");
    $("reader-title").textContent = "Loading book…";
    updatePageControls();
    $("page-status").hidden = false;
    $("page-status").textContent = "Loading book and reader preferences…";
    $("save-status").textContent = "";
    $("retry-save").hidden = true;
    const [book, , pageInfo, bookmarks, order] = await Promise.all([api(`/api/books/${encodeURIComponent(id)}`), loadReaderPreferences(),
      api(`/api/books/${encodeURIComponent(id)}/page-info`), api(`/api/books/${encodeURIComponent(id)}/bookmarks`), api(`/api/books/${encodeURIComponent(id)}/page-order`)]);
    if (epoch !== authEpoch || generation !== routeGeneration || hash !== location.hash || !session) return;
    if (book.availability !== "available") throw new Error("This source is missing or unavailable. Restore or reconnect it and scan again. Metadata and bookmarks remain accessible in Book details.");
    if (book.pageCount < 1) throw new Error("This book has no readable pages.");
    validatePageOrder(order, id, book.pageCount);
    setReadingOrder(order.pages);
    currentBook = book;
    const info = new Map(pageInfo.map((entry) => [entry.page, entry]));
    pages = Array.from({ length: book.pageCount }, (_, page) => createReaderPage(page, info.get(page)));
    personalBookmarks = bookmarks;
    currentPage = book.started ? Math.max(0, Math.min(book.currentPage, book.pageCount - 1)) : readingOrder[0];
    $("reader-title").textContent = bookTitle(book);
    document.title = `${bookTitle(book)} — Panel Haven`;
    $("page-count").textContent = `/ ${book.pageCount}`;
    $("page-number").max = String(book.pageCount);
    $("page-stage").focus({ preventScroll: true });
    renderReader();
    renderBookmarks();
    $("reader-view").scrollIntoView({ block: "start" });
    await restoreMetadataOrigin(hash, generation);
  } catch (error) {
    if (epoch !== authEpoch || generation !== routeGeneration || hash !== location.hash) return;
    if (currentBook) history.replaceState(null, "", `#reader/${encodeURIComponent(currentBook.id)}?location=${catalogLocation}`);
    else if (session) {
      history.replaceState(null, "", catalogLocation === "unsorted" ? "#unsorted" : "#library");
      showView("library");
      loadPublishers();
      const search = await loadLibrary();
      if (epoch === authEpoch && generation === routeGeneration) await restoreLibraryFocus(libraryReturnFocus, search);
    }
    if (metadataPage?.hash === hash) {
      const context = metadataPage;
      resetMetadataEditor();
      if (context.origin.hash === location.hash) {
        metadataReturn = context;
        await restoreMetadataOrigin(location.hash, generation);
      }
    }
    if (epoch === authEpoch && generation === routeGeneration) showError(error);
  }
}

function scheduleRoute() {
  if (passwordBusy) {
    history.replaceState(null, "", "#library");
    return;
  }
  if (!leavePassword()) {
    history.replaceState(null, "", "#library");
    return;
  }
  if (kavitaImport?.applying) {
    history.replaceState(history.state, "", kavitaImport.hash);
    return;
  }
  if (kavitaImport) resetKavitaImport();
  if (metadataPage && location.hash !== metadataPage.hash) {
    if (!leaveMetadata()) {
      history.replaceState({ returnCatalog: catalogReturn, metadataOrigin: metadataPage.origin }, "", metadataPage.hash);
      return;
    }
    if (location.hash === metadataPage?.origin.hash) metadataReturn = metadataPage;
    resetMetadataEditor();
  }
  if (deletionBusy) {
    history.replaceState(null, "", deletion.hash);
    rememberCatalogView();
    return;
  }
  if (intake) {
    const dialogs = [...document.querySelectorAll("dialog[open]")].filter((dialog) => dialog.id !== "intake-dialog").reverse();
    if (!canCloseDialog("intake-dialog") || dialogs.some((dialog) => !canCloseDialog(dialog.id))) {
      history.replaceState(null, "", intake.hash);
      rememberCatalogView();
      return;
    }
    for (const dialog of dialogs) dialog.close();
    resetIntake();
  }
  if (location.hash !== "#settings" && !leaveServerSettings()) {
    history.replaceState(null, "", "#settings");
    return;
  }
  const generation = ++routeGeneration;
  const epoch = authEpoch;
  searchGeneration++;
  finishLibraryLoading();
  publisherGeneration++;
  route(location.hash, generation).catch((error) => {
    if (epoch === authEpoch && generation === routeGeneration) showError(error);
  });
}

document.querySelector(".skip-link").addEventListener("click", (event) => {
  event.preventDefault();
  $("main").focus();
  $("main").scrollIntoView({ block: "start" });
});
for (const button of document.querySelectorAll("[data-settings-section]")) button.addEventListener("click", (event) => {
  event.preventDefault();
  const section = $(button.dataset.settingsSection);
  section.focus();
  section.scrollIntoView({ block: "start" });
});

$("login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = event.submitter;
  button.disabled = true;
  clearError();
  $("login-message").textContent = "Signing in…";
  try {
    const values = Object.fromEntries(new FormData(event.currentTarget));
    signedIn(await api("/api/login", { method: "POST", body: JSON.stringify(values) }));
  } catch (error) { showError(error); }
  finally { button.disabled = false; if ($("login-message").textContent === "Signing in…") $("login-message").textContent = ""; }
});

$("logout").addEventListener("click", async () => {
  if (passwordBusy || kavitaImport?.applying || deletionBusy || organizationBusy) return;
  resetKavitaImport();
  if (!leavePassword() || !leaveServerSettings() || !leaveMetadata()) return;
  const epoch = authEpoch;
  $("logout").disabled = true;
  try {
    await leaveReader();
    if (epoch !== authEpoch) return;
    await api("/api/logout", { method: "POST" });
    clearError();
    showLogin();
  } catch (error) { if (epoch === authEpoch) showError(error); }
  finally { if (epoch === authEpoch) $("logout").disabled = passwordBusy; }
});

function changeLibrary(resetGroup = true, focusResults = false) {
  if (seriesView() && $("reading-list").value) $("book-status").value = "all";
  clearSelection();
  if (resetGroup || libraryQuery !== $("search").value.trim()) {
    groupKey = sectionKey = null;
    groupOrigin = null;
    groupLabel = "";
    closedSections.clear();
  }
  $("sort-direction").disabled = $("book-sort").value === "list";
  if ($("book-sort").value === "list") $("sort-direction").value = "asc";
  libraryPage = 1;
  libraryQuery = $("search").value.trim();
  clearError();
  loadLibrary(focusResults);
}

$("clear-filters").addEventListener("click", () => {
  rememberCatalogView();
  history.pushState(null, "", location.href);
  const discovery = Boolean(seriesView());
  advancedFilter = null;
  $("search").value = libraryQuery = "";
  $("reading-list").value = $("publisher").value = $("book-rating").value = "";
  $("book-status").value = $("book-availability").value = "all";
  if ($("book-sort").value === "list") $("book-sort").value = "number";
  changeLibrary(discovery, true);
});
$("search-form").addEventListener("submit", (event) => { event.preventDefault(); changeLibrary(); });
$("clear-search").addEventListener("click", () => { $("search").value = ""; changeLibrary(); });
$("search").addEventListener("search", () => { if (!$("search").value) changeLibrary(); });
$("library-location").addEventListener("change", () => {
  const hash = $("library-location").value === "unsorted" ? "#unsorted" : "#library";
  if (location.hash !== hash) { location.hash = hash; return; }
  setCatalogLocation($("library-location").value);
  document.title = `${$("library-title").textContent} — Panel Haven`;
  loadPublishers();
  changeLibrary();
});
for (const id of ["book-sort", "sort-direction", "book-rating", "publisher", "page-size", "book-availability"]) $(id).addEventListener("change", () => changeLibrary(false));
$("book-status").addEventListener("change", () => {
  if (seriesView() && $("reading-status-hint").hidden) discoveryReturn = catalogReturn;
  const resetGroup = Boolean(seriesView()) || !$("reading-status-hint").hidden || $("book-status").value === "in-progress";
  if (seriesView()) {
    advancedFilter = null;
    $("search").value = libraryQuery = "";
    $("reading-list").value = $("publisher").value = $("book-rating").value = "";
    $("book-availability").value = "all";
    $("group-by").value = "none";
    $("stack-by").value = "series";
    $("book-sort").value = "number";
    $("sort-direction").value = "asc";
  } else if ($("book-status").value === "in-progress") $("stack-by").value = "none";
  changeLibrary(resetGroup);
});
for (const id of ["group-by", "stack-by"]) $(id).addEventListener("change", () => changeLibrary());
$("return-to-browse").addEventListener("click", () => {
  if (!discoveryReturn || discoveryReturn.username !== session?.username) return;
  const previous = discoveryReturn;
  rememberCatalogView();
  history.pushState({ catalog: previous }, "", previous.hash);
  scheduleRoute();
});
$("back-to-groups").addEventListener("click", async () => {
  const origin = groupOrigin;
  if (!origin) { changeLibrary(true, true); return; }
  rememberCatalogView();
  history.pushState(null, "", location.href);
  for (const id of catalogControls) {
    $(id).value = origin.filters[id];
    if ($(id).selectedIndex < 0) $(id).selectedIndex = 0;
  }
  advancedFilter = origin.advancedFilter ?? null;
  closedSections = new Set(origin.closedSections);
  groupKey = sectionKey = null;
  groupLabel = "";
  groupOrigin = null;
  libraryPage = origin.page;
  libraryReturnFocus = origin.key;
  clearSelection();
  const generation = await loadLibrary();
  restoreLibraryFocus(origin.key, generation);
});
for (const suffix of ["", "-bottom"]) {
  $("previous-books" + suffix).addEventListener("click", () => { if (libraryPage > 1) { libraryPage--; loadLibrary(true); } });
  $("next-books" + suffix).addEventListener("click", () => { if (libraryPage < libraryPages) { libraryPage++; loadLibrary(true); } });
}
$("refresh-library").addEventListener("click", () => { failedCovers.clear(); clearError(); loadLibrary(true); });
$("scan").addEventListener("click", () => requestScan("start"));
$("cancel-scan").addEventListener("click", () => requestScan("cancel"));
$("retry-scan-status").addEventListener("click", () => requestScan());
$("export-scan-log").addEventListener("click", async () => {
  if (session?.role !== "admin" || !scanStatus?.logAvailable || scanLogRequest) return;
  const epoch = authEpoch;
  const request = scanLogRequest = new AbortController();
  $("scan-log-status").textContent = "Preparing scan log download…";
  renderScan();
  try {
    const blob = await api("/api/scan/log", { blob: true, signal: request.signal });
    if (epoch !== authEpoch || request !== scanLogRequest) return;
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `panelhaven-scan-log-${new Date().toISOString().replace(/[:.]/g, "-")}.jsonl`;
    document.body.append(link);
    try { link.click(); }
    finally { link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
    $("scan-log-status").textContent = "Scan log download started.";
  } catch (error) {
    if (epoch === authEpoch && request === scanLogRequest && error.name !== "AbortError")
      $("scan-log-status").textContent = `Scan log export failed: ${error.message} Try exporting again.`;
  } finally {
    if (request === scanLogRequest) { scanLogRequest = null; renderScan(); }
  }
});
$("dismiss-scan").addEventListener("click", () => {
  rememberScanDismissal(scanResultSignature());
  renderScan();
  $("account-menu").open = false;
  $("account-menu").querySelector("summary").focus();
});
$("maintenance").addEventListener("click", () => {
  $("maintenance-error").hidden = true;
  $("maintenance-dialog").showModal();
  loadMaintenance();
});
$("maintenance-dialog").addEventListener("close", () => { maintenanceGeneration++; });
for (const id of ["maintenance-kind", "maintenance-ignored"]) $(id).addEventListener("change", () => {
  maintenancePage = 1;
  $("maintenance-items").replaceChildren();
  $("maintenance-error").hidden = true;
  loadMaintenance();
});
$("refresh-maintenance").addEventListener("click", () => { $("maintenance-error").hidden = true; loadMaintenance(); });
$("remove-all-missing").addEventListener("click", () => removeMissingBooks());
$("previous-maintenance").addEventListener("click", () => { if (maintenancePage > 1) { maintenancePage--; loadMaintenance(); } });
$("next-maintenance").addEventListener("click", () => { if (maintenancePage < maintenancePages) { maintenancePage++; loadMaintenance(); } });

for (const button of document.querySelectorAll("[data-close]")) button.addEventListener("click", () => {
  if (canCloseDialog(button.dataset.close)) $(button.dataset.close).close();
});
$("details-dialog").addEventListener("close", () => {
  if (intake?.replacement && !intake.saving) resetIntake();
  detailGeneration++;
  ratingDirty = false;
  if (comicExport && !comicExport.selection) resetComicExport();
});
$("read-book").addEventListener("click", () => {
  if (!detailBook || detailBook.availability !== "available" || !canCloseDialog("details-dialog")) return;
  if ($("list-dialog").open && !canCloseDialog("list-dialog")) return;
  $("list-dialog").close();
  $("details-dialog").close();
  $("maintenance-dialog").close();
  $("metadata-report-dialog").close();
  location.hash = `reader/${encodeURIComponent(detailBook.id)}?location=${catalogLocation}`;
});

function passwordDirty() {
  return !$("password-view").hidden && [...$("password-form").querySelectorAll('input[type="password"]')].some(input => input.value);
}

function leavePassword() {
  if (passwordBusy || (passwordDirty() && !confirm("Discard the entered passwords?"))) return false;
  $("password-form").reset();
  return true;
}


$("change-password").addEventListener("click", async () => {
  if (deletionBusy || !leaveServerSettings() || !leaveMetadata()) return;
  const epoch = authEpoch;
  try {
    await leaveReader();
    if (epoch !== authEpoch) return;
    history.replaceState(null, "", "#library");
    openPasswordView();
  } catch (error) { if (epoch === authEpoch) showError(error); }
});
$("cancel-password").addEventListener("click", () => {
  if (!session?.mustChangePassword && !passwordBusy) {
    $("password-form").reset();
    scheduleRoute();
  }
});
for (const event of ["input", "reset"]) $("password-form").addEventListener(event, () => {
  clearFieldError($("password-form").elements.confirmation, $("password-status"));
  clearFieldError($("password-form").elements.newPassword, $("password-status"));
});
$("password-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (passwordBusy) return;
  const form = event.currentTarget;
  const { currentPassword, newPassword, confirmation } = form.elements;
  if (newPassword.value !== confirmation.value) {
    $("password-status").textContent = "The new passwords do not match.";
    confirmation.setCustomValidity($("password-status").textContent);
    confirmation.reportValidity();
    return;
  }
  if (currentPassword.value === newPassword.value) {
    $("password-status").textContent = "Choose a password different from your current password.";
    newPassword.setCustomValidity($("password-status").textContent);
    newPassword.reportValidity();
    return;
  }
  const epoch = authEpoch;
  setPasswordBusy(true);
  $("password-status").textContent = "Changing password…";
  try {
    await api("/api/account/password", { method: "POST", body: JSON.stringify({ currentPassword: currentPassword.value, newPassword: newPassword.value }) });
    if (epoch !== authEpoch) return;
    showLogin();
    $("login-message").textContent = "Password changed. All your sessions have ended. Sign in with your new password.";
  } catch (error) {
    if (epoch === authEpoch) $("password-status").textContent = `Password change: ${error.message}${error.status && error.status < 500
      ? " Review your entries before submitting again."
      : " The outcome may be unknown. No automatic retry was made; sign out and check which password works before trying another change."}`;
  }
  finally {
    if (epoch === authEpoch) {
      setPasswordBusy(false);
      if (!$("password-view").hidden) $("password-status").focus();
    }
  }
});

function accountDirty() {
  return [...$("user-controls").querySelectorAll("input, select")].some((control) =>
    control.value !== (control.tagName === "SELECT" ? control.dataset.savedValue ?? control.options[0].value : control.defaultValue));
}

async function loadUsers() {
  const epoch = authEpoch;
  const users = await api("/api/users");
  if (epoch !== authEpoch || !$("user-dialog").open) return;
  for (const row of [...$("user-list").children])
    if (!users.some((user) => user.id === row.dataset.userId)) row.remove();
  for (const user of users) {
    const existing = $("user-list").querySelector(`[data-user-id="${CSS.escape(user.id)}"]`);
    const passwordStatus = user.mustChangePassword ? "Password change required at next sign-in." : "Password change not required.";
    if (existing) {
      const role = existing.querySelector('select[name="role"]');
      if (role.value === role.dataset.savedValue) role.value = user.role;
      role.dataset.savedValue = user.role;
      existing.querySelector("p").textContent = passwordStatus;
      continue;
    }
    const row = textElement("section", "", "user-row");
    row.dataset.userId = user.id;
    const heading = textElement("h3", `${user.username}${user.id === session.id ? " (you)" : ""}`);
    heading.id = `user-heading-${user.id}`;
    row.setAttribute("aria-labelledby", heading.id);
    row.append(heading, textElement("p", passwordStatus, "muted"));
    const roleForm = document.createElement("form");
    roleForm.className = "actions";
    roleForm.innerHTML = '<label>Role <select name="role"><option value="reader">Reader</option><option value="admin">Administrator</option></select></label><button type="submit">Save role</button>';
    roleForm.querySelector("label").firstChild.textContent = `Role for ${user.username} `;
    roleForm.querySelector("button").setAttribute("aria-label", `Save role for ${user.username}`);
    roleForm.elements.role.value = user.role;
    roleForm.elements.role.dataset.savedValue = user.role;
    roleForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const role = roleForm.elements.role.value;
      const endSession = user.id === session.id && role !== roleForm.elements.role.dataset.savedValue;
      if (!confirm(`Change “${user.username}” to ${role}? Changed roles end all sessions for this account.${endSession ? " You will be signed out." : ""}`)) return;
      mutateAccount(`/api/users/${encodeURIComponent(user.id)}/role`, "PUT", { role }, "Role saved.", endSession, null, `Role change for “${user.username}”`);
    });
    row.append(roleForm);
    if (user.id === session.id) {
      const ownPassword = textElement("button", "Change my password");
      ownPassword.type = "button";
      ownPassword.setAttribute("aria-label", `Change my password: ${user.username}`);
      ownPassword.addEventListener("click", () => {
        if (!canCloseDialog("user-dialog")) return;
        $("user-dialog").close();
        $("change-password").click();
      });
      row.append(ownPassword);
    } else {
      const reset = document.createElement("details");
      reset.append(textElement("summary", "Reset password"));
      reset.querySelector("summary").setAttribute("aria-label", `Reset password for ${user.username}`);
      const form = document.createElement("form");
      form.className = "form-stack";
      form.innerHTML = '<p class="muted">Both password fields are required. Use 12–256 characters.</p><label>New password<input name="password" type="password" autocomplete="new-password" required minlength="12" maxlength="256"></label><label>Confirm new password<input name="confirmation" type="password" autocomplete="new-password" required minlength="12" maxlength="256"></label><button type="submit">Reset password</button>';
      form.elements.password.parentElement.firstChild.textContent = `New password for ${user.username}`;
      form.elements.confirmation.parentElement.firstChild.textContent = `Confirm new password for ${user.username}`;
      form.querySelector("button").setAttribute("aria-label", `Reset password for ${user.username}`);
      form.querySelector("p").id = `reset-password-help-${user.id}`;
      for (const input of form.querySelectorAll("input")) input.setAttribute("aria-describedby", `reset-password-help-${user.id}`);
      for (const event of ["input", "reset"]) form.addEventListener(event, () => clearFieldError(form.elements.confirmation, $("user-status")));
      form.addEventListener("submit", (event) => {
        event.preventDefault();
        if (form.elements.password.value !== form.elements.confirmation.value) {
          $("user-status").textContent = "The new passwords do not match.";
          form.elements.confirmation.setCustomValidity($("user-status").textContent);
          form.elements.confirmation.reportValidity();
          return;
        }
        if (!confirm(`Reset the password for “${user.username}”? All their sessions will end and they must change this password at next sign-in.`)) return;
        mutateAccount(`/api/users/${encodeURIComponent(user.id)}/password`, "PUT", { password: form.elements.password.value }, "Password reset. The account must change it at next sign-in.", false, form, `Password reset for “${user.username}”`);
      });
      reset.append(form);
      row.append(reset);
    }
    const remove = textElement("button", "Delete account", "danger");
    remove.type = "button";
    remove.setAttribute("aria-label", `Delete account: ${user.username}`);
    remove.addEventListener("click", () => {
      if (!confirm(`Permanently delete “${user.username}”? Their private reading history, ratings, bookmarks, lists, list folders, and reader preferences will be lost. Shared books and files are kept. This cannot be undone. The last administrator is protected.${user.id === session.id ? " You will be signed out." : ""}`)) return;
      mutateAccount(`/api/users/${encodeURIComponent(user.id)}`, "DELETE", null, "Account deleted.", user.id === session.id, null, `Account deletion for “${user.username}”`);
    });
    row.append(remove);
    $("user-list").append(row);
  }
}

async function mutateAccount(path, method, body, message, endSession = false, form = null, context) {
  if (accountBusy) return;
  accountBusy = true;
  let focus = document.activeElement;
  const nextFocus = focus.closest(".user-row")?.nextElementSibling?.querySelector("button") || $("user-form").elements.username;
  const epoch = authEpoch;
  let submitted = false;
  $("user-controls").disabled = true;
  $("user-dialog").querySelector("[data-close]").disabled = true;
  $("user-status").textContent = "Saving…";
  try {
    if (endSession) await leaveReader();
    if (epoch !== authEpoch) return;
    submitted = true;
    await api(path, { method, ...(body ? { body: JSON.stringify(body) } : {}) });
    if (epoch !== authEpoch) return;
    form?.reset();
    if (endSession) {
      showLogin();
      $("login-message").textContent = "Your account changed. You have been signed out.";
      return;
    }
    $("user-status").textContent = message;
    try { await loadUsers(); }
    catch (error) {
      if (epoch === authEpoch) {
        focus = $("user-status");
        focus.textContent = `${context}: ${message} Accounts could not refresh: ${error.message}. Other drafts are kept; reopen Accounts to check the saved state.`;
      }
    }
  } catch (error) {
    if (epoch !== authEpoch) return;
    focus = $("user-status");
    if (!submitted || error.name === "AbortError" || (error.status && error.status < 500)) {
      $("user-status").textContent = `${context}: ${error.message} Review the message and your entries before submitting again. Your draft is retained.`;
    } else {
      const warning = `${context}: the outcome of this account change is unknown. It may have been saved. No automatic retry was made.`;
      $("user-status").textContent = `${warning} Refreshing accounts…`;
      try {
        await loadUsers();
        if (epoch === authEpoch) $("user-status").textContent = `${warning} Account details refreshed; review them before submitting again. A refreshed list cannot confirm a password change. Drafts are kept.`;
      } catch {
        if (epoch === authEpoch) $("user-status").textContent = `${warning} Accounts could not refresh. Close and reopen Accounts to check before submitting again. A refreshed list cannot confirm a password change. Drafts are kept until you close.`;
      }
    }
  }
  finally {
    if (epoch === authEpoch) {
      accountBusy = false;
      $("user-controls").disabled = false;
      $("user-dialog").querySelector("[data-close]").disabled = false;
      if ($("user-dialog").open) (focus.isConnected ? focus : nextFocus.isConnected ? nextFocus : $("user-form").elements.username).focus();
    }
  }
}

$("manage-users").addEventListener("click", async () => {
  if (accountBusy) return;
  $("user-status").textContent = "Loading accounts…";
  $("user-form").reset();
  $("user-list").replaceChildren();
  $("user-controls").disabled = true;
  $("user-dialog").querySelector("[data-close]").disabled = false;
  $("user-dialog").showModal();
  const epoch = authEpoch;
  try { await loadUsers(); if (epoch === authEpoch) $("user-status").textContent = ""; }
  catch (error) {
    if (epoch === authEpoch && $("user-dialog").open) {
      $("user-status").textContent = `Could not load accounts: ${error.message} Close and reopen Accounts to try again.`;
      $("user-status").focus();
    }
  }
  finally { if (epoch === authEpoch) $("user-controls").disabled = false; }
});
$("user-dialog").addEventListener("close", () => {
  $("user-form").reset();
  $("user-list").replaceChildren();
});
$("user-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const values = Object.fromEntries(new FormData(form));
  mutateAccount("/api/users", "POST", values, `Account “${values.username}” created.`, false, form, `Create account “${values.username}”`);
});

$("close-reader").addEventListener("click", () => { location.hash = catalogLocation === "unsorted" ? "unsorted" : "library"; });
$("previous-page").addEventListener("click", () => turnPage(-1));
$("next-page").addEventListener("click", () => turnPage(1));
for (const event of ["focus", "input"]) $("page-number").addEventListener(event, () => { pageNumberDraft = true; });
$("page-form").addEventListener("submit", (event) => { event.preventDefault(); showPage(readingOrder[Number($("page-number").value) - 1]); });
$("bookmark-form").addEventListener("submit", (event) => {
  event.preventDefault();
  if (!currentBook || !activePages.length) return;
  const name = $("bookmark-name").value.trim();
  if (!name || name.length > 200 || /[\p{Cc}]/u.test(name)) {
    $("bookmark-name").setCustomValidity("Enter a name of 1–200 characters without control characters.");
    $("bookmark-name").reportValidity();
    return;
  }
  queueBookmark({ page: bookmarkEditorPage, name });
});
$("bookmark-name").addEventListener("input", () => {
  $("bookmark-name").setCustomValidity("");
  bookmarkDirty = $("bookmark-name").value !== (personalBookmarks.find((bookmark) => bookmark.page === bookmarkEditorPage)?.name || "");
  updatePageControls();
});
$("discard-bookmark").addEventListener("click", () => {
  if (leavingReader || bookmarkBusy || unsavedBookmark) return;
  bookmarkDirty = false;
  bookmarkEditorPage = null;
  updatePageControls();
  $("bookmark-name").focus({ preventScroll: true });
});
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
$("page-stage").addEventListener("keydown", (event) => {
  if (!currentBook || leavingReader || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.target.closest("button, a, input, textarea, select, summary, [contenteditable], dialog")) return;
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
window.addEventListener("popstate", () => { if (history.state?.catalog) scheduleRoute(); });
window.addEventListener("hashchange", () => { if (!history.state?.catalog) scheduleRoute(); });
window.addEventListener("hashchange", resetComicExport);
window.addEventListener("pagehide", cancelIntakeRequest);
window.addEventListener("pagehide", resetComicExport);
window.addEventListener("beforeunload", (event) => {
  if (unsavedProgress || unsavedPreferences || unsavedBookmark || bookmarkDirty || passwordDirty() || serverSettingsDirty() || serverSettingsBusy || dataRelocationDirty() || pageOrderDirty() || pageOrderBusy || ratingDirty || ratingBusy || folderDirty || folderBusy || metadataDirty || metadataBusy || pageMetadataDirty || pageMetadataBusy || listDirty || listBusy || archiveBusy
    || intake?.saving || kavitaImport?.applying || fileOperationBusy || organizationBusy || accountBusy || passwordBusy || deletionBusy
    || ($("file-operation-dialog").open && fileOperationDirty())
    || ($("organization-dialog").open && organizationDirty())
    || ($("user-dialog").open && accountDirty())) { event.preventDefault(); event.returnValue = ""; }
});

(async () => {
  try {
    const data = await api("/api/session");
    if (data.authenticated) signedIn(data);
    else showLogin();
  } catch (error) { showLogin(); showError(error); }
})();
