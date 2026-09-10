# Shade agent instructions

Read README.md, docs/investigation.md, docs/research.md, and the decision records before work. Inspect Git status and preserve unrelated changes.

Read [the shared-preference application](docs/preferences.md) and its current canonical PLAN sources before relevant work. It is part of the plan and covers mandatory workflow and conditional defaults beyond this short instruction list. PLAN is the shared preference source of truth; explicit current user requirements take precedence.

- Use C# and .NET 10 with top-level statements and CupriFace for the application interface.
- Never copy, port, translate, or adapt code from ScreenDimmer-MultiScreen or its upstream ScreenDimmer. Do not import their history, assets, or configuration. Consult public documentation and observed behavior to write an independent specification.
- Implement native integration from platform documentation and original code. Record dependency provenance and licenses separately.
- Windows and Linux support are required, with Windows primary. Do not claim support without tests. Multi-screen variable shading and Home Assistant setup/operation must work through the UI on supported environments.
- Prefer NativeAOT and a single self-contained executable when proven compatible. Record exceptions and compare published functionality, not just successful startup.
- Use Windows PowerShell 5.1 and .NET tooling. No Python, Node.js, or their transitive build tooling without explicit user permission.
- Vendor and document any third-party UI assets; do not rely on public CDNs.
- Add working repository-local VS Code build/debug configurations when runnable code is introduced.
- Keep settings, MQTT credentials, local logs, screenshots, and generated binaries out of Git. Never commit secrets.
- Keep this repository local until remote hosting and visibility are decided. Before any push, perform the user's complete outgoing-history privacy review and apply the required host-specific Git identity and access rules.
- Separate facts, assumptions, decisions, risks, and open questions. Date research and give next actions an owner. Preserve decision history.
