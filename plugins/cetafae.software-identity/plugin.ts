function handle(request: PluginRequest): PluginResult {
  if (request.operation !== "identity-query") throw new Error("plugin_operation");
  const product = request.input.product;
  return { query: product && product.length >= 2 && product.length <= 80 &&
    /^[\p{L}\p{N} .()\-]+$/u.test(product) && /\p{L}/u.test(product) ? product + " software" : null };
}
