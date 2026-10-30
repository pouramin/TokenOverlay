# TunnelLab TokenOverlay

A lightweight, **independently implemented** Windows 10/11 desktop overlay for **Codex Desktop**, **Claude Code**, and **OpenCode**. Token usage and task time directly on top of your active coding app. Created and maintained by TunnelLab.

## Features

- One English-only floating window for all 3 coding apps.
- Compact view: platform, cumulative total tokens, elapsed time.
- Expand by clicking the capsule to see input, cache read/write, output, reasoning, last-turn duration, recorded cost and context (where available).
- Visible **×** button to exit, or system tray → **Exit TokenOverlay**.
- Choose the source (Auto / Codex / Claude / OpenCode) and position (top / bottom right) from the tray.
- Local-only. No account, API key, administrator access, analytics, or telemetry.

## OpenCode accounting

The current OpenCode release stores session counters in a local SQLite database. We read it **read-only**, calculate each session's **cumulative** usage as:

`input + cache.read + cache.write + output + reasoning`.

These counters are disjoint according to OpenCode's session implementation; no cache or reasoning double counting. The overlay separately displays **Main session** and **Total consumed**, which includes independently recorded child/subagent sessions if present.

When the main session shows 23,345 tokens but a subagent consumed another 10,655, the combined total reads 34,000. **This is an illustrative example, not a claim about your actual usage.** A missing local record cannot be reconstructed, and totals may still differ from provider billing or upstream OpenCode stats.

## Data sources and limitations

- Codex: local `~/.codex/sessions` JSONL, including recorded token counts and turn durations.
- Claude Code: local `~/.claude/projects` JSONL, deduplicating assistant messages by ID and using `turn_duration` events.
- OpenCode: read-only `~/.local/share/opencode/opencode.db` SQLite, using cumulative `session` counters. Honors `OPENCODE_DB`.
- Selects the most recently updated local session, which may differ from the foreground conversation if several are open.
- Cloud-only tasks and providers that do not write local usage cannot be measured.
- Context percentage only appears when an explicit context limit is in the source log. OpenCode's cumulative total must **never** be described as context usage.
- Elapsed time includes tools/agent orchestration; it is not model tokens-per-second throughput.

## Installation

Open [Releases](../../releases), download `TunnelLab-TokenOverlay-win-x64.zip`, unzip, and run `TokenOverlay.exe`. The build is standalone and does not require a separate .NET runtime.

The app is not code-signed. Verify the release checksum and scan the executable before using third-party binaries.

## Building

Requires .NET 10 SDK:

```powershell
dotnet run --project tests/Smoke/Smoke.csproj -c Release
dotnet publish src/TunnelLabOverlay/TokenOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

The source does not copy the old overlay implementation. Older release tags and their respective source history remain accessible independently.

## 1.1.0 UI and compatibility update

The overlay defaults to the bottom-right of the active app to avoid toolbar buttons. Drag the header to position it anywhere; the offset is saved locally. The floating bar is 302px wide and the × button exits immediately. OpenCode supports both legacy and new SQLite session/message tables, and checks additional Windows data directories. When no reliable usage record exists, the program displays **No data**, never a misleading 0. Stale timestamps do not produce multi-day timers. In Claude Desktop the latest local Claude Code JSONL is not necessarily the current cloud chat, so local numbers may not match the visible tab.

## v1.2.0 — session selection and Claude Desktop

- **OpenCode session picker:** Right-click the tray icon, select **OpenCode session**, then choose the exact session by title. The newest session is used automatically unless you explicitly choose one. This avoids confusing a freshly modified unrelated conversation with the visible one.
- **Claude Desktop Code-tab:** reads local Desktop manifests (`cliSessionId`, `title`, `lastActivityAt`) from both normal roaming storage and Windows MSIX package storage, then associates the selected metadata with its actual local JSONL transcript. Ordinary CLI transcripts with no Desktop manifest are no longer displayed as though they belonged to the Desktop tab.
- Claude Desktop lacks a supported public active-tab API. **Recent local** and **historical local** are labelled accordingly. Cloud chats are not tracked.
