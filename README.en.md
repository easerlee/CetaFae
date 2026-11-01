# CetaFae · 鲸汐

Drag a small whale onto a window to understand what it is, interpret its current state, and find the next control to use.

[简体中文](README.md) | English

> A Windows desktop development preview. CetaFae explains and guides; you operate the target application. Formal release acceptance is pending.

## Features

- **Lightweight management**: left-clicking the whale opens no management window. The tray and whale right-click share one menu. Agree once during installation and drag to use afterward, with no first-launch confirmation or routine cloud toggle; settings retains disclosure and withdrawal. Startup alone never collects or sends content.
- **Unobtrusive presence**: dismiss transient notices with their header close button or an outside click. The idle whale hides while another foreground application is fullscreen and returns afterward. Ordinary maximization and active guidance remain available; fullscreen detection uses local window geometry without analyzing content.
- **Drag to select**: an attached input bar, on-demand explanations, and control highlights keep interaction close to the target. Ending a session restores the whale.
- **Contextual feedback**: explanations expand below the input bar; validated controls can have a short nearby bubble. Details move to the reader, which changes side to avoid the control or collapses when space is insufficient. It can be reopened. Collapse preserves the session and highlights, while close or clear ends it. Long explanations scroll.
- **Understand the current window**: plain-language feedback explains the window and its purpose, distinguishing ordinary pages, actual errors/warnings and uncertain content. Main feedback omits process names, handles and signing metadata. Recognition failure is not evidence of an application fault, and an ordinary page is not a safety verdict. Generic vision can attempt unfamiliar software without promising recognition of every window.
- **Explain and guide**: interpret visible state and ask one clarification if needed. Highlight controls only when their location is reliable; otherwise explain the limitation.
- **Analyze a region**: use reliable task scopes or select an image region manually. Generic image understanding does not require an adapter for every application.
- **Chinese/English explanation and translation**: describe your translation goal and select a text region when needed. Show the recognized original and translation, checking recognizable error-code, date, numeric and unit literals. Unreadable content is reported honestly; the application's interface is not replaced.
- **Bounded error explanations**: distinguish visible information, unconfirmed possible causes and one safe next check. Missing evidence stays unknown. No automatic log inspection, other-process scan or repair is performed; the explanation does not certify a root cause.
- **User-led follow-up**: choose Continue asking after a result, perform any action yourself, then submit the next question. Each submission rechecks the same window, for up to eight steps, without reusing an old image or executing actions. Native multi-step acceptance remains pending.
- **Optional software references**: after separate confirmation, query a public encyclopedia using the product name and show sources. This does not authenticate the installed executable.
- **Plugin-based capabilities**: generic vision, software identity, and application adapters share a declarative contract. First-party features are plugins too.
- **On-demand settings**: open settings from the tray or whale right-click menu to manage connection, privacy and shipped capabilities, explicitly check the service, or export content-free diagnostics. Changing recipient, model or image scope requires new consent.

Representative human-tested flows include locating an Edge download toggle, explaining paused Windows Update status, finding the WinRAR extraction destination, and interpreting generated Notepad text within a selected region. These examples do not establish complete application support or full regression coverage of the latest source.

## Safety

- **Explicit consent and revocation**: disclose payload scope, recipient, model, and retention before enabling cloud processing. Withdrawing consent in Settings stops new collection and requests and invalidates active sessions. Content already sent cannot be recalled from upstream services.
- **Selected window only**: no full-desktop capture followed by cropping, and no automatic reading of other windows or background tabs.
- **Limited outbound scope**: initial understanding may send the selected whole-window image. Once the goal is known, use reliable local scopes or user selections; wait when scope is uncertain. PrintWindow still renders the selected whole window in local memory before cropping.
- **Conservative sensitive-content rejection**: visible password fields or recognizable credential text stop processing. Unavailable privacy checks fail closed. This is not a universal secret detector or a pixel-redaction guarantee.
- **No persisted session content**: no persisted images, raw control text, or conversations. Fixed metadata logs are retained for seven days; cleanup failures can delay deletion. Proxy and upstream retention policies remain unverified.
- **User-controlled actions**: no automatic clicks, typing into target applications, settings changes, extraction, or document editing. Switching away, closing the target, revoking consent, or ending the session clears invalid results.

## Getting started

Download a Windows x64 preview from [GitHub Releases](https://github.com/easerlee/CetaFae/releases), extract it, and run `CetaFae.Probe.exe`. Legacy extracted packages require Windows and the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) are required. Published packages may lag behind the main branch; consult the matching release notes.

The default service is `http://127.0.0.1:8787/v1`, with model `deepseek-v4.1-flash`. Supply a compatible model service yourself; CetaFae bundles neither a model nor a proxy. Image understanding requires multimodal input support.

The main branch provides advanced connection settings for saving the service address, model and image flag. Remote services require HTTPS; loopback HTTP is allowed. Connection checks explicitly request the displayed service's model list without reading a window; reachability does not prove image inference support. Credentials remain runtime-only and cannot be entered or saved in settings. The capabilities page enables or disables shipped plugins; it does not download or install plugins.

The main branch includes a self-contained Windows x64 installer candidate. Review the actual connection disclosure and choose Agree and install once. After installation, drag the whale directly; first launch and subsequent restarts have no second consent prompt and never collect content automatically. Settings retains read-only disclosure and withdrawal. Revocation, changed recipient/model/scope/terms or unusable records require reviewing the agreement in the installer again. The candidate compiles; native installation, cancellation and uninstall acceptance are pending, and it is not a formal distribution release.

Drag onto the target, describe your goal when asked, and select a region if requested. Read the guidance and operate the target yourself, or end the session. Changed recipient, model, sending scope or terms require confirmation again.

To continue, first choose Continue asking in the result, then act manually and submit your next question. Content reads pause while this input is armed. Switching to another window ends the entire flow and requires another drop. Changes to the disclosed sending scope or terms invalidate saved consent.

### Run from source

Install the .NET 8 SDK on Windows and run from the repository root:

```powershell
dotnet build src/CetaFae.Checks/CetaFae.Checks.csproj -c Debug
dotnet run --project src/CetaFae.Checks -c Debug --no-build
dotnet run --project src/CetaFae.Probe -c Release -- --guide
```

The first two commands build and run non-desktop checks; the last opens CetaFae. Override the current launch with `--base-url https://your-service.example/v1 --model your-model`; changes persist only after an explicit save in settings. Optional authentication is read only from the process environment variable `CETAFAE_API_KEY`; never commit credentials. Non-secret preferences are stored at `%LOCALAPPDATA%/CetaFae/settings.json`. Invalid configuration disables cloud processing until reviewed, saved and confirmed through the installer again.

### Build the installer candidate

On Windows, install Inno Setup 6.7.4 with the Simplified Chinese messages file and the .NET 8 SDK, then run:

```powershell
./installer/Build-Installer.ps1 -Compiler 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe' -ChineseMessagesFile 'C:/Program Files (x86)/Inno Setup 6/Languages/ChineseSimplified.isl'
```

Output is under `.artifacts/installer/`. The package includes the runtime, a Start menu shortcut and uninstall support. It installs per user without elevation or startup registration. Its agreement reflects existing non-secret settings; cancellation or failure creates no new consent, and silent installation cannot replace personal confirmation. A model service is still required. Extracting or launching source builds does not imply installation consent.

## Technical implementation

- **Windows / C# / WPF / .NET 8**: native window management, UI Automation, and selected-window capture.
- **Host/plugin separation**: the host owns consent, generic collection, model transport, validation, and lifecycle. Declarative plugins own application matching, traversal strategies, and task rules.
- **Complementary inputs**: control semantics support reliable targeting, while image analysis covers unfamiliar interfaces. Local process/product identity is shown separately from visual interpretation.
- **Constrained model output**: structured results, candidate/scope validation, and checks before display prevent cancelled requests from restoring stale results.
- **Isolated collection**: short-lived helper processes perform native reads and can be reclaimed after cancellation or timeout. Helpers count toward resource acceptance.
- **Independent feedback**: the input bar and edge overlays do not inject code into target applications.

| Directory | Purpose |
|---|---|
| `src/CetaFae.Probe` | Windows host, interaction, consent, and collection |
| `src/CetaFae.Plugins` | UI-independent plugin data contract and validation |
| `plugins` | Declarative capability packages and enabled catalog |
| `src/CetaFae.Checks` | Executable checks and generated fixtures |

## Roadmap

- Complete native settings, remembered-consent, coordinate and input acceptance, including single-display125%/150%/200% scaling.
- Improve generic understanding, translation and visible-error accuracy and failure recovery.
- Complete real-application acceptance for bounded tasks and user-led follow-up; actions remain manual.
- Complete consolidated privacy, platform, performance, and real-task acceptance. The 3000ms processing target and 90MB idle-memory gate have not passed full formal acceptance.
- Stabilize the contract and validate independent contributions before publishing third-party interfaces and installation support.
- Develop constrained document editing only after explicit task intent, confirmation, snapshot, and rollback gates are met; it is not currently provided.

Most native validation targets Simplified Chinese Windows 11, one display, and 100% scaling. Protected windows, restricted interfaces, faulty accessibility providers, and dynamic custom rendering may be unsupported. Multiple displays, high DPI, and broad compatibility need further acceptance testing.

## Maintenance and contributions

Report OS/application versions, reproduction steps, and actual messages through [Issues](https://github.com/easerlee/CetaFae/issues). Do not upload screenshots or logs containing passwords, tokens, or private content.

See the [requirements](需求文档.md) for scope, [Tech-Spec](Tech-Spec.md) for design, [Test-Plan](Test-Plan.md) for coverage, and [verification records](验证记录.md) for executed evidence and limitations. Build outputs and diagnostics belong in the ignored `.artifacts/` directory.

Public visibility does not grant an open-source license. No open-source license has been selected yet.
