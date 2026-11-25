using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

// Spike: verify the claims in drafts/telemetry.md and drafts/stack.md against the real packages.
// No real model is used. A scripted IChatClient stands in for it.

var activities = new List<Activity>();
var sources = new SortedSet<string>();
var measurements = new List<string>();

using var activityListener = new ActivityListener
{
	ShouldListenTo = source => { lock (sources) { sources.Add(source.Name); } return true; },
	Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
	ActivityStopped = a => { lock (activities) { activities.Add(a); } },
};
ActivitySource.AddActivityListener(activityListener);

using var meterListener = new MeterListener();
meterListener.InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument);
void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) where T : struct
{
	var t = string.Join(",", tags.ToArray().Select(kv => $"{kv.Key}={kv.Value}"));
	lock (measurements) { measurements.Add($"{instrument.Meter.Name} | {instrument.Name} ({instrument.Unit}) = {value} | {t}"); }
}
meterListener.SetMeasurementEventCallback<double>(Record);
meterListener.SetMeasurementEventCallback<long>(Record);
meterListener.SetMeasurementEventCallback<int>(Record);
meterListener.Start();

var hive = new ActivitySource("Honeycomb");

// Modes: no argument runs the telemetry and workflow checks. "nested", "sharedid" and "json" run one extra check each.
if (args.Length > 0)
{
	switch (args[0])
	{
		case "nested": await NestedResumeSpike.RunAsync(hive, PrintTree); break;
		case "sharedid": await SharedIdSpike.RunAsync(activities); break;
		case "json": await JsonSpike.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 3); break;
		case "validate": await ValidationSpike.RunAsync(args.Length > 1 ? args[1] : "", args.Length > 2 ? int.Parse(args[2]) : 5); break;
			case "json5": await JsonSpike5.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 4); break;
		case "foldraw": await JsonSpike6.RawAsync(); break;
		case "fold": await JsonSpike6.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 4); break;
		case "json3": await JsonSpike3.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 12); break;
		case "json4": await JsonSpike4.RunAsync(); break;
		case "json2": await JsonSpike2.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 6); break;
		default: Console.WriteLine("unknown mode"); break;
	}
	return;
}

// ---------- helpers ----------
IChatClient Fake(string model, Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse> script) => new FakeChatClient(model, script);

AIAgent MakeCell(string type, IChatClient inner, IList<AITool>? tools = null, bool sensitive = false, string? source = "Honeycomb", string? id = null, bool wrapChat = true, bool wrapAgent = true)
{
	var chat = wrapChat ? inner.AsBuilder().UseOpenTelemetry(sourceName: source, configure: c => c.EnableSensitiveData = sensitive).Build() : inner;
	var agent = new ChatClientAgent(chat, new ChatClientAgentOptions
	{
		Id = id ?? type,
		Name = type,
		ChatOptions = new ChatOptions { Instructions = $"You are the {type} cell.", Tools = tools },
	});
	return wrapAgent ? agent.AsBuilder().UseOpenTelemetry(sourceName: source, configure: a => a.EnableSensitiveData = sensitive).Build() : agent;
}

void PrintMetricSummary(string title)
{
	Console.WriteLine($"--- metrics: {title}");
	List<string> snapshot;
	lock (measurements) { snapshot = measurements.ToList(); measurements.Clear(); }
	foreach (var g in snapshot.GroupBy(m =>
	{
		var parts = m.Split(" | ");
		var tokenType = parts[2].Split(',').FirstOrDefault(t => t.StartsWith("gen_ai.token.type=")) ?? "";
		var op = parts[2].Split(',').FirstOrDefault(t => t.StartsWith("gen_ai.operation.name=")) ?? "";
		return $"{parts[1].Split(" = ")[0]} {tokenType} {op}";
	}))
	{
		var sum = g.Sum(m => double.Parse(m.Split(" | ")[1].Split(" = ")[1], System.Globalization.CultureInfo.InvariantCulture));
		Console.WriteLine($"  {g.Key}: records={g.Count()} sum={sum:0.####}");
	}
}

ChatResponse Text(string text, int inTokens = 120, int outTokens = 30) =>
	new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "fake-model", Usage = new UsageDetails { InputTokenCount = inTokens, OutputTokenCount = outTokens, TotalTokenCount = inTokens + outTokens } };

void PrintTree(string title)
{
	Console.WriteLine();
	Console.WriteLine($"=== {title}");
	List<Activity> snapshot;
	lock (activities) { snapshot = activities.ToList(); }
	var ids = snapshot.Select(a => a.Id).ToHashSet();
	void Walk(Activity a, int depth)
	{
		var pad = new string(' ', depth * 2);
		var tags = a.TagObjects.Where(t => t.Key.StartsWith("gen_ai.") || t.Key.StartsWith("honeycomb.") || t.Key == "error.type")
			.Where(t => !t.Key.StartsWith("gen_ai.request.") || t.Key == "gen_ai.request.model")
			.Select(t => $"{t.Key}={Trunc(t.Value?.ToString())}");
		Console.WriteLine($"{pad}{a.DisplayName}  [{a.Kind}] src={a.Source.Name} status={a.Status}");
		foreach (var tag in tags) Console.WriteLine($"{pad}    {tag}");
		foreach (var ev in a.Events) Console.WriteLine($"{pad}    event: {ev.Name}");
		foreach (var child in snapshot.Where(c => c.ParentId == a.Id).OrderBy(c => c.StartTimeUtc)) Walk(child, depth + 1);
	}
	foreach (var root in snapshot.Where(a => a.ParentId is null || !ids.Contains(a.ParentId)).OrderBy(a => a.StartTimeUtc)) Walk(root, 0);
	lock (activities) { activities.Clear(); }
}

static string Trunc(string? s) => s is null ? "(null)" : s.Length > 70 ? s[..70] + "..." : s;

// ---------- Test 1: telemetry shape, with a neighbour consulted as a tool ----------
var codeSearch = MakeCell("code-search", Fake("fake-model", (_, _) => Text("Found 3 call sites.", 80, 12)));

var consult = AIFunctionFactory.Create(
	async (string question) => (await codeSearch.RunAsync(question)).Text,
	new AIFunctionFactoryOptions { Name = "consult_code_search", Description = "Ask the code-search neighbour." });

var jiraAnalysis = MakeCell(
	"jira-analysis",
	Fake("fake-model", (messages, _) =>
		messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
			? Text("Analysis complete.", 300, 40)
			: new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "consult_code_search", new Dictionary<string, object?> { ["question"] = "where is X used?" })]))
			{ ModelId = "fake-model", Usage = new UsageDetails { InputTokenCount = 150, OutputTokenCount = 20, TotalTokenCount = 170 } }),
	tools: [consult]);

using (var root = hive.StartActivity("invoke_workflow hive", ActivityKind.Internal))
{
	root?.SetTag("gen_ai.operation.name", "invoke_workflow");
	root?.SetTag("honeycomb.run.id", "run-1");
	using (var plan = hive.StartActivity("plan planning", ActivityKind.Internal))
	{
		plan?.SetTag("gen_ai.operation.name", "plan");
		var response = await jiraAnalysis.RunAsync("Analyse JIRA-1");
		Console.WriteLine($"Test 1 response: {response.Text}");
	}
}
PrintTree("Test 1: spans (sensitive data OFF)");

Console.WriteLine();
Console.WriteLine("=== Test 1: metrics (one tool-calling cell, one neighbour; real model calls: jira 150+300 input, neighbour 80 input)");
PrintMetricSummary("both wrappers");

// ---------- Test 2: sensitive data ON ----------
var sensitiveCell = MakeCell("sensitive-cell", Fake("fake-model", (_, _) => Text("secret reply")), sensitive: true);
await sensitiveCell.RunAsync("secret question");
PrintTree("Test 2: spans (sensitive data ON)");

// ---------- Test 3: default source names when none is passed ----------
var defaultSrc = MakeCell("default-source-cell", Fake("fake-model", (_, _) => Text("ok")), source: null);
await defaultSrc.RunAsync("hi");
PrintTree("Test 3: spans with no source name passed");

// ---------- Test 4: two cells of one type with the same Id in one MAF workflow ----------
Console.WriteLine();
Console.WriteLine("=== Test 4: two cells, same type and Id, one concurrent MAF workflow");
try
{
	var a = MakeCell("jira-analysis", Fake("fake-model", (_, _) => Text("a")));
	var b = MakeCell("jira-analysis", Fake("fake-model", (_, _) => Text("b")));
	var wf = AgentWorkflowBuilder.BuildConcurrent([a, b]);
	Console.WriteLine("  built without error");
}
catch (Exception ex)
{
	Console.WriteLine($"  FAILED: {ex.GetType().Name}: {Trunc(ex.Message)}");
}

Console.WriteLine("=== Test 4b: same two cells but unique Ids");
try
{
	var a = MakeCell("jira-analysis", Fake("fake-model", (_, _) => Text("a")), id: "jira-analysis-1");
	var b = MakeCell("jira-analysis", Fake("fake-model", (_, _) => Text("b")), id: "jira-analysis-2");
	var wf = AgentWorkflowBuilder.BuildConcurrent([a, b]);
	Console.WriteLine("  built without error");
	await using var run = await InProcessExecution.RunStreamingAsync(wf, new List<ChatMessage> { new(ChatRole.User, "go") });
	await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
	await foreach (var evt in run.WatchStreamAsync())
	{
		if (evt is WorkflowOutputEvent) { Console.WriteLine("  workflow produced output"); break; }
	}
}
catch (Exception ex)
{
	Console.WriteLine($"  FAILED: {ex.GetType().Name}: {Trunc(ex.Message)}");
}
PrintTree("Test 4b: spans from a MAF workflow");

// ---------- Test 5: a workflow executor builds and runs a nested workflow in its handler ----------
Console.WriteLine();
Console.WriteLine("=== Test 5: nested workflow built and run inside an executor handler (workflow telemetry enabled on the hive's source, in-memory checkpoints)");
try
{
	var outerExecutor = new NestedRunner("outer");
	var outer = new WorkflowBuilder(outerExecutor).WithOutputFrom(outerExecutor).WithOpenTelemetry(activitySource: hive).Build();
	await using var run = await InProcessExecution.RunAsync(outer, "start", CheckpointManager.CreateInMemory());
	Console.WriteLine($"  status: {await run.GetStatusAsync()}");
	foreach (var evt in run.OutgoingEvents)
	{
		Console.WriteLine($"  event: {evt.GetType().Name} {(evt is WorkflowOutputEvent o ? Trunc(o.Data?.ToString()) : "")}");
	}
	Console.WriteLine($"  checkpoints: {run.Checkpoints.Count}");
}
catch (Exception ex)
{
	Console.WriteLine($"  FAILED: {ex.GetType().Name}: {Trunc(ex.Message)}");
}
Console.WriteLine($"  inner workflow events seen by the outer handler: {string.Join(", ", NestedRunner.InnerEvents)}");
PrintTree("Test 5: spans from the nested workflow");

// ---------- Test 6: do the agent and chat-client wrappers double-count? ----------
Console.WriteLine();
Console.WriteLine("=== Test 6: one cell, one model call (120 input, 30 output tokens), wrappers varied");
foreach (var (label, wrapChat, wrapAgent) in new[] { ("agent + chat", true, true), ("agent only", false, true), ("chat only", true, false) })
{
	lock (measurements) { measurements.Clear(); }
	lock (activities) { activities.Clear(); }
	var cell = MakeCell("variant-cell", Fake("fake-model", (_, _) => Text("ok")), wrapChat: wrapChat, wrapAgent: wrapAgent);
	await cell.RunAsync("hi");
	lock (measurements) { measurements.RemoveAll(m => !m.Contains("gen_ai.")); }
	List<string> raw; lock (measurements) { raw = measurements.Where(m => m.Contains("token.usage") && m.Contains("token.type=input")).ToList(); }
	Console.WriteLine($"--- {label}: input-token records = {string.Join(", ", raw.Select(r => r.Split(" | ")[1].Split(" = ")[1]))}");
	PrintMetricSummary(label);
	List<Activity> snap; lock (activities) { snap = activities.ToList(); activities.Clear(); }
	Console.WriteLine($"  spans: {string.Join(" | ", snap.Select(a => a.DisplayName))}");
}

// ---------- Test 7: a real chat against Docker Model Runner through Microsoft.Extensions.AI.OpenAI ----------
Console.WriteLine();
Console.WriteLine("=== Test 7: real model through Docker Model Runner");
try
{
	using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
	var openAi = new OpenAI.OpenAIClient(
		new System.ClientModel.ApiKeyCredential("not-needed"),
		new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
	IChatClient local = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();
	lock (measurements) { measurements.Clear(); }
	var cell = MakeCell("local-cell", local);
	var options = new ChatClientAgentRunOptions(new ChatOptions
	{
		ResponseFormat = ChatResponseFormat.ForJsonSchema<SpikeResult>(),
		MaxOutputTokens = 120,
	});
	var response = await cell.RunAsync("Reply with a status of answered and an answer of the number 4.", options: options, cancellationToken: cts.Token);
	Console.WriteLine($"  reply: {Trunc(response.Text)}");
	Console.WriteLine($"  parsed as SpikeResult: {System.Text.Json.JsonSerializer.Deserialize<SpikeResult>(response.Text, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })}");
}
catch (Exception ex)
{
	Console.WriteLine($"  FAILED: {ex.GetType().Name}: {Trunc(ex.Message)}");
}
PrintTree("Test 7: spans from a real model call");
PrintMetricSummary("real model");

Console.WriteLine();
Console.WriteLine("=== Activity sources seen");
lock (sources) { foreach (var s in sources) Console.WriteLine("  " + s); }

sealed class NestedRunner(string id) : Executor<string, string>(id)
{
	public static readonly List<string> InnerEvents = [];

	public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
	{
		var inner = new InnerWorkflowFactory().Build();
		await using var run = await InProcessExecution.RunAsync(inner, message, cancellationToken: cancellationToken);
		foreach (var e in run.OutgoingEvents) InnerEvents.Add(e.GetType().Name + (e is WorkflowOutputEvent o2 ? $"({o2.Data})" : "") + (e is ExecutorFailedEvent or WorkflowErrorEvent ? $" [{e.Data}]" : ""));
		var output = run.OutgoingEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data?.ToString()).FirstOrDefault();
		await context.YieldOutputAsync($"inner said: {output}", cancellationToken);
		return $"inner said: {output}";
	}
}

sealed record SpikeResult(string Status, string Answer);

sealed class InnerExecutor() : Executor<string, string>("inner")
{
	public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
	{
		await context.YieldOutputAsync($"echo {message}", cancellationToken);
		return $"echo {message}";
	}
}

sealed class InnerWorkflowFactory
{
	public Workflow Build()
	{
		var inner = new InnerExecutor();
		return new WorkflowBuilder(inner).WithOutputFrom(inner).Build();
	}
}

sealed class FakeChatClient(string model, Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse> script) : IChatClient
{
	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
		=> Task.FromResult(script(messages, options));

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		foreach (var update in script(messages, options).ToChatResponseUpdates()) yield return update;
		await Task.CompletedTask;
	}

	public object? GetService(Type serviceType, object? serviceKey = null)
		=> serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake-provider", new Uri("http://localhost/"), model) : null;

	public void Dispose() { }
}
