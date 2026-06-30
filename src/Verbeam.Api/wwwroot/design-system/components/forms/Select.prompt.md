Dark dropdown used for provider, model, prompt-mode, glossary, and OCR/ASR engine pickers in Settings.

```jsx
<Select options={["external", "tesseract", "paddleocr"]} value="external" />
```

Accepts `options` as strings or `{value,label,disabled}` objects, or pass `<option>` children directly. Custom chevron, blue focus ring.
