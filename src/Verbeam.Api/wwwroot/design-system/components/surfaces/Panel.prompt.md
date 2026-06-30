Titled card for the inspector rail and grouped readouts (desktop / routes panels).

```jsx
<Panel title="routes">
  <StatusLine label="app" value="/app" />
  <StatusLine label="viewer" value="/viewer" />
</Panel>
```

Header is mono `#b8b8b8`; body is a 12px-gap grid. Pass `action` for a trailing control in the title bar.
