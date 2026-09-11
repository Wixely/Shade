# Current-source Windows packaging comparison

- Reviewed: 2026-09-10; next review: 2026-09-17
- Owner: Codex
- Status: Evaluation only; release defaults unchanged

This comparison uses the source-vendored Cupri engine 0.20.0-shade.2 and host 0.20.0-shade.5. It supersedes earlier package-based size figures for this source state. The full [packaging oracle](packaging-2026-09-10.md) remains the acceptance requirement.

`scripts/evaluate-packaging.ps1 -EvidenceDirectory <fresh-directory>` reproducibly builds the baseline, loose control, trimmed, compressed and combined variants. It verifies source provenance, captures separate process stdout/stderr, records exact publish arguments and hashes every output file. It refuses to overwrite evidence or accept PDBs. It does not change project publishing defaults or declare functional equivalence. `-RuntimeIdentifier linux-x64` selects the other evaluation target; see the separate [current-source Linux comparison](packaging-linux-current-source-2026-09-10.md).

All five Windows publishes complete without warning/error output. The first two script-development attempts failed in argument construction and size summarization respectively; the corrected full run completed and is the source of these figures.

| Variant | Total bytes | Output files |
| --- | ---: | ---: |
| Baseline single-file | 95,415,667 | 1 |
| Loose untrimmed control | 102,195,512 | 215 |
| Trimmed | 32,467,712 | 1 |
| Compressed | 45,570,187 | 1 |
| Trimmed and compressed | 19,288,448 | 1 |

Largest loose components: CoreLib 16,013,096 bytes, Skia 9,605,176, Private.Xml 7,788,368, coreclr 4,609,320, Linq.Expressions 3,639,080 and Silk OpenGL 2,820,816. The complete inventory and raw logs are in ignored packaging artifacts.

Cold-extraction and warm launches use a dedicated extraction directory per variant. The first run starts with that directory absent; the second reuses it. Each run exercises GPU controls, global/per-screen behavior, native keyboard navigation, protected password semantics and actual tray hide/reopen/Exit Shade. This does not simulate a cold OS filesystem cache or a clean machine. Timing includes process launch, UIA discovery and polling; one pair per variant is not a benchmark.

All eight GPU/keyboard/tray runs pass. Every application stdout/stderr log is empty, matching the baseline without new fallback output.

| Variant | Cold extraction to accessible slider, ms | Warm, ms |
| --- | ---: | ---: |
| Baseline | 1,090 | 978 |
| Trimmed | 1,311 | 1,237 |
| Compressed | 1,187 | 1,095 |
| Trimmed and compressed | 1,362 | 1,243 |

These samples show smaller output, not faster startup. No startup regression budget or release exception has been accepted.

All four single-file variants also pass the published application's native/MQTT oracle: command routing, native opacity, settings and credential recall after restart, retained-command rejection, credential/subscription/publication rejection, automatic recovery and cleanup. Each passing result is matched to the executable SHA-256 in the build manifest; the mapping is retained in private `verified-oracles.json`. These tests use an isolated loopback broker and temporary settings. They are not real Home Assistant or physical hotplug/game acceptance.

All four single-file variants also pass software-renderer broker setup, native keyboard navigation and normal exit. The actual form tests authenticate, save encrypted credentials, enable discovery, remove discovery and forget the password. Application stderr is empty; stdout is identical across all four builds and contains only the expected explicit software-renderer selection message. Cupri debug captures of the combined candidate's monitor map and scrolled Advanced controls were inspected.

All four pass published untrusted-TLS rejection, retries and local shading isolation. Each result is matched to the exact executable hash in `verified-oracles.json`. The one MQTT application connection in each fixture belongs to its test-only positive control; Shade rejects the untrusted endpoint before sending MQTT. These results do not establish successful Windows system-trusted TLS or real Home Assistant operation.

The tested baseline is copied, with its SHA-256 verified, into the ignored local Windows acceptance folder. A launcher uses separate persistent test settings; the [physical acceptance checklist](windows-acceptance-2026-09-10.md) covers controls, topology, identity and matched game measurements. Preparing this folder does not establish physical acceptance.

Further acceptance still includes complete Linux packaging, representative physical overlays/topology/game testing, Windows trusted TLS and real Home Assistant. No reduced-size release configuration is approved by these results. Owner: Codex, continue the functional comparison and present the completed evidence before any release-pipeline change.
