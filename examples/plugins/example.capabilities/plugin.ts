/** Independently compiled example. Selecting a path supplies context, never file authority. */
function handle(request: PluginRequest): PluginResult {
  if (request.operation !== "capability-plan" || request.input.capabilityIntent !== "inspect-file" || !request.input.selectedFile)
    throw new Error("plugin_operation");
  return { requests: [{ apiVersion: 1, id: "inspect-selected-file", operation: "file.info",
    parameters: { path: request.input.selectedFile } }] };
}
