# CetaFae · 鲸汐

**Something on your computer doesn't make sense? Drag the little whale over.**

CetaFae is a desktop assistant for everyday computer problems. Ask about an unfamiliar interface, an error, or a file task. When it needs to see the situation, drag the whale onto that window. It explains what is happening, helps you find the next step, and handles supported tasks with you.

[简体中文](README.md) | English

**[Download installer · Windows x64](https://github.com/easerlee/CetaFae/releases/download/v0.3.2/CetaFae-0.3.2-win-x64-setup.exe)** · [Release notes](https://github.com/easerlee/CetaFae/releases/tag/v0.3.2)

> **Version 0.3.2 · Preview release.** The installer includes the runtime; no separate .NET installation is needed. AI chat and image understanding require a compatible model service, which is not bundled and may charge for usage. Testing has mainly used Chinese-language Windows 11. The application UI is currently in Chinese.

## Highlights

### Understand what you are looking at

Drag the whale onto a window for a short explanation of the application and its visible content. Ask what an option, English text, or an error means. You can also select a small part of the window for explanation or translation.

### Work out what to do next

Describe your goal, such as finding the option that asks where to save downloads. CetaFae explains the steps and highlights a control or text when it has reliable location evidence. Ask follow-up questions after acting. You can visit another window and return to continue. For errors, it summarizes the problem, suggests an initial approach, and adjusts its guidance based on your feedback.

### Handle everyday tasks

- **Screenshots and text**: capture the selected window, crop, annotate, cover sensitive areas, copy, save, or pin the image for reference. Extract text locally, edit it, then translate, summarize, or rewrite it.
- **Image conversion and compression**: explain what the image is for and get a format recommendation. Convert static images to JPG, PNG, WebP, or PDF. Compress a single JPG, PNG, or BMP into a size-limited JPG copy. Processing stays on your computer and preserves the original. An optional component may need downloading on first use.
- **File help**: inspect disk space, total a selected folder, or read a selected UTF-8 text file. Preview supported text saves, file copies, moves, deletions, and literal text replacements. Changes are scoped and checked afterward; supported recovery is available through the operation history.
- **Memory help**: see which applications use more memory, release a selected application's temporary working memory, and check whether it rebounds. Optional sustained-use reminders are off by default and never trigger automatic cleanup. Releasing memory does not guarantee better performance.

Click the resting whale to ask about computers and software directly. Its “＋” button accepts an image from a file or the clipboard. The window-attached input stays compact; tools remain available through the whale's right-click menu or the system tray.

## Getting started

1. **Install**: download the Windows x64 installer above, read the agreement, and accept it yourself. An upgrade exits the installed copy before replacing it, then launches CetaFae. Installation uses your current-user directory and needs neither administrator access nor a separate .NET runtime.
2. **Connect a model service**: right-click the whale → **鲸汐设置 → 连接** (Settings → Connection), then enter the service address and model. Window understanding requires image input support. “检测图片识别” sends a generated test image to check the connection and may consume model credits.
3. **Ask for help**: click the whale to type, or drag it onto a window. Try “What does this message mean?”, “Where can I change the download folder?”, or “This image is too large to send. What can I do?”

**No model service yet?** This release does not include a ready-to-use AI service. You may need someone familiar with configuration to help. Services requiring a key currently use an environment variable, described in the maintenance section below. Local screenshots, text extraction, and image conversion with an installed component do not depend on an AI response.

The installer is not code-signed, so Windows may show an unknown-publisher warning. Check the download source; release assets include a `SHA256SUMS.txt` checksum file.

### Everyday controls

- **Continue a conversation**: type in the same input without repeating the entire background.
- **Hide desktop chat**: click elsewhere, then click the whale to reopen it. Completed text conversations are encrypted locally and survive restarts; the broom button deletes them.
- **End window help**: use the “×” inside the input bar or drag the whale away. Window guidance is separate from desktop chat.
- **Open settings and tools**: right-click the whale or use its system-tray icon.
- **Fullscreen applications**: the resting whale hides automatically and returns when fullscreen ends.

## Safety

- **Only the selected window**: CetaFae does not capture the entire desktop and crop it afterward, or automatically read other windows or background tabs. Initial understanding may send the complete selected window. A user-selected region is not silently enlarged.
- **Know where content goes**: the installation agreement discloses the content scope and model service. Review or withdraw consent in settings. Changing the recipient, model, or image scope requires confirmation again. Content already sent cannot be recalled from upstream services.
- **Sensitive-content checks have limits**: detected password fields or obvious credentials stop recognition. Checks can miss information; hide anything you do not want to send.
- **Local work and AI requests are separate**: screenshots, text extraction, and image conversion run locally. Asking AI, sending an image, or requesting translation sends the relevant content to your configured service. Screenshots and raw control content are not saved to chat history. Desktop text conversations and file-recovery materials are encrypted for the current Windows user. Metadata logs normally remain for seven days; upstream retention policies have not been verified and should be checked before use.
- **Changes have boundaries**: CetaFae does not freely click, type into other applications, or change system settings. Supported file changes use selection, preview, result checks, and applicable recovery. Not every action is reversible.
- **Use trusted plugins**: the executor limits interfaces and resources; it is not a complete operating-system security sandbox.

## Current limitations

CetaFae is still being refined. AI can misunderstand content; check advice involving important files, payments, deletion, or system settings. Protected windows, permission boundaries, changing interfaces, display scaling, and multiple monitors can affect recognition and highlighting.

Some utilities open separate tool windows. Conversion focuses on single static images, with no universal format support or batch processing. General automatic repair, arbitrary scripts, and full document editing are not available. Broad application compatibility, multi-step tasks, response time, memory use, and third-party plugin security still have acceptance work outstanding. This release does not claim all those checks have passed. See [executed verification](验证记录.md) and the [test plan](Test-Plan.md) for their distinct status.

## Technical implementation

The **C# / WPF / .NET 8** Windows host owns interaction, selected-window collection, consent, model transport, result validation, and storage. **TypeScript plugins** implement feature policies. First-party and third-party features share the same contract.

Understanding is screenshot-first, with optional controls and local text recognition supporting location evidence. Capture is bound to the selected window and does not inject code into the target application. The model selects from existing location candidates; unreliable coordinates are not invented. Some capture paths may show Windows' own capture border.

Image conversion uses a local [Filee](https://github.com/KnifeLemon/Filee) component. A fixed version can be downloaded when needed or imported offline. Sources, licenses, and uses of third-party components are documented in [Third-party notices](THIRD-PARTY-NOTICES.md).

<details>
<summary>Maintenance and building from source</summary>

Use Windows, the .NET 8 SDK, and a Node.js development environment. Node.js builds the plugins; end users do not need it.

```powershell
npm ci
dotnet build src/CetaFae.Checks/CetaFae.Checks.csproj -c Debug
dotnet run --project src/CetaFae.Checks -c Debug --no-build
dotnet run --project src/CetaFae.Probe -c Release -- --guide
```

The final command opens the application UI. The development defaults are `http://127.0.0.1:8787/v1` and `deepseek-v4.1-flash`; no such service is bundled. Save your connection in settings. Remote services require HTTPS; loopback services may use HTTP. Optional authentication comes from the process environment variable `CETAFAE_API_KEY`; settings do not store keys. Never commit credentials.

Building a self-contained installer requires Inno Setup 6.7.4 and its Simplified Chinese language file:

```powershell
./installer/Build-Installer.ps1 -Compiler 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe' -ChineseMessagesFile 'C:/Program Files (x86)/Inno Setup 6/Languages/ChineseSimplified.isl'
```

Output goes to `.artifacts/installer/`. Main directories: `src/CetaFae.Probe` (host), `src/CetaFae.Plugins` (runtime), `plugins` (features), `plugin-sdk` (TS contract), and `src/CetaFae.Checks` (executable checks).

The [requirements](需求文档.md) define scope; the [technical specification](Tech-Spec.md) describes the design. Build output, screenshots, and diagnostic artifacts belong in the ignored `.artifacts/` directory.

</details>

## Roadmap

- Improve general window understanding, error troubleshooting, and multi-step guidance around real tasks.
- Complete common-application, high-DPI, and multi-monitor verification; improve waiting, recovery, and resource use.
- Simplify model-service setup and distribution for people unfamiliar with configuration.
- Complete end-to-end screenshot, text, image-conversion, and memory-help workflows.
- Extend scoped local actions with verifiable results and suitable recovery.
- Stabilize plugin interfaces and complete security review; later support themes and effects under a shared declarative contract.

## Third-party plugin interface

Authors can use TypeScript for features or JSON for adaptation rules. Import through **鲸汐设置 → 功能 → 导入插件文件** (Settings → Features → Import plugin), review the declared scope, then enable and save. The API is under development and does not expose arbitrary Node/npm modules or direct computer control outside the host.

Start with the [Plugin API](Plugin-API.md), [adaptation example](examples/plugins/example.editor.json), and [TypeScript feature example](examples/plugins/example.chat/plugin.ts). There is currently no plugin marketplace or general silent updating.

## Feedback and licensing

Report problems through [Issues](https://github.com/easerlee/CetaFae/issues), including the CetaFae version, application name, steps, and actual message. Remove personal information from screenshots; do not upload passwords, keys, or private files.

The source is publicly visible, but the project has not selected an open-source license. Third-party components retain their own licenses; see [Third-party notices](THIRD-PARTY-NOTICES.md).
