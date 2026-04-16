using System.Net;
using System.Text;
using System.Text.Json;

namespace SpecIQ;

/// <summary>
/// Lightweight HTTP dev server. Only started in DEBUG builds.
/// GET /        → HTML dashboard (auto-refreshes every second)
/// GET /stats   → JSON snapshot of current system stats
/// </summary>
internal static class DevServer
{
    private static HttpListener? _listener;

    internal static void Start(int port = 5000)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        Task.Run(Loop);
    }

    internal static void Stop() => _listener?.Stop();

    private static async Task Loop()
    {
        while (_listener is { IsListening: true })
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => Handle(ctx));
            }
            catch { break; }
        }
    }

    private static void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/stats")
                WriteJson(ctx);
            else
                WriteHtml(ctx);
        }
        catch { }
        finally { ctx.Response.Close(); }
    }

    private static void WriteJson(HttpListenerContext ctx)
    {
        var stats = SystemStats.Snapshot;
        var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = false });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.OutputStream.Write(bytes);
    }

    private static void WriteHtml(HttpListenerContext ctx)
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8"/>
              <title>SpecIQ Dev Dashboard</title>
              <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  background: #0f0f1a;
                  color: #fff;
                  font-family: 'Segoe UI', system-ui, sans-serif;
                  display: flex;
                  justify-content: center;
                  align-items: flex-start;
                  padding: 32px 16px;
                  min-height: 100vh;
                }
                .card {
                  background: #1a1a2e;
                  border-radius: 16px;
                  padding: 24px 28px;
                  width: 280px;
                  box-shadow: 0 8px 32px rgba(0,0,0,.5);
                }
                .time { font-size: 28px; font-weight: 700; }
                .date { font-size: 12px; color: rgba(255,255,255,.5); margin-top: 2px; }
                hr { border: none; border-top: 1px solid rgba(255,255,255,.1); margin: 14px 0; }
                .row { display: flex; align-items: center; gap: 10px; margin: 10px 0; }
                .label { font-size: 10px; color: rgba(255,255,255,.5); letter-spacing: .5px; }
                .val { font-size: 13px; font-weight: 600; margin-left: auto; white-space: nowrap; }
                .bar-wrap { background: rgba(255,255,255,.1); border-radius: 3px; height: 4px; margin-top: 3px; flex: 1; }
                .bar { height: 4px; border-radius: 3px; transition: width .4s ease; }
                .col { flex: 1; min-width: 0; }
                .icon { font-size: 16px; color: rgba(255,255,255,.5); width: 20px; text-align: center; flex-shrink: 0; }
                .net-speeds { text-align: right; }
                .net-speed { font-size: 11px; color: rgba(255,255,255,.7); }
                .net-speed.down { color: #fff; font-weight: 600; }
                .status { font-size: 10px; color: rgba(255,255,255,.3); text-align: center; margin-top: 16px; }
              </style>
            </head>
            <body>
              <div>
                <div class="card" id="card">Loading...</div>
                <div class="status" id="status">connecting...</div>
              </div>
              <script>
                function color(pct, palette) {
                  if (pct > 80) return '#f87171';
                  if (pct > 50) return '#fbbf24';
                  return palette;
                }
                function bar(pct, col) {
                  return `<div class="bar-wrap"><div class="bar" style="width:${pct}%;background:${col}"></div></div>`;
                }
                function row(icon, label, val, pct, col) {
                  const c = color(pct, col);
                  return `<div class="row">
                    <span class="icon">${icon}</span>
                    <div class="col">
                      <div class="label">${label}</div>
                      ${bar(pct, c)}
                    </div>
                    <span class="val">${val}</span>
                  </div>`;
                }
                async function refresh() {
                  try {
                    const r = await fetch('/stats');
                    const d = await r.json();
                    const card = document.getElementById('card');
                    const batColor = d.batteryCharging ? '#4ade80' : (d.batteryPct <= 20 ? '#f87171' : d.batteryPct <= 50 ? '#fbbf24' : '#4ade80');
                    const npuRow = d.npuPct >= 0
                      ? `<hr>${row('🧠','NPU', d.npuPct+'%', d.npuPct, '#f472b6')}`
                      : `<hr><div class="row"><span class="icon">🧠</span><div class="col"><div class="label">NPU</div></div><span class="val" style="color:rgba(255,255,255,.4)">N/A</span></div>`;
                    card.innerHTML = `
                      <div class="time">${d.time}</div>
                      <div class="date">${d.date}</div>
                      <hr>
                      ${row('🔋','BATTERY', d.batteryPct+'%'+(d.batteryCharging?' ⚡':''), d.batteryPct, batColor)}
                      <hr>
                      ${row('🖥','CPU', d.cpuPct+'%', d.cpuPct, '#60a5fa')}
                      <hr>
                      ${row('💾','RAM', d.ramUsed+'/'+d.ramTotal+' GB', d.ramPct, '#c084fc')}
                      <hr>
                      ${row('🎮','GPU', d.gpuPct+'%', d.gpuPct, '#fb923c')}
                      ${npuRow}
                      <hr>
                      <div class="row">
                        <span class="icon">📶</span>
                        <div class="col">
                          <div class="label">NETWORK</div>
                          <div class="label" style="color:rgba(255,255,255,.7);font-size:10px">${d.networkName}</div>
                        </div>
                        <div class="net-speeds">
                          <div class="net-speed">↑ ${d.networkUp}</div>
                          <div class="net-speed down">↓ ${d.networkDown}</div>
                        </div>
                      </div>`;
                    document.getElementById('status').textContent = 'updated ' + new Date().toLocaleTimeString();
                  } catch(e) {
                    document.getElementById('status').textContent = 'waiting for SpecIQ...';
                  }
                }
                refresh();
                setInterval(refresh, 1500);
              </script>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.OutputStream.Write(bytes);
    }
}
