// Run as admin on an idle DISPOSABLE server, with Book details open for an
// available non-ZIP fixture with no same-stem .cbz destination. This permanently replaces it.
// window.panelHavenCbzReplacementCheck = { disposable: true, bookId: "fixture-id" };
(async () => {
  const $ = id => document.getElementById(id);
  const config = window.panelHavenCbzReplacementCheck;
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  assert(config?.disposable === true && config.bookId, "Explicit disposable fixture required.");
  const fetchReal = window.fetch;
  const path = `/api/books/${encodeURIComponent(config.bookId)}`;
  const json = async suffix => {
    const response = await fetchReal(path + suffix);
    assert(response.ok, `Fixture request failed: ${suffix}`);
    return response.json();
  };
  const waitFor = async condition => {
    const deadline = Date.now() + 120000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Conversion UI timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const before = await json("");
  const metadata = await json("/metadata");
  const bookmarks = await json("/bookmarks");
  const order = await json("/page-order");
  assert(/\.(cbr|rar|cb7|7z|cbt|tar)$/i.test(before.location), "Fixture must be a supported non-ZIP source.");
  assert($("details-dialog").open && $("details-content").textContent.includes(before.location), "Open this fixture's Book details first.");
  assert(!$("convert-cbz").hidden, "Admin conversion action is missing.");
  $("convert-cbz").focus();
  $("convert-cbz").click();
  await waitFor(() => !$("apply-cbz-replacement").disabled);
  assert((await json("/intake-processing")).state === "none", "Opening conversion must not replace the source.");
  await new Promise(resolve => {
    $("intake-dialog").addEventListener("close", resolve, { once: true });
    $("intake-dialog").querySelector("[data-close]").click();
  });
  await waitFor(() => !$("intake-dialog").open && document.activeElement === $("convert-cbz"));
  $("convert-cbz").click();
  await waitFor(() => !$("apply-cbz-replacement").disabled);
  let applies = 0, previews = 0, statusReads = 0, release, prepared;
  const heldResponse = new Promise(resolve => { release = resolve; });
  window.fetch = async (...args) => {
    const url = new URL(args[0], location.href).pathname;
    if (url === path + "/intake-processing") statusReads++;
    if (url === path + "/cbz-replacement/preview") {
      previews++;
      if (previews === 1) return new Response(JSON.stringify({ error: "Injected preflight refusal" }), {
        status: 409, headers: { "Content-Type": "application/json" }
      });
      const response = await fetchReal(...args);
      if (response.ok) prepared = await response.clone().json();
      return response;
    }
    if (url !== path + "/cbz-replacement" || args[1]?.method !== "POST") return fetchReal(...args);
    applies++;
    const response = await fetchReal(...args);
    assert(response.ok, "Real replacement request must succeed.");
    await response.arrayBuffer();
    await heldResponse;
    throw new TypeError("Deliberately lost the real successful apply response");
  };
  try {
    $("apply-cbz-replacement").click();
    await waitFor(() => previews === 1 && !$("intake-refresh").disabled);
    assert(applies === 0 && (await json("")).location === before.location, "Failed safety checks must not start replacement.");
    $("apply-cbz-replacement").click();
    await waitFor(() => !$("intake-confirm").disabled);
    assert(applies === 0 && [prepared.source, prepared.destination, ...prepared.warnings]
      .every(value => $("intake-preview-details").textContent.includes(value)), "Conversion did not show source, destination and all warnings before mutation.");
    $("intake-discard").click();
    assert($("intake-preview").hidden && applies === 0 && (await json("")).location === before.location, "Cancelling conversion review changed the source.");
    $("apply-cbz-replacement").click();
    await waitFor(() => !$("intake-confirm").disabled);
    $("intake-refresh").click();
    await waitFor(() => !$("intake-refresh").disabled);
    assert($("intake-preview").hidden && $("intake-confirm").disabled && applies === 0, "Status refresh retained a conversion preview.");
    $("apply-cbz-replacement").click();
    await waitFor(() => !$("intake-confirm").disabled);
    $("intake-confirm").click();
    await waitFor(() => applies === 1);
    assert($("apply-cbz-replacement").disabled && $("intake-dialog").querySelector("[data-close]").disabled, "Mutation must disable duplicate apply and Close.");
    const escape = new Event("cancel", { cancelable: true });
    $("intake-dialog").dispatchEvent(escape);
    assert(escape.defaultPrevented && $("intake-dialog").open, "Escape must not dismiss an active replacement.");
    const hash = location.hash;
    location.hash = hash === "#library" ? "#unsorted" : "#library";
    await waitFor(() => location.hash === hash);
    assert($("intake-dialog").open, "Navigation must not close an active replacement.");
    release();
    await waitFor(() => !$("intake-refresh").disabled && $("convert-cbz").hidden);
    const after = await json("");
    const status = await json("/intake-processing");
    assert(previews === 4 && applies === 1 && statusReads > 0, "Conversion must check safety before applying and reconcile a lost response without retrying.");
    assert(status.state === "completed" && status.converted && !status.canRecover, "Conversion must complete with no pending recovery.");
    for (const key of ["id", "pageCount", "currentPage", "completed", "personalRating"])
      assert(after[key] === before[key], `Conversion changed ${key}.`);
    assert(after.location === before.location.replace(/\.[^.]+$/, ".cbz"), "Catalog path did not change to CBZ.");
    assert(JSON.stringify(await json("/metadata")) === JSON.stringify(metadata), "Saved catalog metadata changed.");
    assert(JSON.stringify(await json("/bookmarks")) === JSON.stringify(bookmarks), "Private bookmarks changed.");
    assert(JSON.stringify((await json("/page-order")).pages) === JSON.stringify(order.pages), "Saved physical page order changed.");
    await new Promise(resolve => {
      $("intake-dialog").addEventListener("close", resolve, { once: true });
      $("intake-dialog").querySelector("[data-close]").click();
    });
    await waitFor(() => document.activeElement === $("intake-history"));
    $("intake-history").click();
    await waitFor(() => !$("intake-refresh").disabled);
    assert($("cbz-replacement").hidden && $("intake-form").hidden, "History must not offer conversion or provider matching.");
    console.log("PASS: direct conversion review/cancellation/invalidation, preflight refusal, lost-response reconciliation, mutation guards, identity/metadata/bookmarks/page order, and nested focus. Preflight refusal and response delivery were fault-injected.");
  } finally {
    release();
    window.fetch = fetchReal;
  }
})();
