using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.ML.Tokenizers;

// Hive skeleton spike: checks the rules in drafts/stack.md and drafts/telemetry.md that need no model.
// Usage: dotnet run                  runs everything
//        dotnet run -- <name>        runs one of: values counters tokens store hive cancel

var passed = 0; var failed = 0;
void Check(string name, bool ok, string detail = "")
{
	if (ok) passed++; else failed++;
	Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
}

// ---------- collectors ----------
var started = new List<Activity>(); var stopped = new List<Activity>();
var measurements = new List<(string Name, long Value, string Tags)>();
var gate = new object();
using var al = new ActivityListener
{
	ShouldListenTo = s => s.Name == Telemetry.Name,
	Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
	ActivityStarted = a => { lock (gate) started.Add(a); },
	ActivityStopped = a => { lock (gate) stopped.Add(a); },
};
ActivitySource.AddActivityListener(al);
using var ml = new MeterListener();
ml.InstrumentPublished = (i, l) => { if (i.Meter.Name == Telemetry.Name) l.EnableMeasurementEvents(i); };
ml.SetMeasurementEventCallback<long>((i, v, tags, _) => { lock (gate) measurements.Add((i.Name, v, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))); });
ml.Start();

void Reset() { lock (gate) { started.Clear(); stopped.Clear(); measurements.Clear(); } }

var only = args.Length > 0 ? args[0] : null;
bool Run(string name) => only is null || only == name;
if (only == "atomic") { AtomicWrite.Run(); return 0; }
if (only == "fresh") { AtomicFresh.Run(); return 0; }
if (only == "compaction") { await CompactionSpike.RunAsync(Check); Console.WriteLine($"=== {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
if (only == "compaction2") { await CompactionSpike2.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 4); return 0; }
if (only == "compaction3") { await CompactionSpike2.RunOptionsAsync(args.Length > 1 ? int.Parse(args[1]) : 4); return 0; }
if (only == "mcp") { await McpSpike.RunAsync(Check); Console.WriteLine($"=== {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
if (only == "idsharing") { await IdSharingSpike.RunAsync(Check); Console.WriteLine($"=== {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
if (only == "anthropic") { await AnthropicSpike.RunAsync(Check); Console.WriteLine($"=== {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }

// ---------- scenario builder ----------
const string Ans = """{"status":"answered","answer":"done"}""";
const string Cna = """{"status":"cannot_answer","reason":"needs data"}""";
const string Chk = """{"status":"checkpointed","reason":"needs a human","question":"Which tone should it use?"}""";

Models NewModels()
{
	var m = new Models();
	m.Script("planner:0", """[{"type":"analyst","goal":"g1"},{"type":"planning","goal":"g2"},{"type":"writer","goal":"g3"}]""");
	m.Script("planner:1", """[{"type":"researcher","goal":"g2a"},{"type":"planning","goal":"g2b"}]""");
	m.Script("judge", "decompose");
	m.Script("analyst", Cna, Cna, Ans);
	m.Script("researcher", Cna, Ans);
	m.Script("reviewer", Ans);
	m.Script("writer", "oops this is not json", Chk);
	return m;
}

ServiceProvider Container(Models models, IFileSystem fs, TimeProvider time, HiveOptions? options = null)
{
	var s = new ServiceCollection();
	s.AddSingleton(models); s.AddSingleton(fs); s.AddSingleton(time); s.AddSingleton(options ?? new HiveOptions());
	s.AddSingleton<CounterRegistry>();
	s.AddSingleton(sp => new SnapshotStore(sp.GetRequiredService<IFileSystem>(), "/state"));
	s.AddSingleton<Tokenizer>(TiktokenTokenizer.CreateForModel("gpt-4o"));
	s.AddSingleton<RunState>();
	s.AddSingleton<Hive>();
	s.AddScoped<CellContext>();
	s.AddScoped<ICellContext>(sp => sp.GetRequiredService<CellContext>());
	s.AddScoped<ICell, AnalystCell>(); s.AddScoped<ICell, ResearcherCell>(); s.AddScoped<ICell, WriterCell>(); s.AddScoped<ICell, ReviewerCell>();
	s.AddScoped<ICell, PlanningCell>();
	return s.BuildServiceProvider();
}

// =============================== 1. value objects ===============================
if (Run("values"))
{
	Console.WriteLine("=== 1. Vogen value objects");
	var node = new NodeSnap(CellId.From(Guid.NewGuid()), null, "writer", 1, "g", "answered", null, 0, null, null, null, null, false);
	var json = JsonSerializer.Serialize(node, Json.Options);
	var back = JsonSerializer.Deserialize<NodeSnap>(json, Json.Options);
	Check("a record holding value objects round-trips through System.Text.Json", back == node, json.Split('\n')[1].Trim());
	Check("a value object compares by value", CellId.From(node.CellId.Value) == node.CellId);
	Check("CellType wraps a string", CellType.From("writer").Value == "writer");
}

// =============================== 2. re-spin counters on a fake clock ===============================
if (Run("counters"))
{
	Console.WriteLine("=== 2. Re-spin counters with FakeTimeProvider");
	var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
	var reg = new CounterRegistry(new HiveOptions(CellTotal: 5, PlannerTotal: 5, HiveTotal: 5, WindowMax: 2, WindowMinutes: 60), time);
	var p = CellId.From(Guid.NewGuid());
	Check("first two re-spins are allowed", reg.TryRespin(p, "x").Ok && reg.TryRespin(p, "x").Ok);
	var third = reg.TryRespin(p, "x");
	Check("the third inside the window is refused, naming the scope", !third.Ok && third.Scope == "cell", $"{third.Scope}: {third.Why}");
	time.Advance(TimeSpan.FromMinutes(61));
	Check("after the window passes the counter allows another", reg.TryRespin(p, "x").Ok);
	reg.TryRespin(p, "x"); time.Advance(TimeSpan.FromMinutes(61)); reg.TryRespin(p, "x");
	time.Advance(TimeSpan.FromMinutes(61));
	var afterTotal = reg.TryRespin(p, "x");
	Check("the total maximum still refuses after the window resets", !afterTotal.Ok && afterTotal.Why.StartsWith("total"), afterTotal.Why);
	var snap = reg.Snapshot();
	var reg2 = new CounterRegistry(new HiveOptions(CellTotal: 5, PlannerTotal: 5, HiveTotal: 5, WindowMax: 2, WindowMinutes: 60), time);
	reg2.Restore(snap);
	Check("counters restore from a snapshot", reg2.Snapshot().OrderBy(s => s.Scope).Select(s => s.Total).SequenceEqual(snap.OrderBy(s => s.Scope).Select(s => s.Total)));
}

// =============================== 3. tokenizer ===============================
if (Run("tokens"))
{
	Console.WriteLine("=== 3. Microsoft.ML.Tokenizers");
	var tk = TiktokenTokenizer.CreateForModel("gpt-4o");
	Check("counts tokens", tk.CountTokens("Hello, world") is > 0 and < 10, tk.CountTokens("Hello, world") + " tokens");
	foreach (var paragraphs in new[] { 0, 12, 40 })
	{
		var counts = Enumerable.Range(1, 6).Select(seed => tk.CountTokens(Tickets.Instructions + "\n" + Tickets.Ticket(true, paragraphs, seed) + "\nQuestion: list the authentication and session problems.")).ToList();
		Console.WriteLine($"    o200k count, {paragraphs,2} padding paragraphs, mean of 6 tickets: {counts.Average():0}");
	}
}

// =============================== 4. checkpoint store through IFileSystem ===============================
if (Run("store"))
{
	Console.WriteLine("=== 4. Checkpoint store through IFileSystem");
	var snapA = new Snapshot(1, RunId.From(Guid.NewGuid()), "q", DateTimeOffset.UtcNow, null, null, [], []);
	var snapB = snapA with { Question = "q-two" };
	var fs = new MockFileSystem();
	var store = new SnapshotStore(fs, "/state");
	Check("no file is 'no checkpoint'", store.Load() is null);
	store.Save(snapA); store.Save(snapB);
	Check("the second save replaces the first", store.Load()!.Question == "q-two");
	Check("no temporary file is left behind", !fs.File.Exists(store.Path + ".tmp"));

	fs.File.WriteAllText(store.Path + ".tmp", "{ half written");
	Check("a leftover half-written temporary file does not affect the good snapshot", store.Load()!.Question == "q-two");
	store.Save(snapA);
	Check("the next save overwrites the stale temporary file and completes", store.Load()!.Question == "q" && !fs.File.Exists(store.Path + ".tmp"));

	fs.File.WriteAllText(store.Path, "{ not valid json");
	var threw = false; try { store.Load(); } catch (InvalidDataException) { threw = true; }
	Check("a corrupt snapshot is an error, not 'no checkpoint'", threw);

	var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hive-store-" + Guid.NewGuid().ToString("N"));
	var real = new SnapshotStore(new FileSystem(), dir);
	real.Save(snapA); real.Save(snapB);
	Check("the same store works on the real file system, replacing the old file", real.Load()!.Question == "q-two" && Directory.GetFiles(dir).Length == 1, string.Join(",", Directory.GetFiles(dir).Select(System.IO.Path.GetFileName)));
	Directory.Delete(dir, true);
}

// =============================== 5. the hive: run, checkpoint, resume ===============================
if (Run("hive"))
{
	Console.WriteLine("=== 5. Hive skeleton: run to a checkpoint");
	Reset();
	var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T09:00:00Z"));
	var fs = new MockFileSystem();
	var models = NewModels();
	using var c1 = Container(models, fs, time, new HiveOptions(CellTotal: 1));
	var result = await c1.GetRequiredService<Hive>().RunAsync("analyse JIRA-1", CancellationToken.None);

	List<Activity> sp; lock (gate) { sp = stopped.ToList(); Check("every span that started was stopped", started.Count == stopped.Count, $"{started.Count} started, {stopped.Count} stopped"); }
	var root1 = sp.Single(a => a.DisplayName == "invoke_workflow hive");
	var traceId1 = root1.TraceId;
	string Tag(Activity a, string k) => a.GetTagItem(k)?.ToString() ?? "";
	IEnumerable<Activity> Agents(string type) => sp.Where(a => a.DisplayName.StartsWith($"invoke_agent {type}("));
	int Events(string name) => sp.Sum(a => a.Events.Count(e => e.Name == name));

	Check("the hive result is a checkpoint", result.Status == "checkpointed", result.Status);
	Check("root span is invoke_workflow, with run id, question reference and outcome", Tag(root1, "gen_ai.operation.name") == "invoke_workflow" && Tag(root1, "honeycomb.run.id").Length > 0 && Tag(root1, "honeycomb.question.ref").StartsWith("q-") && Tag(root1, "honeycomb.cell.outcome") == "checkpointed");
	Check("the root span ends last", sp.Where(a => a != root1).All(a => a.StartTimeUtc + a.Duration <= root1.StartTimeUtc + root1.Duration + TimeSpan.FromMilliseconds(1)));
	Check("agent span counts: planning 2, analyst 2, researcher 2, reviewer 1, writer 1",
		Agents("planning").Count() == 2 && Agents("analyst").Count() == 2 && Agents("researcher").Count() == 2 && Agents("reviewer").Count() == 1 && Agents("writer").Count() == 1,
		$"planning={Agents("planning").Count()} analyst={Agents("analyst").Count()} researcher={Agents("researcher").Count()} reviewer={Agents("reviewer").Count()} writer={Agents("writer").Count()}");
	Check("span name and agent id are stable per cell type", Agents("writer").Single().DisplayName == "invoke_agent writer(writer)" && Tag(Agents("writer").Single(), "gen_ai.agent.id") == "writer" && Tag(Agents("planning").First(), "gen_ai.agent.id") == "planning");
	Check("every cell span has honeycomb.cell.id, depth and goal id", sp.Where(a => a.DisplayName.StartsWith("invoke_agent")).All(a => Tag(a, "honeycomb.cell.id").Length > 0 && Tag(a, "honeycomb.cell.depth").Length > 0 && Tag(a, "honeycomb.goal.id").Length > 0));

	var p0 = Agents("planning").Single(a => Tag(a, "honeycomb.cell.depth") == "0");
	var p1 = Agents("planning").Single(a => Tag(a, "honeycomb.cell.depth") == "1");
	Check("a planning cell's span is the parent of its children's spans", Agents("writer").Single().ParentId == p0.Id && Agents("researcher").All(a => a.ParentId == p1.Id) && p1.ParentId == p0.Id, "writer->P0, researcher->P1, P1->P0");
	Check("the root span is the parent of the root planning cell", p0.ParentId == root1.Id);
	Check("a plan span exists for each planning cell", sp.Count(a => a.DisplayName == "plan planning") == 2 && sp.Where(a => a.DisplayName == "plan planning").All(a => a.GetTagItem("gen_ai.operation.name")?.ToString() == "plan"));

	var analysts = Agents("analyst").OrderBy(a => a.StartTimeUtc).ToList();
	var respun = analysts.Single(a => Tag(a, "honeycomb.respin.attempt") == "1");
	Check("a re-spun cell links to the attempt it replaces", respun.Links.Count() == 1 && analysts.Any(a => a.SpanId == respun.Links.Single().Context.SpanId && a != respun));
	Check("re-spin: analyst re-spun once, then refused by the cell counter", Events("respin.decided") == 2 && Events("respin.refused") == 1, $"decided={Events("respin.decided")} (analyst 1 + researcher 1) refused={Events("respin.refused")}");
	Check("events: judge.asked 2, depth.capped 1, result.invalid 1, checkpoint.raised 1, routing.selected 5, goal.folded 5",
		Events("judge.asked") == 2 && Events("depth.capped") == 1 && Events("result.invalid") == 1 && Events("checkpoint.raised") == 1 && Events("routing.selected") == 5 && Events("goal.folded") == 5,
		$"judge={Events("judge.asked")} cap={Events("depth.capped")} invalid={Events("result.invalid")} chk={Events("checkpoint.raised")} routing={Events("routing.selected")} folded={Events("goal.folded")}");
	Check("goal.folded carries the goal size in tokens", sp.SelectMany(a => a.Events).Where(e => e.Name == "goal.folded").All(e => e.Tags.Any(t => t.Key == "goal_tokens" && Convert.ToInt32(t.Value) > 0)));
	Check("no span ended in error (an invalid reply was recovered by a re-ask)", sp.All(a => a.Status != ActivityStatusCode.Error));
	Check("the writer cell's outcome is checkpointed", Tag(Agents("writer").Single(), "honeycomb.cell.outcome") == "checkpointed");

	List<(string Name, long Value, string Tags)> ms; lock (gate) ms = measurements.ToList();
	Check("honeycomb.respin.count recorded once per scope per re-spin", ms.Count(m => m.Name == "honeycomb.respin.count") == 6 && ms.Where(m => m.Name == "honeycomb.respin.count").Select(m => m.Tags.Split(',').First(t => t.StartsWith("honeycomb.respin.scope"))).Distinct().Count() == 3, $"{ms.Count(m => m.Name == "honeycomb.respin.count")} records");
	Check("honeycomb.respin.refused.count = 1, checkpoint.raised.count = 1", ms.Count(m => m.Name == "honeycomb.respin.refused.count") == 1 && ms.Count(m => m.Name == "honeycomb.checkpoint.raised.count") == 1);

	var store = c1.GetRequiredService<SnapshotStore>();
	var snap = store.Load();
	Check("a snapshot was written with the whole tree", snap is not null && snap.Tree.Count == 8, $"{snap?.Tree.Count} nodes");
	var wnode = snap!.Tree.Single(n => n.CellType == "writer");
	Check("the snapshot joins to the trace by honeycomb.cell.id and holds the span context", wnode.CellId.Value.ToString() == Tag(Agents("writer").Single(), "honeycomb.cell.id") && ActivityContext.TryParse(wnode.Traceparent, wnode.Tracestate, out var wctx) && wctx.SpanId == Agents("writer").Single().SpanId);
	Check("the snapshot holds the human's question and the counters", wnode.HumanQuestion == "Which tone should it use?" && snap.Counters.Count == 5, $"{snap.Counters.Count} counters (cell x2, planner x2, hive x1)");

	// ------------------------------- resume in a fresh container -------------------------------
	Console.WriteLine("=== 5b. Resume in a fresh container, after two hours");
	var callsBefore = new Dictionary<string, int>(models.Calls);
	Reset();
	time.Advance(TimeSpan.FromHours(2));
	models.Script("writer", Ans);
	using var c2 = Container(models, fs, time, new HiveOptions(CellTotal: 1));
	var result2 = await c2.GetRequiredService<Hive>().ResumeAsync("formal tone", CancellationToken.None);

	List<Activity> sp2; lock (gate) { sp2 = stopped.ToList(); Check("every span started after resume was stopped", started.Count == stopped.Count); }
	var root2 = sp2.Single(a => a.DisplayName == "invoke_workflow hive");
	Check("resume completes", result2.Status == "answered", result2.Status);
	Check("resume is a new trace", root2.TraceId != traceId1);
	Check("the resumed root keeps the run id", Tag(root2, "honeycomb.run.id") == Tag(root1, "honeycomb.run.id"));
	Check("the resumed root links to the first trace's root and to the checkpointed cell", root2.Links.Count() == 2 && root2.Links.All(l => l.Context.TraceId == traceId1) && root2.Links.Any(l => l.Context.SpanId == root1.SpanId) && root2.Links.Any(l => l.Context.SpanId == Agents("writer").Single().SpanId));
	var w2 = sp2.Single(a => a.DisplayName == "invoke_agent writer(writer)");
	Check("the resumed cell keeps its durable cell id", Tag(w2, "honeycomb.cell.id") == wnode.CellId.Value.ToString());
	Check("the resumed cell links to the span of the checkpointed attempt", w2.Links.Count() == 1 && w2.Links.Single().Context.SpanId == Agents("writer").Single().SpanId && w2.Links.Single().Context.TraceId == traceId1);
	Check("only the checkpointed path ran: no analyst, researcher or reviewer spans", !sp2.Any(a => a.DisplayName.StartsWith("invoke_agent analyst") || a.DisplayName.StartsWith("invoke_agent researcher") || a.DisplayName.StartsWith("invoke_agent reviewer")));
	Check("model calls: only the writer was called again", models.Calls["writer"] == callsBefore["writer"] + 1 && new[] { "analyst", "researcher", "reviewer", "judge", "planner:0", "planner:1" }.All(k => models.Calls[k] == callsBefore[k]), $"writer {callsBefore["writer"]}->{models.Calls["writer"]}");
	var waited = root2.Events.Single(e => e.Name == "checkpoint.cleared").Tags.Single(t => t.Key == "waited_seconds").Value;
	Check("checkpoint.cleared records the wait", Math.Abs(Convert.ToDouble(waited) - 7200) < 1, $"{waited} s");
	Check("the human's answer was folded into the writer's goal", c2.GetRequiredService<RunState>().Nodes.Values.Single(n => n.CellType == "writer").Goal.Contains("human answer: formal tone"));
	Check("the root planner's result includes the earlier answers without re-running them", result2.Answer is not null && result2.Answer.Contains("analyst") && result2.Answer.Contains("researcher"), result2.Answer?.Length > 90 ? result2.Answer[..90] + "..." : result2.Answer);
}

// =============================== 6. cancellation ===============================
if (Run("cancel"))
{
	Console.WriteLine("=== 6. Cancellation flows from the hive to every cell");
	Reset();
	var models = NewModels();
	models.Script("planner:0", """[{"type":"writer","goal":"w"},{"type":"analyst","goal":"a"},{"type":"reviewer","goal":"r"}]""");
	models.Script("writer", Ans); models.Script("analyst", Ans); models.Script("reviewer", Ans);
	models.Delays["writer"] = TimeSpan.FromSeconds(10); models.Delays["analyst"] = TimeSpan.FromSeconds(10);
	using var c = Container(models, new MockFileSystem(), TimeProvider.System);
	using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
	var sw = Stopwatch.StartNew();
	var r = await c.GetRequiredService<Hive>().RunAsync("cancel me", cts.Token);
	sw.Stop();
	List<Activity> sp; lock (gate) { sp = stopped.ToList(); Check("every span that started was stopped", started.Count == stopped.Count, $"{started.Count} started, {stopped.Count} stopped"); }
	Check("the run ended quickly after cancellation, not after the 10 s model delay", sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.Elapsed.TotalSeconds:0.0}s");
	Check("the hive result is cancelled", r.Status == "cancelled", r.Status);
	var cancelled = sp.Where(a => a.GetTagItem("honeycomb.cell.outcome")?.ToString() == "cancelled").Select(a => a.DisplayName).ToList();
	Check("the cancelled cells carry outcome=cancelled", cancelled.Count >= 2, string.Join(" | ", cancelled));
	Console.WriteLine("    span status of each agent span:");
	foreach (var a in sp.Where(a => a.DisplayName.StartsWith("invoke_")).OrderBy(a => a.StartTimeUtc))
		Console.WriteLine($"      {a.DisplayName,-40} status={a.Status} outcome={a.GetTagItem("honeycomb.cell.outcome")} error.type={a.GetTagItem("error.type")}");
	Check("a cancelled cell does not end in Error status", sp.Where(a => a.DisplayName.StartsWith("invoke_agent") && a.GetTagItem("honeycomb.cell.outcome")?.ToString() == "cancelled").All(a => a.Status != ActivityStatusCode.Error));
}

Console.WriteLine();
Console.WriteLine($"=== {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// ---------- the ticket generator from the model test, so token counts can be compared ----------
static class Tickets
{
	public const string Instructions =
		"You are an analysis cell. Reply with ONLY a JSON object with the fields: status (answered or cannot_answer), reason (string or null), " +
		"findings (array of {title, severity (low|medium|high), evidence}), nextQuestions (array of strings), confidence (number 0 to 1), summary (string). " +
		"Findings must be about authentication and session problems only, with evidence quoted from the ticket. " +
		"If the ticket contains nothing about authentication or sessions, use status cannot_answer with a reason and an empty findings array. Never invent evidence.";

	private static readonly string[] Facts =
	[
		"The login endpoint /api/v2/login returns HTTP 500 when the X-Tenant header is missing.",
		"Session tokens are stored in the browser's localStorage and are not cleared on logout.",
		"Password reset links remain valid for 30 days and can be reused.",
		"The admin role check in AuditController is performed in the client and not on the server.",
		"Failed login attempts are not rate limited for service accounts.",
	];

	public static string Ticket(bool hasAuthContent, int paragraphs, int seed)
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"TICKET PLN-{4000 + seed}");
		sb.AppendLine(hasAuthContent ? "Summary: Customers report problems signing in and staying signed in." : "Summary: Change the colour of the secondary button on the settings page.");
		sb.AppendLine("Description:");
		if (hasAuthContent) foreach (var f in Facts.Take(3 + seed % 3)) sb.AppendLine("- " + f);
		else sb.AppendLine("- The secondary button should use the theme's accent colour, not grey.\n- The change applies to the settings page only.");
		for (var i = 0; i < paragraphs; i++)
		{
			sb.AppendLine($"Note {seed}-{i}: the weekly planning meeting on day {(i % 5) + 1} covered capacity for sprint {20 + i}, " +
				$"with {3 + i} engineers available and {100 + i * 7} story points committed. The retrospective action number {i + 1} was to review " +
				$"the build pipeline duration, currently {12 + i} minutes, and to agree on a target of {8 + (i % 3)} minutes.");
		}
		return sb.ToString();
	}
}
