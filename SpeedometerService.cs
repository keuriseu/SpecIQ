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
    // Selectors verified against Speedometer 3.1 DOM:
    //   start button → .start-tests-button
    //   score        → #result-number  (shown when data-visible-section="summary")
    private const string InjectTemplate = """
        (function() {
            console.log('[SpecIQ] script injected, readyState=' + document.readyState);

            function tryStart() {
                const btn = document.querySelector('.start-tests-button');
                if (btn && !btn.disabled) {
                    console.log('[SpecIQ] clicking start button');
                    btn.click();
                    return true;
                }
                console.log('[SpecIQ] start button not found yet');
                return false;
            }

            function findScore() {
                // Primary: #result-number appears when data-visible-section="summary"
                const section = document.documentElement.dataset.visibleSection;
                if (section === 'summary') {
                    const el = document.querySelector('#result-number');
                    if (el) {
                        const v = parseFloat(el.textContent.replace(/[^\d.]/g, ''));
                        if (v > 0 && v < 100000) {
                            console.log('[SpecIQ] score ' + v + ' via #result-number');
                            return v;
                        }
                    }
                }
                return null;
            }

            function watchScore(cb) {
                const done = (v) => { obs.disconnect(); clearInterval(poll); cb(v); };
                const check = () => { const v = findScore(); if (v) done(v); };
                const obs  = new MutationObserver(check);
                obs.observe(document.documentElement, { attributes: true, subtree: true, childList: true, characterData: true });
                const poll = setInterval(check, 3000); // poll every 3 s as fallback
                check(); // immediate check in case score already present
            }

            function init() {
                if (!tryStart()) { setTimeout(init, 500); return; }
                console.log('[SpecIQ] benchmark started, watching for score');
                watchScore(score => {
                    console.log('[SpecIQ] reporting score: ' + score);
                    REPORT_FN(score.toString());
                });
            }

            if (document.readyState === 'loading')
                document.addEventListener('DOMContentLoaded', () => setTimeout(init, 1500));
            else
                setTimeout(init, 1500);
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

        // Use a throw-away profile dir so the browser always launches as a fresh
        // process even when Edge/Chrome is already running.  Without this, the OS
        // hands the window to the existing process which has no CDP listener.
        var profileDir = Path.Combine(Path.GetTempPath(), $"speciq_cdp_{Guid.NewGuid():N}");

        progress.Report("Launching browser…");
        var proc = Process.Start(new ProcessStartInfo(browserExe,
            $"--remote-debugging-port={port} --user-data-dir=\"{profileDir}\" " +
            $"--no-first-run --no-default-browser-check --disable-extensions about:blank")
            { UseShellExecute = false })
            ?? throw new InvalidOperationException("Failed to launch browser.");

        try
        {
            // Wait for CDP endpoint — allow up to 20 s for slower machines
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string? wsUrl = null;
            for (int i = 0; i < 40 && !ct.IsCancellationRequested; i++)
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
            try { Directory.Delete(profileDir, recursive: true); } catch { }
        }
    }
}
