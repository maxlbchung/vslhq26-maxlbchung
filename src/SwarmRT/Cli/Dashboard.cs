using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmRT.Contracts;
using SwarmRT.Orchestration;
using SwarmRT.Org;
using SwarmRT.Reporting;

namespace SwarmRT.Cli;

/// <summary>A pretext or lever slice as the report charts read it.</summary>
public sealed record SliceView
{
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("success")] public required int Success { get; init; }
    [JsonPropertyName("delivered")] public required int Delivered { get; init; }
    [JsonPropertyName("rate")] public double? Rate { get; init; }
}

/// <summary>
/// The numbers behind the report page's visuals — the headline tally, the strategies that
/// landed, and how attempts were resisted. Projected straight from <see cref="EngagementStatistics"/>
/// so the charts and the generated markdown never disagree.
/// </summary>
public sealed record ReportSummary
{
    [JsonPropertyName("engagement")] public required string Engagement { get; init; }
    [JsonPropertyName("org")] public required string Org { get; init; }
    [JsonPropertyName("engine")] public required string Engine { get; init; }
    [JsonPropertyName("success")] public required int Success { get; init; }
    [JsonPropertyName("failure")] public required int Failure { get; init; }
    [JsonPropertyName("blocked")] public required int Blocked { get; init; }
    [JsonPropertyName("delivered")] public required int Delivered { get; init; }
    [JsonPropertyName("total")] public required int Total { get; init; }
    [JsonPropertyName("success_rate")] public double? SuccessRate { get; init; }
    [JsonPropertyName("top_pretexts")] public required IReadOnlyList<SliceView> TopPretexts { get; init; }
    [JsonPropertyName("top_levers")] public required IReadOnlyList<SliceView> TopLevers { get; init; }
    [JsonPropertyName("resistance")] public required IReadOnlyDictionary<string, int> Resistance { get; init; }

    public static ReportSummary From(EngagementStatistics stats, EngagementManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(manifest);

        static SliceView View(SliceStats slice, string label) => new()
        {
            Label = label,
            Success = slice.Tally.Success,
            Delivered = slice.Tally.Delivered,
            Rate = slice.Tally.SuccessRate,
        };

        int Sum(ResistanceSignal signal) =>
            stats.Employees.Sum(e => e.ResistanceSignals.GetValueOrDefault(signal));

        return new ReportSummary
        {
            Engagement = stats.EngagementId,
            Org = stats.Org.OrgName,
            Engine = manifest.Engine.Backend,
            Success = stats.Tally.Success,
            Failure = stats.Tally.Failure,
            Blocked = stats.Tally.Blocked,
            Delivered = stats.Tally.Delivered,
            Total = stats.Tally.Total,
            SuccessRate = stats.Tally.SuccessRate,
            // ByPretext / ByLever are already ordered most-landed first.
            TopPretexts = stats.ByPretext.Where(s => s.Tally.Success > 0).Take(3)
                .Select(s => View(s, PretextCatalog.Find(s.Key)?.Label ?? s.Key)).ToArray(),
            TopLevers = stats.ByLever.Where(s => s.Tally.Success > 0).Take(3)
                .Select(s => View(s, s.Key)).ToArray(),
            Resistance = new Dictionary<string, int>
            {
                ["escalated"] = Sum(ResistanceSignal.Escalated),
                ["verified"] = Sum(ResistanceSignal.Verified),
                ["disengaged"] = Sum(ResistanceSignal.Disengaged),
                ["unclassified"] = Sum(ResistanceSignal.Unclassified),
            },
        };
    }
}

/// <summary>One attempt as the browser dashboard sees it: assignment, orchestrator rationale, result.</summary>
public sealed record DashboardItem
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("total")] public required int Total { get; init; }
    [JsonPropertyName("attempt_id")] public required string AttemptId { get; init; }
    [JsonPropertyName("target_name")] public required string TargetName { get; init; }
    [JsonPropertyName("target_role")] public required string TargetRole { get; init; }
    [JsonPropertyName("target_dept")] public required string TargetDept { get; init; }
    [JsonPropertyName("exposure")] public required string Exposure { get; init; }
    [JsonPropertyName("pretext")] public required string Pretext { get; init; }
    [JsonPropertyName("channel")] public required string Channel { get; init; }
    [JsonPropertyName("tactic")] public required string Tactic { get; init; }
    [JsonPropertyName("rationale")] public required string Rationale { get; init; }

    /// <summary>success | failure | blocked | error.</summary>
    [JsonPropertyName("outcome")] public required string Outcome { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
    [JsonPropertyName("is_control")] public required bool IsControl { get; init; }
}

/// <summary>
/// A local, black-and-white wireframe dashboard for a live run. The <c>run</c> command
/// pushes each attempt here as the orchestrator's callbacks fire; the browser polls
/// <c>/feed</c> and cycles through them. When the run finishes the report page unlocks and
/// renders the generated org summary. Served on localhost only, so no URL ACL is needed on
/// Windows, and built on <see cref="HttpListener"/> so the tool keeps its zero-dependency rule.
/// </summary>
public sealed class LiveDashboard : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Lock _gate = new();
    private readonly List<string> _items = [];
    private bool _complete;
    private int _total;
    private string? _reportPath;
    private string? _summaryJson;

    public LiveDashboard(int port)
    {
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
    }

    public string Url { get; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public void PublishAttempt(int index, int total, PlannedAttempt planned, AttemptResult result)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(result);

        Publish(new DashboardItem
        {
            Index = index,
            Total = total,
            AttemptId = result.AttemptId,
            TargetName = planned.Target.Name,
            TargetRole = planned.Target.Role,
            TargetDept = planned.Target.Department,
            Exposure = planned.Target.Exposure.Count == 0 ? "none" : string.Join(", ", planned.Target.Exposure),
            Pretext = planned.Pretext.Label,
            Channel = planned.Pretext.Channel,
            Tactic = result.Tactic,
            Rationale = AttemptPlanner.Explain(planned),
            Outcome = result.Outcome switch
            {
                AttemptOutcome.Success => "success",
                AttemptOutcome.Failure => "failure",
                _ => "blocked",
            },
            Reason = result.Reason,
            Summary = result.AttemptSummary,
            IsControl = planned.IsControlTest,
        });
    }

    public void PublishError(int index, int total, PlannedAttempt planned, Exception error)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(error);

        Publish(new DashboardItem
        {
            Index = index,
            Total = total,
            AttemptId = planned.Assignment.AttemptId,
            TargetName = planned.Target.Name,
            TargetRole = planned.Target.Role,
            TargetDept = planned.Target.Department,
            Exposure = planned.Target.Exposure.Count == 0 ? "none" : string.Join(", ", planned.Target.Exposure),
            Pretext = planned.Pretext.Label,
            Channel = planned.Pretext.Channel,
            Tactic = planned.Assignment.Tactic,
            Rationale = AttemptPlanner.Explain(planned),
            Outcome = "error",
            Reason = $"{error.GetType().Name}: {error.Message}",
            Summary = "Attempt failed before producing a result; recorded in the run manifest, not logged as an outcome.",
            IsControl = planned.IsControlTest,
        });
    }

    private void Publish(DashboardItem item)
    {
        string json = JsonSerializer.Serialize(item, SwarmJson.Line);
        lock (_gate)
        {
            _items.Add(json);
            _total = item.Total;
        }
    }

    /// <summary>Marks the run finished; supplies the markdown report path and the chart numbers.</summary>
    public void Complete(string reportPath, ReportSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string json = JsonSerializer.Serialize(summary, SwarmJson.Line);

        lock (_gate)
        {
            _reportPath = reportPath;
            _summaryJson = json;
            _complete = true;
        }
    }

    public void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        }
        catch
        {
            // Opening the browser is a convenience; the printed URL is the fallback.
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // Listener stopped; drain the loop.
            }

            try
            {
                Handle(context);
            }
            catch
            {
                try { context.Response.Abort(); } catch { /* client already gone */ }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        switch (context.Request.Url?.AbsolutePath)
        {
            case "/":
            case "/index.html":
                Write(context, 200, "text/html; charset=utf-8", Page);
                break;

            case "/feed":
                Write(context, 200, "application/json; charset=utf-8", BuildFeed(context.Request.QueryString["since"]));
                break;

            case "/report.md":
                string? path;
                lock (_gate)
                {
                    path = _reportPath;
                }

                if (path is null || !File.Exists(path))
                {
                    Write(context, 503, "text/plain; charset=utf-8", "Report not ready.");
                }
                else
                {
                    Write(context, 200, "text/markdown; charset=utf-8", File.ReadAllText(path));
                }

                break;

            case "/report.json":
                string? summary;
                lock (_gate)
                {
                    summary = _summaryJson;
                }

                Write(context, summary is null ? 503 : 200, "application/json; charset=utf-8",
                    summary ?? "{}");
                break;

            default:
                Write(context, 404, "text/plain; charset=utf-8", "Not found.");
                break;
        }
    }

    private string BuildFeed(string? sinceRaw)
    {
        int since = int.TryParse(sinceRaw, out int parsed) && parsed > 0 ? parsed : 0;

        lock (_gate)
        {
            IEnumerable<string> fresh = since < _items.Count ? _items.Skip(since) : [];
            return $"{{\"total\":{_total},\"complete\":{(_complete ? "true" : "false")}," +
                   $"\"items\":[{string.Join(",", fresh)}]}}";
        }
    }

    private static void Write(HttpListenerContext context, int status, string contentType, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
    }

    // A single self-contained page: two views toggled in JS, black/white wireframe only.
    private const string Page = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>SwarmRT — live</title>
        <style>
          * { box-sizing: border-box; }
          body { margin: 0; background: #000; color: #fff; font-family: "Courier New", monospace;
                 font-size: 14px; line-height: 1.5; }
          a { color: #fff; }
          .bar { position: sticky; top: 0; z-index: 10;
                 display: flex; align-items: center; justify-content: space-between;
                 background: #000; border-bottom: 1px solid #fff; padding: 10px 16px; }
          .bar .title { letter-spacing: 2px; }
          .status { opacity: 0.8; }
          .btn { border: 1px solid #fff; background: #000; color: #fff; font: inherit;
                 padding: 6px 14px; cursor: pointer; letter-spacing: 1px; }
          .btn:hover { background: #fff; color: #000; }
          .btn[hidden] { display: none; }
          .wrap { padding: 20px 16px; max-width: 860px; margin: 0 auto; }
          .box { border: 1px solid #fff; padding: 12px 14px; margin-bottom: 14px; }
          .box h2 { margin: 0 0 8px; font-size: 12px; letter-spacing: 2px; opacity: 0.7;
                    border-bottom: 1px solid #fff; padding-bottom: 6px; text-transform: uppercase; }
          .row { display: flex; gap: 8px; }
          .row .k { width: 120px; opacity: 0.6; flex: none; }
          .verdict { font-weight: bold; margin-bottom: 10px; }
          .head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 14px; }
          .badge { border: 1px solid #fff; padding: 2px 10px; letter-spacing: 2px; }
          .badge.success { background: #fff; color: #000; }
          .badge.blocked, .badge.error { border-style: dashed; }
          .control-tag { border: 1px dashed #fff; padding: 1px 8px; margin-left: 8px; font-size: 11px; opacity: 0.8; }
          .nav { position: fixed; left: 0; right: 0; bottom: 0; z-index: 10;
                 display: flex; align-items: center; justify-content: center; gap: 18px;
                 background: #000; border-top: 1px solid #fff; padding: 12px 16px; }
          #feed { padding-bottom: 72px; }
          .nav .count { min-width: 90px; text-align: center; }
          .empty { opacity: 0.5; text-align: center; padding: 60px 0; }
          /* report view */
          #report { display: none; }
          .charts { display: flex; gap: 14px; flex-wrap: wrap; margin-bottom: 14px; }
          .charts .box { flex: 1; min-width: 260px; margin-bottom: 0; }
          .pierow { display: flex; gap: 16px; align-items: center; }
          .rate { font-size: 30px; font-weight: bold; line-height: 1; }
          .legend { margin-top: 10px; display: flex; flex-direction: column; gap: 4px; }
          .legend span { display: flex; align-items: center; gap: 8px; }
          .sw { width: 12px; height: 12px; border: 1px solid #fff; flex: none; }
          .bar { display: flex; align-items: center; gap: 8px; margin: 8px 0; }
          .bar .bl { width: 150px; flex: none; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
          .bar .track { flex: 1; border: 1px solid #fff; height: 14px; }
          .bar .fill { display: block; height: 100%; background: #fff; }
          .bar .bn { width: 54px; flex: none; text-align: right; opacity: 0.8; }
          .rec { border-width: 2px; }
          .rec h2 { letter-spacing: 3px; }
          .md { border: 1px solid #fff; padding: 20px 22px; margin-bottom: 14px; }
          .md h1 { font-size: 20px; } .md h2 { font-size: 16px; } .md h3 { font-size: 13px; }
          .md h1, .md h2 { border-bottom: 1px solid #fff; padding-bottom: 6px; }
          .md table { border-collapse: collapse; width: 100%; margin: 10px 0; display: block; overflow-x: auto; }
          .md th, .md td { border: 1px solid #fff; padding: 4px 8px; text-align: left; vertical-align: top; }
          .md blockquote { border-left: 3px solid #fff; margin: 10px 0; padding: 4px 12px; opacity: 0.85; }
          .md code { border: 1px solid #fff; padding: 0 4px; }
          .md .lnk { text-decoration: underline; }
          .md hr { border: none; border-top: 1px solid #fff; }
        </style>
        </head>
        <body>
          <div class="bar">
            <span class="title">SWARMRT // LIVE</span>
            <span class="status" id="status">connecting...</span>
            <button class="btn" id="toReport" hidden>VIEW REPORT &#9654;</button>
            <button class="btn" id="toFeed" hidden>&#9664; BACK TO ATTEMPTS</button>
          </div>

          <div id="feed" class="wrap">
            <div id="card"><div class="empty">Waiting for the first attempt...</div></div>
            <div class="nav">
              <button class="btn" id="prev">&#9664; PREV</button>
              <span class="count" id="count">0 / 0</span>
              <button class="btn" id="next">NEXT &#9654;</button>
            </div>
          </div>

          <div id="report" class="wrap"><div id="reportBody">Loading report...</div></div>

        <script>
        const items = [];
        let cur = 0, following = true, complete = false, reportLoaded = false;
        const $ = id => document.getElementById(id);
        const esc = s => (s ?? "").replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));

        const VERDICT = {
          success: "The target fell for it — they complied with the request.",
          failure: "The target resisted — they did not comply.",
          blocked: "Stopped by the safety gate before it was ever sent to the target.",
          error: "The attempt errored out before it produced a result.",
        };

        function card(it) {
          if (!it) return '<div class="empty">Waiting for the first attempt...</div>';
          const badge = it.outcome.toUpperCase();
          const ctrl = it.is_control ? '<span class="control-tag">SAFETY CONTROL TEST</span>' : '';
          const why = it.reason ? '<div class="row"><span class="k">why</span><span>' + esc(it.reason) + '</span></div>' : '';
          return `
            <div class="head">
              <span>Attempt ${esc(it.attempt_id)} ${ctrl}</span>
              <span class="badge ${it.outcome}">${badge}</span>
            </div>
            <div class="box"><h2>Who was targeted</h2>
              <div class="row"><span class="k">name</span><span>${esc(it.target_name)}</span></div>
              <div class="row"><span class="k">job</span><span>${esc(it.target_role)} &middot; ${esc(it.target_dept)}</span></div>
              <div class="row"><span class="k">public info</span><span>${esc(it.exposure)}</span></div>
            </div>
            <div class="box"><h2>What the agent tried</h2>
              <div class="row"><span class="k">cover story</span><span>${esc(it.pretext)}</span></div>
              <div class="row"><span class="k">sent via</span><span>${esc(it.channel)}</span></div>
              <div class="row"><span class="k">levers used</span><span>${esc(it.tactic)}</span></div>
            </div>
            <div class="box"><h2>Why the orchestrator chose this</h2><div>${esc(it.rationale)}</div></div>
            <div class="box"><h2>Result</h2>
              <div class="verdict">${esc(VERDICT[it.outcome] || it.outcome)}</div>
              ${why}
              <div class="row"><span class="k">what happened</span><span>${esc(it.summary)}</span></div>
            </div>`;
        }

        function render() {
          $("card").innerHTML = card(items[cur]);
          $("count").textContent = items.length ? (cur + 1) + " / " + items.length : "0 / 0";
        }

        $("prev").onclick = () => { if (cur > 0) { cur--; following = false; render(); } };
        $("next").onclick = () => { if (cur < items.length - 1) { cur++; following = (cur === items.length - 1); render(); } };
        $("toReport").onclick = () => { showReport(); };
        $("toFeed").onclick = () => { showFeed(); };

        function showReport() {
          $("feed").style.display = "none"; $("report").style.display = "block";
          $("toReport").hidden = true; $("toFeed").hidden = false;
          if (!reportLoaded) loadReport();
        }
        function showFeed() {
          $("report").style.display = "none"; $("feed").style.display = "block";
          $("toFeed").hidden = true; $("toReport").hidden = false;
        }

        async function loadReport() {
          try {
            const [sum, md] = await Promise.all([
              (await fetch("/report.json")).json(),
              (await fetch("/report.md")).text(),
            ]);
            $("reportBody").innerHTML = buildReport(sum, md);
            drawPie("pie", [
              { v: sum.success, fill: "#fff" },
              { v: sum.failure, fill: "#888" },
              { v: sum.blocked, fill: "#000" },
            ]);
            reportLoaded = true;
          } catch (e) { $("reportBody").textContent = "Could not load report."; }
        }

        // Split the generated markdown into { title, body } sections keyed by "## " headings.
        function sectionize(md) {
          const lines = md.replace(/\r\n/g, "\n").split("\n");
          const secs = []; let cur = { title: "", body: [] };
          for (const ln of lines) {
            const m = ln.match(/^##\s+(.*)/);
            if (m) { secs.push(cur); cur = { title: m[1].trim(), body: [] }; }
            else cur.body.push(ln);
          }
          secs.push(cur);
          return secs;
        }

        function buildReport(sum, md) {
          const secs = sectionize(md);
          const find = t => secs.find(s => s.title === t);
          const bodyHtml = s => s ? renderMd(s.body.join("\n")) : "";
          const pct = r => r == null ? "n/a" : Math.round(r * 100) + "%";

          // Top strategies bar chart (by number of targets that fell for it).
          const top = sum.top_pretexts || [];
          const maxS = Math.max(1, ...top.map(p => p.success));
          const bars = top.length
            ? top.map(p => `<div class="bar">
                  <span class="bl">${esc(p.label)}</span>
                  <span class="track"><span class="fill" style="width:${Math.round(p.success / maxS * 100)}%"></span></span>
                  <span class="bn">${p.success}/${p.delivered}</span></div>`).join("")
            : "<div>No approach produced a favorable reply.</div>";

          const blockedNote = sum.blocked
            ? `${sum.blocked} ${sum.blocked === 1 ? "attempt was" : "attempts were"} stopped by the safety gate before delivery. `
            : "Nothing was stopped by the safety gate. ";
          const overview = `
            <p>Ran <strong>${sum.total}</strong> simulated attempts against ${esc(sum.org)} (engine: ${esc(sum.engine)}). ` +
              `<strong>${sum.delivered}</strong> reached a target. ${blockedNote}</p>
            <p><strong>${sum.success}</strong> of ${sum.delivered} delivered attempts succeeded (<strong>${pct(sum.success_rate)}</strong>).</p>` +
            (top.length ? `<p>The most effective cover story was <strong>${esc(top[0].label)}</strong> ` +
              `(${top[0].success} of ${top[0].delivered} landed).</p>` : "");

          // Detailed sections at the bottom, in report order, minus what the top already shows.
          const skip = new Set(["", "Result at a glance", "Engagement", "Recommendations", "Summary"]);
          const details = secs.filter(s => !skip.has(s.title))
            .map(s => `<div class="md"><h2>${esc(s.title)}</h2>${renderMd(s.body.join("\n"))}</div>`).join("");

          const summarySec = find("Summary");

          return `
            <div class="md">${bodyHtml(secs[0]) /* H1 + simulation disclaimer */}</div>
            <div class="charts">
              <div class="box"><h2>Outcome</h2>
                <div class="pierow">
                  <canvas id="pie" width="120" height="120"></canvas>
                  <div><div class="rate">${pct(sum.success_rate)}</div><div>success rate</div></div>
                </div>
                <div class="legend">
                  <span><i class="sw" style="background:#fff"></i>Succeeded — ${sum.success}</span>
                  <span><i class="sw" style="background:#888"></i>Resisted — ${sum.failure}</span>
                  <span><i class="sw" style="background:#000"></i>Blocked — ${sum.blocked}</span>
                </div>
              </div>
              <div class="box"><h2>Top strategies that landed</h2>${bars}</div>
            </div>
            ${summarySec ? `<div class="md"><h2>Summary</h2>${bodyHtml(summarySec)}</div>` : ""}
            <div class="md"><h2>Overview</h2>${overview}</div>
            <div class="md rec"><h2>Recommendations</h2>${bodyHtml(find("Recommendations"))}</div>
            ${details}`;
        }

        // Pie chart in the wireframe palette: white / grey / black wedges, white separators.
        function drawPie(id, parts) {
          const c = $(id); if (!c) return;
          const ctx = c.getContext("2d"); const w = c.width, cx = w / 2, cy = w / 2, r = w / 2 - 2;
          const total = parts.reduce((s, p) => s + p.v, 0);
          ctx.clearRect(0, 0, w, w);
          if (total === 0) { ctx.strokeStyle = "#fff"; ctx.beginPath(); ctx.arc(cx, cy, r, 0, Math.PI * 2); ctx.stroke(); return; }
          let a = -Math.PI / 2;
          for (const p of parts) {
            if (p.v === 0) continue;
            const slice = p.v / total * Math.PI * 2;
            ctx.beginPath(); ctx.moveTo(cx, cy); ctx.arc(cx, cy, r, a, a + slice); ctx.closePath();
            ctx.fillStyle = p.fill; ctx.fill();
            ctx.strokeStyle = "#fff"; ctx.lineWidth = 1; ctx.stroke();
            a += slice;
          }
        }

        async function poll() {
          try {
            const data = await (await fetch("/feed?since=" + items.length)).json();
            if (data.items.length) {
              for (const it of data.items) items.push(it);
              if (following) cur = items.length - 1;
              render();
            }
            $("status").textContent = data.complete
              ? "complete — " + items.length + " attempts"
              : "running — " + items.length + (data.total ? " / " + data.total : "");
            if (data.complete && !complete) { complete = true; $("toReport").hidden = false; }
            if (data.complete) return; // stop polling
          } catch (e) { $("status").textContent = "waiting..."; }
          setTimeout(poll, 1000);
        }

        // Minimal Markdown renderer for exactly the constructs the report emits.
        function renderMd(src) {
          const lines = src.replace(/\r\n/g, "\n").split("\n");
          const out = []; let i = 0;
          const inline = s => esc(s)
            .replace(/`([^`]+)`/g, "<code>$1</code>")
            .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
            .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<span class="lnk">$1</span>');
          while (i < lines.length) {
            let ln = lines[i];
            if (!ln.trim()) { i++; continue; }
            if (/^#{1,6}\s/.test(ln)) {
              const h = ln.match(/^#+/)[0].length;
              out.push("<h" + h + ">" + inline(ln.replace(/^#+\s/, "")) + "</h" + h + ">"); i++; continue;
            }
            if (/^(---|\*\*\*)\s*$/.test(ln)) { out.push("<hr>"); i++; continue; }
            if (ln.startsWith(">")) {
              const buf = [];
              while (i < lines.length && lines[i].startsWith(">")) { buf.push(lines[i].replace(/^>\s?/, "")); i++; }
              out.push("<blockquote>" + inline(buf.join(" ")) + "</blockquote>"); continue;
            }
            if (ln.startsWith("|")) {
              const rows = [];
              while (i < lines.length && lines[i].startsWith("|")) { rows.push(lines[i]); i++; }
              const cells = r => r.replace(/^\||\|$/g, "").split("|").map(c => c.trim());
              const sep = rows[1] && /^\s*:?-+:?\s*$/.test(cells(rows[1])[0]);
              let t = "<table>";
              rows.forEach((r, idx) => {
                if (sep && idx === 1) return;
                const tag = (sep && idx === 0) ? "th" : "td";
                t += "<tr>" + cells(r).map(c => "<" + tag + ">" + inline(c) + "</" + tag + ">").join("") + "</tr>";
              });
              out.push(t + "</table>"); continue;
            }
            if (/^\d+\.\s/.test(ln)) {
              const buf = [];
              while (i < lines.length && /^\d+\.\s/.test(lines[i])) { buf.push("<li>" + inline(lines[i].replace(/^\d+\.\s/, "")) + "</li>"); i++; }
              out.push("<ol>" + buf.join("") + "</ol>"); continue;
            }
            if (/^[-*]\s/.test(ln)) {
              const buf = [];
              while (i < lines.length && /^[-*]\s/.test(lines[i])) { buf.push("<li>" + inline(lines[i].replace(/^[-*]\s/, "")) + "</li>"); i++; }
              out.push("<ul>" + buf.join("") + "</ul>"); continue;
            }
            const buf = [];
            while (i < lines.length && lines[i].trim() && !/^(#|>|\||\d+\.\s|[-*]\s)/.test(lines[i])) { buf.push(lines[i]); i++; }
            out.push("<p>" + inline(buf.join(" ")) + "</p>");
          }
          return out.join("\n");
        }

        render();
        poll();
        </script>
        </body>
        </html>
        """;
}
