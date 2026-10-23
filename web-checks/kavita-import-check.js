// Run in the browser console on an idle DISPOSABLE server, signed in as admin.
// Upload the fixture via the import dialog first, or provide config.file (a File).
// window.panelHavenKavitaImportCheck = { disposable: true, sourceUserId: 1,
//   readyBookId: "id", alreadyReadBookId: "id", untouchedBookId: "id", file,
//   expected: { completedFiles: 7, partialChapters: 1, readyBooks: 4,
//     alreadyReadBooks: 1, unmatchedFiles: 1, ambiguousFiles: 1 } };
// Fixture: >=2 Kavita users, a readable multi-page ready book, an already-read
// book (preferably not on its last physical page), and a book not completed by
// the selected Kavita user. Real imports run; only failure/response delivery and
// the large pagination response are injected. Ends at the login UI (injected 401).
(async () => {
  const $ = id => document.getElementById(id);
  const config = window.panelHavenKavitaImportCheck;
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  assert(config?.disposable === true && config.sourceUserId !== undefined && config.readyBookId && config.alreadyReadBookId && config.untouchedBookId, "Explicit disposable fixture configuration required.");
  const file = config.file || $("kavita-import-file").files[0];
  assert(file instanceof File, "Supply config.file or choose the disposable ZIP in the import dialog first.");
  assert(!$("admin-actions").hidden, "Sign in as an administrator first.");
  const fetchReal = window.fetch;
  const waitFor = async condition => {
    const deadline = Date.now() + 120000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Kavita import check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const book = async id => {
    const response = await fetchReal(`/api/books/${encodeURIComponent(id)}`);
    assert(response.ok, "Fixture book must be readable.");
    return response.json();
  };
  const beforeReady = await book(config.readyBookId);
  const beforeRead = await book(config.alreadyReadBookId);
  const beforeUntouched = await book(config.untouchedBookId);
  assert(!beforeReady.completed && beforeReady.pageCount > 1 && beforeRead.completed, "Require an unread multi-page ready book and an already-read book.");
  const status = () => $("kavita-import-status").textContent;
  const selectFile = chosen => {
    const transfer = new DataTransfer();
    transfer.items.add(chosen);
    $("kavita-import-file").files = transfer.files;
    $("kavita-import-file").dispatchEvent(new Event("change", { bubbles: true }));
  };
  const selectUser = id => {
    $("kavita-import-user").value = String(id);
    $("kavita-import-user").dispatchEvent(new Event("change", { bubbles: true }));
  };
  const open = () => {
    $("import-kavita").click();
    assert($("kavita-import-dialog").open, "Admin action must open the native dialog.");
    selectFile(file);
  };
  const close = async () => {
    $("kavita-import-dialog").querySelector("[data-close]").click();
    await waitFor(() => !$("kavita-import-dialog").open && !$("kavita-import-file").files.length);
  };
  let mode = "normal", previewCalls = 0, applyCalls = 0, lastPreview, release, held, heldReady = false;
  const hold = () => { heldReady = false; held = new Promise(resolve => { release = resolve; }); };
  const json = (data, statusCode = 200) => new Response(JSON.stringify(data), { status: statusCode, headers: { "Content-Type": "application/json" } });
  window.fetch = async (...args) => {
    const url = new URL(args[0], location.href);
    if (!url.pathname.startsWith("/api/reading-import/kavita/")) return fetchReal(...args);
    const options = args[1];
    assert(options.method === "POST" && options.body instanceof File, "Importer must send the ZIP itself, not JSON or multipart.");
    assert(new Headers(options.headers).get("Content-Type") === "application/zip" && new Headers(options.headers).get("X-PanelHaven-Request") === "1", "Raw ZIP and request protection headers required.");
    assert([...url.searchParams.keys()].every(key => ["sourceUserId", "revision"].includes(key)), "No request-supplied Panel Haven target account allowed.");
    if (url.pathname.endsWith("/preview")) {
      previewCalls++;
      const requestMode = mode;
      if (requestMode === "auth") return json({ error: "Injected expired session" }, 401);
      const response = await fetchReal(...args);
      if (!response.ok) return response;
      lastPreview = await response.clone().json();
      if (requestMode === "hold-preview") { heldReady = true; await held; }
      if (requestMode === "large") return json({ ...lastPreview, entries: Array.from({ length: 123 }, (_, index) => ({ source: `Pagination fixture ${index}.cbz`, target: null, bookId: null, status: index % 2 ? "unmatched" : "ambiguous" })) });
      return response;
    }
    applyCalls++;
    assert(url.searchParams.get("sourceUserId") === String(config.sourceUserId) && url.searchParams.get("revision"), "Apply must bind the selected source and fresh revision.");
    const requestMode = mode;
    if (requestMode === "stale") return json({ error: "Injected changed catalog" }, 409);
    const response = await fetchReal(...args);
    assert(response.ok, "The real fixture import must succeed.");
    if (requestMode === "hold-apply") await held;
    if (requestMode === "lose-apply") {
      await response.arrayBuffer();
      throw new TypeError("Injected lost successful import response");
    }
    return response;
  };
  const preview = async () => {
    const count = previewCalls;
    $("preview-kavita-import").click();
    await waitFor(() => previewCalls > count && !$("preview-kavita-import").disabled);
  };
  const chooseSource = async () => {
    await preview();
    assert(lastPreview.users.length >= 2 && $("kavita-import-user").value === "" && $("apply-kavita-import").disabled, "Multiple Kavita users must require explicit selection.");
    selectUser(config.sourceUserId);
    await preview();
  };
  try {
    if ($("kavita-import-dialog").open) await close();
    assert(!document.querySelector("dialog[open]"), "Close other dialogs before this check.");
    open();
    await chooseSource();
    for (const [key, value] of Object.entries(config.expected || {})) assert(lastPreview[key] === value, `Unexpected real fixture preview count: ${key}`);
    assert(lastPreview.entries.some(row => row.bookId === config.readyBookId && row.status === "ready"), "Ready fixture must be a real match.");
    assert(lastPreview.entries.some(row => row.bookId === config.alreadyReadBookId && row.status === "already-read"), "Completed fixture must not be reapplied.");
    assert($("kavita-import-summary").textContent.includes($("username").textContent) && $("kavita-import-summary").textContent.includes(lastPreview.users.find(user => user.id === config.sourceUserId).name), "Preview must identify both source user and signed-in target.");
    for (const [key, label] of [["completedFiles", "completed files"], ["partialChapters", "partial chapters"], ["readyBooks", "matched books ready"], ["alreadyReadBooks", "matched books already read"], ["unmatchedFiles", "unmatched files"], ["ambiguousFiles", "ambiguous files"]])
      assert($("kavita-import-summary").textContent.includes(`${lastPreview[key]} ${label}`), `Summary must display exact ${key}.`);
    selectUser(lastPreview.users.find(user => user.id !== config.sourceUserId).id);
    assert($("apply-kavita-import").disabled && $("kavita-import-preview").hidden, "Changing source user must invalidate confirmation.");
    selectUser(config.sourceUserId);
    await preview();
    selectFile(file);
    assert($("apply-kavita-import").disabled && $("kavita-import-user").value === "", "Changing backup must clear both preview and source selection.");
    await chooseSource();
    mode = "large";
    await preview();
    assert($("kavita-import-entries").children.length === 50, "Large preview must render only 50 entries.");
    $("next-kavita-import").click();
    assert($("kavita-import-entries").firstElementChild.textContent.includes("fixture 50.cbz"), "Next page must show the next source files.");
    $("next-kavita-import").click();
    assert($("kavita-import-entries").children.length === 23 && $("next-kavita-import").disabled, "All remaining files must be reachable.");
    $("kavita-import-filter").value = "ambiguous";
    $("kavita-import-filter").dispatchEvent(new Event("change"));
    assert($("kavita-import-page").textContent.includes("62 files") && [...$("kavita-import-entries").children].every(row => row.textContent.includes("Ambiguous")), "Status filter must include ambiguous files and reset pagination.");
    mode = "normal";
    selectFile(new File(["not a zip"], "invalid.zip", { type: "application/zip" }));
    await preview();
    assert(status().includes("Preview failed") && $("apply-kavita-import").disabled, "Invalid ZIP must expose an actionable error without enabling apply.");
    selectFile(file);
    await chooseSource();
    mode = "hold-preview";
    hold();
    $("preview-kavita-import").click();
    await waitFor(() => heldReady);
    await close();
    release();
    await new Promise(resolve => setTimeout(resolve, 100));
    assert(!$("kavita-import-entries").children.length && !$("kavita-import-file").files.length, "Closing preview must clear upload and ignore late responses.");
    assert(document.activeElement === $("account-menu").querySelector("summary"), "Close must return focus to the menu trigger.");
    mode = "normal";
    location.hash = `#reader/${encodeURIComponent(config.readyBookId)}?location=library`;
    await waitFor(() => !$("reader-view").hidden && $("page-stage").querySelector(".reader-page img") && !$("next-page").disabled);
    open();
    await chooseSource();
    mode = "stale";
    $("apply-kavita-import").click();
    await waitFor(() => !$("preview-kavita-import").disabled && status().includes("stale"));
    assert($("apply-kavita-import").disabled && !(await book(config.readyBookId)).completed, "409 must require a new preview, with no mutation.");
    mode = "normal";
    await preview();
    mode = "hold-apply";
    hold();
    const beforeApplies = applyCalls;
    $("apply-kavita-import").click();
    await waitFor(() => applyCalls > beforeApplies);
    assert($("apply-kavita-import").disabled && $("kavita-import-dialog").querySelector("[data-close]").disabled && $("kavita-import-file").disabled && $("kavita-import-user").disabled, "Applying must disable repeat apply, inputs and Close.");
    const escape = new Event("cancel", { cancelable: true });
    $("kavita-import-dialog").dispatchEvent(escape);
    assert(escape.defaultPrevented, "Escape must not dismiss a running import.");
    const hash = location.hash;
    location.hash = "#library";
    await waitFor(() => location.hash === hash);
    $("logout").click();
    assert($("kavita-import-dialog").open && !$("account").hidden, "Navigation and signout must not interrupt applying.");
    release();
    await waitFor(() => !$("preview-kavita-import").disabled && status().includes("books marked read"));
    assert((await book(config.readyBookId)).completed, "Real apply must mark the matched fixture read.");
    await close();
    await waitFor(() => !$("reader-view").hidden && Number($("page-number").value) === beforeReady.pageCount);
    assert(location.hash === hash, "Import must restore the same reader with imported final-page progress.");
    $("close-reader").click();
    await waitFor(() => !$("library-view").hidden);
    const reset = await fetchReal("/api/reading-status", { method: "POST", headers: { "Content-Type": "application/json", "X-PanelHaven-Request": "1" }, body: JSON.stringify({ bookIds: [config.readyBookId], completed: false }) });
    assert(reset.ok, "Disposable fixture reset failed.");
    mode = "normal";
    open();
    await chooseSource();
    mode = "lose-apply";
    const beforeLost = applyCalls;
    $("apply-kavita-import").click();
    await waitFor(() => !$("preview-kavita-import").disabled && status().includes("Cannot confirm"));
    assert($("apply-kavita-import").disabled && applyCalls === beforeLost + 1 && (await book(config.readyBookId)).completed, "Lost response must not auto-retry and must leave the real successful write intact.");
    mode = "normal";
    await preview();
    assert(lastPreview.readyBooks === 0 && $("apply-kavita-import").disabled && applyCalls === beforeLost + 1, "Re-preview must reconcile the successful import without reapplying already-read books.");
    const afterRead = await book(config.alreadyReadBookId);
    const afterUntouched = await book(config.untouchedBookId);
    for (const key of ["currentPage", "completed", "started", "personalRating"])
      assert(afterRead[key] === beforeRead[key] && afterUntouched[key] === beforeUntouched[key], `Import changed existing or unrelated private ${key}.`);
    window.dispatchEvent(new PageTransitionEvent("pagehide", { persisted: true }));
    assert($("apply-kavita-import").disabled && $("kavita-import-preview").hidden, "Pagehide must invalidate preview authority.");
    mode = "auth";
    $("preview-kavita-import").click();
    await waitFor(() => !$("login-view").hidden);
    assert(!$("kavita-import-dialog").open && !$("kavita-import-file").files.length && !$("kavita-import-entries").children.length, "Session expiry must discard upload, results and apply authority.");
    console.log("PASS: real ZIP/source selection, exact preview, invalidation, bounded/filterable pagination, invalid backup, late-preview cancellation, stale refusal, apply guards, real import, reader refresh, already-read/unrelated progress preservation, lost-response reconciliation and auth/pagehide resets. Stale/expiry/large-results/response-delivery cases were fault-injected; other-account isolation and saved timestamps require backend checks.");
  } finally {
    release?.();
    window.fetch = fetchReal;
  }
})();
