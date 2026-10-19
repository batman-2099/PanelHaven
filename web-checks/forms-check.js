// Run in the browser console on an idle library page, signed in as an administrator.
// Requires at least one book and one other account. Does not submit or save any form.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 10000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Forms check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const change = control => control.dispatchEvent(new Event("change", { bubbles: true }));
  const originalConfirm = window.confirm;
  assert(!$('library-view').hidden && !document.querySelector('dialog[open]'), "Start on the idle library page.");
  try {
    $("group-by").value = "none";
    change($("group-by"));
    await waitFor(() => $("books").getAttribute("aria-busy") !== "true" && $("books").querySelector(".book-card:not(.group-card)"));
    $("clear-selection").click();
    $("books").querySelector(".book-selection input").click();
    if ($("books").querySelector(".book-selection input:not(:checked)")) $("select-page").click();
    assert(!$("bulk-metadata").disabled, "At least one available book is required.");
    $("bulk-metadata").click();
    await waitFor(() => !$("metadata-controls").disabled);
    const schema = await fetch("/api/metadata-fields").then(response => response.json());
    const rows = [...$("metadata-fields").querySelectorAll(".metadata-change")];
    assert(rows.length === schema.length && schema.every(field => rows.some(row => row.dataset.field === field.name)), "Grouping lost a metadata field.");
    assert(rows.every(row => row.tagName === "FIELDSET" && row.querySelector("legend")), "Metadata fields lack contextual groups.");
    const title = rows.find(row => row.dataset.field === "Title");
    const selected = title.querySelector(".apply-change");
    selected.checked = true;
    change(selected);
    $("metadata-changed-only").checked = true;
    change($("metadata-changed-only"));
    assert(!title.hidden && rows.filter(row => !row.hidden).length === 1, "Checked-only filter lost the selected field.");
    assert($("metadata-change-summary").textContent.includes("Title"), "Checked-field summary lacks field context.");
    $("metadata-field-search").value = "no matching field";
    $("metadata-field-search").dispatchEvent(new Event("input", { bubbles: true }));
    assert(rows.every(row => row.hidden) && selected.checked && rows.length === $("metadata-fields").querySelectorAll(".metadata-change").length, "Searching changed the metadata draft.");
    window.confirm = () => false;
    document.querySelector('[data-close="metadata-dialog"]').click();
    assert($("metadata-dialog").open, "Metadata close discarded the draft without approval.");
    window.confirm = () => true;
    document.querySelector('[data-close="metadata-dialog"]').click();
    $("clear-selection").click();
    $("create-list").click();
    assert($("list-kind").value === "manual" && !$("list-controls").disabled && !$("save-list").disabled, "Manual list depends on smart-rule loading.");
    document.querySelector('[data-close="list-dialog"]').click();
    $("manage-users").click();
    await waitFor(() => !$("user-controls").disabled);
    let prompts = 0;
    window.confirm = () => { prompts++; return false; };
    document.querySelector('[data-close="user-dialog"]').click();
    assert(!$("user-dialog").open && prompts === 0, "Untouched account roles were treated as a draft.");
    $("manage-users").click();
    await waitFor(() => !$("user-controls").disabled);
    const reset = $("user-list").querySelector("details");
    assert(reset, "Another account is required for the reset-draft check.");
    reset.open = true;
    const password = reset.querySelector('input[name="password"]');
    password.value = "unsaved-check-password";
    password.dispatchEvent(new Event("input", { bubbles: true }));
    reset.open = false;
    await new Promise(resolve => setTimeout(resolve, 25));
    assert(password.value === "unsaved-check-password", "Collapsing reset discarded its draft.");
    document.querySelector('[data-close="user-dialog"]').click();
    assert($("user-dialog").open && prompts === 1, "Account close discarded a genuine draft.");
    window.confirm = () => true;
    document.querySelector('[data-close="user-dialog"]').click();
    console.log("PASS: complete metadata grouping, contextual controls, draft-safe filtering, manual-list readiness and account draft guards.");
  } finally { window.confirm = originalConfirm; }
})();
