# TunnelLab TokenOverlay

A transparent, local-only token-usage overlay for Codex Desktop on Windows.

## Quick start

1. Open **Actions** in this repository and wait for **Build Windows** to finish.
2. Download the **TokenOverlay-win-x64** artifact (ZIP).
3. Extract the ZIP and run **CodexTokenOverlay.exe**.
4. Open a Codex Desktop conversation. A compact floating token counter will follow the Codex window; click it to expand.
5. Use the system tray icon to configure placement, shown metrics, or exit.

No API key, cloud account, or administrator access is required. This is a standalone companion: it does not inject code into Codex Desktop.

## Displayed metrics

Session totals, input/output tokens, cached tokens, cache-hit rate, reasoning tokens (when available), and current context usage. The overlay is a **separate always-on-top window**, not an embedded feature of the Codex app. It **does not** measure per-message latency or token-generation speed.

## Privacy and security

This Windows tool reads Codex session JSONL files in read-only mode and listens to Codex's local IPC to identify the selected conversation. No telemetry, analytics, remote API calls or upload features are included in the application. It does not modify Codex data. Session logs may contain private messages: **never upload them to issue trackers**.

## Source and licensing

This is a clearly attributed Windows distribution of the open-source [Codex Token Overlay](https://github.com/soleillevant0125/codex-token-overlay) project by **soleillevant0125**, licensed under MIT. Its original copyright and license are preserved in [LICENSE](LICENSE). See [UPSTREAM.md](UPSTREAM.md) for the original documentation. This is *not* an OpenAI product, and the Codex Desktop JSONL/IPC formats may change.

## Build from source

Requires the .NET 10 SDK for Windows.

```powershell
dotnet publish src/CodexTokenOverlay/CodexTokenOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

See the GitHub Actions build workflow for a repeatable cloud build.

### Per-turn response time

The overlay displays **Worked for**: the most recently started or completed Codex turn. During an active turn it counts upward from the recorded `task_started` timestamp; after completion it uses Codex's own `duration_ms` from `task_complete` (or the recorded timestamps when needed). Aborted turns are supported. This is **wall-clock agent work**, including tool and orchestration time, not raw model inference speed. When no valid timing event is in the local log tail, the display shows a dash. The user may choose Time as either compact field in the tray menu.

