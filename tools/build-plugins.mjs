import fs from "node:fs";
import path from "node:path";
import ts from "typescript";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const out = path.join(root, ".artifacts", "plugin-build");
function build(manifestPath, outputPath, bundled = false) {
  const dir = path.dirname(manifestPath);
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  const id = manifest.id;
  const files = ["plugin-sdk/contracts.d.ts", "plugin-sdk/runtime.ts"];
  if (bundled) {
    files.push("plugins/shared/common.ts");
    if (manifest.kind !== "identity" && manifest.kind !== "chat") files.push("plugins/shared/visual.ts");
  }
  files.push(path.join(dir, "plugin.ts"));
  const options = { strict: true, target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.None,
    types: [], lib: ["lib.es2020.d.ts"], noEmitOnError: true, newLine: ts.NewLineKind.LineFeed };
  const program = ts.createProgram(files.map(f => path.resolve(root, f)), options);
  const diagnostics = ts.getPreEmitDiagnostics(program);
  if (diagnostics.length) throw new Error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
    getCanonicalFileName: f => f, getCurrentDirectory: () => root, getNewLine: () => "\n"
  }));
  const source = files.filter(f => !f.endsWith(".d.ts")).map(f =>
    ts.transpileModule(fs.readFileSync(path.resolve(root, f), "utf8"), { compilerOptions: { ...options, removeComments: true } }).outputText).join("\n");
  if (Buffer.byteLength(source) > 24 * 1024) throw new Error(`Program limit: ${id}`);
  manifest.program = { apiVersion: 1, source };
  const json = JSON.stringify(manifest, null, 2);
  if (Buffer.byteLength(json) > 32 * 1024) throw new Error(`Package limit: ${id}`);
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, json + "\n");
  console.log(`Built ${id} (${Buffer.byteLength(json)} bytes)`);
}
const args = process.argv.slice(2);
if (args.length === 4 && args[0] === "--plugin" && args[2] === "--out") {
  build(path.resolve(args[1]), path.resolve(args[3]));
} else if (args.length === 0 || args.length === 1 && args[0] === "--check") {
  fs.mkdirSync(out, { recursive: true });
  const catalog = JSON.parse(fs.readFileSync(path.join(root, "plugins/catalog.json"), "utf8"));
  for (const id of catalog.enabled) build(path.join(root, "plugins", id, "plugin.json"), path.join(out, id, "plugin.json"), true);
  build(path.join(root, "examples/plugins/example.chat/plugin.json"), path.join(out, "example.chat.json"));
  fs.copyFileSync(path.join(root, "plugins/catalog.json"), path.join(out, "catalog.json"));
} else throw new Error("Usage: node tools/build-plugins.mjs [--plugin manifest.json --out package.json]");
