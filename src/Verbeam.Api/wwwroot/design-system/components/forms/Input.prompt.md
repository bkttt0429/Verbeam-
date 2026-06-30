Dark form field used for every text entry in the workbench (source text, language codes, model name, base64 payloads).

```jsx
<Input value="ja" />
<Input multiline value="こんにちは、勇者さん。" />
```

`multiline` renders the resizable monospace textarea seen in the source/result/OCR panes; `mono` forces monospace on a single-line field; `invalid` shows a red border. Focus paints the blue ring (`--focus-ring`).
