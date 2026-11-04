function invoke(json: string): string {
  const request: unknown = JSON.parse(json);
  if (request === null || typeof request !== "object" || !("apiVersion" in request) || request.apiVersion !== 1 ||
      !("operation" in request) || typeof request.operation !== "string" || !("input" in request) ||
      request.input === null || typeof request.input !== "object") throw new Error("plugin_request");
  return JSON.stringify(handle(request as PluginRequest));
}
