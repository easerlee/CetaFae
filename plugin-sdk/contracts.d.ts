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
  | { text: string } | { query: string | null } | { requests: PlatformRequest[] };
/** Proposal only. The host supplies a separate expiring resource grant and verifies the operation. */
interface PlatformRequest { apiVersion: 1; id: string; operation: string; parameters: { [key: string]: JsonValue } }
declare function handle(request: PluginRequest): PluginResult;
