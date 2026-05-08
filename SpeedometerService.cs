using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpecIQ;

public enum SpeedometerBrowser { WebView2, Edge, Chrome }

public static class SpeedometerService
{
    public const string SpeedometerUrl = "https://browserbench.org/Speedometer3.1/";

    // Injected after page load: auto-clicks Start, calls REPORT_FN(score) when done.
    // REPORT_FN is replaced at call-site with the appropriate bridge function.
    private const string InjectTemplate = """
        (function() {
            function tryStart() {
                const btn =
                    document.querySelector('.start-tests-button') ??
                    document.querySelector('#run-benchmark-button') ??
                    [...document.querySelectorAll('button')].find(
                        b => /^(start|run)$/i.test(b.textContent.trim()) && !b.disabled);
                if (btn) { btn.click(); return true; }
                return false;
            }
            function watchScore(cb) {
                const read = () => {
                    const el =
                        document.querySelector('.score-container .score') ??
                        document.querySelector('#result .score')           ??
                        document.querySelector('.result .score')           ??
                        document.querySelector('[class="score"]');
                    if (!el) return false;
                    const v = parseFloat(el.textContent.replace(/[^\d.]/g, ''));
                    if (v > 0 && v < 100000) { cb(v); return true; }
                    return false;
                };
                if (read()) return;
                const obs = new MutationObserver(() => { if (read()) obs.disconnect(); });
                obs.observe(document.body, { childList: true, subtree: true, characterData: true });
            }
            function init() {
                if (!tryStart()) { setTimeout(init, 500); return; }
                watchScore(score => REPORT_FN(score.toString()));
            }
            if (document.readyState === 'loading')
                document.addEventListener('DOMContentLoaded', () => setTimeout(init, 1000));
            else
                setTimeout(init, 1000);
        })();
        """;

    public static string BuildWebView2Script() =>
        InjectTemplate.Replace("REPORT_FN", "window.chrome.webview.postMessage");

    public static string? FindBrowserExe(SpeedometerBrowser browser)
    {
        string[] paths = browser == SpeedometerBrowser.Edge
            ? [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft", "Edge", "Application", "msedge.exe"),
              ]
            : [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "Application", "chrome.exe"),
              ];
        return paths.FirstOrDefault(File.Exists);
    }

    public static async Task<double> RunViaCdpAsync(
        string browserExe,
        IProgress<string> progress,
        CancellationToken ct)
    {
        const int port = 9222;

        progress.Report("Launching browser…");
        var proc = Process.Start(new ProcessStartInfo(browserExe,
            $"--remote-debugging-port={port} --new-window --no-first-run --no-default-browser-check about:blank")
            { UseShellExecute = true })
            ?? throw new InvalidOperationException("Failed to launch browser.");

        try
        {
            // Wait for CDP endpoint
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            string? wsUrl = null;
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                try
                {
                    var json = await http.GetStringAsync($"http://localhost:{port}/json", ct);
                    wsUrl = JsonNode.Parse(json)?.AsArray()
                        .FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page")
                        ?["webSocketDebuggerUrl"]?.GetValue<string>();
                    if (wsUrl != null) break;
                }
                catch { }
            }
            if (wsUrl == null) throw new InvalidOperationException("Browser DevTools did not respond.");

            progress.Report("Connected to DevTools…");
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);

            var scoreTcs = new TaskCompletionSource<double>();
            var loadTcs  = new TaskCompletionSource<bool>();
            int cmdId    = 1;

            async Task SendCmd(object cmd)
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cmd));
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }

            _ = Task.Run(async () =>
            {
                var buf = new byte[1 << 20]; // 1 MB receive buffer
                var acc = new List<byte>();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var r = await ws.ReceiveAsync(buf, ct);
                        acc.AddRange(buf.AsSpan(0, r.Count).ToArray());
                        if (!r.EndOfMessage) continue;

                        var node   = JsonNode.Parse(Encoding.UTF8.GetString(acc.ToArray()));
                        acc.Clear();
                        var method = node?["method"]?.GetValue<string>();

                        if (method == "Page.loadEventFired")
                            loadTcs.TrySetResult(true);

                        if (method == "Runtime.bindingCalled" &&
                            node!["params"]?["name"]?.GetValue<string>() == "__speedometerScore__")
                        {
                            var payload = node["params"]?["payload"]?.GetValue<string>();
                            if (double.TryParse(payload,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var s))
                                scoreTcs.TrySetResult(s);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                }
            }, ct);

            await SendCmd(new { id = cmdId++, method = "Runtime.enable" });
            await SendCmd(new { id = cmdId++, method = "Page.enable" });
            await SendCmd(new { id = cmdId++, method = "Runtime.addBinding",
                @params = new { name = "__speedometerScore__" } });

            progress.Report("Navigating to Speedometer 3.1…");
            await SendCmd(new { id = cmdId++, method = "Page.navigate",
                @params = new { url = SpeedometerUrl } });

            await loadTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await Task.Delay(500, ct);

            progress.Report("Benchmark starting…  (3–5 min)");
            var script = InjectTemplate.Replace("REPORT_FN", "window.__speedometerScore__");
            await SendCmd(new { id = cmdId++, method = "Runtime.evaluate",
                @params = new { expression = script } });

            var score = await scoreTcs.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
            progress.Report($"Score: {score:F2}");
            return score;
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }
}
