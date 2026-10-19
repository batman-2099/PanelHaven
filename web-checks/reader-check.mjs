// Run with: node web-checks/reader-check.mjs
// Real DOM readiness, move controls and scan persistence: run ui-audit-check.js in the browser.
import assert from "node:assert/strict";
import { spreadPages, swipeTurn, pageIncluded, nextPage, rotatedDimensions, pageGeometry } from "../web/wwwroot/reader-logic.mjs";

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
console.log("Reader spread, skip/navigation, rotation/geometry, and swipe checks passed.");
