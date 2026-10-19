// Run in the browser console on an idle Library page as an administrator.
// Requires a readable series containing a book with at least four pages and a configured move root.
// Writes are intercepted; disposable real-app frames cover reloads and reader permissions.
// Reload afterward to discard local-only preferences. Pass true below to inject a cleanup failure.
(async (failCleanup = false) => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 30000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("UI audit check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const change = (id, value) => { $(id).value = value; $(id).dispatchEvent(new Event("change", { bubbles: true })); };
  const idle = () => $("books").getAttribute("aria-busy") !== "true";
  const visibleFocus = () => document.activeElement !== document.body && document.activeElement.checkVisibility();
  const originalFetch = window.fetch;
  const originalConfirm = window.confirm;
  const pageRequests = [];
  let release, holdCatalog = false, failCatalog = false;
  let scanOverride = false, watchOverride = false, scanPolls = 0;
  assert(!$("library-view").hidden && !document.querySelector("dialog[open]"), "Start on the idle Library page.");
  window.fetch = async (input, options = {}) => {
    const url = new URL(input instanceof Request ? input.url : input, location.href);
    const method = options.method || (input instanceof Request ? input.method : "GET");
    if (method !== "GET" && (url.pathname.endsWith("/progress") || url.pathname === "/api/reader-preferences")) return new Response(null, { status: 204 });
    if (/\/pages\/\d+$/.test(url.pathname)) pageRequests.push(url.pathname);
    if (method === "GET" && url.pathname === "/api/scan" && scanOverride) {
      const status = await originalFetch(input, options).then(response => response.json());
      scanPolls++;
      return new Response(JSON.stringify({ ...status, state: "running", errorCount: 1, errors: ["Audit-only simulated scan error"] }), { headers: { "Content-Type": "application/json" } });
    }
    if (method === "GET" && url.pathname === "/api/watch-status" && watchOverride) {
      const status = await originalFetch(input, options).then(response => response.json());
      return new Response(JSON.stringify({ ...status, unavailableRoots: 1 }), { headers: { "Content-Type": "application/json" } });
    }
    if (url.pathname === "/api/books") {
      if (holdCatalog) { holdCatalog = false; await new Promise(resolve => { release = resolve; }); }
      if (failCatalog) { failCatalog = false; throw new TypeError("Simulated catalog failure"); }
    }
    return originalFetch(input, options);
  };
  try {
    $("clear-selection").click();
    change("group-by", "series");
    await waitFor(idle);
    const group = $("books").querySelector(".group-card .cover-button");
    assert(group, "A series group is required.");
    group.click();
    await waitFor(idle);
    const groupLabel = $("group-label").textContent;
    holdCatalog = true;
    change("book-sort", "title");
    assert($("books").inert, "Obsolete results must not remain interactive while loading.");
    release();
    await waitFor(idle);
    assert(!$("books").inert && !$("group-navigation").hidden && $("group-label").textContent === groupLabel, "Sorting must unlock results and preserve the opened series.");
    assert(visibleFocus(), "Result navigation must leave visible focus.");
    failCatalog = true;
    $("refresh-library").click();
    await waitFor(idle);
    assert(!$("books").inert, "Failed loads must release the inert result state.");
    $("refresh-library").click();
    await waitFor(idle);
    const checkbox = $("books").querySelector(".book-selection input");
    assert(checkbox, "The series must contain a selectable comicbook.");
    const bookId = checkbox.dataset.bookId;
    checkbox.click();
    $("clear-selection").focus();
    $("clear-selection").click();
    assert(visibleFocus() && $("management-status").textContent.includes("cleared"), "Clearing selection must preserve visible focus and announce its result.");
    checkbox.click();
    $("bulk-metadata").click();
    await waitFor(() => !$("metadata-controls").disabled);
    const apply = $("metadata-fields").querySelector(".apply-change");
    apply.click();
    $("metadata-changed-only").click();
    apply.focus();
    apply.click();
    assert(visibleFocus(), "Unchecking the last filtered metadata row must not hide keyboard focus.");
    window.confirm = () => true;
    document.querySelector('[data-close="metadata-dialog"]').click();
    $("clear-selection").click();
    $("account-menu").open = true;
    $("maintenance").focus();
    $("maintenance").click();
    document.querySelector('[data-close="maintenance-dialog"]').click();
    assert(document.activeElement === $("account-menu").querySelector("summary") && visibleFocus(), "Administration dialogs must return focus to the visible menu trigger.");
    location.hash = `#reader/${encodeURIComponent(bookId)}?location=library`;
    await waitFor(() => !$("reader-details").disabled && $("reader-pages").querySelector("img")?.naturalWidth > 0);
    $("reader-details").click();
    await waitFor(() => !$("move-archive").hidden);
    $("move-archive").click();
    await waitFor(() => !$("file-operation-dialog").querySelector("[data-close]").disabled);
    assert(document.activeElement === $("file-operation-title") && $("file-operation-dialog").scrollTop === 0, "Move must open at its visible heading, not its history.");
    document.querySelector('[data-close="file-operation-dialog"]').click();
    document.querySelector('[data-close="details-dialog"]').click();
    change("reader-mode", "single");
    if (!$("cover-alone").checked) $("cover-alone").click();
    $("page-number").value = "1";
    $("page-form").requestSubmit();
    await waitFor(() => $("reader-pages").querySelector("img")?.naturalWidth > 0 && $("page-number").value === "1");
    const coverPath = `/api/books/${encodeURIComponent(bookId)}/pages/0`;
    const before = pageRequests.filter(path => path === coverPath).length;
    change("reader-mode", "spread");
    await waitFor(() => $("reader-pages").querySelector("img")?.naturalWidth > 0);
    assert(pageRequests.filter(path => path === coverPath).length === before, "Changing layout must reuse an already loaded visible page without fetching it again.");
    $("close-reader").click();
    await waitFor(() => $("reader-view").hidden);
    scanOverride = watchOverride = true;
    await waitFor(() => $("scan-summary").textContent.includes("1 error") && $("watch-status").textContent.includes("1 unavailable"));
    assert($("scan-panel").open && $("watch-status").closest("details").open, "New scan and watcher errors must reveal their details.");
    $("scan-panel").open = false;
    $("watch-status").closest("details").open = false;
    const polls = scanPolls;
    await waitFor(() => scanPolls > polls);
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    assert(!$("scan-panel").open && !$("watch-status").closest("details").open, "Unchanged polling must preserve manually collapsed details.");
    // A fresh real document/module retains reload coverage without source extraction or a fake DOM.
    const html = await originalFetch("/").then(response => response.text());
    const identity = await originalFetch("/api/session").then(response => response.json());
    const bookPath = `/api/books/${encodeURIComponent(bookId)}`;
    const book = await originalFetch(bookPath).then(response => response.json());
    const order = await originalFetch(`${bookPath}/page-order`).then(response => response.json());
    assert(book.pageCount >= 4, "The selected series book must have at least four pages.");
    const storageKey = `panelhaven.dismissed-scan:${identity.username}`;
    const savedDismissal = localStorage.getItem(storageKey);
    const scanResult = { state: "completed", scanned: 987654, added: 0, updated: 0, skipped: 987654, errorCount: 0, errors: [], revision: 1 };
    const failedPages = new Set();
    let frame, win, doc, holdPath, releaseRequest, heldPage, releaseImage, allowMove = false, confirmCalls = 0;
    const element = id => doc.getElementById(id);
    const close = id => element(id).querySelector("[data-close]").click();
    const clean = () => {
      const calls = confirmCalls;
      const event = new win.Event("beforeunload", { cancelable: true });
      win.dispatchEvent(event);
      assert(confirmCalls === calls, "Unload protection must not call confirm.");
      return !event.defaultPrevented;
    };
    const pollScan = async () => {
      await waitFor(() => !element("retry-scan-status").disabled);
      element("retry-scan-status").click();
      await waitFor(() => !element("retry-scan-status").disabled);
    };
    const openReader = async () => {
      win.location.hash = `#reader/${encodeURIComponent(bookId)}?location=library`;
      await waitFor(() => !element("reader-details").disabled);
    };
    const closeReader = async () => {
      element("close-reader").click();
      await waitFor(() => element("reader-view").hidden && element("books").getAttribute("aria-busy") !== "true");
    };
    const loadPage = async (role = "admin") => {
      frame?.remove();
      frame = document.createElement("iframe");
      frame.title = "Isolated UI regression check";
      frame.style.cssText = "width:100%;height:800px";
      document.body.append(frame);
      win = frame.contentWindow;
      doc = win.document;
      doc.open();
      win.confirm = () => { confirmCalls++; return true; };
      win.fetch = async (input, options = {}) => {
        const url = new URL(input instanceof win.Request ? input.url : input, location.href);
        const path = url.pathname;
        const method = options.method || "GET";
        const json = value => new win.Response(JSON.stringify(value), { headers: { "Content-Type": "application/json" } });
        if (path === holdPath) {
          holdPath = null;
          await new Promise(resolve => { releaseRequest = resolve; });
        }
        if (path === "/api/session") return json({ ...identity, role });
        if (path === "/api/scan") return json(scanResult);
        if (path === "/api/reader-preferences" && method === "GET") {
          const preferences = await originalFetch(input, options).then(response => response.json());
          return json({ ...preferences, mode: "spread", coverAlone: false, rotation: 0, skipPageTypes: 0 });
        }
        if (path === bookPath) return json({ ...book, started: false });
        if (path.startsWith(`${bookPath}/pages/`)) {
          const page = Number(path.split("/").at(-1));
          if (page === heldPage) {
            heldPage = null;
            await new Promise(resolve => { releaseImage = resolve; });
          }
          if (failedPages.has(page)) return new win.Response("Simulated page failure", { status: 500 });
          return new win.Response('<svg xmlns="http://www.w3.org/2000/svg" width="600" height="900"><rect width="600" height="900" fill="white"/></svg>', { headers: { "Content-Type": "image/svg+xml" } });
        }
        if (path === `${bookPath}/file-operations/preview`) return json({
          bookId, operation: "move", revision: "audit-preview", canApply: allowMove, conflicts: [],
          source: "audit/source.cbz", destination: "audit/destination.cbz", sourceHash: "audit-only"
        });
        if (method !== "GET") {
          if (path.endsWith("/progress") || path === "/api/reader-preferences") return new win.Response(null, { status: 204 });
          // Never forward writes, even if a later assertion or selector is wrong.
          return new win.Response(JSON.stringify({ error: "Audit-only rejected write" }), { status: 400, headers: { "Content-Type": "application/json" } });
        }
        return originalFetch(input, options);
      };
      doc.write(html);
      doc.close();
      await waitFor(() => element("startup")?.hidden && !element("library-view").hidden && element("books").getAttribute("aria-busy") !== "true");
      if (role === "admin") await pollScan();
    };
    try {
      localStorage.removeItem(storageKey);
      await loadPage();
      assert(clean(), "A clean library must not block unload.");
      element("manage-users").click();
      await waitFor(() => !element("user-controls").disabled);
      const form = element("user-form");
      form.elements.username.value = "ui-audit-only";
      assert(!clean(), "An open account draft must block unload.");
      form.elements.password.value = "audit-only-password";
      holdPath = "/api/users";
      form.requestSubmit();
      await waitFor(() => Boolean(releaseRequest));
      form.reset();
      assert(!clean(), "An in-flight account write must block unload even with a clean draft.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("user-controls").disabled);
      close("user-dialog");
      await waitFor(clean);
      form.elements.username.value = "closed-stale-draft";
      assert(clean(), "A closed account draft must not block unload.");
      form.reset();

      await openReader();
      await waitFor(() => !element("next-page").disabled && clean());
      element("reader-details").click();
      await waitFor(() => !element("move-archive").hidden);
      holdPath = "/api/file-operation-roots";
      element("move-archive").click();
      await waitFor(() => Boolean(releaseRequest));
      assert(!clean(), "Loading a move must block unload.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("file-operation-dialog").querySelector("[data-close]").disabled);
      assert(clean() && element("apply-file-operation").disabled, "An unchanged move is clean and cannot apply without a preview.");
      for (const allowed of [false, true]) {
        allowMove = allowed;
        holdPath = `${bookPath}/file-operations/preview`;
        element("file-operation-draft").requestSubmit();
        await waitFor(() => Boolean(releaseRequest));
        assert(element("apply-file-operation").disabled && !clean(), "Busy move preview must disable apply and protect unload.");
        releaseRequest(); releaseRequest = null;
        await waitFor(() => !element("file-operation-dialog").querySelector("[data-close]").disabled);
        assert(element("apply-file-operation").disabled === !allowed, "Only an applicable reviewed move enables apply, without a second checkbox.");
      }
      element("file-operation-folder").value += "audit-only";
      assert(!clean(), "An open move draft must block unload.");
      element("file-operation-folder").dispatchEvent(new win.Event("input", { bubbles: true }));
      assert(element("apply-file-operation").disabled && element("file-operation-preview").hidden, "Changing a draft must invalidate its reviewed move.");
      close("file-operation-dialog");
      close("details-dialog");
      await waitFor(clean);

      for (const [state, errorCount, hidden] of [["completed", 0, true], ["cancelled", 0, true], ["running", 0, false], ["failed", 0, false], ["completed", 1, false]]) {
        Object.assign(scanResult, { state, errorCount, errors: errorCount ? ["Audit-only scan error"] : [] });
        await pollScan();
        assert(element("scan-panel").hidden === hidden, `${state}/${errorCount}: reader scan visibility.`);
      }
      Object.assign(scanResult, { state: "completed", errorCount: 0, errors: [] });
      await pollScan();
      await closeReader();
      assert(!element("scan-panel").hidden, "Successful scan results remain visible in the library.");
      element("group-by").value = "none";
      element("group-by").dispatchEvent(new win.Event("change", { bubbles: true }));
      await waitFor(() => element("books").getAttribute("aria-busy") !== "true");
      element("books").querySelector(".book-selection input").click();
      holdPath = "/api/file-operation-roots";
      element("organize-selected").click();
      await waitFor(() => Boolean(releaseRequest));
      assert(!clean(), "Loading organization must block unload.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("organization-dialog").querySelector("[data-close]").disabled);
      assert(clean(), "Unchanged organization must not block unload.");
      holdPath = "/api/file-operations/template-preview";
      element("generate-organization").click();
      await waitFor(() => Boolean(releaseRequest));
      assert(!clean(), "A busy organization preview must block unload even with an unchanged draft.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("organization-dialog").querySelector("[data-close]").disabled);
      element("organization-folder").value = "audit-only";
      assert(!clean(), "An open organization draft must block unload.");
      element("organization-folder").value = "";
      element("confirm-organization").checked = true;
      assert(!clean(), "Organization confirmation must protect unload even with an unchanged draft.");
      close("organization-dialog");
      await waitFor(clean);
      element("organization-folder").value = "closed-stale-draft";
      element("file-operation-folder").value = "closed-stale-draft";
      assert(clean(), "Closed organization/move drafts must not block unload.");

      failedPages.add(order.pages[1]);
      heldPage = order.pages[1];
      await openReader();
      await waitFor(() => Boolean(releaseImage) && element("reader-pages").querySelector("img")?.naturalWidth > 0);
      assert(element("next-page").disabled, "A spread must wait for its loading partner.");
      releaseImage(); releaseImage = null;
      await waitFor(() => !element("next-page").disabled);
      heldPage = order.pages[1];
      element("reader-pages").querySelector(".failed button").click();
      await waitFor(() => Boolean(releaseImage));
      assert(element("next-page").disabled, "Explicit retry must wait for its result.");
      releaseImage(); releaseImage = null;
      await waitFor(() => !element("next-page").disabled);
      element("next-page").click();
      await waitFor(() => element("page-number").value === "3");
      await closeReader();
      failedPages.add(order.pages[0]);
      await openReader();
      await waitFor(() => element("reader-pages").querySelectorAll(".failed").length === 2);
      assert(!element("next-page").disabled, "Two failed pages must still allow leaving the spread.");
      element("next-page").click();
      await waitFor(() => element("page-number").value === "3");
      await closeReader();
      failedPages.clear();

      element("dismiss-scan").click();
      scanResult.revision++;
      await pollScan();
      assert(element("scan-panel").hidden, "An identical automatic scan stays dismissed despite a new revision.");
      await loadPage();
      assert(element("scan-panel").hidden, "A real app reload must retain the same scan dismissal.");
      scanResult.added = 1;
      await pollScan();
      assert(!element("scan-panel").hidden, "Changed results must remain visible.");
      scanResult.added = 0;
      scanResult.errorCount = 1;
      scanResult.errors = ["Audit-only scan error"];
      await pollScan();
      assert(!element("scan-panel").hidden, "A saved success must never hide errors.");
      Object.assign(scanResult, { state: "running", errorCount: 0, errors: [] });
      await pollScan();
      assert(!element("scan-panel").hidden, "A saved success must never hide an active scan.");
      scanResult.state = "completed";
      await pollScan();
      element("scan").click(); // Intercepted above; never starts a real scan.
      await waitFor(() => !element("retry-scan-status").disabled);
      await loadPage();
      assert(!element("scan-panel").hidden, "Starting a manual scan clears the persisted dismissal.");
      const setItem = win.Storage.prototype.setItem;
      try {
        win.Storage.prototype.setItem = () => { throw new Error("Audit-only storage denial"); };
        element("dismiss-scan").click();
        await pollScan();
        assert(element("scan-panel").hidden, "Blocked storage still permits dismissal for this page.");
      } finally { win.Storage.prototype.setItem = setItem; }
      await loadPage("reader");
      await openReader();
      element("reader-details").click();
      await waitFor(() => element("details-dialog").open && !element("read-book").disabled);
      assert(element("move-archive").hidden && element("apply-file-operation").disabled, "Reader accounts must not be offered move controls.");
      element("move-archive").click();
      assert(!element("file-operation-dialog").open, "Reader accounts cannot open moves through a synthetic click.");
    } finally {
      releaseRequest?.();
      releaseImage?.();
      frame?.remove();
      if (savedDismissal === null) localStorage.removeItem(storageKey);
      else localStorage.setItem(storageKey, savedDismissal);
    }
    console.log("PASS: UI audit, unload guards, move readiness/permissions, spread failure/retry navigation, and scan dismissal/reload/storage coverage.");
  } finally {
    try {
      release?.();
      scanOverride = watchOverride = false;
      window.confirm = () => true;
      for (const dialog of [...document.querySelectorAll("dialog[open]")].reverse()) dialog.querySelector("[data-close]")?.click();
      if (!$("reader-view").hidden) {
        $("close-reader").click();
        await waitFor(() => $("reader-view").hidden);
      }
      if (failCleanup) throw new Error("Injected UI audit cleanup failure");
    } finally {
      window.fetch = originalFetch;
      window.confirm = originalConfirm;
    }
  }
})(false);
