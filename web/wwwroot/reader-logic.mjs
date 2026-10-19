// Fixed pairs never regroup later pages when a landscape page is discovered.
export function spreadPages(page, count, coverAlone, isWide = () => false, isIncluded = () => true, order = null) {
  const position = order ? order.indexOf(page) : page;
  const physical = (index) => order ? order[index] : index;
  if (coverAlone && position === 0) return isIncluded(page) ? [page] : [];
  const start = position - (position - Number(coverAlone)) % 2;
  const visible = (start + 1 < count ? [physical(start), physical(start + 1)] : [physical(start)]).filter(isIncluded);
  return visible.some(isWide) ? visible.filter((member) => member === page) : visible;
}

export function pageIncluded(typeMask, skipPageTypes) {
  return !Number.isInteger(typeMask) || typeMask < 0 || typeMask > 2047 || !(typeMask & skipPageTypes);
}

export function nextPage(page, count, direction, isIncluded, order = null) {
  const position = order ? order.indexOf(page) : page;
  for (let next = position + direction; next >= 0 && next < count; next += direction) {
    const physical = order ? order[next] : next;
    if (isIncluded(physical)) return physical;
  }
  return null;
}

export function rotatedDimensions(width, height, rotation) {
  return rotation % 180 ? [height, width] : [width, height];
}

export function pageGeometry(width, height, rotation, availableWidth, availableHeight, fit, zoom) {
  const [rotatedWidth, rotatedHeight] = rotatedDimensions(width, height, rotation);
  const scale = (fit === "page" ? Math.min(availableWidth / rotatedWidth, availableHeight / rotatedHeight)
    : availableWidth / rotatedWidth) * zoom / 100;
  return { width: rotatedWidth * scale, height: rotatedHeight * scale, imageWidth: width * scale, imageHeight: height * scale };
}

export function swipeTurn(dx, dy, elapsed, preferences, viewportScale) {
  if (preferences.mode === "continuous" || preferences.zoom !== 100 || viewportScale > 1.01 ||
      elapsed > 900 || Math.abs(dx) < 60 || Math.abs(dx) < Math.abs(dy) * 1.5) return 0;
  return (dx < 0 ? 1 : -1) * (preferences.rtl ? -1 : 1);
}
