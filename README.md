# CetaFae

Windows selected-window explanation and visual guidance, built with C# / WPF and .NET 8.

Private development repository: [easerlee/CetaFae](https://github.com/easerlee/CetaFae). This is a development preview, not an accepted production release. See `验证记录.md` for actual results, failures and remaining gates.

## Repository Map

- `需求文档.md`: sole product scope and acceptance criteria; v1.5 requires every application feature, including first-party features, to use the plugin contract. Task/state, timing, resource, recovery and privacy gates remain unchanged. Document revision is not implementation acceptance.
- `AGENTS.md`: current engineering and desktop-consent rules.
- `MEMORY.md`: durable project decisions and pitfalls.
- `Tech-Spec.md`: current implementation, contracts and outstanding gates.
- `验证记录.md`: consolidated direct Codex verification, including failures and unverified areas.
- `src/CetaFae.Probe/`: software-independent Windows guidance host.
- `src/CetaFae.Plugins/`: UI-independent data contract, validation and enabled-plugin resolver.
- `plugins/`: all concrete application/task plugins, including the two first-party packages, and their explicit enabled catalog.
- `src/CetaFae.Checks/`: executable checks and generated desktop fixtures.
- `Directory.Build.props`: places build output/intermediates under ignored `.artifacts/`.

Keep verification results in the existing record. Do not add parallel legacy plans, per-attempt reports, browser profiles or runtime dumps. Runtime logs default to `.artifacts/logs/` and stay out of Git.

The host retains consent, target-only collection mechanisms, model transport, validation, rendering and cancellation. Every software-specific capability is a plugin, including first-party features. The current build loads declarative packages through the common validated contract; it has no public installation manager or executable plugin loader. See `Tech-Spec.md` for the contract and remaining release gates.

## Application Plugins

`plugins/catalog.json` explicitly lists enabled package IDs. Each maps to `plugins/<id>/plugin.json`. Windows Settings and Edge are preinstalled packages with no privileged host path. An empty enabled list supports no applications. Missing or invalid catalogs reject startup; unknown and conflicting matches reject recognition.

For a developer contribution, start from an existing package, choose a unique ID/version and declare target process names, optional exact titles, `control`/`raw` traversal, task IDs and any blocked intent/control-name pairs. Enable it in the development catalog, build, and run the non-desktop checks. No host application-name branch is needed. More complex application rules require an explicitly extended contract, not hidden host special cases. Package matching does not establish that all pages/tasks in that software are supported.

The build copies the catalog and packages beside the executable; they must be distributed together. The running process retains a validated startup snapshot, so editing files does not change an active session. Development catalog changes take effect after rebuilding/restarting; visible restarts still need the user's permission. Public contribution readiness also needs version/language metadata, installation review, user-facing disable/remove, and affected-session cancellation for live changes. Executable third-party code remains unsupported.

## Build and Non-Desktop Checks

Requires Windows and the .NET 8 SDK. No external NuGet package is added.

Non-desktop checks include hidden child-process fixtures with generated data. They open no app windows, inspect no desktop content and send no model requests unless `--live` is explicitly supplied. The application also uses the installed `dotnet` host for short-lived, headless observation workers; ship the normal DLL/deps/runtimeconfig output together. These workers are part of product resource measurements.

```powershell
dotnet build src/CetaFae.Checks/CetaFae.Checks.csproj -c Debug
dotnet run --project src/CetaFae.Checks -c Debug --no-build
# Also use the application's runtime configuration to catch deployment-option regressions.
dotnet exec --runtimeconfig .artifacts/bin/CetaFae.Probe/Debug/net8.0-windows/CetaFae.Probe.runtimeconfig.json .artifacts/bin/CetaFae.Checks/Debug/net8.0-windows/CetaFae.Checks.dll
```

Generated-data real-model checks through the selected local proxy:

```powershell
dotnet run --project src/CetaFae.Checks -c Debug --no-build -- --live
```

Model: `deepseek-v4.1-flash`. Default endpoint: `http://127.0.0.1:8787/v1`, the user's opencode Go channel. Optional authentication comes from process variable `CETAFAE_API_KEY`; do not place secrets in files or commands.

## Desktop Runs

The agent must obtain permission before these commands or any desktop interaction. The user personally enables cloud processing in the panel.

```powershell
# Fixed data only; no other app is read and no model request is sent.
dotnet run --project src/CetaFae.Probe -c Debug -- --guide-preview

# Live application; cloud is off until the user enables it.
dotnet run --project src/CetaFae.Probe -c Debug -- --guide

# Owned generated windows only; no model or saved screenshots.
dotnet run --project src/CetaFae.Checks -c Debug -- --capture-desktop
dotnet run --project src/CetaFae.Checks -c Debug -- --scroll-desktop
```

The question ball selects a window. With cloud enabled, releasing it on a valid supported target attempts to activate that target once before recognition; Windows may refuse, in which case the panel asks the user to click the target and retry. The ball and panel do not automatically take focus. Cloud-off drops do not activate or collect recognition content. The explanation panel contains cloud consent, results, an optional short-answer field, clear and exit. The whole-window outline identifies the selection; a smaller outline identifies a validated control. Clear removes both outlines, keeping the ball and panel. Esc is handled only when this application receives it; it is not a global hotkey.

The collector admits Settings and Edge. Human evidence covers specific Windows Update states and the Edge Downloads question, Chinese input and submission path with a displayed marker. Detailed marker accuracy, remaining interaction cases and the complete support list remain open. See `验证记录.md` for the precise boundary; this is not a release.
