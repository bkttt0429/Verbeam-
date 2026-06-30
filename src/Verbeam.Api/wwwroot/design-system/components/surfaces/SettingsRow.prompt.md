A row in the Settings screens — copy on the left, control on the right.

```jsx
<SettingsRow
  title="Provider"
  description="Runtime used by text, OCR, audio and region translate."
  control={<Field label="provider"><Select options={["ollama","lmstudio","openai"]} /></Field>}
/>
```

Titles are Sentence case; descriptions are full sentences. Group rows under a `.settings-section-title`. Pass `last` to drop the divider on the final row.
