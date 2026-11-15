/** Versioned JSON-only feature boundary. No Node, DOM, CLR or host objects are available. */
type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };
interface InterpretationPolicy {
  languages: readonly string[];
  translateSelectedText: boolean;
  explainVisibleErrors: boolean;
  userLedFollowup: boolean;
  locateTextRegions: boolean;
}
interface Message { role: "system" | "user" | "assistant"; content: string }
interface Identification {
  application: string | null; page: string | null; question: string | null;
  kind: string; state: string | null; nextCheck: string | null;
}
interface PluginInput {
  memory?: MemoryPolicyInput;
  memoryFacts?: { percent: number; availableMB: number; growing: number; cpuPercent: number };
  imageAttached?: boolean;
  taskAvailable?: boolean;
  conversionAvailable?: boolean;
  utilityAvailable?: boolean;
  conversion?: { sourceFormat: string; purpose: string | null; format: string | null };
  compression?: { sourceFormat: string; maxBytes: number };
  taskEvidence?: JsonValue;
  find?: string;
  replacement?: string;
  capabilityIntent?: string;
  selectedFile?: string;
  identifyWindow?: boolean;
  interpretationPolicy?: InterpretationPolicy | null;
  context?: JsonValue;
  history?: readonly { question: string; reply: string }[];
  question?: string;
  allowWindow?: boolean;
  windowGuidanceAvailable?: boolean;
  activeGoal?: string | null;
  previousUnderstanding?: JsonValue;
  identification?: Identification;
  product?: string | null;
  problem?: string;
  next?: string;
  source?: string;
  translation?: string;
  language?: string;
  followup?: JsonValue;
}
interface PluginRequest { apiVersion: 1; operation: string; input: PluginInput }
type PluginResult = { messages: Message[] } | { mode: string; explanation: string; candidateId: null }
  | { text: string } | { query: string | null } | { requests: PlatformRequest[] }
  | { conversion: null | { format: string; reason: string; recipe: string | null } }
  | { actions: { id: string; label: string }[] } | { action: string | null }
  | { memory: MemoryPolicyState; remind: boolean };

interface MemoryPolicyState {
  highSince: number | null; lowSince: number | null; lastSample: number | null;
  lastNotice: number | null; armed: boolean;
}
interface MemoryPolicyInput {
  now: number; usedPercent: number; suppressed: boolean; ignoredUntil: number;
  config: { enabled: boolean; high: number; low: number; sustainedSeconds: number; cooldownMinutes: number };
  state: MemoryPolicyState;
}
/** Proposal only. The host supplies a separate expiring resource grant and verifies the operation. */
interface PlatformRequest { apiVersion: 1; id: string; operation: string; parameters: { [key: string]: JsonValue } }
declare function handle(request: PluginRequest): PluginResult;

/** The host chooses actual resources, admission and postconditions; no path or grant is accepted here. */
interface TaskProposal {
  operation: "system.info" | "storage.list" | "directory.scan" | "file.info" | "file.readText" |
    "file.write" | "file.copy" | "file.move" | "file.delete" | "text.replace" | "file.convert" | "file.compress" | "utility.run";
  text?: string;
  find?: string;
  replacement?: string;
  purpose?: "compatible" | "transparent" | "web" | "print";
  format?: string;
  maxBytes?: number;
  action?: "image.capture" | "text.extract" | "text.translate" | "text.paste" | "memory.inspect";
}
