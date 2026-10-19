// Run in the browser console on an idle library page with a readable Unsorted comicbook.
// Uses real catalog responses; never moves files or saves server settings. Reading may save progress.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 15000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Area navigation timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const change = id => $(id).dispatchEvent(new Event("change", { bubbles: true }));
  const idle = () => !$('library-view').hidden && $("books").getAttribute("aria-busy") !== "true";
  assert(idle() && !document.querySelector("dialog[open]"), "Start on the idle library page.");
  const originalFetch = window.fetch;
  const requests = [];
  let holdLibrary = false, blocked = false, release;
  window.fetch = async (...args) => {
    const url = new URL(args[0] instanceof Request ? args[0].url : args[0], location.href);
    const catalog = ["/api/books", "/api/book-groups", "/api/book-filters"].includes(url.pathname);
    if (catalog) requests.push(url);
    const hold = holdLibrary && url.pathname === "/api/books" && url.searchParams.get("location") === "library";
    if (hold) holdLibrary = false;
    const response = await originalFetch(...args);
    if (hold) { blocked = true; await new Promise(resolve => { release = resolve; }); }
    return response;
  };
  try {
    $("clear-selection").click();
    $("search").value = "";
    $("reading-list").value = "";
    $("book-status").value = $("book-availability").value = "all";
    $("book-rating").value = $("publisher").value = "";
    $("group-by").value = "none";
    $("library-location").value = "library";
    $("library-navigation").click();
    change("library-location");
    await waitFor(idle);
    assert($("library-title").textContent === "Your library", "Library must be the default area.");
    $("library-location").value = "archived";
    change("library-location");
    await waitFor(idle);
    assert($("library-title").textContent === "Archived comicbooks", "Archived filter did not select its area.");
    holdLibrary = true;
    $("library-location").value = "library";
    change("library-location");
    await waitFor(() => blocked);
    $("library-location").value = "archived";
    change("library-location");
    await waitFor(idle);
    const archivedResults = $("books").textContent;
    release();
    await new Promise(resolve => setTimeout(resolve, 100));
    assert($("books").textContent === archivedResults && $("library-title").textContent === "Archived comicbooks", "A stale Library response replaced Archived results.");
    $("unsorted-navigation").click();
    await waitFor(() => idle() && $("library-title").textContent === "Unsorted intake");
    assert(location.hash === "#unsorted" && $("library-location-filter").hidden && $("catalog-home").hash === "#unsorted", "Intake navigation or Home lost its area.");
    assert($("unsorted-navigation").getAttribute("aria-current") === "page", "Intake navigation lacks an accessible active state.");
    const checkbox = $("books").querySelector("[data-book-id]");
    assert(checkbox, "A readable Unsorted comicbook is required.");
    checkbox.click();
    assert(!document.querySelector(".selection-toolbar").hidden, "Intake selection is unavailable.");
    $("library-navigation").click();
    await waitFor(() => idle() && $("library-title").textContent === "Your library");
    assert(document.querySelector(".selection-toolbar").hidden && $("group-navigation").hidden && $("library-page").textContent.startsWith("Page 1 "), "Changing areas retained selection, a group, or pagination.");
    $("unsorted-navigation").click();
    await waitFor(() => idle() && $("library-title").textContent === "Unsorted intake");
    $("books").querySelector(".cover-button").click();
    await waitFor(() => !$('reader-view').hidden && $("page-stage").querySelector(".reader-page"));
    assert(location.hash.endsWith("?location=unsorted"), "Reader link lost the return area.");
    $("close-reader").click();
    await waitFor(() => idle() && location.hash === "#unsorted");
    history.back();
    await waitFor(() => !$('reader-view').hidden && $("page-stage").querySelector(".reader-page"));
    $("catalog-home").click();
    await waitFor(() => idle() && location.hash === "#unsorted");
    $("group-by").value = "series";
    change("group-by");
    await waitFor(idle);
    assert(requests.some(url => url.pathname === "/api/book-groups" && url.searchParams.get("location") === "unsorted"), "Grouped intake request lost its area.");
    for (const area of ["library", "archived", "unsorted"]) {
      assert(requests.some(url => url.pathname === "/api/books" && url.searchParams.get("location") === area), `Missing ${area} book request.`);
      assert(requests.some(url => url.pathname === "/api/book-filters" && url.searchParams.get("location") === area), `Missing ${area} publisher request.`);
    }
    console.log("PASS: Library/Archived/Unsorted requests, stale response isolation, selection reset, grouping, reader return, Home and browser Back.");
  } finally {
    release?.();
    window.fetch = originalFetch;
  }
})();
