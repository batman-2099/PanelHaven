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
  let scanOverride = false, scanPolls = 0;
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
    if (url.pathname === "/api/books") {
      if (holdCatalog) { holdCatalog = false; await new Promise(resolve => { release = resolve; }); }
      if (failCatalog) { failCatalog = false; throw new TypeError("Simulated catalog failure"); }
    }
    return originalFetch(input, options);
  };
  try {
    $("clear-selection").click();
    $("group-by").value = "none";
    change("stack-by", "series");
    await waitFor(idle);
    const obsoleteCover = $("books").querySelector(".cover-button img[src]");
    assert(obsoleteCover, "A catalog thumbnail is required.");
    const coverSource = obsoleteCover.src;
    $("refresh-library").click();
    await waitFor(idle);
    assert(!obsoleteCover.isConnected, "Refresh must replace the old catalog result.");
    obsoleteCover.dispatchEvent(new Event("error"));
    change("book-sort", "title");
    await waitFor(idle);
    const healthyCover = [...$("books").querySelectorAll("img")].find(image => image.src === coverSource);
    assert(healthyCover, "An obsolete thumbnail error poisoned the current catalog.");
    await waitFor(() => healthyCover.naturalWidth > 0);
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
    const bookPath = `/api/books/${encodeURIComponent(bookId)}`;
    const book = await originalFetch(bookPath).then(response => response.json());
    const order = await originalFetch(`${bookPath}/page-order`).then(response => response.json());
    checkbox.click();
    $("clear-selection").focus();
    $("clear-selection").click();
    assert(visibleFocus() && $("management-status").textContent.includes("cleared"), "Clearing selection must preserve visible focus and announce its result.");
    checkbox.click();
    $("bulk-metadata").click();
    await waitFor(() => !$("metadata-view").hidden && !$("metadata-controls").disabled);
    const apply = $("metadata-fields").querySelector(".apply-change");
    apply.click();
    $("metadata-changed-only").click();
    apply.focus();
    apply.click();
    assert(visibleFocus(), "Unchecking the last filtered metadata row must not hide keyboard focus.");
    window.confirm = () => true;
    $("metadata-back").click();
    await waitFor(() => !$("library-view").hidden && idle());
    $("clear-selection").click();
    $("account-menu").open = true;
    $("maintenance").focus();
    $("maintenance").click();
    document.querySelector('[data-close="maintenance-dialog"]').click();
    assert(document.activeElement === $("account-menu").querySelector("summary") && visibleFocus(), "Administration dialogs must return focus to the visible menu trigger.");
    location.hash = `#reader/${encodeURIComponent(bookId)}?location=library`;
    await waitFor(() => !$("reader-details").disabled && $("reader-pages").querySelector("img")?.naturalWidth > 0);
    const display = document.querySelector(".reader-display");
    display.open = true;
    $("fit").focus();
    $("zoom").focus();
    assert(display.open, "Moving between display controls must keep settings open.");
    $("page-stage").focus();
    assert(!display.open && document.activeElement === $("page-stage"), "Leaving display settings must uncover the reader without stealing focus.");
    display.open = true;
    $("fit").focus();
    $("fit").dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    assert(!display.open && document.activeElement === display.querySelector("summary"), "Escape must close display settings and restore summary focus.");
    $("reader-details").click();
    await waitFor(() => !$("move-archive").hidden);
    $("move-archive").click();
    await waitFor(() => !$("file-operation-dialog").querySelector("[data-close]").disabled);
    assert(document.activeElement === $("file-operation-title") && $("file-operation-dialog").scrollTop === 0, "Move must open at its visible heading, not its recovery controls.");
    document.querySelector('[data-close="file-operation-dialog"]').click();
    document.querySelector('[data-close="details-dialog"]').click();
    change("reader-mode", "single");
    if (!$("cover-alone").checked) $("cover-alone").click();
    $("page-number").value = "1";
    $("page-form").requestSubmit();
    const physicalPage = order.pages[0];
    const visibleImage = () => $("reader-pages").querySelector(`[data-page="${physicalPage}"] img`);
    await waitFor(() => visibleImage()?.naturalWidth > 0 && $("page-number").value === "1");
    const image = visibleImage();
    const pagePath = `${bookPath}/pages/${physicalPage}`;
    const before = pageRequests.filter(path => path === pagePath).length;
    assert(before > 0, "Image reuse needs a positive request baseline for the displayed physical page.");
    change("reader-mode", "spread");
    await waitFor(() => visibleImage()?.naturalWidth > 0);
    assert(visibleImage() === image && pageRequests.filter(path => path === pagePath).length === before,
      "Changing layout must reuse the displayed physical image without fetching it again, including custom reading order.");
    $("close-reader").click();
    await waitFor(() => $("reader-view").hidden);
    scanOverride = true;
    await waitFor(() => $("scan-summary").textContent.includes("1 error"));
    assert($("scan-panel").open, "New scan errors must reveal their details.");
    $("scan-panel").open = false;
    const polls = scanPolls;
    await waitFor(() => scanPolls > polls);
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    assert(!$("scan-panel").open, "Unchanged polling must preserve manually collapsed scan details.");
    // A fresh real document/module retains reload coverage without source extraction or a fake DOM.
    const html = await originalFetch("/").then(response => response.text());
    const identity = await originalFetch("/api/session").then(response => response.json());
    assert(book.pageCount >= 4, "The selected series book must have at least four pages.");
    const storageKey = `panelhaven.dismissed-scan:${identity.username}`;
    const savedDismissal = localStorage.getItem(storageKey);
    const scanResult = { state: "completed", scanned: 987654, added: 0, updated: 0, skipped: 987654, errorCount: 0, errors: [], revision: 1 };
    const failedPages = new Set();
    let frame, win, doc, holdPath, releaseRequest, heldPage, releaseImage, allowMove = false, confirmCalls = 0;
    let sourceOrder = null, sourceWrites = 0, sourceLocation = book.location, nextLookups = 0, failProgress = false;
    let expireDetails = false;
    let pendingMove = true, recoveryWrites = 0;
    let archiveRoots = null, organizationPreviews = 0, organizationWrites = 0;
    const privateFailures = { bookmark: false, preferences: false };
    const privateWrites = { bookmark: [], preferences: [] };
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
        if (path === "/api/login" && method === "POST") return json({ ...identity, role });
        if (path === bookPath && expireDetails) {
          expireDetails = false;
          return new win.Response(null, { status: 401 });
        }
        const privateKind = path === "/api/reader-preferences" && method === "PUT" ? "preferences"
          : path.startsWith(`${bookPath}/bookmarks/`) && method !== "GET" ? "bookmark" : null;
        if (privateKind) {
          privateWrites[privateKind].push({ path, body: options.body });
          return new win.Response(null, { status: privateFailures[privateKind] ? 503 : 204 });
        }
        if (path === "/api/reader-preferences" && method === "GET") {
          const preferences = await originalFetch(input, options).then(response => response.json());
          return json({ ...preferences, mode: "spread", coverAlone: false, rotation: 0, skipPageTypes: 0 });
        }
        if (path === bookPath) return json({ ...book, started: false, location: sourceLocation });
        if (sourceOrder && path === `${bookPath}/page-order`) return json({ bookId, revision: `audit-${sourceWrites}`, pages: sourceOrder });
        if (sourceOrder && path === `${bookPath}/page-info`) return json(sourceOrder.map(page => ({
          page, type: sourceWrites ? "Story" : "", typeMask: 0, bookmark: sourceWrites ? `Source revision ${sourceWrites}` : ""
        })));
        if (sourceOrder && path === `${bookPath}/next-issue`) {
          nextLookups++;
          return json({ id: sourceLocation === book.location ? "audit-next-issue" : null });
        }
        if (sourceOrder && path === `${bookPath}/archive-write/preview`)
          return json({ bookId,
            revision: "audit", canApply: true, title: book.title, location: sourceLocation, sourceHash: "audit", changes: [], entries: [] });
        if (sourceOrder && path === `${bookPath}/archive-write`) {
          sourceWrites++;
          sourceOrder = Array.from({ length: book.pageCount }, (_, page) => page);
          return json({ bookId });
        }
        if (sourceOrder && path === `${bookPath}/file-operations` && method === "POST") {
          sourceLocation = book.location === "archived" ? "library" : "archived";
          return new win.Response(null, { status: 204 });
        }
        if (path === `${bookPath}/file-operations` && method === "GET") return json(pendingMove ? [{
          id: "audit-pending", state: "pending", createdAt: "2026-01-01T00:00:00Z",
          source: "audit/source.cbz", destination: "audit/destination.cbz", hash: "audit-only", canRecover: true
        }] : []);
        if (path === `${bookPath}/file-operations/audit-pending/recover/preview`) return json({
          bookId, operationId: "audit-pending", operation: "recover", revision: "audit-recovery",
          canApply: true, conflicts: [], source: "audit/source.cbz", destination: "audit/destination.cbz", sourceHash: "audit-only"
        });
        if (path === `${bookPath}/file-operations/audit-pending/recover` && method === "POST") {
          const body = JSON.parse(options.body);
          assert(body.confirmed === true && body.expectedRevision === "audit-recovery", "Recovery lost explicit confirmation or its preview revision.");
          recoveryWrites++;
          pendingMove = false;
          return new win.Response(null, { status: 204 });
        }
        if (path === "/api/file-operation-roots" && archiveRoots !== null) return json(archiveRoots);
        if (path === "/api/file-operations/template-preview") organizationPreviews++;
        if (path === "/api/file-operations" && method === "POST") organizationWrites++;
        if (failProgress && path.endsWith("/progress") && method === "POST")
          return new win.Response("Simulated progress save failure", { status: 503 });
        if (path.startsWith(`${bookPath}/pages/`)) {
          const page = Number(path.split("/").at(-1));
          if (page === heldPage) {
            heldPage = null;
            await new Promise(resolve => { releaseImage = resolve; });
          }
          if (failedPages.has(page)) return new win.Response("Simulated page failure", { status: 500 });
          return new win.Response(`<svg xmlns="http://www.w3.org/2000/svg" width="${600 + sourceWrites}" height="900"><rect width="100%" height="100%" fill="white"/></svg>`, { headers: { "Content-Type": "image/svg+xml" } });
        }
        if (path === `${bookPath}/file-operations/preview`) return json({
          bookId, operation: "move", revision: "audit-preview", canApply: allowMove, conflicts: [],
          source: "audit/source.cbz", destination: "audit/destination.cbz", sourceHash: "audit-only"
        });
        if (method !== "GET") {
          if (path.endsWith("/progress")) return new win.Response(null, { status: 204 });
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
      frame.style.width = "1000px";
      await waitFor(() => element("catalog-arrangement").open);
      element("group-by").focus();
      frame.style.width = "390px";
      await waitFor(() => !element("catalog-arrangement").open);
      const arrangementSummary = element("catalog-arrangement").querySelector("summary");
      assert(doc.activeElement === arrangementSummary && element("book-status").checkVisibility() && !element("group-by").checkVisibility(),
        "Narrow layout must retain primary browsing and move focus before collapsing arrangement controls.");
      arrangementSummary.click();
      assert(element("group-by").checkVisibility(), "Arrange must reveal its native controls.");
      arrangementSummary.focus();
      frame.style.width = "1000px";
      await waitFor(() => !arrangementSummary.checkVisibility());
      assert(element("group-by").checkVisibility() && doc.activeElement === element("book-status"),
        "Desktop layout must expose arrangement controls without hiding focus.");
      frame.style.width = "100%";
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
      const recovery = element("file-operation-recovery");
      assert(recovery.textContent.includes("audit/source.cbz") && recovery.textContent.includes("audit/destination.cbz"),
        "Pending recovery must retain both paths.");
      recovery.querySelector("button").click();
      await waitFor(() => !element("apply-file-operation").disabled);
      assert(recoveryWrites === 0 && doc.activeElement === element("file-operation-preview"),
        "Recovery preview must require a separate accessible confirmation.");
      element("refresh-file-operation-recovery").click();
      await waitFor(() => !element("refresh-file-operation-recovery").disabled);
      assert(element("apply-file-operation").disabled && recoveryWrites === 0, "Refreshing recovery must invalidate confirmation without applying.");
      recovery.querySelector("button").click();
      await waitFor(() => !element("apply-file-operation").disabled);
      element("file-operation-apply-form").requestSubmit();
      await waitFor(() => !element("refresh-file-operation-recovery").disabled);
      assert(recoveryWrites === 1 && !recovery.children.length,
        "Resolved recovery must disappear without retained completed history.");
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
      element("file-operation-name").value = "audit-only.cbz";
      assert(!clean(), "An open move draft must block unload.");
      element("file-operation-name").dispatchEvent(new win.Event("input", { bubbles: true }));
      assert(element("apply-file-operation").disabled && element("file-operation-preview").hidden, "Changing a draft must invalidate its reviewed move.");
      close("file-operation-dialog");
      close("details-dialog");
      await waitFor(clean);
      await closeReader();

      await openReader();
      element("reader-mode").value = "single";
      element("reader-mode").dispatchEvent(new win.Event("change", { bubbles: true }));
      element("page-number").value = "1";
      element("page-form").requestSubmit();
      await waitFor(() => !element("save-bookmark").disabled && clean());
      const draftPage = order.pages[0];
      const draftName = "Unsaved page-bound name";
      const writeCount = privateWrites.bookmark.length;
      const typeBookmark = name => {
        doc.querySelector(".reader-display").open = true;
        element("bookmark-name").focus();
        element("bookmark-name").value = name;
        element("bookmark-name").dispatchEvent(new win.Event("input", { bubbles: true }));
      };
      typeBookmark(draftName);
      element("page-number").value = "3";
      element("page-form").requestSubmit();
      element("reader-mode").value = "continuous";
      element("reader-mode").dispatchEvent(new win.Event("change", { bubbles: true }));
      await waitFor(() => element("page-number").value === "3" && element("reader-pages").querySelector(`[data-page="${order.pages[2]}"] img`));
      element("page-stage").dispatchEvent(new win.WheelEvent("wheel", { bubbles: true }));
      element("page-stage").scrollTop = element("page-stage").scrollHeight;
      element("page-stage").dispatchEvent(new win.Event("scroll"));
      await waitFor(() => element("page-number").value === String(book.pageCount));
      assert(element("bookmark-name").value === draftName && element("bookmark-page").textContent === `physical page ${draftPage + 1}`
        && !element("bookmark-draft").hidden && !clean() && privateWrites.bookmark.length === writeCount,
        "Direct jumps and continuous scrolling must retain the named physical-page draft without autosaving.");
      const cancelledLeave = confirmCalls;
      win.confirm = () => { confirmCalls++; return false; };
      element("close-reader").click();
      await waitFor(() => confirmCalls > cancelledLeave && win.location.hash.startsWith("#reader/"));
      assert(element("bookmark-name").value === draftName, "Cancelling reader departure must retain the unsubmitted name.");
      win.confirm = () => { confirmCalls++; return true; };
      element("bookmark-form").requestSubmit();
      await waitFor(() => element("bookmark-draft").hidden && clean());
      assert(privateWrites.bookmark.at(-1).path === `${bookPath}/bookmarks/${draftPage}`
        && JSON.parse(privateWrites.bookmark.at(-1).body).name === draftName,
        "Submitting after navigation must save to the draft's physical page, not the current page.");
      typeBookmark("Discard this name");
      element("discard-bookmark").click();
      assert(element("bookmark-draft").hidden && element("bookmark-name").value !== "Discard this name"
        && doc.activeElement === element("bookmark-name"), "Explicit discard must restore the current-page editor and its focus.");
      typeBookmark("Discard on departure");
      await closeReader();
      await openReader();
      await waitFor(() => !element("save-bookmark").disabled && clean());
      assert(element("bookmark-draft").hidden && element("bookmark-name").value !== "Discard on departure",
        "A confirmed departure must clear the draft before another reader session.");
      element("reader-mode").value = "spread";
      element("reader-mode").dispatchEvent(new win.Event("change", { bubbles: true }));
      await waitFor(clean);
      await closeReader();

      for (const kind of ["bookmark", "preferences"]) {
        await openReader();
        await waitFor(() => !element("save-bookmark").disabled && clean());
        const retry = element(kind === "bookmark" ? "retry-bookmark" : "retry-preferences");
        const savePrivate = name => {
          if (kind === "bookmark") {
            element("bookmark-name").value = name;
            element("bookmark-name").dispatchEvent(new win.Event("input", { bubbles: true }));
            element("bookmark-form").requestSubmit();
          } else {
            element("zoom").value = element("zoom").value === "125" ? "150" : "125";
            element("zoom").dispatchEvent(new win.Event("change", { bubbles: true }));
          }
        };
        privateFailures[kind] = true;
        savePrivate("Retained private bookmark");
        await waitFor(() => !retry.hidden && !retry.disabled);
        const failedWrite = privateWrites[kind].at(-1);
        assert(!clean(), `${kind}: a failed write must protect unload.`);
        element("close-reader").click();
        await waitFor(() => win.location.hash.startsWith("#reader/") && !element("reader-error").hidden && !element("reader-details").disabled);
        assert(!element("reader-view").hidden && !retry.hidden, `${kind}: failed saving must block departure and retain retry.`);
        privateFailures[kind] = false;
        doc.querySelector(".reader-display").open = true;
        retry.focus();
        retry.click();
        await waitFor(() => retry.hidden && clean());
        assert(doc.activeElement === element("page-stage"), `${kind}: hiding a focused retry must restore focus to the page stage.`);
        assert(JSON.stringify(privateWrites[kind].at(-1)) === JSON.stringify(failedWrite),
          `${kind}: retry must save the retained edit, not a changed page or default preference.`);
        if (kind === "bookmark") assert(element("personal-bookmarks").textContent.includes("Retained private bookmark"),
          "A successful private bookmark retry must update the bookmark list.");
        await closeReader();

        await openReader();
        await waitFor(() => !element("save-bookmark").disabled && clean());
        holdPath = kind === "bookmark" ? `${bookPath}/bookmarks/${order.pages[0]}` : "/api/reader-preferences";
        savePrivate("Obsolete private bookmark");
        await waitFor(() => Boolean(releaseRequest));
        expireDetails = true;
        element("reader-details").click();
        await waitFor(() => !element("login-view").hidden);
        const login = element("login-form");
        login.elements.username.value = identity.username;
        login.elements.password.value = "audit-only-password";
        login.requestSubmit(login.querySelector('button[type="submit"]'));
        await waitFor(() => !element("library-view").hidden && element("books").getAttribute("aria-busy") !== "true");
        await openReader();
        await waitFor(() => !element("save-bookmark").disabled && clean());
        const bookmarkStatus = element("bookmark-status").textContent;
        const preferenceStatus = element("preferences-status").textContent;
        privateFailures[kind] = kind === "preferences";
        releaseRequest(); releaseRequest = null;
        await new Promise(resolve => win.requestAnimationFrame(() => win.requestAnimationFrame(resolve)));
        assert(clean() && element("retry-bookmark").hidden && element("retry-preferences").hidden
          && element("bookmark-status").textContent === bookmarkStatus && element("preferences-status").textContent === preferenceStatus
          && !element("personal-bookmarks").textContent.includes("Obsolete private bookmark") && element("reader-error").hidden
          && element("bookmark-draft").hidden && element("bookmark-name").value !== "Obsolete private bookmark",
          `${kind}: an old session's completion must not mutate the new session's reader.`);
        privateFailures[kind] = false;
        await closeReader();
      }

      for (const [state, errorCount] of [["completed", 0], ["cancelled", 0], ["running", 0], ["failed", 0], ["completed", 1]]) {
        element("account-menu").open = false;
        Object.assign(scanResult, { state, errorCount, errors: errorCount ? ["Audit-only scan error"] : [] });
        await pollScan();
        assert(!element("scan-panel").hidden && !element("scan-panel").checkVisibility(), `${state}/${errorCount}: scan results must stay inside the closed menu.`);
        element("account-menu").open = true;
        if (state === "running") {
          element("scan-panel").open = true;
          element("cancel-scan").focus();
        }
        await pollScan();
        assert(element("account-menu").open, "Checking scan status closed the menu.");
        if (state === "running") assert(doc.activeElement === element("scan-summary"), "A disabled scan action must leave focus on the scan summary.");
      }
      element("account-menu").open = false;
      Object.assign(scanResult, { state: "completed", errorCount: 0, errors: [] });
      await pollScan();
      assert(!element("scan-panel").hidden, "Successful scan results remain visible in the library.");
      element("group-by").value = "none";
      element("stack-by").value = "none";
      element("stack-by").dispatchEvent(new win.Event("change", { bubbles: true }));
      await waitFor(() => element("books").getAttribute("aria-busy") !== "true");
      element("books").querySelector(".book-selection input").click();
      holdPath = "/api/file-operation-roots";
      element("organize-selected").click();
      await waitFor(() => Boolean(releaseRequest));
      assert(!clean(), "Loading organization must block unload.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("organization-dialog").querySelector("[data-close]").disabled);
      assert(clean(), "Unchanged organization must not block unload.");
      const recoveryButton = element("organization-recovery").querySelector("button");
      holdPath = `/api/books/${encodeURIComponent(recoveryButton.dataset.bookId)}/file-operations`;
      recoveryButton.click();
      await waitFor(() => Boolean(releaseRequest));
      assert(element("file-operation-dialog").open && element("organization-dialog").open,
        "Per-book recovery must open the selected book's recovery dialog above organization.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("file-operation-dialog").querySelector("[data-close]").disabled);
      close("file-operation-dialog");
      holdPath = "/api/file-operations/template-preview";
      element("generate-organization").click();
      await waitFor(() => Boolean(releaseRequest));
      assert(!clean(), "A busy organization preview must block unload even with an unchanged draft.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("organization-dialog").querySelector("[data-close]").disabled);
      const originalNameTemplate = element("organization-name").value;
      element("organization-name").value = "audit-only";
      assert(!clean(), "An open organization draft must block unload.");
      element("organization-name").value = originalNameTemplate;
      const originalFolderTemplate = element("organization-folder").value;
      element("organization-folder").value = "audit-only";
      element("organization-folder").dispatchEvent(new win.Event("input", { bubbles: true }));
      assert(!clean() && element("apply-organization").disabled, "Changing a folder template must protect unload and invalidate Move.");
      element("organization-folder").value = originalFolderTemplate;
      close("organization-dialog");
      await waitFor(clean);
      element("organization-name").value = "closed-stale-draft";
      element("file-operation-name").value = "closed-stale-draft";
      assert(clean(), "Closed organization/move drafts must not block unload.");
      archiveRoots = [{ index: 0, label: "Library 1" }];
      const beforeArchive = [organizationPreviews, organizationWrites];
      holdPath = "/api/file-operation-roots";
      element("archive-selected").click();
      await waitFor(() => Boolean(releaseRequest));
      assert(element("archive-selected").disabled && element("organize-selected").disabled && !clean()
        && !doc.querySelector("dialog[open]"), "Archiving must lock repeated moves and protect unload without opening a dialog.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("archive-selected").disabled);
      assert(organizationPreviews === beforeArchive[0] && organizationWrites === beforeArchive[1]
        && !doc.querySelector("dialog[open]") && clean() && element("management-status").textContent.includes("Archived"),
        "Archive without a configured Archived root must report inline, never preview or move into another root.");
      archiveRoots = null;

      failedPages.add(order.pages[1]);
      heldPage = order.pages[1];
      await openReader();
      await waitFor(() => Boolean(releaseImage) && element("reader-pages").querySelector("img")?.naturalWidth > 0);
      assert(element("next-page").disabled, "A spread must wait for its loading partner.");
      releaseImage(); releaseImage = null;
      await waitFor(() => !element("next-page").disabled);
      heldPage = order.pages[1];
      element("reader-pages").querySelector(".failed button").focus();
      element("reader-pages").querySelector(".failed button").click();
      await waitFor(() => Boolean(releaseImage));
      assert(doc.activeElement === element("page-stage"), "Replacing a focused page retry must restore stage focus without waiting for its request.");
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
      sourceOrder = [...order.pages.slice(1), order.pages[0]];
      await openReader();
      element("reader-mode").value = "single";
      element("reader-mode").dispatchEvent(new win.Event("change", { bubbles: true }));
      failProgress = true;
      element("page-number").value = "2";
      element("page-form").requestSubmit();
      const physical = sourceOrder[1];
      const sourceImage = () => element("reader-pages").querySelector(`[data-page="${physical}"] img`);
      await waitFor(() => sourceImage()?.naturalWidth > 0 && !element("retry-save").hidden);
      element("reader-details").click();
      await waitFor(() => !element("write-archive").hidden);
      element("write-archive").click();
      await waitFor(() => !element("apply-archive").disabled);
      const previous = sourceImage();
      holdPath = `${bookPath}/archive-write/preview`;
      element("archive-form").requestSubmit();
      await waitFor(() => Boolean(releaseRequest));
      assert(sourceWrites === 0 && element("apply-archive").disabled, "Direct writing must wait for preflight and prevent duplicate submission.");
      const writeCancel = new win.Event("cancel", { cancelable: true });
      element("archive-dialog").dispatchEvent(writeCancel);
      assert(writeCancel.defaultPrevented, "Escape must not close an active metadata write.");
      releaseRequest(); releaseRequest = null;
      await waitFor(() => !element("apply-archive").disabled && sourceImage()?.naturalWidth === 601 && sourceImage() !== previous);
      assert(sourceWrites === 1 && element("page-number").value === String(sourceOrder.indexOf(physical) + 1),
        "Writing must reload saved reading order while retaining the physical page.");
      assert(element("embedded-bookmarks").textContent.includes(`Source revision ${sourceWrites}`),
        "Writing must refresh source-dependent page metadata and embedded bookmarks.");
      assert(!element("retry-save").hidden, "Source refresh must not clear a failed progress save.");
      close("archive-dialog");
      close("details-dialog");
      element("close-reader").click();
      await waitFor(() => win.location.hash.startsWith("#reader/") && !element("reader-error").hidden && !element("reader-details").disabled);
      assert(!element("reader-view").hidden && !element("retry-save").hidden, "Source refresh must retain the unsaved-progress route guard.");
      failProgress = false;
      element("retry-save").focus();
      element("retry-save").click();
      await waitFor(() => element("retry-save").hidden);
      assert(doc.activeElement === element("page-stage"), "Hiding a focused progress retry must restore stage focus.");
      element("page-number").value = String(book.pageCount);
      element("page-form").requestSubmit();
      await waitFor(() => element("next-page").getAttribute("aria-label") === "Next issue" && !element("next-page").disabled);
      const lookups = nextLookups;
      element("reader-details").click();
      await waitFor(() => !element("move-archive").hidden);
      element("move-archive").click();
      await waitFor(() => !element("file-operation-dialog").querySelector("[data-close]").disabled);
      element("file-operation-draft").requestSubmit();
      await waitFor(() => !element("apply-file-operation").disabled);
      element("file-operation-apply-form").requestSubmit();
      await waitFor(() => !element("file-operation-dialog").querySelector("[data-close]").disabled &&
        element("next-page").getAttribute("aria-label") === "No more issues");
      assert(nextLookups > lookups && element("next-page").disabled, "Moving the current issue must invalidate its cached location-dependent successor.");
      close("file-operation-dialog");
      close("details-dialog");
      await closeReader();
      sourceOrder = null;

      element("dismiss-scan").click();
      assert(!element("account-menu").open && frame.contentDocument.activeElement === element("account-menu").querySelector("summary"), "Dismissing scan results must restore focus to the menu trigger.");
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
      assert(!element("dismiss-scan").hidden, "Finished scans with errors must offer dismissal.");
      element("dismiss-scan").click();
      await pollScan();
      assert(element("scan-panel").hidden, "Acknowledged scan errors stay dismissed.");
      await loadPage();
      assert(element("scan-panel").hidden, "Dismissed scan errors stay dismissed after reload.");
      scanResult.errors = ["A different scan error"];
      await pollScan();
      assert(!element("scan-panel").hidden, "Changed errors must reappear even when the error count is unchanged.");
      scanResult.state = "failed";
      await pollScan();
      assert(!element("dismiss-scan").hidden, "Failed scans must offer dismissal.");
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
      scanOverride = false;
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
