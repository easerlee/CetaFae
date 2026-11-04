using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class ChatHistoryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated");
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "checks", "chat-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.dat");
        try
        {
            var store = new ChatHistoryStore(options, path);
            check(store.Load() && store.Turns.Count == 0 && !File.Exists(path), "empty chat startup creates no history file");
            for (var index = 0; index < 7; index++)
                check(store.Append(new("生成问题" + index, "生成回答" + index)), "completed resting exchange is atomically saved");
            var bytes = File.ReadAllBytes(path);
            check(!Encoding.UTF8.GetString(bytes).Contains("生成问题"), "stored history is current-user protected ciphertext");
            var restored = new ChatHistoryStore(options, path);
            check(restored.Load() && restored.Turns.Count == 7 && restored.Turns[0].Question == "生成问题0" &&
                  restored.Context.Count == 4 && restored.Context[0].Question == "生成问题3",
                "restart restores the full archive while model context remains four recent exchanges");
            var session = new FreeChatSession((history, _, _) =>
            {
                check(history.SequenceEqual(restored.Context), "only restored bounded context enters the next explicit request");
                return Task.FromResult<string?>("继续说明");
            });
            session.RestoreHistory(restored.Context);
            check((await session.SubmitAsync("继续问"))?.Reply == "继续说明", "restored text chat can continue after restart");
            var window = new FreeChatWindow();
            foreach (var turn in restored.Turns) window.ShowTurn(turn);
            check(((StackPanel)window.Transcript.Content).Children.Count == 7 && window.Handle == IntPtr.Zero,
                "the real offscreen surface keeps all saved rows without opening a native window");

            foreach (var changed in new[] { options with { Model = "another" }, options with { IncludeImage = false },
                         options with { BaseUrl = new Uri("https://example.invalid/v1/") } })
            {
                var other = new ChatHistoryStore(changed, path);
                check(other.Load() && other.Turns.Count == 7 && other.Context.Count == 0,
                    "changed connection can display old records but cannot send them as model context");
            }
            var changedStore = new ChatHistoryStore(options with { Model = "another" }, path);
            check(changedStore.Append(new("新服务问题", "新服务回答")) && changedStore.Context.Count == 1,
                "new recipient context starts with its own exchange rather than importing an earlier conversation");
            var returned = new ChatHistoryStore(options, path);
            check(returned.Load() && returned.Context.Count == 0 && returned.Turns.Count == 8,
                "returning to an older configuration does not revive a non-contiguous model context");

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var before = File.ReadAllBytes(path);
                check(!returned.Append(new("无法写入", "临时回答")) && File.ReadAllBytes(path).SequenceEqual(before),
                    "failed atomic replacement preserves every old byte");
                var clears = 0;
                window.Cleared += () => clears++;
                window.ClearRequested += () => { if (returned.Clear()) window.ClearConversation(); };
                window.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(clears == 0 && ((StackPanel)window.Transcript.Content).Children.Count == 7,
                    "failed disk deletion cannot pretend to clear visible history");
            }
            window.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!File.Exists(path) && returned.Turns.Count == 0 && ((StackPanel)window.Transcript.Content).Children.Count == 0 &&
                  new ChatHistoryStore(options, path).Load(), "successful user clear deletes disk and displayed history before restart");
            window.Close();

            var pending = new TaskCompletionSource<string?>();
            var cancelled = new FreeChatSession((_, _, _) => pending.Task);
            var request = cancelled.SubmitAsync("迟到问题");
            cancelled.Reset(); returned.Clear(); pending.SetResult("迟到回答");
            var late = await request;
            if (late is not null) returned.Append(late);
            check(late is null && !File.Exists(path), "clearing a pending request prevents its late reply from recreating the archive");
            check(!returned.Append(new("密码：generated-test", "不应保存")) && !returned.Append(new("问题", "", RequiresWindow: true)) &&
                  !File.Exists(path), "sensitive input and unfinished window handoffs never enter the resting archive");

            foreach (var malformed in new[] { "not-json", "{\"Version\":2,\"Entries\":[]}",
                         "{\"Version\":1,\"Entries\":[null]}",
                         JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new { Turn = new ChatTurn("generated", ""), Connection = new string('A', 64) } } }) })
            {
                var invalid = ProtectedData.Protect(Encoding.UTF8.GetBytes(malformed), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(path, invalid);
                var broken = new ChatHistoryStore(options, path);
                check(!broken.Load() && !broken.Append(new("新问题", "新回答")) && File.ReadAllBytes(path).SequenceEqual(invalid),
                    "invalid protected archive is rejected without silently overwriting old data");
                check(broken.Clear(), "explicit user clear can remove an unreadable archive");
            }
            File.WriteAllBytes(path, [1, 2, 3]);
            check(!new ChatHistoryStore(options, path).Load(), "unprotected or corrupt history cannot enter a request");
            using (var oversized = new FileStream(path, FileMode.Create)) oversized.SetLength(ChatHistoryStore.MaxBytes + 1L);
            check(!new ChatHistoryStore(options, path).Load() && new FileInfo(path).Length == ChatHistoryStore.MaxBytes + 1L,
                "oversized archive is left intact rather than loaded or silently truncated");
            File.Delete(path);
            Directory.CreateDirectory(path);
            check(!new ChatHistoryStore(options, path).Append(new("问题", "回答")), "non-file archive target reports a storage failure");
            Directory.Delete(path);

            var boundHistory = Array.Empty<ChatTurn>();
            var bound = new FreeChatSession((history, _, _) =>
            { boundHistory = history.ToArray(); return Task.FromResult<string?>("窗口回答"); });
            await bound.SubmitAsync("窗口问题");
            check(boundHistory.Length == 0 && returned.Turns.Count == 0,
                "separate window-question sessions neither import resting records nor append to their archive");

            var first = new ChatHistoryStore(options, path);
            check(first.Append(new("保留问题", "保留回答")), "concurrent-store fixture saves an initial exchange");
            var stale = new ChatHistoryStore(options, path);
            check(stale.Load() && first.Clear() && !stale.Append(new("过期问题", "过期回答")) && !File.Exists(path),
                "another live instance cannot recreate cleared history from its stale cache");
            check(first.Append(new("新问题", "新回答")), "fresh context can save after an explicit clear");
            var staleWriter = new ChatHistoryStore(options, path); staleWriter.Load();
            check(first.Append(new("再问", "再答")) && !staleWriter.Append(new("过期写入", "不应覆盖")) &&
                  new ChatHistoryStore(options, path) is { } latest && latest.Load() && latest.Turns.Count == 2,
                "a stale instance cannot overwrite newer exchanges from another process");
            var localOnly = new FreeChatWindow();
            localOnly.ShowTurn(new("本机问题", "本机回答"));
            localOnly.Input.Text = "不能发送";
            localOnly.SetSendingAllowed(false);
            var sends = 0; localOnly.Submitted += _ => sends++;
            localOnly.Send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(localOnly.Input.IsReadOnly && !localOnly.Send.IsEnabled && sends == 0 &&
                  ((StackPanel)localOnly.Transcript.Content).Children.Count == 1,
                "withdrawn consent permits local reading while refusing all new sends");
            localOnly.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(((StackPanel)localOnly.Transcript.Content).Children.Count == 0 && localOnly.Input.IsReadOnly,
                "local-only history can be cleared without re-enabling network access");
            localOnly.Close();

            var redirected = Path.Combine(directory, "redirected");
            try { Directory.CreateSymbolicLink(redirected, directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var start = new ProcessStartInfo("cmd") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/d", "/c", "mklink", "/J", redirected, directory }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                _ = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("chat_reparse_fixture_unavailable");
            }
            try
            {
                var redirectedStore = new ChatHistoryStore(options, Path.Combine(redirected, "history.dat"));
                var before = File.ReadAllBytes(path);
                check(!redirectedStore.Load() && !redirectedStore.Append(new("绕行问题", "绕行回答")) &&
                      !redirectedStore.Clear() && File.ReadAllBytes(path).SequenceEqual(before),
                    "redirected archive paths cannot read, overwrite or delete the generated target file");
            }
            finally { Directory.Delete(redirected); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
