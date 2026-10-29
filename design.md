# Design: Panel Haven

Shared system for the existing application screens. Preserve routes, controls, permissions, and behavior when changing presentation.

## System
- Genre: modern-minimal, app scope.
- Theme: existing Panel Haven brand, dark navy with blue primary actions.
- Signature: comicbook cover stacks and private reading progress.
- Keep the existing icon and wordmark assets unchanged.
- Use native HTML controls and the existing JavaScript; no UI framework or font dependency.

## Tokens
`web/wwwroot/tokens.css` is the source of truth; `base.css` contains only shared platform foundations. `styles.css` owns components and screen layouts.
- Canvas: `--canvas`; surfaces: `--surface`, `--surface-inset`, `--surface-control`.
- Text: `--ink`, `--muted`; boundaries: `--line`, `--divider`.
- Blue: primary actions, links, selection and focus.
- Green: saves and progress; yellow: missing issues and intake; red: destructive actions and errors.
- Every action fill has readable paired ink; color never replaces labels.
- Typography: `--font-body`, native system sans; 16px body, 14px labels, 1.2rem section heads, 1.728rem page heads.
- Spacing: quarter-rem through 2.5rem shared tokens. Controls retain 44px minimum targets.
- Radius: 6px controls and covers; flat sections use dividers instead of nested cards.

## Layout
- Full-width application pages with responsive edge gutters; reader retains its full-window canvas.
- Library: reading-list picker and Manage lists in the browse row below search, followed by Location and View.
- Library opens directly with search; its page heading remains screen-reader-only for accessible naming and navigation hooks.
- Cover columns adapt to the available width; at 650px and below, covers use two columns.
- Settings: section navigation beside the form on desktop, wrapping above it on narrow screens.
- Forms and dialogs preserve existing validation, loading, empty, failure and confirmation states.
- Native dialogs contain their own scroll; wide comparisons use an explicit scroll region.

## Motion and access
- No decorative motion or reveals. Reduced motion disables transitions and animation.
- Instant visible focus rings, logical DOM order, native labels and dialogs, keyboard-operable controls.
- Content and safety guidance remain unchanged except presentation punctuation and heading hierarchy.

## Exports
`web/wwwroot/tokens.css`; no framework-specific export required.
