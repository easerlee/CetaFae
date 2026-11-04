# Declarative plugin API

This is the development contract for local data plugins in CetaFae 0.3.1-preview.24. It is not an executable SDK or a claim that every application is supported. First-party packages and imported packages use the same `GuidePlugin` parser and runtime. The host owns consent, target selection, capture, privacy checks, transport, output validation and lifecycle.

## Install, update and remove

1. Obtain a trusted author's UTF-8 `.json` manifest. The supplied [example](examples/plugins/example.editor.json) matches a generated test application, not a real editor.
2. Open the whale's context menu → 鲸汐设置 → 功能 → 导入插件文件. Review its name, source, version and matching scope. Names are self-reported, not verified publisher identities.
3. Imported or updated packages start disabled. Check the package and click 保存 to activate it. Imported data and enablement are saved together. Closing settings without saving discards changes.
4. A custom same-ID update needs a strictly higher numeric version, such as `0.2.0` after `0.1.0`; it is disabled again for review. Bundled IDs cannot be overwritten. Disable a bundled adapter before enabling an overlapping replacement with another ID.
5. Use 移除插件, then 保存 to remove a custom package. Bundled packages can be disabled but not removed from this page. To discard an unsuccessful draft, close settings without saving.

Installed manifests are copied into the current user's non-secret settings, not loaded from the original file on every request. Moving or deleting that source file does not uninstall a saved plugin. Program upgrades do not replace this user inventory. There is no automatic download/update, archive extraction, dependency installation, DLL/script loading, publisher signature verification or marketplace. Import does not read a target window or call a model. A normal filename, including `plugin.json`, is sufficient; no special extension is needed.

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

For general schema5/6, `interpretationPolicy` contains `languages` (one or two unique values from `zh-CN`, `en`), `translateSelectedText` (boolean), and optional booleans `explainVisibleErrors`, `userLedFollowup`, `locateTextRegions`. Only schema6 may enable `locateTextRegions`. These select existing host mechanisms; they do not add tools, clicks, keystrokes, scripts, file access or permission. See the bundled [general](plugins/cetafae.visual-guide/plugin.json), [identity](plugins/cetafae.software-identity/plugin.json) and [chat](plugins/cetafae.chat/plugin.json) manifests for complete examples. A package cannot install a new implementation of a capability through JSON.

## Trust and limits

- Input is a single UTF-8 JSON file, maximum32KiB. Canonical serialized definitions must also fit this limit. Unknown/duplicate fields, unsupported schemas, null/invalid values, control characters, recipient/command/permission fields, UNC/device paths, alternate stream names and linked path ancestors are rejected.
- The combined installed inventory is at most32 packages, including disabled packages and bundled ones. Settings are bounded to1MiB and atomically replaced; old v1 settings without custom packages remain readable. Invalid saved configuration is preserved for recovery and does not enable cloud collection. An invalid imported file does not change the draft.
- Display names, task labels and screen/model text are untrusted. A plugin cannot accept the installation agreement, expand a selection, change the model receiver, extract credentials, disable privacy checks or authorize automatic operations.
- New application support should normally add data here, not software-name branches to the host. If a new mechanism is necessary, propose a contract change with acceptance checks first. JSON cannot express arbitrary algorithms, tool calls or custom model system prompts.
- Scope/contract checks do not prove the adapter points to the correct real control. Validate each supported application/version/language and keep accurate coverage claims. Do not use real passwords, keys or private documents in contribution fixtures.

## Author verification

From the project root on Windows:

```powershell
dotnet run --project src/CetaFae.Checks -c Debug
dotnet run --project src/CetaFae.Checks -c Release
dotnet run --project src/CetaFae.Checks -c Release -- --render-settings
```

These use generated fixtures and controlled transport; they do not run a model or observe real application windows. The last command renders the production settings visual offscreen under `.artifacts/checks`. The included example is copied to the check output and imported by the checks, exercising author JSON → bounded reader → draft → atomic persistence → enabled resolver, update, conflict and removal. Actual independent-author and native target acceptance are separate gates, as are direct-provider semantics and performance. Changes to this development contract must update this document, the requirements and tests together.
