Monospace status capsule for chrome — broadcast state, health, current mode, latency readout.

```jsx
<Pill dot="live">live</Pill>
<Pill>142 ms</Pill>
```

`dot`: `live` (green), `idle` (amber), `error` (red), `neutral`. Without `dot` it's a plain capsule (used for latency/mode labels).
