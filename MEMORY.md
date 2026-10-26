# CetaFae Project Memory

Updated: 2026-10-26

## Scope and Sources of Truth

- `需求文档.md` v1.1 defines the first-release requirements. `开发计划-CetaFae-0.01.md` narrows version 0.01 to explanation and visual guidance; document writing is outside that version.
- `探针/验证现状.md` is the current probe-status reference. Historical measurements are evidence for their recorded environments, not proof of current runtime behavior or support-list acceptance.

## Durable Review Notes

- Computer control can be implemented as an observation, model decision, local tool execution, and outcome-verification loop. CetaFae can reuse this architecture without reproducing the entire Codex product.
- Windows foreground input control is distinct from capturing an occluded window. Occluded-window capture does not establish non-disruptive background input.
- Stable UIA rectangles do not establish correct task interpretation. Fixture success does not establish acceptance of the 19 listed tasks.
- Reliable rollback requires application and file-state handling; screenshots and synthesized input do not provide rollback by themselves.
- `Codex计算机控制原理与CetaFae复现可行性分析.md` records a read-only architecture analysis. Its recommendations have not changed the approved requirements or development scope.
- The local Codex install ships a Computer Use plugin under `C:\Users\easer\.codex\plugins\cache\openai-bundled\computer-use\` (UIA, Windows.Graphics.Capture, SendInput via `@oai/sky`). It runs on the active desktop and takes the foreground, so it can cross-check probes but cannot replace human drag/acceptance runs.

## Measured Environment Facts

- 2026-10-26: `PrintWindow(PW_RENDERFULLCONTENT)` returns the target window without occluding windows; screen-region `BitBlt` includes whatever overlaps it.
- 2026-10-26: Chromium page content is reachable through the UIA RawView (374 nodes) but not ControlView (58 chrome-only nodes); no MSAA fallback is needed at this stage.

## Verification Boundary

- The 2026-10-26 analysis inspected source files, existing probe records, the installed Computer Use plugin documentation, and official OpenAI documentation.
- That analysis and its Markdown delivery did not run desktop probes, build the application, or verify model connectivity.
