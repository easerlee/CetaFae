using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class ChatImageChecks
{
    internal static async Task RunAsync(Action<bool, string> check, bool render = false)
    {
        var folder = Path.GetFullPath(".artifacts/checks");
        Directory.CreateDirectory(folder);
        var image = ChatImage.FromBitmap(Fixture(100, 70));
        var clearFile = Path.Combine(folder, "chat-image-fixture.jpg");
        var invalidFile = Path.Combine(folder, "chat-image-invalid.png");
        var largeFile = Path.Combine(folder, "chat-image-large.bmp");
        try
        {
            var metadata = new BitmapMetadata("jpg");
            metadata.Comment = "generated source metadata";
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Fixture(2600, 1300), null, metadata, null));
            using (var file = File.Create(clearFile)) encoder.Save(file);
            var normalized = Decode(ChatImage.FromFile(clearFile));
            check(normalized.PixelWidth == 1024 && normalized.PixelHeight == 2048 &&
                (normalized.Metadata as BitmapMetadata)?.Comment is null,
                "selected JPEG is proportionally resized, oriented and cleared of source metadata");
            File.WriteAllText(invalidFile, "not an image");
            Reject(() => ChatImage.FromFile(invalidFile), "chat_image_format");
            using (var file = File.Create(largeFile)) file.SetLength(ChatImage.MaxFileBytes + 1);
            Reject(() => ChatImage.FromFile(largeFile), "chat_image_size");
            check(Decode(image).PixelWidth == 100 && image.Preview.PixelWidth == 64,
                "generated pasted bitmap preserves pixels and has a separate bounded thumbnail");
            var transparent = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 4, 8, 12, 0 }, 4);
            var flattened = new FormatConvertedBitmap(Decode(ChatImage.FromBitmap(transparent)), PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            flattened.CopyPixels(pixel, 4, 0);
            check(pixel.All(value => value == 255), "transparent pixels are flattened without exposing hidden colors");
        }
        finally
        {
            File.Delete(clearFile);
            File.Delete(invalidFile);
            File.Delete(largeFile);
        }

        var home = new FreeChatWindow();
        var results = new List<RenderTargetBitmap>();
        try
        {
            var anchor = new Rect(600, 480, 88, 88);
            var work = new Rect(0, 0, 720, 600);
            home.Place(anchor, work);
            var bottom = home.Top + home.Height;
            home.Attachment.SetImage(image);
            home.Place(anchor, work);
            check(home.Send.IsEnabled && Math.Abs(bottom - home.Top - home.Height) < .01 &&
                home.Height > FreeChatWindow.InputHeight + FreeChatWindow.FramePadding * 2,
                "resting image-only send is enabled and the preview grows upward");
            var body = (FrameworkElement)home.Content;
            Render(body, home.Width, home.Height);
            CheckComposer(body, home.Input, home.Send, home.Attachment);
            string? question = null;
            ChatImage? submitted = null;
            home.ImageSubmitted += (text, selected) => (question, submitted) = (text, selected);
            home.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(question == ChatAttachment.DefaultQuestion && ReferenceEquals(submitted, image),
                "resting send passes the selected image with the default question");
            home.SetSendingAllowed(false);
            check(!home.Send.IsEnabled && !home.Attachment.Add.IsEnabled && home.Attachment.Image is null,
                "withdrawal disables the image entry and removes the draft");
        }
        finally { home.Close(); }

        foreach (var top in new[] { false, true })
        {
            var prompt = new WhalePrompt();
            prompt.SetTopAttached(top);
            prompt.SetQuestion(true);
            prompt.Attachment.SetImage(image);
            Render(prompt, 240, WhalePrompt.HeightDip);
            CheckComposer(prompt, prompt.Answer, prompt.Send, prompt.Attachment);
            var preview = Bounds(prompt.Attachment.Preview, prompt);
            var row = Bounds(prompt.DragSurface, prompt);
            check(prompt.Attachment.Add.Visibility == Visibility.Collapsed &&
                (top ? row.Bottom <= preview.Top : preview.Bottom <= row.Top),
                "attached composer has no add button and its preview fits both dock orientations");
            var remove = (Button)((DockPanel)prompt.Attachment.Preview.Child).Children[0];
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!prompt.Send.IsEnabled && prompt.Attachment.Preview.Visibility == Visibility.Collapsed,
                "removing the only attachment returns to an empty composer");
            prompt.Attachment.SetImage(image);
            prompt.Answer.Text = "这是什么提示？";
            string? question = null;
            ChatImage? submitted = null;
            prompt.ImageSubmitted += (text, selected) => (question, submitted) = (text, selected);
            prompt.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(question == "这是什么提示？" && ReferenceEquals(submitted, image) && prompt.Attachment.Image is null,
                "bound send snapshots its own text and image before clearing the draft");
        }
        if (render)
        {
            var board = new DrawingVisual();
            using (var draw = board.RenderOpen())
            {
                draw.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 280, 296));
                for (var i = 0; i < results.Count; i++)
                    draw.DrawImage(results[i], new Rect(20, 20 + i * 92, results[i].Width, results[i].Height));
            }
            var bitmap = new RenderTargetBitmap(420, 444, 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(board);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, "chat-image-composers.png"));
            encoder.Save(output);
        }

        var receiver = new ChatReceiver();
        var options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated-chat-image");
        using var model = new GuideModel(options, receiver);
        var session = new FreeChatSession(async (history, question, token) => await model.ChatAsync(history, question, token));
        var turn = await session.SubmitAsync("这张图是什么意思？", respond: async (history, question, token) =>
            (await model.ConverseAsync(history, question, allowWindow: true, token, taskAvailable: true, image: image)).Reply);
        await session.SubmitAsync("接下来呢？");
        using var first = JsonDocument.Parse(receiver.Bodies[0]);
        using var second = JsonDocument.Parse(receiver.Bodies[1]);
        var messages = first.RootElement.GetProperty("messages");
        var content = messages[messages.GetArrayLength() - 1].GetProperty("content");
        check(turn?.Reply == "收到生成图片。" && messages[0].GetProperty("content").GetString()!.Contains("picture they selected") &&
            content[0].GetProperty("text").GetString() == "这张图是什么意思？" &&
            content[1].GetProperty("image_url").GetProperty("url").GetString() == image.DataUrl(),
            "attached-photo prompt and exactly the current chosen image reach the configured multimodal transport");
        check(second.RootElement.GetProperty("messages").EnumerateArray().All(
            message => message.GetProperty("content").ValueKind == JsonValueKind.String),
            "the next turn receives text history only");
        using var disabled = new GuideModel(options with { IncludeImage = false }, receiver);
        await RejectResponse(disabled, "image_disabled");
        check(receiver.Bodies.Count == 2, "disabled image sending issues no HTTP request");
        receiver.Reply = "{\"task\":{\"operation\":\"system.info\"}}";
        await RejectResponse(model, "chat_reply");
        receiver.Reply = "{\"reply\":null}";
        await RejectResponse(model, "chat_reply");

        async Task RejectResponse(GuideModel client, string code)
        {
            var rejected = false;
            try { await client.ConverseAsync([], "测试图片", true, default, taskAvailable: true, image: image); }
            catch (InvalidDataException ex) when (ex.Message == code) { rejected = true; }
            check(rejected, $"photo turn rejects {code} before any window routing or task dispatch");
        }

        void Reject(Action read, string code)
        {
            var rejected = false;
            try { read(); }
            catch (InvalidDataException ex) when (ex.Message == code) { rejected = true; }
            check(rejected, $"unsafe attachment input is refused with {code}");
        }

        void Render(FrameworkElement visual, double width, double height)
        {
            visual.Measure(new Size(width, height));
            visual.Arrange(new Rect(0, 0, width, height));
            visual.UpdateLayout();
            if (!render) return;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 1.5), (int)Math.Ceiling(height * 1.5),
                144, 144, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            results.Add(bitmap);
        }

        void CheckComposer(FrameworkElement visual, FrameworkElement input, Button send, ChatAttachment attachment)
        {
            var area = new Rect(visual.RenderSize);
            var add = Bounds(attachment.Add, visual);
            var editor = Bounds(input, visual);
            check(area.Contains(add) && area.Contains(editor) && area.Contains(Bounds(attachment.Preview, visual)) &&
                add.Right <= editor.Left && editor.Right <= Bounds(send, visual).Left,
                "add, editor, send and preview have distinct bounded areas");
        }
    }

    static Rect Bounds(FrameworkElement part, FrameworkElement root) =>
        part.TransformToAncestor(root).TransformBounds(new Rect(part.RenderSize));

    static BitmapSource Fixture(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var offset = 0; offset < pixels.Length; offset += 3)
        {
            pixels[offset] = 245;
            pixels[offset + 1] = (byte)(120 + offset / (width * 3) % 100);
            pixels[offset + 2] = 30;
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
    }

    static BitmapFrame Decode(ChatImage image)
    {
        var url = image.DataUrl();
        using var stream = new MemoryStream(Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    sealed class ChatReceiver : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal string Reply { get; set; } = "{\"reply\":\"收到生成图片。\"}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            var body = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = Reply } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
