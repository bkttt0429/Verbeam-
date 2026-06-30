Flat OpenCode-style action button — use for any clickable command in the workbench (Run, Clear, Capture Screen, settings actions).

```jsx
<Button variant="primary" commandKey=">">Run</Button>
<Button>Clear</Button>
<Button variant="danger">Stop</Button>
```

Variants: `primary` (blue, the affirmative action), `secondary` (default grey), `ghost` (transparent — used inside nav/tab rows), `danger` (dark-red destructive). Sizes `sm` (28px) / `md` (32px). Pass `commandKey=">"` for the green prompt glyph seen on the Run button; `fullWidth` for sidebar-width buttons.
