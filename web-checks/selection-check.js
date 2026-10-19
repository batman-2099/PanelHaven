// Run in the browser console on an idle library with at least two readable books.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 15000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Timed out waiting for selection check.");
      await new Promise(resolve => setTimeout(resolve, 50));
    }
  };
  assert(!$("library-view").hidden && !document.querySelector("dialog[open]"), "Start on the idle library page.");
  $("clear-selection").click();
  $("group-by").value = "none";
  $("group-by").dispatchEvent(new Event("change", { bubbles: true }));
  await waitFor(() => $("books").getAttribute("aria-busy") !== "true");
  const cards = [...$("books").querySelectorAll(".book-card:not(.group-card)")];
  assert(cards.length >= 2, "At least two books are required.");
  const first = cards[0].querySelector("input[type=checkbox]");
  const second = cards[1].querySelector("input[type=checkbox]");
  const cover = cards[1].querySelector(".cover-button");
  try {
    first.click();
    $("select-page").click();
    assert(cards.every(card => card.querySelector("input[type=checkbox]").checked), "Select page must select every book on this page.");
    $("select-page").click();
    assert(cards.every(card => !card.querySelector("input[type=checkbox]").checked) && document.querySelector(".selection-toolbar").hidden, "Clicking again must clear selection and leave selection mode.");
    first.click();
    cover.click();
    assert(first.checked && second.checked && !$("details-dialog").open && !$("library-view").hidden, "Cover click must add to selection, not open a book.");
    cover.click();
    assert(first.checked && !second.checked, "Clicking a selected cover must deselect it.");
    first.click();
    assert(document.querySelector(".selection-toolbar").hidden, "Deselecting the last book must leave selection mode.");
    cover.click();
    await waitFor(() => !$("reader-view").hidden && $("page-stage").querySelector(".reader-page"));
    assert(location.hash === `#reader/${encodeURIComponent(second.dataset.bookId)}?location=${$("library-location-filter").hidden ? "unsorted" : $("library-location").value}` && !second.checked && !$("details-dialog").open, "Outside selection mode, a cover must open the reader directly and retain its area.");
    console.log("PASS: cover selection mode, toggling, and direct reader navigation.");
  } finally {
    if ($("details-dialog").open) $("details-dialog").querySelector("[data-close]").click();
    if (location.hash.startsWith("#reader/")) {
      $("close-reader").click();
      await waitFor(() => !$("library-view").hidden);
    }
    $("clear-selection").click();
  }
})();
