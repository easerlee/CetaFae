# TypeScript feature and adapter API

This is the development contract for CetaFae 0.3.1-preview.25. First-party and imported features share the same manifest parser, JSON invocation, restricted ECMAScript interpreter and host validation. It does not imply support for every application or arbitrary Node/npm packages. The C# host owns consent, target selection, capture/accessibility/OCR, credentials, transport, persistence, evidence validation and lifecycle. TypeScript features own request construction, software-help policy and presentation; application adapters also declare matching and local scope rules.

## Install, update and remove

1. Obtain a trusted author's UTF-8 `.json` manifest. The supplied [example](examples/plugins/example.editor.json) matches a generated test application, not a real editor.
2. Open the whale's context menu → 鲸汐设置 → 功能 → 导入插件文件. Review its name, source, version and matching scope. Names are self-reported, not verified publisher identities.
3. Imported or updated packages start disabled. Check the package and click 保存 to activate it. Imported data and enablement are saved together. Closing settings without saving discards changes.
4. A custom same-ID update needs a strictly higher numeric version, such as `0.2.0` after `0.1.0`; it is disabled again for review. Bundled IDs cannot be overwritten. Disable a bundled adapter before enabling an overlapping replacement with another ID.
5. Use 移除插件, then 保存 to remove a custom package. Bundled packages can be disabled but not removed from this page. To discard an unsuccessful draft, close settings without saving.

Installed manifests are copied into the current user's non-secret settings, not loaded from the original file on every request. Moving or deleting that source file does not uninstall a saved plugin. Program upgrades do not replace this user inventory. No automatic download/update, archive extraction, dependency installation, native DLL loading, publisher signature verification or marketplace is provided. Import does not execute the program, read a target window or call a model. A normal filename, including `plugin.json`, is sufficient; no special extension is needed. Programs are identified in the capability list and execute only when the enabled feature is invoked.

## Write and export a TypeScript feature

Install Node.js and run `npm ci` once for development. End users need neither Node nor TypeScript. Start with [example.chat](examples/plugins/example.chat/plugin.ts) and its adjacent manifest, using the types in [contracts.d.ts](plugin-sdk/contracts.d.ts):

```typescript
function handle(request: PluginRequest): PluginResult {
  if (request.operation !== "chat") throw new Error("plugin_operation");
  return { messages: [
    { role: "system", content: "Answer software questions in Chinese. Return only a JSON object with reply. Do not claim to see a screen or perform actions." },
    { role: "user", content: request.input.question ?? "" }
  ] };
}
```

```powershell
node tools/build-plugins.mjs --plugin examples/plugins/example.chat/plugin.json --out .artifacts/example.chat.json
```

The compiler checks strict TypeScript and exports one importable JSON file. It includes the small SDK `invoke` wrapper and the adjacent `plugin.ts`, compiled to ES2020. Use self-contained functions; there is no module loader, npm dependency resolver, DOM, filesystem, timers or network API inside a feature. Bundled implementations reuse source under `plugins/shared`; that is ordinary first-party code, not a privileged host service. `npm run check` builds/typechecks all bundled programs and the example. A .NET build runs the same compiler before copying the compiled packages, including on a clean checkout.

Exported manifests add optional `program: { "apiVersion": 1, "source": "compiled JavaScript" }`. Program source is bounded to24KiB UTF-8; the entire JSON package, including escaped program text, must fit32KiB. A program must expose `invoke(json: string): string`. Its input is `{apiVersion:1, operation, input}` and its result must be a JSON object encoded as a string. The SDK implements this wrapper around `handle`. Both process pipes use explicit UTF-8, independent of the Windows console code page. Never put credentials or private fixture data in source or manifests.

| Operation | Input | Result and host boundary |
|---|---|---|
| `chat` | `question`, recent `history` pairs, `allowWindow`, `windowGuidanceAvailable`, optional `activeGoal` / historical `previousUnderstanding` | `messages`:2–14 text messages; system first and only once, user last. Host supplies model/receiver and validates the reply/null routing. |
| `window` | `identifyWindow`, optional `interpretationPolicy`, `context` with task, visible context, previous understanding and bounded candidate IDs/types/geometry | Exactly two system/user text messages. No image bytes are exposed to script; the host appends the original authorized image. Model-selected markers still require real in-scope candidate IDs. |
| `identify-result` | Validated `identification` (`application`, `page`, `state`, `kind`, `question`, `nextCheck`) and an optional cleaned local `product` | `{mode:"ask" or "explain", explanation, candidateId:null}`. Maximum1000 characters; host privacy/shape checks remain mandatory. |
| `error-result` | Validated `problem`, `next` | Same presentation object, no marker or automatic action. |
| `translation-result` | Validated `source`, `translation`, `language` | Same presentation object. Allowed-language and literal-preservation checks run in the host before formatting. |
| `followup` | `followup`: originalUserGoal, currentUserRequest, step, userClarification, recentSteps, initialAssistantSuggestion | `{text:string}`,1–8000 characters. Historical steps retain `Request` / `Explanation` keys. Host owns target binding, step budget, fresh evidence and cancellation. |
| `identity-query` | Only the cleaned `product` label or null | `{query:string or null}`. Host permits only the same qualified product plus ` software`, then requires the existing separate disclosure before a fixed-receiver public query. No automatic public query is added. |

System/chat message text is limited to24000 characters each. An aggregated window-context message may contain up to128000 characters; its individual text fields retain the existing32Ki-character privacy bound and checks. Aggregate invocation input/output limits are256KiB each in UTF-8. Scope and candidate evidence remain host-owned. A feature cannot return an endpoint, tool, image, arbitrary coordinate, grant or executable action. Missing/disabled providers and failing programs produce a feature error, never a hidden bundled fallback. Data-only application adapters reuse the explicitly enabled general program; a chat/general/identity replacement needs its own program to implement that capability.

Invocations are serialized per host, and programs execute in a fresh hidden child with a4-second parent deadline and5-second watchdog. Jint4.17.0 adds a1-second execution timeout,100000-statement limit,16MiB cooperative allocation budget,64 recursion limit,100000 array limit and100ms regex timeout. A Windows Job Object caps child process memory at256MiB and active processes at one; inability to apply it rejects execution. Parent cancellation kills and reaps its owned child before returning. No CLR bridge, host callback, module loader or dynamic string compilation is enabled. The child inherits only explicit runtime environment entries, never model credentials.

This is fault/resource containment, **not an operating-system security sandbox**. Interpreter vulnerabilities and hostile built-in resource usage need independent security review before unrestricted public distribution. Constraints are defense in depth; see the [Jint threat model](https://github.com/sebastienros/jint/blob/main/.github/THREAT_MODEL.md). Versions and APIs must be checked against the pinned package; the linked upstream main branch may describe newer APIs.

## Application manifest

```json
{
  "schemaVersion": 2,
  "id": "example.editor",
  "version": "0.1.0",
  "displayName": "Example editor guidance",
  "targets": [{ "processNames": ["generated-editor"], "windowTitles": ["Generated editor fixture"] }],
  "uiaView": "control",
  "taskIds": ["example.destination"],
  "blockedPoints": [],
  "observationScopes": [{
    "intentContainsAny": ["destination"],
    "anchorNameContainsAny": ["Destination"],
    "anchorControlTypes": ["ControlType.Edit"],
    "padding": 12
  }]
}
```

| Field | Contract |
|---|---|
| `schemaVersion` | Application packages: `1` without scope rules, or `2` with one to sixteen rules. Application schema3 remains accepted as a legacy form with the same scope constraints; use `2` for a new scoped adapter. |
| `kind` | Optional for applications; defaults to `application`. Other kinds are described below. |
| `id` | Unique inventory ID, at most64 ASCII letters, digits, `.`, `_`, `-`; `.` and `..` alone are invalid. Collisions are case-insensitive. Do not reuse a bundled ID. |
| `version` | Numeric .NET `System.Version` syntax, two to four components. Prerelease suffixes are unsupported for package versions. |
| `displayName` | One nonempty line, at most200 characters. Self-reported, not trusted publisher evidence. |
| `targets` | One to sixteen target objects, each with one to sixteen process names and optionally up to sixteen exact window titles. Process names exclude `.exe` and match case-insensitively. Titles match exactly and case-sensitively; omit or use `[]` for any title. No globs or regex. Process names use token characters, maximum128; titles maximum200 characters. |
| `uiaView` | `control` or `raw`, selecting an existing optional accessibility traversal. It cannot bypass capture/privacy checks or grant background access. |
| `taskIds` | One to32 unique token strings, maximum64 characters each. These document package task coverage; they are not code callbacks or arbitrary prompt templates. |
| `blockedPoints` | Zero to32 objects containing nonempty `intentContainsAny` and `candidateNameContainsAny` string arrays. If both sides match, the host rejects that marker. |
| `observationScopes` | Schema2: one to sixteen objects. Rules describe a reliable local scope, not permission to enlarge a user selection. Schema1 does not accept these rules. |

Each matching array uses literal, case-insensitive substring checks unless specified otherwise. `blockedPoints` arrays contain at most16 unique nonempty strings of at most200 characters. A scope has `intentContainsAny` and `anchorNameContainsAny` with the same bounds, `anchorControlTypes` containing one to eight distinct supported types, and integer `padding` from0 to64 physical pixels. Supported types: `ControlType.Text`, `Button`, `CheckBox`, `ComboBox`, `RadioButton`, `Hyperlink`, `Edit` (each with the `ControlType.` prefix). Ambiguous rules or missing/ambiguous anchors do not justify invented geometry or a broader fallback. The existing user selection/explanation recovery remains available.

Application matching requires a process match and, when supplied, a title match in the same target object. Different target objects are alternatives. Two enabled application packages conflict if any target pair shares a process and their title sets intersect or either title set is unrestricted. Disjoint exact titles may coexist. The settings save rejects conflicts before activation; it never chooses the first package by order. The runtime still rejects ambiguity defensively.

## Shared capability packages

Imported capabilities can replace bundled capabilities only after the existing provider is disabled. There may be at most one enabled provider of each kind. This is the same rule for first-party and community definitions; there is no special fallback that ignores a user's disabled state.

| Kind | Supported schema and fields |
|---|---|
| `general` | Schema3 basic vision; schema5 or6 with nonnull `interpretationPolicy`. Empty `targets`/`blockedPoints`, no application scope rules. `uiaView` may be `control` or `raw`; nonempty `taskIds`. |
| `identity` | Schema4, `identityPolicy: "local-metadata-public-reference"`, empty `targets`/`blockedPoints`, `uiaView: "control"`, nonempty `taskIds`. This opts into an existing constrained mechanism, not arbitrary network endpoints or proof of software safety. |
| `chat` | Schema7, empty `targets`/`taskIds`/`blockedPoints`, `uiaView: "control"`. No `identityPolicy` or `interpretationPolicy`. |

For general schema5/6, `interpretationPolicy` contains `languages` (one or two unique values from `zh-CN`, `en`), `translateSelectedText` (boolean), and optional booleans `explainVisibleErrors`, `userLedFollowup`, `locateTextRegions`. Only schema6 may enable `locateTextRegions`. These select existing host mechanisms; they do not add tools, clicks, keystrokes, file access or permission. See the bundled [general](plugins/cetafae.visual-guide/plugin.json), [identity](plugins/cetafae.software-identity/plugin.json) and [chat](plugins/cetafae.chat/plugin.json) source manifests; build them to obtain the compiled program fields.

## Trust and limits

- Input is a single UTF-8 JSON file, maximum32KiB. Canonical serialized definitions must also fit this limit. Unknown/duplicate fields, unsupported schemas, null/invalid values, control characters, recipient/command/permission fields, UNC/device paths, alternate stream names and linked path ancestors are rejected.
- The combined installed inventory is at most32 packages, including disabled packages and bundled ones. Settings are bounded to1MiB and atomically replaced; old v1 settings without custom packages remain readable. Invalid saved configuration is preserved for recovery and does not enable cloud collection. An invalid imported file does not change the draft.
- Display names, task labels and screen/model text are untrusted. A plugin cannot accept the installation agreement, expand a selection, change the model receiver, extract credentials, disable privacy checks or authorize automatic operations.
- Add application matching and scope rules as plugin data, and feature algorithms/prompts as TS programs. Do not add software-name branches to the host. New native mechanisms or authority require an explicit contract change and acceptance checks.
- Scope/contract checks do not prove the adapter points to the correct real control. Validate each supported application/version/language and keep accurate coverage claims. Do not use real passwords, keys or private documents in contribution fixtures.

## Theme and effects extension rules (design boundary, not implemented)

Feature programs do not receive brushes, windows, frame clocks, visual trees or drawing callbacks. The current importer accepts feature/adapter manifests only. There is no supported `kind: theme`, theme-file import or external rendering API in this build; unknown kinds and fields continue to be rejected. The following rules guide the next theme contract rather than claiming an available loader:

| Layer | Responsibility |
|---|---|
| C# presentation host | Layout, DPI/monitor coordinates, input hit testing, window clipping, cancellation, accessibility, animation scheduling and resource budgets. |
| TS feature | Validated answers and guidance intent. No global style mutation, arbitrary UI tree, drawing or direct animation control. |
| Declarative theme | Reviewed palette, bounded decorative dimensions/timing, local image assets and mappings from host-owned semantic states to allowed visual presets. |

1. A versioned theme contract will expose semantic slots, not internal WPF names: whale, input shell, reading card, target frame, location hint, waiting indicator and scan. Preserve a common palette/shape system across slots. Host-derived states include idle, dragging, attaching, reading, awaiting input, answer ready, attention, unavailable and detaching; decoration cannot change the meaning or completion of a state.
2. Themes use data and vetted local assets. No executable render script, XAML, native library, remote URI, arbitrary font or shader. Normalize and contain asset references, reject links/traversal and unsupported formats, and bound decoded dimensions, frames and memory before activation. Do not repurpose the current32KiB feature manifest limit for unbounded embedded images.
3. Color, corner radius, edge depth, opacity and motion settings must be finite and validated against host limits. Maintain readable contrast, visible focus/actions and non-obstructive content. Titles and frames share tokens. Target geometry, click regions, body font minimums, card fit/overflow and DPI math remain host-owned; a theme cannot replace them with arbitrary coordinates or relative screen scaling.
4. Render only host-approved, target-clipped effects. Reduce motion on request; stop clocks when hidden, minimized, full-screen-suppressed or closed. A decorative transition never delays cancellation, holds a binding or restores a stale answer. Validate frame rate, finite transition length, idle frequency and pixel/decode budgets on small and maximized windows before freezing numeric limits.
5. First-party visuals will use the same public theme contract. Preview/review and atomic activation must preserve the previous theme on failure; restoring the bundled default is an explicit user choice, not a hidden fallback that re-enables a disabled feature. Theme changes grant no collection, model or desktop authority.

Theme implementation acceptance requires an independently authored package, malformed/resource-heavy assets, rollback, reduced motion, native interaction, clipping, multiple DPI scales and performance checks. Current built-in `GuideTheme`, frame renderer and whale timelines remain the implementation reference; their presence does not mean theme loading has already been delivered.

## Author verification commands

From the project root on Windows:

```powershell
npm ci
node tools/build-plugins.mjs --plugin examples/plugins/example.chat/plugin.json --out .artifacts/example.chat.json
dotnet run --project src/CetaFae.Checks -c Debug -- --plugin-programs
dotnet run --project src/CetaFae.Checks -c Debug
dotnet run --project src/CetaFae.Checks -c Release
dotnet run --project src/CetaFae.Checks -c Release -- --render-settings
```

These use generated fixtures and controlled transport; they do not run a model or observe real application windows. The last command renders the production settings visual offscreen under `.artifacts/checks`. The included example is copied to the check output and imported by the checks, exercising author JSON → bounded reader → draft → atomic persistence → enabled resolver, update, conflict and removal. Actual independent-author and native target acceptance are separate gates, as are direct-provider semantics and performance. Changes to this development contract must update this document, the requirements and tests together.
