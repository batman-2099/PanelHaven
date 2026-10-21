// Run on an idle DISPOSABLE server with two selected, unprocessed Unsorted fixtures.
// A real Comic Vine key is required. Both fixtures will be processed using the same
// known provider issue; use duplicate disposable copies, never live comicbooks.
// window.panelHavenIntakeQueueCheck = { disposable: true, bookIds: ["first", "second"], issueId: 1109530, volumeId: 164287 };
(async () => {
  const $ = id => document.getElementById(id);
  const config = window.panelHavenIntakeQueueCheck;
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  assert(config?.disposable === true && config.bookIds?.length === 2 && new Set(config.bookIds).size === 2, "Two distinct disposable fixtures are required.");
  const waitFor = async condition => {
    const deadline = Date.now() + 120000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Queue processing timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const originalFetch = window.fetch;
  const response = await originalFetch(`/api/comic-vine/issues/${config.issueId}/metadata?volumeId=${config.volumeId}`);
  assert(response.ok, "Real provider metadata is required.");
  const metadata = await response.json();
  window.fetch = async (...args) => {
    if (new URL(args[0], location.href).pathname.endsWith("/intake/matches"))
      return new Response(JSON.stringify({ candidates: [{ issueId: config.issueId, volumeId: config.volumeId, series: metadata.fields.Series, number: metadata.fields.Number, title: metadata.fields.Title, url: metadata.url, score: 100 }], warnings: [] }), { headers: { "Content-Type": "application/json" } });
    return originalFetch(...args);
  };
  try {
    $("clear-selection").click();
    for (const id of config.bookIds) document.querySelector(`[data-book-id="${CSS.escape(id)}"]`).click();
    $("process-unsorted").click();
    let previousCover = null;
    for (let index = 0; index < config.bookIds.length; index++) {
      assert($("intake-queue").value === config.bookIds[index], "Queue did not automatically select the next comicbook.");
      await waitFor(() => !$("intake-hints").disabled);
      if (index > 0) {
        const nextStatus = await (await originalFetch(`/api/books/${config.bookIds[index]}/intake-processing`)).json();
        assert(nextStatus.state === "none", "Advancing must not automatically process the next comicbook.");
        assert(document.activeElement === $("intake-queue"), "Queue advancement must restore keyboard focus.");
      }
      const currentCover = $("intake-current-cover").querySelector("img");
      previousCover?.dispatchEvent(new Event("error"));
      await currentCover.decode();
      assert(currentCover.parentElement === $("intake-current-cover"), "An old cover failure replaced the newly selected comicbook cover.");
      previousCover = currentCover;
      $("intake-series").value = metadata.fields.Series;
      $("intake-series").dispatchEvent(new Event("input", { bubbles: true }));
      $("intake-match").click();
      await waitFor(() => $("intake-candidates").querySelector("button:not(:disabled)"));
      $("intake-candidates").querySelector("button").click();
      await waitFor(() => index === 0 ? $("intake-queue").value === config.bookIds[1] && !$("intake-hints").disabled : !$("intake-dialog").open);
      const status = await (await originalFetch(`/api/books/${config.bookIds[index]}/intake-processing`)).json();
      assert(status.state === "completed", "Real queued processing did not complete.");
      assert($("intake-dialog").open === (index === 0), "Window must stay open for remaining work and close after the final success.");
    }
    assert($("selection-count").textContent.startsWith("0 "), "Processed queue entries were not deselected.");
    console.log("PASS: two real queue operations; automatically advances after first success and closes after final success. Only candidate matching was simulated.");
  } finally { window.fetch = originalFetch; }
})();
