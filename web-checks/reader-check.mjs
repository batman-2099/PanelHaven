// Run with: node web-checks/reader-check.mjs
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { spreadPages, swipeTurn, pageIncluded, nextPage, rotatedDimensions, pageGeometry } from "../web/wwwroot/reader-logic.mjs";

// Exercise the reader's actual readiness gate: a terminal error must not trap a spread.
const appSource = readFileSync(new URL("../web/wwwroot/app.js", import.meta.url), "utf8");
const spreadResolvedSource = appSource.match(/^function spreadResolved\(\) \{[\s\S]*?^\}/m)?.[0];
assert.ok(spreadResolvedSource, "Reader spread readiness gate is present");
const spreadEntries = [{ width: 600, state: "ready" }, { width: 0, state: "loading" }];
const spreadResolved = runInNewContext(`${spreadResolvedSource}\nspreadResolved`, {
  spreadCandidates: () => [0, 1], pages: spreadEntries, isPageWide: () => false
});
assert.equal(spreadResolved(), false, "Wait while the other page is still loading");
spreadEntries[1].state = "error";
assert.equal(spreadResolved(), true, "A failed partner allows navigation");
spreadEntries[0] = { width: 0, state: "error" };
assert.equal(spreadResolved(), true, "Two failed pages still allow leaving the spread");
spreadEntries[1].state = "loading";
assert.equal(spreadResolved(), false, "An explicit retry waits for its result");

// Applying a reviewed move needs a valid preview, not a second checkbox.
const moveElements = {};
const moveContext = {
  session: { role: "admin" }, fileOperationBookId: "book", fileOperationBusy: false,
  fileOperationPreview: null, fileOperationDraftGeneration: 0,
  $: id => moveElements[id] ||= { options: [0], value: "", querySelectorAll: () => [],
    querySelector: () => ({}), replaceChildren() {} }
};
const moveFunctions = ["updateFileOperationControls", "invalidateFileOperationPreview"];
const moveSource = moveFunctions.map(name => appSource.match(new RegExp(`^function ${name}\\([^\\n]*\\) \\{[\\s\\S]*?^\\}`, "m"))?.[0]).join("\n");
const moveControls = runInNewContext(`${moveSource}\n({${moveFunctions.join(",")}})`, moveContext);
for (const [role, busy, preview, disabled] of [
  ["admin", false, null, true], ["admin", false, { canApply: false }, true],
  ["admin", false, { canApply: true }, false], ["admin", true, { canApply: true }, true],
  ["reader", false, { canApply: true }, true]
]) {
  Object.assign(moveContext, { session: { role }, fileOperationBusy: busy, fileOperationPreview: preview });
  moveControls.updateFileOperationControls();
  assert.equal(moveElements["apply-file-operation"].disabled, disabled);
}
moveContext.session.role = "admin";
moveControls.invalidateFileOperationPreview();
assert.equal(moveElements["apply-file-operation"].disabled, true, "Changing a reviewed draft requires a new preview");
assert.equal(moveContext.fileOperationPreview, null);

// Scan dismissal survives polling/reload without hiding changed results or errors.
const scanElements = {};
const scanStorage = new Map();
const scanContext = { scanBusy: false, dismissedScanResult: null, scanTimer: null, scanRequest: null,
  session: { username: "scan-check" }, clearTimeout,
  localStorage: { getItem: key => scanStorage.get(key) ?? null, setItem: (key, value) => scanStorage.set(key, value) },
  scanStatus: { state: "completed", scanned: 10, added: 0, updated: 0, skipped: 10, errorCount: 0, errors: [], revision: 2 },
  $: (id) => scanElements[id] ||= { hidden: true } };
const scanFunctions = ["resetScan", "scanResultSignature", "rememberScanDismissal", "renderScan"];
const scanSource = scanFunctions.map(name => appSource.match(new RegExp(`^function ${name}\\([^\\n]*\\) \\{[\\s\\S]*?^\\}`, "m"))?.[0]).join("\n");
const scan = runInNewContext(`${scanSource}\n({${scanFunctions.join(",")}})`, scanContext);
for (const [libraryHidden, state, errorCount, expectedHidden] of [
  [false, "completed", 0, false], [true, "completed", 0, true],
  [true, "cancelled", 0, true], [true, "running", 0, false],
  [true, "failed", 0, false], [true, "completed", 1, false]
]) {
  scanContext.$("library-view").hidden = libraryHidden;
  Object.assign(scanContext.scanStatus, { state, errorCount });
  scan.renderScan();
  assert.equal(scanElements["scan-panel"].hidden, expectedHidden, `${state}, library hidden: ${libraryHidden}, errors: ${errorCount}`);
}
scanContext.$("library-view").hidden = false;
scanContext.scanStatus.errorCount = 0;
scan.rememberScanDismissal(scan.scanResultSignature());
scanContext.scanStatus.revision++;
scan.renderScan();
assert.equal(scanElements["scan-panel"].hidden, true, "An identical automatic scan stays dismissed despite a new revision");
const completedScan = { ...scanContext.scanStatus };
scan.resetScan();
scanContext.scanStatus = { ...completedScan };
scan.renderScan();
assert.equal(scanElements["scan-panel"].hidden, true, "Reloaded dismissal still hides the same result");
scanContext.scanStatus.added = 1;
scan.renderScan();
assert.equal(scanElements["scan-panel"].hidden, false, "Changed results remain visible");
scanContext.scanStatus = { ...completedScan, errorCount: 1, errors: ["Cannot read archive"] };
scan.renderScan();
assert.equal(scanElements["scan-panel"].hidden, false, "Errors cannot be dismissed by a saved success");
scanContext.scanStatus = { ...completedScan, state: "running" };
scan.renderScan();
assert.equal(scanElements["scan-panel"].hidden, false, "Running scans remain visible");
scanContext.scanStatus = { ...completedScan };
scan.rememberScanDismissal(null);
scan.resetScan();
scanContext.scanStatus = { ...completedScan };
scan.renderScan();
assert.equal(scanElements["scan-panel"].hidden, false, "Starting a manual scan clears the remembered dismissal");
scanContext.localStorage.setItem = () => { throw new Error("Storage blocked"); };
scan.rememberScanDismissal(scan.scanResultSignature());
scan.renderScan();
assert.equal(scanElements["scan-panel"].hidden, true, "Blocked storage still permits dismissal for this page");

for (const count of [1, 2, 7, 8, 40]) {
  for (const coverAlone of [false, true]) {
    for (const wide of [[], [0], [3, 6], [12, 21]]) {
      const isWide = (page) => wide.includes(page);
      const groups = [];
      for (let page = 0; page < count;) {
        const group = spreadPages(page, count, coverAlone, isWide);
        groups.push(group);
        for (const member of group) assert.deepEqual(spreadPages(member, count, coverAlone, isWide), group);
        page = group.at(-1) + 1;
      }
      assert.deepEqual(groups.flat(), Array.from({ length: count }, (_, page) => page));
      for (let i = groups.length - 1; i > 0; i--) {
        assert.deepEqual(spreadPages(groups[i][0] - 1, count, coverAlone, isWide), groups[i - 1]);
      }
      assert.ok(groups.every((group) => group.length === 1 || !group.some(isWide)));
      if (coverAlone) assert.deepEqual(groups[0], [0]);
    }
  }
}
assert.deepEqual(spreadPages(2, 8, true, (page) => page === 3), [1, 2]);
assert.deepEqual(spreadPages(3, 8, true, (page) => page === 3), [3]);
assert.deepEqual(spreadPages(4, 8, true, (page) => page === 3), [4]);
assert.deepEqual(spreadPages(5, 8, true, (page) => page === 3), [5, 6]);
// Filters retain physical indices and fixed pair boundaries, including empty books.
const types = [1, 8, 16, 8, 64, 8, 128, 256];
for (const skip of [0, 16, 16 | 64 | 128, 2047]) {
  const included = (page) => pageIncluded(types[page], skip);
  const expected = types.flatMap((_, page) => included(page) ? [page] : []);
  for (const direction of [1, -1]) {
    const visited = [];
    let page = direction === 1 ? -1 : types.length;
    while ((page = nextPage(page, types.length, direction, included)) !== null) visited.push(page);
    assert.deepEqual(visited, direction === 1 ? expected : [...expected].reverse());
  }
  for (const cover of [true, false]) {
    for (const page of expected) {
      assert.deepEqual(spreadPages(page, types.length, cover, () => false, included),
        spreadPages(page, types.length, cover).filter(included));
    }
  }
}
assert.equal(pageIncluded(0, 2047), true);
assert.equal(pageIncluded(undefined, 2047), true);
assert.equal(pageIncluded(2048 | 8, 2047), true);
assert.equal(pageIncluded(8 | 16, 16), false);
const skipAd = (page) => pageIncluded(types[page], 16);
assert.deepEqual(spreadPages(1, 8, true, (page) => page === 2, skipAd), [1]);
assert.deepEqual(spreadPages(2, 8, true, () => false, (page) => page === 2 || skipAd(page)), [1, 2]);
assert.equal(nextPage(2, 8, 1, skipAd), 3); // A revealed ad returns to normal navigation.
assert.equal(nextPage(3, 8, -1, skipAd), 1);
// Saved order is positional; page IDs, metadata and bookmarks stay physical.
const order = [7, 2, 0, 6, 1, 4, 3, 5];
const includedOrder = (page) => page !== 2 && page !== 4;
assert.deepEqual(spreadPages(7, 8, true, () => false, () => true, order), [7]);
assert.deepEqual(spreadPages(0, 8, true, () => false, () => true, order), [2, 0]);
assert.deepEqual(spreadPages(0, 8, true, () => false, includedOrder, order), [0]);
assert.deepEqual(spreadPages(6, 8, true, (page) => page === 1, includedOrder, order), [6]);
assert.deepEqual(spreadPages(1, 8, true, (page) => page === 1, includedOrder, order), [1]);
assert.equal(nextPage(7, 8, 1, includedOrder, order), 0);
assert.equal(nextPage(0, 8, -1, includedOrder, order), 7);
assert.equal(nextPage(5, 8, 1, includedOrder, order), null);
assert.equal(nextPage(7, 8, -1, includedOrder, order), null);
const visitedOrder = [order[0]];
let orderedPage = order[0];
while ((orderedPage = nextPage(orderedPage, order.length, 1, includedOrder, order)) !== null) visitedOrder.push(orderedPage);
assert.deepEqual(visitedOrder, order.filter(includedOrder));
assert.notEqual(visitedOrder.at(-1), 7); // Highest physical page does not finish the book.
assert.equal(visitedOrder.at(-1), 5);
assert.deepEqual(spreadPages(2, 8, true, () => false, (page) => page === 2 || includedOrder(page), order), [2, 0]);
assert.equal(nextPage(2, 8, 1, includedOrder, order), 0); // Revealed skipped page returns to ordered navigation.
for (const rotation of [0, 90, 180, 270]) {
  const quarterTurn = rotation % 180 !== 0;
  assert.deepEqual(rotatedDimensions(600, 900, rotation), quarterTurn ? [900, 600] : [600, 900]);
  const fit = pageGeometry(600, 900, rotation, 1000, 600, "page", 100);
  assert.ok(Math.abs(fit.height - 600) < 1e-8);
  assert.ok(Math.abs(fit.width - (quarterTurn ? 900 : 400)) < 1e-8);
  assert.equal(quarterTurn ? fit.imageWidth : fit.imageHeight, fit.height);
  assert.equal(quarterTurn ? fit.imageHeight : fit.imageWidth, fit.width);
  const zoom = pageGeometry(600, 900, rotation, 1000, 600, "page", 200);
  assert.equal(zoom.width, fit.width * 2);
  assert.equal(zoom.height, fit.height * 2);
  const widthFit = pageGeometry(600, 900, rotation, 900, 200, "width", 100);
  assert.ok(Math.abs(widthFit.width - 900) < 1e-8);
  assert.ok(Math.abs(widthFit.height - (quarterTurn ? 600 : 1350)) < 1e-8);
  const wide = (page) => {
    const [width, height] = rotatedDimensions(page === 1 ? 900 : 600, page === 1 ? 600 : 900, rotation);
    return width > height;
  };
  assert.deepEqual(spreadPages(1, 8, true, wide), [1]);
}
const preferences = { mode: "single", zoom: 100, rtl: false };
assert.equal(swipeTurn(-100, 10, 200, preferences, 1), 1);
assert.equal(swipeTurn(100, 10, 200, preferences, 1), -1);
assert.equal(swipeTurn(-100, 10, 200, { ...preferences, rtl: true }, 1), -1);
assert.equal(swipeTurn(-100, 10, 200, { ...preferences, mode: "spread" }, 1), 1);
assert.equal(swipeTurn(-100, 10, 200, { ...preferences, mode: "continuous" }, 1), 0);
assert.equal(swipeTurn(-100, 10, 200, { ...preferences, zoom: 150 }, 1), 0);
assert.equal(swipeTurn(-100, 10, 200, preferences, 1.2), 0);
assert.equal(swipeTurn(-100, 100, 200, preferences, 1), 0);
assert.equal(swipeTurn(-20, 0, 200, preferences, 1), 0);
assert.equal(swipeTurn(-100, 0, 1200, preferences, 1), 0);
console.log("Reader spread/readiness, skip/navigation, rotation/geometry, and swipe checks passed.");
