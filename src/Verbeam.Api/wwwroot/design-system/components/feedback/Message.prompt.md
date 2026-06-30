The core unit of the workbench terminal — every run appends one. Header tints by kind so the log scans by color.

```jsx
<Message kind="user" title="source" meta="ollama / qwen2.5:7b">こんにちは、勇者さん。</Message>
<Message kind="result" title="translation" meta="142 ms">你好，勇者大人。</Message>
<Message kind="error" title="translate" meta="0 ms">source is empty</Message>
```

Kinds: `user` (blue), `result` (green), `error` (red), `system` (muted). `title` is the left mono label, `meta` the right-aligned faint label (time/provider/latency).
