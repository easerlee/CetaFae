const supportedSources = ["jpg", "png", "webp", "heic", "heif", "bmp"];

function handle(request: PluginRequest): PluginResult {
  if (request.operation === "compression-plan" && request.input.compression) {
    const c = request.input.compression;
    return { action: ["jpg", "jpeg", "png", "bmp"].includes(c.sourceFormat) && c.maxBytes >= 65536 && c.maxBytes <= 4194304
      ? "image.jpeg.bounded" : null };
  }
  if (request.operation !== "conversion-plan" || !request.input.conversion) throw new Error("plugin_operation");
  const input = request.input.conversion;
  const source = input.sourceFormat === "jpeg" ? "jpg" : input.sourceFormat;
  if (!supportedSources.includes(source)) return { conversion: null };

  const formats = { compatible: "jpg", transparent: "png", web: "webp", print: "pdf" } as const;
  const reasons = {
    compatible: "JPG 在手机、电脑和常见网站上更容易打开。",
    transparent: "PNG 可以保留原图已有的透明背景。",
    web: "WebP 适合网页使用，仍需网站支持这个格式。",
    print: "PDF 方便按页面预览和打印这张图。",
  };
  const purpose = input.purpose as keyof typeof formats | null;
  const requested = input.format === "jpeg" ? "jpg" : input.format;
  const format = requested ?? (purpose && formats[purpose]);
  if (!format || !["jpg", "png", "webp", "pdf"].includes(format)) return { conversion: null };
  if (format === source) return { conversion: { format, recipe: null, reason: "所选文件已经使用这个格式。" } };
  const reason = requested ? "按你指定的格式生成一份副本。" : reasons[purpose!];
  const transparency = format === "jpg" || format === "pdf" ? " 若原图带透明背景，副本不会保留透明。" : "";
  return { conversion: { format, recipe: "image.filee.win.x64.2.4.0", reason: reason + transparency } };
}
