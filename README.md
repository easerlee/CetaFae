# CetaFae

Windows selected-window explanation and visual guidance, built with C# / WPF and .NET 8.

Private development repository: [easerlee/CetaFae](https://github.com/easerlee/CetaFae). This is a development preview, not an accepted production release. See `验证记录.md` for actual results, failures and remaining gates.

## What Works Today

CetaFae is an explanation and guidance assistant. Personally enable cloud processing from its menu, drag the floating handle onto a window, answer one short question when needed, and read the explanation. A small marker appears only when a returned control can be validated against the current target. The user operates the target software; the assistant does not click switches, enter paths, extract archives or change system settings.

The following flows have recorded human verification on earlier development candidates:

- **Edge Downloads:** ask where the save-location prompt switch is; receive its location and visible state, with a control marker. Chinese input, result retention and clearing have been exercised.
- **Windows Update:** explain the visible paused state and end date without confusing “Resume updates” with pausing.
- **WinRAR extraction-options dialog:** identify the destination-path input without performing extraction. This is the bounded X-01 flow, not general WinRAR automation.
- **Generic visual explanation:** read generated text in Notepad, preserve the result through the exercised caret transition, clear after an edit, and explain a user-selected image region. No Notepad-specific adapter was added.
- **Cancellation and refusal:** exercised cases include cloud-off rejection, revocation, switching pages/windows, minimizing or closing the target, selection cancellation, visible password refusal, and recovery on a normal page with the password page in a background tab.

These are evidence for the exact recorded builds and conditions, not a guarantee that every version, page, language, error or latest candidate works. The general visual plugin can attempt other non-elevated windows using selected-window images and available controls. When an explicit goal has no reliable automatic scope, the user selects the outbound region. Unknown privacy metadata, inaccessible targets, unreliable capture and invalid model results can stop the attempt.

The current source also includes a native card/menu presentation with centralized style values: idle starts with the handle only; clicking it opens the menu and separate cloud disclosure; question/result/error cards appear on demand; ending a session dismisses its card. Static generated previews and checks are separate from the pending native focus, IME and lifecycle acceptance. The question-mark handle remains an engineering placeholder; the final whale, water trail and window-edge wave are not integrated.

## What Is Not Accepted or Implemented

- No automatic computer control, system repair, hidden root-cause investigation, or arbitrary document writing. Conditional WPS editing/undo remains unpublished behind TC-3.
- No universal application support, executable third-party plugins, public plugin installation or marketplace. All concrete capabilities, including first-party adapters and the general visual guide, use the same validated declarative plugin contract.
- The full 18-task guidance baseline and conditional nineteenth document task have not passed real-task release acceptance. A 62-state generated-model batch returned 61 expected outcomes and one failure; a separate successful diagnostic does not replace that failed batch.
- The **3000ms processing** and **90,000,000-byte Private Bytes** gates remain unpassed. Earlier resource/timing failures are retained; latency deferral does not waive acceptance. Provider/proxy retention, complete sensitive-pixel coverage and platform/version coverage remain open.
- No production release, installer, signed distribution or final-art acceptance is claimed. New shared code/UI needs a frozen, authorized native verification batch; previous manual tests do not automatically transfer.

## Repository Map

- `需求文档.md`: sole product scope and acceptance criteria; v1.8 records the approved whale/wave and on-demand-card direction. The general visual plugin, user-selected outbound regions, bounded WinRAR task X-01 and19-task baseline remain. Timing, resource, recovery and privacy gates are unchanged. The latest presentation is a candidate; document revision is not implementation acceptance.
- `AGENTS.md`: current engineering and desktop-consent rules.
- `MEMORY.md`: durable project decisions and pitfalls.
- `Tech-Spec.md`: current implementation, contracts and outstanding gates.
- `Test-Plan.md`: comprehensive planned task/state, privacy, failure, platform and performance coverage; concentrated candidate verification, not actual results.
- `Art-Assets.md`: interaction-reference breakdown and the separate original-art production handoff.
- `验证记录.md`: consolidated direct Codex verification, including failures and unverified areas.
- `src/CetaFae.Probe/`: software-independent Windows guidance host.
- `src/CetaFae.Plugins/`: UI-independent data contract, validation and enabled-plugin resolver.
- `plugins/`: all concrete application/task plugins and their explicit enabled development catalog.
- `src/CetaFae.Checks/`: executable checks and generated desktop fixtures.
- `Directory.Build.props`: places build output/intermediates under ignored `.artifacts/`.

Keep verification results in the existing record. Do not add parallel legacy plans, per-attempt reports, browser profiles or runtime dumps. Runtime logs default to `.artifacts/logs/` and stay out of Git. Owned metadata logs expire seven days after their last write, checked on startup and hourly; daily segmentation bounds the active log. Locked files report incomplete retention. Unrelated/nested files and reparse paths are not followed or deleted.

The host refuses observations containing visible password fields, explicit credential-field labels or recognized credential text, with additional text checks before model dispatch and display. This is conservative refusal, not general secret recognition or pixel redaction. Custom-drawn sensitive pixels and incomplete provider metadata remain release gaps. See `Tech-Spec.md` for the privacy boundary and native regression requirements.

The host retains consent, target-only collection mechanisms, model transport, validation, rendering and cancellation. Every software-specific capability is a plugin, including first-party features. The current build loads declarative packages through the common validated contract; it has no public installation manager or executable plugin loader. See `Tech-Spec.md` for the contract and remaining release gates.

## Application Plugins

`plugins/catalog.json` explicitly lists enabled package IDs. Each maps to `plugins/<id>/plugin.json`. Application adapters and the general visual guide are preinstalled packages with no privileged host path. Resolve a unique matching application adapter first; otherwise use the unique explicitly enabled general package. Application conflicts never fall through to general guidance; multiple general packages reject. An empty enabled list supports no applications. Missing or invalid catalogs reject startup. Disabling an adapter removes its enhancement; disable the general package as well to remove generic coverage.

The development catalog also includes `cetafae.winrar` for X-01 only: locating the destination control in the extraction-options dialog. Exact Chinese/English title candidates constrain matching. The recorded Chinese dialog flow has earlier candidate evidence; English behavior and the newest shared presentation remain unverified. Use a generated ZIP, open the dialog manually and stop before extraction. Other WinRAR windows, automatic extraction, repair and personal archives are outside this validation. All three adapters use the same data-only contract; the new package does not add an application-specific host branch or prove independent third-party contributions.

For a developer contribution, start from an existing package, choose a unique ID/version and declare target process names, optional exact titles, `control`/`raw` traversal, task IDs and any blocked intent/control-name pairs. Enable it in the development catalog, build, and run the non-desktop checks. No host application-name branch is needed. More complex application rules require an explicitly extended contract, not hidden host special cases. Package matching does not establish that all pages/tasks in that software are supported.

Application packages use contract v2 `observationScopes`: each rule declares `intentContainsAny`, `anchorNameContainsAny`, `anchorControlTypes`, and `padding` (0–64 physical pixels). Exactly one intent rule and one visible safe anchor must match for automatic cropping. An unmatched/ambiguous scope pauses transmission and offers user selection within the bound target, retaining the submitted intent. User selection may deliberately include that entire target; the program never expands to it automatically. The host crops outbound pixels, sends only fully contained controls and optional anchor text, and validates returned IDs against that subset while retaining full local context. V1 packages still parse and use user selection for explicit goals. Initial unknown-goal context may include the disclosed selected whole window. PrintWindow still uses a transient local whole-window buffer before crop encoding: this is outbound minimization, not proof of complete local or pixel-secret isolation.

The schema3 `cetafae.visual-guide` package declares `kind: general`, empty `targets`/`observationScopes`/`blockedPoints`, and control-view traversal. It supplies generic eligibility through the same catalog/worker path, not host application-name branches. Images support explanation even with zero actionable candidates; markers still require validated observed IDs. Generic and user-selected images are fingerprinted locally and recaptured before display and on target events. Comparison accepts an exact inversion at a stable, target-owned native thin caret; it never masks a region or tolerates arbitrary changed pixels. Unknown/custom carets and other animation can still cancel. Missing privacy metadata still refuses; screenshots do not bypass password/elevation/privacy checks.

The 2026-10-28 caret candidate has human evidence for generated-text Notepad initial explanation, 15-second retention through caret disappearance, clearing after a real edit, and correct state/date interpretation from a user-selected image with zero outbound actionable controls. No Notepad adapter was added. Its isolated Debug/Release suites each pass 428 headless checks, and seven authorized native generated privacy checks pass. This establishes that exercised generic image path, not universal application coverage or release acceptance. Processing still exceeds the 3000ms gate; full privacy, resource and support-list gates remain open. See `验证记录.md` for the exact evidence and preserved failures.

The build copies the catalog and packages beside the executable; they must be distributed together. The running process retains a validated startup snapshot, so editing files does not change an active session. Development catalog changes take effect after rebuilding/restarting; visible restarts still need the user's permission. Public contribution readiness also needs version/language metadata, installation review, user-facing disable/remove, and affected-session cancellation for live changes. Executable third-party code remains unsupported.

## Build and Non-Desktop Checks

Requires Windows and the .NET 8 SDK. No external NuGet package is added.

Non-desktop checks include hidden child-process fixtures with generated data. They open no app windows, inspect no desktop content and send no model requests unless `--live`, `--live-winrar`, `--live-scopes`, `--live-general` or `--live-tasks` is explicitly supplied. The application also uses the installed `dotnet` host for short-lived, headless observation workers; ship the normal DLL/deps/runtimeconfig output together. These workers are part of product resource measurements.

```powershell
dotnet build src/CetaFae.Checks/CetaFae.Checks.csproj -c Debug
dotnet run --project src/CetaFae.Checks -c Debug --no-build
# Render generated card states offscreen, without a desktop window or model request.
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --render-cards
# Also use the application's runtime configuration to catch deployment-option regressions.
dotnet exec --runtimeconfig .artifacts/bin/CetaFae.Probe/Debug/net8.0-windows/CetaFae.Probe.runtimeconfig.json .artifacts/bin/CetaFae.Checks/Debug/net8.0-windows/CetaFae.Checks.dll
```

To diagnose managed request allocations without windows, network or forced collection, run `dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --transport-allocation`. It warms up three generated 1MiB payloads and measures five requests drained by an in-process handler. Compare identical configurations in separate processes; this is not the90MB idle-memory or3000ms end-to-end acceptance test.

Generated-data real-model checks through the selected local proxy:

```powershell
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --live
# Focused generated extraction-dialog checks only (plus the non-desktop suite).
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --live-winrar
# Focused cropped-payload checks for the three generated task fixtures.
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --live-scopes
# Generated image-only general guidance with no actionable UIA controls.
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --live-general
# All18 baseline guidance tasks,62 frozen generated state cases. No native task acceptance.
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --live-tasks
# A separately recorded diagnostic attempt; never replaces a failed batch result.
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --live-tasks --task-case=W-04/unavailable
```

Model: `deepseek-v4.1-flash`. Default endpoint: `http://127.0.0.1:8787/v1`, the user's opencode Go channel. Optional authentication comes from process variable `CETAFAE_API_KEY`; do not place secrets in files or commands.

## Desktop Runs

Passive Release resource sampler (run only after permission to start the required idle round):

```powershell
# Generated arithmetic checks only: no application/window launch or real resource sampling.
powershell -NoProfile -File src/CetaFae.Checks/Measure-IdleResources.ps1 -SelfTest

# Bind a freshly launched Release process by its actual PID/path and its startup metadata log.
# Replace the placeholders. Keep the product visible and idle; do not drag, toggle consent or minimize.
powershell -NoProfile -File src/CetaFae.Checks/Measure-IdleResources.ps1 `
  -TargetProcessId <actual-pid> -ExecutablePath <release-exe> `
  -LogPath <startup-jsonl> -OutputPath .artifacts/checks/idle-fresh.jsonl -Round fresh
```

The tool waits 30 seconds, then samples for 600 seconds, includes owned descendants and records every interval. Outputs must not already exist. Process/counter failures, delayed samples or log changes prevent a pass; Private Bytes and working set stay separate. It does not launch/stop apps, read windows/command lines, invoke the model, force GC or trim memory. A fresh round alone never establishes full acceptance. Run `-Round post-use` on the same process only after the separately recorded 20 actual help/cancel/clear cycles. Freeze display, OS, runtime, model and network conditions in `验证记录.md` before either actual round.

`-Round activity -StopPath .artifacts/checks/activity.stop` records the actual help period without applying idle thresholds or the 30-second warmup. The stop marker must not exist when starting; create it only when the human test ends. Keep the same PID. Its one-second peak is a sampled lower bound, not an instantaneous maximum; missing short-lived helper counters are recorded, never replaced with zero. Stop this sampler before starting `-Round post-use`. Activity results cannot establish idle or full resource acceptance.

The agent must obtain permission before these commands or any desktop interaction. The user personally enables cloud processing in the panel.

```powershell
# Fixed data only; no other app is read and no model request is sent.
dotnet run --project src/CetaFae.Probe -c Debug -- --guide-preview

# Live application; cloud is off until the user enables it.
dotnet run --project src/CetaFae.Probe -c Debug -- --guide

# Owned generated windows only; no model or saved screenshots.
dotnet run --project src/CetaFae.Checks -c Debug -- --capture-desktop
dotnet run --project src/CetaFae.Checks -c Debug -- --scroll-desktop

# Owned generated privacy window only; no model, real credentials or saved screenshots.
dotnet run --project src/CetaFae.Checks -c Debug -- --privacy-desktop
```

The current candidate starts with a question-mark handle, the placeholder for the planned whale. Click it to open the menu; choose “查看云端授权”, read the disclosure and personally click “同意并启用云端”. Opening the menu/disclosure alone never grants consent or resumes an old drop. Dragging beyond the system pointer threshold selects a window. With cloud enabled, release on a valid target attempts to activate it once before recognition; Windows may refuse and require a manual click before a new drop. Cloud-off drops do not activate or collect recognition content.

Cards appear only when needed. A question accepts one answer of up to300 characters; click the field to type. The whole-window blue outline identifies selection, while a thicker rounded control outline identifies a validated control. Neither is evidence that the target was modified. Clear/end removes both frames, pending state and the session card, leaving the handle. Click the handle for cloud-off or exit. Ordinary buttons do not request activation; the answer field requires deliberate focus. Esc applies when this application receives it, not as a global hotkey. Generated offscreen visual QA does not establish native focus/IME, target binding or dismissal acceptance for this new presentation. Final whale and wave integration remains pending.

The catalog offers specific adapters and an explicit general visual guide for other non-elevated targets. In an active session, “选择识别区域” opens a target-only selection overlay: drag an area to submit that scope, or press Esc to cancel the session. No screenshot is taken by the picker. The resumed stage revalidates target/context and applies the normal privacy/capture/model gates. General eligibility is not tested coverage for every application. Human evidence from earlier candidates does not establish the new selector or binary's behavior. See `验证记录.md` for the precise boundary; this is not a release.
