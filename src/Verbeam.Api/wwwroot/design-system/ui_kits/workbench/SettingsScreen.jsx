/* global React */
const { useState } = React;
const { SettingsRow, Field, Select, Input, Notice, StatusLine } = window.LocalTranslateHubDesignSystem_32566a;

const NAV = [
  { group: "desktop", items: [["general", "General"], ["providers", "Providers"], ["sound", "Sound"]] },
  { group: "pipelines", items: [["ocr", "OCR"], ["audio", "Audio"], ["region", "Region"]] },
  { group: "runtime", items: [["broadcast", "Broadcast"]] },
];

function SettingsScreen() {
  const [section, setSection] = useState("general");
  return (
    <section style={stStyles.pane} aria-label="Settings">
      <nav style={stStyles.nav}>
        {NAV.map((g) => (
          <div key={g.group} style={stStyles.navGroup}>
            <div className="vb-label" style={stStyles.navTitle}>{g.group}</div>
            {g.items.map(([id, label]) => (
              <button
                key={id}
                type="button"
                onClick={() => setSection(id)}
                style={{
                  ...stStyles.navBtn,
                  background: section === id ? "var(--vb-active-bg)" : "transparent",
                  color: section === id ? "var(--vb-blue)" : "#969696",
                  boxShadow: section === id ? "var(--ring-active)" : "none",
                  fontWeight: section === id ? 600 : 400,
                }}
              >
                {label}
              </button>
            ))}
          </div>
        ))}
        <div style={stStyles.navFooter}><span>Verbeam</span><span>OpenCode desktop</span></div>
      </nav>
      <div style={stStyles.body}>{SECTIONS[section]()}</div>
    </section>
  );
}

function SectionBlock({ title, children }) {
  return (
    <section style={{ display: "flex", flexDirection: "column", gap: "0" }}>
      <div style={stStyles.sectionTitle}>{title}</div>
      {children}
    </section>
  );
}

const ICON_BASE = "../../icons";

const PROVIDER_CATEGORIES = [
  { id: "local", label: "Local" },
  { id: "official", label: "Official" },
  { id: "cn_official", label: "CN Official" },
  { id: "aggregator", label: "Aggregator" },
  { id: "third_party", label: "Third Party" },
];

const PROVIDERS = [
  { id: "ollama", name: "Ollama", icon: "ollama.svg", color: "#000000", category: "local", endpoint: "http://localhost:11434", model: "verbeam-mort-qwen2.5-0.5b:latest",
    description: "Ollama 是本機 LLM 執行環境，支援一鍵下載和執行 GGUF 模型。翻譯、OCR 後處理、語音後處理都走 Ollama API。適合離線使用、隱私需求高的場景。",
    models: [
      { name: "verbeam-mort-qwen2.5-0.5b:latest", installed: true, isDefault: true, source: "ollama" },
      { name: "yomibridge-mort-qwen2.5-0.5b:latest", installed: true, isDefault: false, source: "ollama" },
      { name: "lth-mort-qwen2.5-0.5b:latest", installed: true, isDefault: false, source: "ollama" },
      { name: "qwen2.5:0.5b", installed: true, isDefault: false, source: "ollama" },
      { name: "qwen2.5:1.5b", installed: false, isDefault: false, source: "recommended" },
      { name: "deepseek-r1:7b", installed: false, isDefault: false, source: "configured" },
    ]
  },
  { id: "llamacpp", name: "llama.cpp", icon: null, color: "#22c55e", category: "local", endpoint: "http://localhost:8080", model: "qwen2.5-7b-instruct-q4_k_m",
    description: "llama.cpp 是輕量高效的本機 LLM 推論引擎，直接載入 GGUF 模型檔案。Verbeam 內建自動下載和管理功能，不需要額外安裝。記憶體佔用低，適合低配機器。",
    models: [
      { name: "qwen2.5-7b-instruct-q4_k_m.gguf", installed: true, isDefault: true, source: "managed" },
      { name: "qwen2.5-14b-instruct-q4_k_m.gguf", installed: false, isDefault: false, source: "recommended" },
      { name: "gemma-2-9b-it-q4_k_m.gguf", installed: true, isDefault: false, source: "managed" },
    ]
  },
  { id: "lmstudio", name: "LM Studio", icon: null, color: "#6366F1", category: "local", endpoint: "http://localhost:1234", model: "local-model",
    models: [
      { name: "local-model", installed: true, isDefault: true, source: "configured" },
    ]
  },
  { id: "openai", name: "OpenAI", icon: "openai.svg", color: "#00A67E", category: "official", endpoint: "https://api.openai.com/v1", model: "gpt-5.5",
    models: [
      { name: "gpt-5.5", installed: true, isDefault: true, source: "fetched" },
      { name: "gpt-5.5-mini", installed: true, isDefault: false, source: "fetched" },
      { name: "gpt-5", installed: true, isDefault: false, source: "fetched" },
      { name: "o3", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "anthropic", name: "Anthropic", icon: "claude.svg", color: "#D97757", category: "official", endpoint: "https://api.anthropic.com/v1", model: "claude-sonnet-4",
    models: [
      { name: "claude-sonnet-4", installed: true, isDefault: true, source: "fetched" },
      { name: "claude-opus-4", installed: true, isDefault: false, source: "fetched" },
      { name: "claude-haiku-3.5", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "google", name: "Google Gemini", icon: "gemini.svg", color: "#4285F4", category: "official", endpoint: "https://generativelanguage.googleapis.com/v1", model: "gemini-2.5-pro",
    models: [
      { name: "gemini-2.5-pro", installed: true, isDefault: true, source: "fetched" },
      { name: "gemini-2.5-flash", installed: true, isDefault: false, source: "fetched" },
      { name: "gemini-2.0-flash", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "mistral", name: "Mistral", icon: "mistral.svg", color: "#FF7000", category: "official", endpoint: "https://api.mistral.ai/v1", model: "mistral-large",
    models: [
      { name: "mistral-large", installed: true, isDefault: true, source: "fetched" },
      { name: "mistral-medium", installed: true, isDefault: false, source: "fetched" },
      { name: "codestral", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "deepseek", name: "DeepSeek", icon: "deepseek.svg", color: "#1E88E5", category: "cn_official", endpoint: "https://api.deepseek.com", model: "deepseek-v4-pro",
    models: [
      { name: "deepseek-v4-pro", installed: true, isDefault: true, source: "fetched" },
      { name: "deepseek-v4-flash", installed: true, isDefault: false, source: "fetched" },
      { name: "deepseek-r1", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "qwen", name: "Qwen / Bailian", icon: "bailian.svg", color: "#624AFF", category: "cn_official", endpoint: "https://dashscope.aliyuncs.com/compatible-mode/v1", model: "qwen3-coder-plus",
    models: [
      { name: "qwen3-coder-plus", installed: true, isDefault: true, source: "fetched" },
      { name: "qwen3-max", installed: true, isDefault: false, source: "fetched" },
      { name: "qwen3-235b-a22b", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "zhipu", name: "Zhipu GLM", icon: "zhipu.svg", color: "#0F62FE", category: "cn_official", endpoint: "https://open.bigmodel.cn/api/coding/paas/v4", model: "glm-5.1",
    models: [
      { name: "glm-5.1", installed: true, isDefault: true, source: "fetched" },
      { name: "glm-4-plus", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "kimi", name: "Kimi", icon: "kimi.svg", color: "#6366F1", category: "cn_official", endpoint: "https://api.moonshot.cn/v1", model: "kimi-k2.6",
    models: [
      { name: "kimi-k2.6", installed: true, isDefault: true, source: "fetched" },
      { name: "kimi-k2.5", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "minimax", name: "MiniMax", icon: "minimax.svg", color: "#FF6B6B", category: "cn_official", endpoint: "https://api.minimaxi.com/v1", model: "MiniMax-M2.7",
    models: [
      { name: "MiniMax-M2.7", installed: true, isDefault: true, source: "fetched" },
      { name: "MiniMax-M2.5", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "stepfun", name: "StepFun", icon: "stepfun.svg", color: "#16D6D2", category: "cn_official", endpoint: "https://api.stepfun.com/step_plan/v1", model: "step-3.5-flash",
    models: [
      { name: "step-3.5-flash-2603", installed: true, isDefault: false, source: "fetched" },
      { name: "step-3.5-flash", installed: true, isDefault: true, source: "fetched" },
    ]
  },
  { id: "doubao", name: "DouBao", icon: "doubao.svg", color: "#3370FF", category: "cn_official", endpoint: "https://ark.cn-beijing.volces.com/api/v3", model: "doubao-seed-2-0",
    models: [
      { name: "doubao-seed-2-0-code-preview-latest", installed: true, isDefault: true, source: "fetched" },
      { name: "doubao-seed-2-0", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "siliconflow", name: "SiliconFlow", icon: "siliconflow.svg", color: "#6E29F6", category: "aggregator", endpoint: "https://api.siliconflow.cn/v1", model: "MiniMaxAI/MiniMax-M2.7",
    models: [
      { name: "MiniMaxAI/MiniMax-M2.7", installed: true, isDefault: true, source: "fetched" },
      { name: "deepseek-ai/DeepSeek-V4", installed: true, isDefault: false, source: "fetched" },
      { name: "Qwen/Qwen3-235B-A22B", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "openrouter", name: "OpenRouter", icon: "openrouter.svg", color: "#6566F1", category: "aggregator", endpoint: "https://openrouter.ai/api/v1", model: "openai/gpt-5.5",
    models: [
      { name: "openai/gpt-5.5", installed: true, isDefault: true, source: "fetched" },
      { name: "anthropic/claude-sonnet-4", installed: true, isDefault: false, source: "fetched" },
      { name: "google/gemini-2.5-pro", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "nvidia", name: "Nvidia", icon: "nvidia.svg", color: "#76B900", category: "aggregator", endpoint: "https://integrate.api.nvidia.com/v1", model: "moonshotai/kimi-k2.5",
    models: [
      { name: "moonshotai/kimi-k2.5", installed: true, isDefault: true, source: "fetched" },
      { name: "meta/llama-3.1-405b-instruct", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "perplexity", name: "Perplexity", icon: "perplexity.svg", color: "#20B2AA", category: "aggregator", endpoint: "https://api.perplexity.ai", model: "sonar-pro",
    models: [
      { name: "sonar-pro", installed: true, isDefault: true, source: "fetched" },
      { name: "sonar", installed: true, isDefault: false, source: "fetched" },
    ]
  },
  { id: "cohere", name: "Cohere", icon: "cohere.svg", color: "#39594D", category: "official", endpoint: "https://api.cohere.com/v2", model: "command-r-plus",
    models: [
      { name: "command-r-plus", installed: true, isDefault: true, source: "fetched" },
      { name: "command-r", installed: true, isDefault: false, source: "fetched" },
    ]
  },
];

function ProviderAvatar({ provider, size = 32 }) {
  const [svgHtml, setSvgHtml] = React.useState(null);
  React.useEffect(() => {
    if (!provider.icon) return;
    fetch(`${ICON_BASE}/${provider.icon}`)
      .then(r => r.ok ? r.text() : null)
      .then(html => {
        if (html) {
          setSvgHtml('<style>svg{width:100%!important;height:100%!important}</style>' + html);
        }
      })
      .catch(() => {});
  }, [provider.icon]);

  if (provider.icon && svgHtml) {
    return (
      <div style={{
        width: size, height: size, borderRadius: 8,
        display: "inline-flex", alignItems: "center", justifyContent: "center",
        background: "rgba(255,255,255,0.06)", flexShrink: 0, overflow: "hidden",
        color: "#e8e8e8",
      }}>
        <span
          dangerouslySetInnerHTML={{ __html: svgHtml }}
          style={{ display: "flex", alignItems: "center", justifyContent: "center", width: size - 8, height: size - 8 }}
        />
      </div>
    );
  }
  if (provider.icon && !svgHtml) {
    return (
      <div style={{
        width: size, height: size, borderRadius: 8,
        display: "inline-flex", alignItems: "center", justifyContent: "center",
        background: "rgba(255,255,255,0.06)", flexShrink: 0,
      }}>
        <i className="ri-loader-4-line" style={{ fontSize: 14, color: "var(--vb-muted)" }} />
      </div>
    );
  }
  const initials = provider.name.split(/\s+/).map(w => w[0]).join("").slice(0, 2).toUpperCase();
  return (
    <div style={{
      width: size, height: size, borderRadius: 8,
      display: "inline-flex", alignItems: "center", justifyContent: "center",
      background: provider.color || "#6366F1", color: "#fff",
      fontFamily: "var(--font-sans)", fontSize: 12, fontWeight: 700, flexShrink: 0,
    }}>
      {initials}
    </div>
  );
}

function ProviderCard({ provider, selected, onSelect }) {
  const [hover, setHover] = useState(false);
  const hasTooltip = !!provider.description;
  const modelCount = provider.models ? provider.models.length : 0;
  return (
    <div style={{ position: "relative" }}>
      <button
        type="button"
        onClick={() => onSelect(provider.id)}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        aria-pressed={selected}
        title={provider.name}
        style={{
          display: "grid",
          gridTemplateColumns: "28px minmax(0,1fr) 14px",
          alignItems: "center",
          gap: 8,
          minHeight: 52,
          padding: "7px 9px",
          border: `1px solid ${selected ? "var(--vb-blue)" : hover ? "var(--vb-line-strong)" : "var(--vb-line)"}`,
          borderRadius: 7,
          background: selected ? "rgba(59,130,246,0.08)" : hover ? "var(--vb-hover-soft)" : "transparent",
          cursor: "pointer", textAlign: "left",
          transition: "all 0.15s ease",
          boxShadow: selected ? "inset 0 0 0 1px rgba(59,130,246,0.2)" : "none",
          width: "100%",
        }}
      >
        <ProviderAvatar provider={provider} size={28} />
        <div style={{ minWidth: 0 }}>
          <div style={{
            color: "var(--vb-text)", fontSize: 12.5, fontWeight: 600,
            overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap",
          }}>{provider.name}</div>
          <div style={{
            color: "var(--vb-muted)", fontSize: 10, fontFamily: "var(--font-mono)",
            overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap",
          }}>{modelCount > 0 ? `${modelCount} models` : provider.category.replace("_", " ")}</div>
        </div>
        <i
          className={selected ? "ri-check-line" : "ri-arrow-right-s-line"}
          style={{ color: selected ? "var(--vb-blue)" : "var(--vb-faint)", fontSize: 14 }}
        />
      </button>
      {hasTooltip && hover && (
        <div style={pvStyles.tooltip}>
          <div style={pvStyles.tooltipTitle}>{provider.name}</div>
          <div style={pvStyles.tooltipBody}>{provider.description}</div>
          {modelCount > 0 && (
            <div style={pvStyles.tooltipMeta}>{modelCount} models available</div>
          )}
        </div>
      )}
    </div>
  );
}

function ModelChip({ model, selected, onSelect }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      type="button"
      onClick={() => onSelect(model.name)}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: "inline-flex", alignItems: "center", gap: 6,
        minHeight: 28, padding: "0 10px",
        border: `1px solid ${selected ? "var(--vb-blue)" : hover ? "var(--vb-line-strong)" : "var(--vb-line)"}`,
        borderRadius: 6,
        background: selected ? "rgba(59,130,246,0.1)" : "transparent",
        color: selected ? "var(--vb-blue)" : "var(--vb-text)",
        fontSize: 11, fontFamily: "var(--font-mono)", fontWeight: 500,
        cursor: "pointer", transition: "all 0.15s ease",
        whiteSpace: "nowrap",
      }}
    >
      {model.installed && <span style={{ width: 6, height: 6, borderRadius: "50%", background: model.isDefault ? "var(--vb-green)" : "var(--vb-muted)", flexShrink: 0 }} />}
      {!model.installed && <i className="ri-download-cloud-line" style={{ fontSize: 11, color: "var(--vb-muted)", flexShrink: 0 }} />}
      <span style={{ overflow: "hidden", textOverflow: "ellipsis" }}>{model.name}</span>
      {model.isDefault && <span style={{ color: "var(--vb-green)", fontSize: 9, fontWeight: 700 }}>DEFAULT</span>}
    </button>
  );
}

function ProvidersSection() {
  const [selected, setSelected] = useState("ollama");
  const [selectedModel, setSelectedModel] = useState("verbeam-mort-qwen2.5-0.5b:latest");
  const [filter, setFilter] = useState("all");
  const active = PROVIDERS.find(p => p.id === selected) || PROVIDERS[0];
  const filtered = filter === "all" ? PROVIDERS : PROVIDERS.filter(p => p.category === filter);
  const grouped = {};
  filtered.forEach(p => {
    if (!grouped[p.category]) grouped[p.category] = [];
    grouped[p.category].push(p);
  });
  const models = active.models || [];
  const installedCount = models.filter(m => m.installed).length;

  function handleSelectProvider(id) {
    setSelected(id);
    const p = PROVIDERS.find(x => x.id === id);
    if (p && p.models && p.models.length > 0) {
      const def = p.models.find(m => m.isDefault) || p.models[0];
      setSelectedModel(def.name);
    }
  }

  return (
    <SectionBlock title="Translation provider">
      <div style={pvStyles.shell}>
        <div style={pvStyles.browserPanel}>
          <div style={pvStyles.panelHeader}>
            <div>
              <div style={pvStyles.panelTitle}>Providers</div>
              <div style={pvStyles.panelMeta}>{filtered.length} shown / {PROVIDERS.length} total</div>
            </div>
            <div style={pvStyles.activeBadge}>{active.category.replace("_", " ")}</div>
          </div>

          <div style={pvStyles.filterBar}>
            <button
              type="button"
              onClick={() => setFilter("all")}
              style={{
                ...pvStyles.filterBtn,
                borderColor: filter === "all" ? "var(--vb-blue)" : "var(--vb-line)",
                color: filter === "all" ? "var(--vb-blue)" : "var(--vb-muted)",
                background: filter === "all" ? "rgba(59,130,246,0.1)" : "transparent",
              }}
            >All</button>
            {PROVIDER_CATEGORIES.map(cat => (
              <button
                key={cat.id}
                type="button"
                onClick={() => setFilter(cat.id)}
                style={{
                  ...pvStyles.filterBtn,
                  borderColor: filter === cat.id ? "var(--vb-blue)" : "var(--vb-line)",
                  color: filter === cat.id ? "var(--vb-blue)" : "var(--vb-muted)",
                  background: filter === cat.id ? "rgba(59,130,246,0.1)" : "transparent",
                }}
              >{cat.label}</button>
            ))}
          </div>

          <div style={pvStyles.providerScroll}>
            {PROVIDER_CATEGORIES.filter(cat => grouped[cat.id]).map(cat => (
              <div key={cat.id} style={pvStyles.categoryBlock}>
                <div style={pvStyles.catLabel}>{cat.label}</div>
                <div style={pvStyles.grid}>
                  {grouped[cat.id].map(p => (
                    <ProviderCard key={p.id} provider={p} selected={selected === p.id} onSelect={handleSelectProvider} />
                  ))}
                </div>
              </div>
            ))}
          </div>
        </div>

        <aside style={pvStyles.detailPanel}>
          <div style={pvStyles.activeHeader}>
            <ProviderAvatar provider={active} size={36} />
            <div style={{ minWidth: 0 }}>
              <div style={pvStyles.activeName}>{active.name}</div>
              <div style={pvStyles.endpoint} title={active.endpoint}>{active.endpoint}</div>
            </div>
          </div>

          {active.description && (
            <div style={pvStyles.aboutText}>{active.description}</div>
          )}

          <div style={pvStyles.detailSection}>
            <div style={pvStyles.detailSectionHeader}>
              <span>Models</span>
              <span>{installedCount} installed / {models.length} total</span>
            </div>
            {models.length > 0 && (
              <div style={pvStyles.modelGrid}>
                {models.map(m => (
                  <ModelChip key={m.name} model={m} selected={selectedModel === m.name} onSelect={setSelectedModel} />
                ))}
              </div>
            )}
          </div>

          <div style={pvStyles.detailSection}>
            <Field label="active model">
              <Input mono value={selectedModel} readOnly />
            </Field>
          </div>

          <div style={{ ...pvStyles.detailSection, borderBottom: "none" }}>
            <div style={pvStyles.detailSectionHeader}>
              <span>Recommendation</span>
              <span>general</span>
            </div>
            <div style={stStyles.stack}>
              <StatusLine label="recommended" value={active.model} />
              <StatusLine label="use" value="general" />
              <StatusLine label="reason" value="balanced ja to zh" />
            </div>
          </div>
        </aside>
      </div>
    </SectionBlock>
  );
}

const pvStyles = {
  shell: {
    display: "grid",
    gridTemplateColumns: "minmax(430px,1.35fr) minmax(320px,0.85fr)",
    gap: 16,
    height: "min(682px, calc(100vh - 160px))",
    minHeight: 560,
  },
  browserPanel: {
    minWidth: 0,
    minHeight: 0,
    display: "grid",
    gridTemplateRows: "auto auto minmax(0,1fr)",
    border: "1px solid var(--vb-line)",
    borderRadius: 8,
    background: "rgba(255,255,255,0.018)",
    overflow: "hidden",
  },
  detailPanel: {
    minWidth: 0,
    minHeight: 0,
    overflow: "auto",
    border: "1px solid var(--vb-line)",
    borderRadius: 8,
    background: "rgba(255,255,255,0.024)",
    padding: "14px 14px 8px",
  },
  panelHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    padding: "12px 12px 10px",
    borderBottom: "1px solid var(--vb-line)",
  },
  panelTitle: {
    color: "var(--vb-text)",
    fontSize: 13,
    fontWeight: 650,
  },
  panelMeta: {
    marginTop: 2,
    color: "var(--vb-muted)",
    fontSize: 10,
    fontFamily: "var(--font-mono)",
  },
  activeBadge: {
    minWidth: 0,
    maxWidth: 150,
    padding: "4px 8px",
    border: "1px solid var(--vb-line)",
    borderRadius: 999,
    color: "var(--vb-slate)",
    background: "rgba(255,255,255,0.025)",
    fontSize: 10,
    fontFamily: "var(--font-mono)",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  filterBar: {
    display: "flex", flexWrap: "wrap", gap: 6, padding: "10px 12px",
    borderBottom: "1px solid var(--vb-line)",
  },
  filterBtn: {
    display: "inline-flex", alignItems: "center",
    minHeight: 24, padding: "0 9px",
    border: "1px solid var(--vb-line)", borderRadius: 5,
    background: "transparent", color: "var(--vb-muted)",
    fontSize: 11, fontFamily: "var(--font-sans)", fontWeight: 500,
    cursor: "pointer", transition: "all 0.15s ease",
  },
  providerScroll: {
    minHeight: 0,
    overflow: "auto",
    padding: "0 12px 12px",
  },
  categoryBlock: {
    paddingTop: 10,
  },
  catLabel: {
    color: "var(--vb-muted)", fontSize: 10, fontFamily: "var(--font-mono)",
    fontWeight: 640, textTransform: "uppercase", letterSpacing: "0.04em",
    marginBottom: 6,
  },
  grid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(148px, 1fr))",
    gap: 7,
  },
  activeHeader: {
    display: "grid",
    gridTemplateColumns: "36px minmax(0,1fr)",
    alignItems: "center",
    gap: 10,
    paddingBottom: 12,
    borderBottom: "1px solid var(--vb-line)",
  },
  activeName: {
    color: "var(--vb-text)",
    fontSize: 15,
    fontWeight: 650,
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  endpoint: {
    marginTop: 2,
    color: "var(--vb-muted)",
    fontSize: 10.5,
    fontFamily: "var(--font-mono)",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  aboutText: {
    margin: "12px 0 0",
    padding: "10px 11px",
    border: "1px solid var(--vb-line)",
    borderRadius: 7,
    color: "var(--vb-slate)",
    background: "rgba(255,255,255,0.018)",
    fontSize: 11.5,
    lineHeight: 1.45,
    maxHeight: 84,
    overflow: "auto",
  },
  detailSection: {
    padding: "12px 0",
    borderBottom: "1px solid var(--vb-line)",
  },
  detailSectionHeader: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    marginBottom: 8,
    color: "var(--vb-text)",
    fontSize: 12,
    fontWeight: 650,
  },
  tooltip: {
    position: "absolute",
    left: "calc(100% + 8px)", top: 0,
    zIndex: 50,
    width: 260,
    padding: "10px 12px",
    border: "1px solid var(--vb-line-strong)",
    borderRadius: 8,
    background: "#1a1a1a",
    boxShadow: "0 8px 24px rgba(0,0,0,0.5)",
    pointerEvents: "none",
  },
  tooltipTitle: {
    color: "var(--vb-text)", fontSize: 13, fontWeight: 600,
    marginBottom: 6,
  },
  tooltipBody: {
    color: "var(--vb-slate)", fontSize: 11, lineHeight: 1.5,
    marginBottom: 6,
  },
  tooltipMeta: {
    color: "var(--vb-muted)", fontSize: 10, fontFamily: "var(--font-mono)",
  },
  modelGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(164px, 1fr))",
    gap: 6,
    maxHeight: 184,
    overflow: "auto",
  },
};

const OCR_SETTINGS_KEY = "verbeam.ocr.settings";

function readSavedOcrSettings() {
  const defaults = {
    provider: "auto",
    contentType: "screenshot_text",
    preference: "balanced",
    language: "ja",
  };

  try {
    return { ...defaults, ...(JSON.parse(window.localStorage.getItem(OCR_SETTINGS_KEY) || "null") || {}) };
  } catch {
    return defaults;
  }
}

function OcrSettingsSection() {
  const [settings, setSettings] = useState(readSavedOcrSettings);
  const [engines, setEngines] = useState([]);
  const [route, setRoute] = useState(null);
  const [error, setError] = useState("");

  React.useEffect(() => {
    let canceled = false;
    fetch("/ocr/engines")
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.json();
      })
      .then((items) => {
        if (!canceled) setEngines(Array.isArray(items) ? items : []);
      })
      .catch((err) => {
        if (!canceled) setError(err.message || "engine list failed");
      });
    return () => { canceled = true; };
  }, []);

  React.useEffect(() => {
    window.localStorage.setItem(OCR_SETTINGS_KEY, JSON.stringify(settings));
    window.dispatchEvent(new CustomEvent("verbeam-ocr-settings", { detail: settings }));

    let canceled = false;
    const query = new URLSearchParams({
      provider: settings.provider,
      contentType: settings.contentType,
      preference: settings.preference,
      profile: "default",
    });
    fetch(`/ocr/route?${query.toString()}`)
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.json();
      })
      .then((decision) => {
        if (!canceled) {
          setRoute(decision);
          setError("");
        }
      })
      .catch((err) => {
        if (!canceled) {
          setRoute(null);
          setError(err.message || "route failed");
        }
      });
    return () => { canceled = true; };
  }, [settings.provider, settings.contentType, settings.preference]);

  function update(key, value) {
    setSettings((current) => ({ ...current, [key]: value }));
  }

  const activeEngine = engines.find((engine) => engine.name === settings.provider) || null;
  const providerOptions = [
    { value: "auto", label: "auto" },
    ...engines.map((engine) => ({
      value: engine.name,
      label: engine.displayName || engine.name,
    })),
  ];

  return (
    <SectionBlock title="Recognition">
      <SettingsRow
        title="OCR engine"
        description="Engine used by OCR and OCR + Translate runs."
        control={
          <Field label="engine">
            <Select options={providerOptions} value={settings.provider} onChange={(event) => update("provider", event.target.value)} />
          </Field>
        }
      />
      <SettingsRow
        title="Routing profile"
        description="Content type and latency preference passed to the OCR router."
        control={
          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 8 }}>
            <Field label="content">
              <Select options={["dialogue", "screenshot_text", "document", "table", "formula", "formula_table", "high_accuracy"]} value={settings.contentType} onChange={(event) => update("contentType", event.target.value)} />
            </Field>
            <Field label="preference">
              <Select options={["speed", "balanced", "accuracy", "vlm"]} value={settings.preference} onChange={(event) => update("preference", event.target.value)} />
            </Field>
          </div>
        }
      />
      <SettingsRow
        title="Language"
        description="OCR recognition language sent with image requests."
        control={
          <Field label="language">
            <Select options={["ja", "zh-TW", "zh-CN", "en", "ko"]} value={settings.language} onChange={(event) => update("language", event.target.value)} />
          </Field>
        }
      />
      <SettingsRow
        title="Engine status"
        description="Live status returned by the OCR engine registry."
        control={
          <div style={stStyles.stack}>
            {error && <Notice>{error}</Notice>}
            <StatusLine label="available" value={settings.provider === "auto" ? "router" : activeEngine?.isAvailable ? "yes" : "no"} valueColor={settings.provider === "auto" || activeEngine?.isAvailable ? "var(--success)" : "var(--danger)"} />
            <StatusLine label="status" value={settings.provider === "auto" ? "auto" : activeEngine?.status || "-"} />
            <StatusLine label="source" value={settings.provider === "auto" ? "routing" : activeEngine?.source || "-"} />
            <StatusLine label="note" value={settings.provider === "auto" ? "server selects provider" : activeEngine?.note || "-"} />
          </div>
        }
      />
      <SettingsRow
        title="Resolved route"
        description="Actual provider selected for the current OCR settings."
        last
        control={
          <div style={stStyles.stack}>
            <StatusLine label="provider" value={route ? `${route.provider} (${route.profile})` : "-"} />
            <StatusLine label="latency" value={route ? `${route.expectedLatencyMs} ms` : "-"} />
            <StatusLine label="async" value={route ? route.preferAsyncJob ? "job" : "inline" : "-"} />
            <StatusLine label="structure" value={route ? route.preservesStructure ? "yes" : "no" : "-"} />
          </div>
        }
      />
    </SectionBlock>
  );
}

const SECTIONS = {
  general: () => (
    <SectionBlock title="Translation defaults">
      <SettingsRow
        title="Language pair"
        description="Default source and target for text, OCR, audio, and region pipelines."
        control={
          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "8px" }}>
            <Field label="source"><Select options={["ja", "en", "ko", "zh"]} defaultValue="ja" /></Field>
            <Field label="target"><Select options={["zh-TW", "zh-CN", "en", "ja"]} defaultValue="zh-TW" /></Field>
          </div>
        }
      />
      <SettingsRow
        title="Prompt mode"
        description="Prompt preset used when the translation request does not override mode."
        control={<Field label="mode"><Select options={["faithful", "natural", "literal"]} defaultValue="faithful" /></Field>}
      />
      <SettingsRow
        title="Glossary"
        description="Optional local glossary file applied to translation requests."
        last
        control={<Field label="glossary"><Select options={["none", "game-terms.csv", "honorifics.csv"]} defaultValue="game-terms.csv" /></Field>}
      />
    </SectionBlock>
  ),
  providers: () => <ProvidersSection />,
  sound: () => (
    <SectionBlock title="Sound effects">
      <SettingsRow
        title="Enable sounds"
        description="Play OpenCode-style feedback sounds for clicks, success, errors, and notifications."
        last
        control={<Field label="state"><Select options={["enabled", "disabled"]} defaultValue="enabled" /></Field>}
      />
    </SectionBlock>
  ),
  ocr: () => (
    <SectionBlock title="Recognition">
      <SettingsRow
        title="OCR engine"
        description="Engine used to read text from images, screenshots and screen regions."
        control={<Field label="engine"><Select options={["windows-ocr", "tesseract", "easyocr", "paddleocr"]} defaultValue="windows-ocr" /></Field>}
      />
      <SettingsRow
        title="Engine status"
        description="Availability of the selected recognition engine."
        last
        control={
          <div style={stStyles.stack}>
            <Notice>預設本機 OCR，輕量，適合一般文字</Notice>
            <StatusLine label="available" value="yes" valueColor="var(--success)" />
            <StatusLine label="source" value="windows" />
            <StatusLine label="blocks" value="12" />
          </div>
        }
      />
    </SectionBlock>
  ),
  audio: () => (
    <SectionBlock title="Speech recognition">
      <SettingsRow
        title="ASR engine"
        description="Engine used to transcribe audio and video into source text."
        control={<Field label="engine"><Select options={["whisper-local", "whisper-cpp", "vosk"]} defaultValue="whisper-local" /></Field>}
      />
      <SettingsRow
        title="Engine status"
        description="Availability of the selected speech engine."
        last
        control={
          <div style={stStyles.stack}>
            <StatusLine label="available" value="yes" valueColor="var(--success)" />
            <StatusLine label="segments" value="0" />
            <StatusLine label="engine" value="whisper-local" />
          </div>
        }
      />
    </SectionBlock>
  ),
  region: () => (
    <SectionBlock title="Screen capture">
      <SettingsRow
        title="Loop interval"
        description="Milliseconds between automatic region snapshots."
        control={<Field label="interval ms"><Input mono type="number" defaultValue="1500" /></Field>}
      />
      <SettingsRow
        title="Capture status"
        description="Current screen-capture and loop state."
        last
        control={
          <div style={stStyles.stack}>
            <StatusLine label="capture" value="off" />
            <StatusLine label="loop" value="off" />
            <StatusLine label="selection" value="—" />
          </div>
        }
      />
    </SectionBlock>
  ),
  broadcast: () => (
    <SectionBlock title="Latest translation">
      <SettingsRow
        title="Latest broadcast"
        description="Most recent message pushed to the viewer and projector surfaces."
        last
        control={
          <div style={stStyles.stack}>
            <StatusLine label="source" value="ja" />
            <StatusLine label="target" value="zh-TW" />
            <StatusLine label="kind" value="text" />
            <StatusLine label="provider" value="ollama" />
          </div>
        }
      />
    </SectionBlock>
  ),
};

SECTIONS.ocr = () => <OcrSettingsSection />;

const stStyles = {
  pane: { display: "grid", gridTemplateColumns: "236px minmax(0,1fr)", overflow: "hidden", background: "var(--vb-bg)" },
  nav: {
    borderRight: "1px solid var(--vb-line)",
    background: "var(--vb-panel)",
    padding: "18px 14px",
    display: "flex",
    flexDirection: "column",
    gap: "20px",
    overflow: "auto",
  },
  navGroup: { display: "grid", gap: "4px" },
  navTitle: { marginBottom: "6px" },
  navBtn: {
    display: "flex",
    alignItems: "center",
    minHeight: "32px",
    padding: "0 12px",
    border: "1px solid transparent",
    borderRadius: "var(--radius-md)",
    background: "transparent",
    fontFamily: "var(--font-sans)",
    fontSize: "13px",
    textAlign: "left",
    cursor: "pointer",
    transition: "var(--transition-control)",
  },
  navFooter: {
    marginTop: "auto",
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    paddingTop: "14px",
    fontFamily: "var(--font-mono)",
    fontSize: "10px",
    color: "var(--vb-faint)",
  },
  body: { padding: "0 40px 40px", overflow: "auto", display: "flex", flexDirection: "column", gap: "36px" },
  sectionTitle: { padding: "28px 0 8px", color: "var(--vb-text)", fontSize: "15px", fontWeight: 640 },
  stack: { display: "grid", gap: "7px" },
};

Object.assign(window, { VbSettingsScreen: SettingsScreen });
