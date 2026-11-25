using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;

// ---------- run state and checkpoint snapshot ----------
public sealed class NodeState
{
	public CellId CellId; public CellId? ParentCellId; public string CellType = ""; public int Depth; public string Goal = "";
	public string State = "running"; public string? Result; public int RespinAttempt;
	public string? Traceparent; public string? Tracestate; public string? HumanQuestion; public string? HumanAnswer;
	public bool Superseded;
}

public sealed class RunState
{
	public RunId RunId; public string Question = "";
	public readonly ConcurrentDictionary<Guid, NodeState> Nodes = new();
	public string? RootTraceparent; public string? RootTracestate;
}

public sealed record NodeSnap(CellId CellId, CellId? ParentCellId, string CellType, int Depth, string Goal, string State, string? Result, int RespinAttempt, string? Traceparent, string? Tracestate, string? HumanQuestion, string? HumanAnswer, bool Superseded);
public sealed record Snapshot(int SchemaVersion, RunId RunId, string Question, DateTimeOffset WrittenAt, string? RootTraceparent, string? RootTracestate, List<NodeSnap> Tree, List<CounterState> Counters);

public sealed class SnapshotStore(IFileSystem fs, string dir)
{
	public string Path => fs.Path.Combine(dir, "checkpoint.json");

	// Write to a temporary file, then move it over the old snapshot, so a half-written file never replaces a good one.
	public static int RetriesUsed;
	private readonly object saveGate = new();

	public void Save(Snapshot s)
	{
		lock (saveGate)
		{
			fs.Directory.CreateDirectory(dir);
			var tmp = Path + ".tmp";
			fs.File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json.Options));
			// On Windows the destination can be briefly locked right after it was written, so the move is retried a few times.
			for (var attempt = 1; ; attempt++)
			{
				try { fs.File.Move(tmp, Path, overwrite: true); return; }
				catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 6)
				{
					Interlocked.Increment(ref RetriesUsed);
					Thread.Sleep(20 * attempt);
				}
			}
		}
	}

	// A missing file is "no checkpoint". A corrupt file is an error, never "no checkpoint".
	public Snapshot? Load()
	{
		if (!fs.File.Exists(Path)) return null;
		try { return JsonSerializer.Deserialize<Snapshot>(fs.File.ReadAllText(Path), Json.Options) ?? throw new InvalidDataException("snapshot was null"); }
		catch (JsonException ex) { throw new InvalidDataException($"checkpoint at {Path} is corrupt", ex); }
	}
}

// ---------- the hive's per-cell layer, inside MAF's OpenTelemetryAgent ----------
public sealed class HoneycombCellAgent(AIAgent inner, ICellContext ctx, RunState run, HiveOptions o) : DelegatingAIAgent(inner)
{
	public CellResult? Result { get; private set; }

	public static bool TryParse(string text, out CellResult result)
	{
		result = null!;
		try
		{
			var r = JsonSerializer.Deserialize<CellResult>(text, Json.Options);
			if (r is null || r.Status is not ("answered" or "cannot_answer" or "checkpointed")) return false;
			result = r; return true;
		}
		catch (JsonException) { return false; }
	}

	protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		var act = Activity.Current;
		var node = run.Nodes[ctx.CellId.Value];
		node.Traceparent = act?.Id; node.Tracestate = act?.TraceStateString;
		act?.SetTag("honeycomb.cell.id", ctx.CellId.Value.ToString());
		act?.SetTag("honeycomb.cell.depth", ctx.Depth);
		act?.SetTag("honeycomb.goal.id", ctx.Goal.Id.Value.ToString());
		if (ctx.PreviousAttempt is { } prev)
		{
			act?.AddLink(new ActivityLink(prev));
			if (ctx.RespinAttempt > 0) act?.SetTag("honeycomb.respin.attempt", ctx.RespinAttempt);
		}

		AgentResponse response = null!;
		try
		{
			for (var attempt = 0; attempt <= o.ReAsk; attempt++)
			{
				response = await InnerAgent.RunAsync(messages, session, options, cancellationToken);
				if (TryParse(response.Text, out var r)) { Result = r; break; }
				Telemetry.Event(act, "result.invalid", ("attempt", attempt + 1));
				messages = messages.Append(new ChatMessage(ChatRole.User, "Reply with valid JSON only."));
			}
		}
		catch (OperationCanceledException)
		{
			// A cancelled cell returns a cancelled result and does not throw, so the telemetry wrapper does not mark the span as an error.
			Result = new CellResult("cancelled", null, "cancelled", null);
			act?.SetTag("honeycomb.cell.outcome", "cancelled");
			return new AgentResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(Result, Json.Options)));
		}

		if (Result is null)
		{
			act?.SetStatus(ActivityStatusCode.Error, "result did not match the schema");
			act?.SetTag("error.type", "result_invalid");
			Result = new CellResult("error", null, "invalid result", null);
		}
		else
		{
			act?.SetTag("honeycomb.cell.outcome", Result.Status);
			if (Result.Status == "checkpointed")
			{
				node.HumanQuestion = Result.Question;
				Telemetry.Event(act, "checkpoint.raised", ("reason", Result.Reason));
				Telemetry.CheckpointRaisedCount.Add(1, new KeyValuePair<string, object?>("gen_ai.agent.name", node.CellType), new KeyValuePair<string, object?>("honeycomb.checkpoint.reason", "needs_human"));
			}
		}
		return response;
	}
}

// ---------- cells ----------
public abstract class SpecialistCell(ICellContext ctx, RunState run, Models models, HiveOptions o) : ICell
{
	public abstract string Type { get; }

	public async Task<CellResult> RunAsync(CancellationToken ct)
	{
		var node = new NodeState { CellId = ctx.CellId, ParentCellId = ctx.ParentCellId, CellType = Type, Depth = ctx.Depth, Goal = ctx.Goal.Text, RespinAttempt = ctx.RespinAttempt };
		run.Nodes[ctx.CellId.Value] = node;

		var chat = models.Client(Type, model: "scripted-local", provider: "scripted").AsBuilder().UseOpenTelemetry(sourceName: Telemetry.Name).Build();
		var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { Id = Type, Name = Type, ChatOptions = new ChatOptions { Instructions = $"You are the {Type} cell." } });
		var cell = new HoneycombCellAgent(agent, ctx, run, o);
		var wrapped = new OpenTelemetryAgent(cell, sourceName: Telemetry.Name);
		try
		{
			await wrapped.RunAsync(ctx.Goal.Text, cancellationToken: ct);
		}
		catch (OperationCanceledException)
		{
			node.State = "cancelled";
			return new CellResult("cancelled", null, "cancelled", null);
		}
		var r = cell.Result!;
		node.State = r.Status; node.Result = JsonSerializer.Serialize(r, Json.Options);
		return r;
	}
}

public sealed class AnalystCell(ICellContext c, RunState r, Models m, HiveOptions o) : SpecialistCell(c, r, m, o) { public override string Type => "analyst"; }
public sealed class ResearcherCell(ICellContext c, RunState r, Models m, HiveOptions o) : SpecialistCell(c, r, m, o) { public override string Type => "researcher"; }
public sealed class WriterCell(ICellContext c, RunState r, Models m, HiveOptions o) : SpecialistCell(c, r, m, o) { public override string Type => "writer"; }
public sealed class ReviewerCell(ICellContext c, RunState r, Models m, HiveOptions o) : SpecialistCell(c, r, m, o) { public override string Type => "reviewer"; }

public sealed record SubGoal(string Type, string Goal);

// A planning cell is an AIAgent. Its loop runs inside RunCoreAsync, so MAF's OpenTelemetryAgent makes it the parent of its children's spans.
public sealed class PlanningAgent(ICellContext ctx, RunState run, Models models, HiveOptions o, CounterRegistry counters, IServiceProvider sp, Tokenizer tokenizer, string? humanAnswer) : AIAgent
{
	protected override string? IdCore => "planning";
	public override string? Name => "planning";
	public CellResult? Result { get; private set; }

	protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		try { return await RunLoopAsync(cancellationToken); }
		catch (OperationCanceledException)
		{
			Result = new CellResult("cancelled", null, "cancelled", null);
			Activity.Current?.SetTag("honeycomb.cell.outcome", "cancelled");
			return new AgentResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(Result, Json.Options)));
		}
	}

	private async Task<AgentResponse> RunLoopAsync(CancellationToken cancellationToken)
	{
		var act = Activity.Current;
		act?.SetTag("honeycomb.cell.id", ctx.CellId.Value.ToString());
		act?.SetTag("honeycomb.cell.depth", ctx.Depth);
		act?.SetTag("honeycomb.goal.id", ctx.Goal.Id.Value.ToString());
		var self = run.Nodes[ctx.CellId.Value];
		self.Traceparent = act?.Id; self.Tracestate = act?.TraceStateString;

		var results = new List<(SubGoal Sub, CellResult Res, NodeState Node)>();
		var ct = cancellationToken;

		// On resume the sub-goals come from the saved tree, and answered cells are not run again.
		var saved = run.Nodes.Values.Where(n => n.ParentCellId == ctx.CellId && !n.Superseded).OrderBy(n => n.Goal).ToList();
		if (saved.Count > 0)
		{
			var tasks = saved.Select(async n =>
			{
				// Only a cell that was waiting on a human runs again. Answered cells, and cells that ended unanswered, keep their saved result.
				if (n.State != "checkpointed")
				{
					return (new SubGoal(n.CellType, n.Goal), JsonSerializer.Deserialize<CellResult>(n.Result!, Json.Options)!, n);
				}
				var goalText = n.Goal + (n.HumanAnswer is null ? "" : $" | human answer: {n.HumanAnswer}");
				ActivityContext? previous = ActivityContext.TryParse(n.Traceparent, n.Tracestate, out var pc) ? pc : null;
				var (res, node) = await RunChildAsync(n.CellType, goalText, n.RespinAttempt, previous, n.CellId, ct);
				return (new SubGoal(n.CellType, goalText), res, node);
			}).ToList();
			results.AddRange(await Task.WhenAll(tasks));
		}
		else
		{
			List<SubGoal> subs;
			using (var plan = Telemetry.Source.StartActivity("plan planning", ActivityKind.Internal))
			{
				plan?.SetTag("gen_ai.operation.name", "plan");
				var chat = models.Client($"planner:{ctx.Depth}", model: "scripted-local").AsBuilder().UseOpenTelemetry(sourceName: Telemetry.Name).Build();
				var reply = await chat.GetResponseAsync(ctx.Goal.Text, cancellationToken: ct);
				subs = JsonSerializer.Deserialize<List<SubGoal>>(reply.Text, Json.Options)!;
				plan?.SetTag("honeycomb.goal.count", subs.Count);
				plan?.SetTag("honeycomb.goal.parallel", true);
			}
			results.AddRange(await Task.WhenAll(subs.Select(s => BranchAsync(s, ct))));
		}

		// Fold the results into the goal. The goal grows by the answers, not by whole transcripts.
		var folded = ctx.Goal.Text;
		foreach (var (sub, res, node) in results)
		{
			folded += $" | {node.CellType}: {res.Status} {res.Answer}";
			Telemetry.Event(act, "goal.folded", ("source_cell_type", node.CellType), ("outcome", res.Status), ("goal_tokens", tokenizer.CountTokens(folded)));
		}

		var anyCheckpoint = results.Any(r => r.Res.Status == "checkpointed");
		var anyCancelled = results.Any(r => r.Res.Status == "cancelled");
		Result = anyCancelled
			? new CellResult("cancelled", null, "a child was cancelled", null)
			: anyCheckpoint
				? new CellResult("checkpointed", null, "a child needs a human", null)
				: new CellResult("answered", folded, null, null);
		act?.SetTag("honeycomb.cell.outcome", Result.Status);
		return new AgentResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(Result, Json.Options)));
	}

	private async Task<(SubGoal, CellResult, NodeState)> BranchAsync(SubGoal sub, CancellationToken ct)
	{
		var act = Activity.Current;
		var type = sub.Type;
		var childDepth = ctx.Depth + 1;

		if (type == "planning")
		{
			if (childDepth >= o.JudgeDepth)
			{
				var judge = models.Client("judge", model: "scripted-local").AsBuilder().UseOpenTelemetry(sourceName: Telemetry.Name).Build();
				var verdict = (await judge.GetResponseAsync($"Should '{sub.Goal}' be decomposed further?", cancellationToken: ct)).Text.Trim();
				Telemetry.Event(act, "judge.asked", ("depth", childDepth), ("decision", verdict));
				if (verdict != "decompose") type = "reviewer";
			}
			if (type == "planning" && childDepth >= o.HardCap)
			{
				Telemetry.Event(act, "depth.capped", ("depth", childDepth), ("limit", o.HardCap));
				type = "reviewer";
			}
		}

		Telemetry.Event(act, "routing.selected", ("provider", "scripted"), ("model", "scripted-local"), ("reason", "small goal"));
		var (res, node) = await RunChildAsync(type, sub.Goal, 0, null, null, ct);
		var attempt = 0;
		while (res.Status == "cannot_answer")
		{
			var d = counters.TryRespin(ctx.CellId, type);
			if (!d.Ok)
			{
				Telemetry.Event(act, "respin.refused", ("scope", d.Scope), ("why", d.Why));
				Telemetry.RespinRefusedCount.Add(1, new KeyValuePair<string, object?>("gen_ai.agent.name", type), new KeyValuePair<string, object?>("honeycomb.respin.scope", d.Scope));
				break;
			}
			attempt++;
			Telemetry.Event(act, "respin.decided", ("attempt", attempt), ("scopes", string.Join(",", d.Scopes)));
			foreach (var scope in d.Scopes)
				Telemetry.RespinCount.Add(1, new KeyValuePair<string, object?>("gen_ai.agent.name", type), new KeyValuePair<string, object?>("honeycomb.respin.scope", scope));

			ActivityContext? prev = null;
			if (ActivityContext.TryParse(node.Traceparent, node.Tracestate, out var parsed)) prev = parsed;
			node.Superseded = true;
			(res, node) = await RunChildAsync(type, sub.Goal + " (a previous cell could not answer)", attempt, prev, null, ct);
		}
		return (sub, res, node);
	}

	private async Task<(CellResult, NodeState)> RunChildAsync(string type, string goal, int attempt, ActivityContext? previous, CellId? reuse, CancellationToken parentCt)
	{
		await using var scope = sp.CreateAsyncScope();
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
		var cellId = reuse ?? CellId.From(Guid.NewGuid());
		var child = scope.ServiceProvider.GetRequiredService<CellContext>();
		child.Init(ctx.RunId, cellId, ctx.CellId, ctx.Depth + 1, new Goal(GoalId.From(Guid.NewGuid()), goal), attempt, previous);
		var cell = scope.ServiceProvider.GetServices<ICell>().Single(c => c.Type == type);
		var res = await cell.RunAsync(cts.Token);
		return (res, run.Nodes[cellId.Value]);
	}

	protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new NoSession());
	protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => new(JsonDocument.Parse("{}").RootElement.Clone());
	protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => new(new NoSession());
	protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

	private sealed class NoSession : AgentSession { }
}

public sealed class PlanningCell(ICellContext ctx, RunState run, Models models, HiveOptions o, CounterRegistry counters, IServiceProvider sp, Tokenizer tokenizer) : ICell
{
	public string Type => "planning";

	public async Task<CellResult> RunAsync(CancellationToken ct)
	{
		var node = new NodeState { CellId = ctx.CellId, ParentCellId = ctx.ParentCellId, CellType = Type, Depth = ctx.Depth, Goal = ctx.Goal.Text, RespinAttempt = ctx.RespinAttempt };
		if (!run.Nodes.ContainsKey(ctx.CellId.Value)) run.Nodes[ctx.CellId.Value] = node; else node = run.Nodes[ctx.CellId.Value];
		node.State = "running";

		var planner = new PlanningAgent(ctx, run, models, o, counters, sp, tokenizer, null);
		var wrapped = new OpenTelemetryAgent(planner, sourceName: Telemetry.Name);
		try { await wrapped.RunAsync(ctx.Goal.Text, cancellationToken: ct); }
		catch (OperationCanceledException) { node.State = "cancelled"; return new CellResult("cancelled", null, "cancelled", null); }
		var r = planner.Result!;
		node.State = r.Status; node.Result = JsonSerializer.Serialize(r, Json.Options);
		return r;
	}
}

// ---------- the hive entry point ----------
public sealed class Hive(IServiceProvider root, SnapshotStore store, CounterRegistry counters, TimeProvider time, RunState run)
{
	public async Task<CellResult> RunAsync(string question, CancellationToken ct)
	{
		run.RunId = RunId.From(Guid.NewGuid()); run.Question = question;
		return await ExecuteAsync(null, ct);
	}

	public async Task<CellResult> ResumeAsync(string humanAnswer, CancellationToken ct)
	{
		var snap = store.Load() ?? throw new InvalidOperationException("no checkpoint to resume");
		run.RunId = snap.RunId; run.Question = snap.Question;
		// The human answers the cells that were waiting, not the planning cells above them.
		var leaf = snap.Tree.Where(n => n.State == "checkpointed" && !snap.Tree.Any(c => c.ParentCellId == n.CellId && c.State == "checkpointed")).Select(n => n.CellId).ToHashSet();
		foreach (var n in snap.Tree)
			run.Nodes[n.CellId.Value] = new NodeState { CellId = n.CellId, ParentCellId = n.ParentCellId, CellType = n.CellType, Depth = n.Depth, Goal = n.Goal, State = n.State, Result = n.Result, RespinAttempt = n.RespinAttempt, Traceparent = n.Traceparent, Tracestate = n.Tracestate, HumanQuestion = n.HumanQuestion, HumanAnswer = leaf.Contains(n.CellId) ? humanAnswer : n.HumanAnswer, Superseded = n.Superseded };
		counters.Restore(snap.Counters);
		return await ExecuteAsync(snap, ct);
	}

	private async Task<CellResult> ExecuteAsync(Snapshot? resumed, CancellationToken ct)
	{
		var tags = new List<KeyValuePair<string, object?>>
		{
			new("gen_ai.operation.name", "invoke_workflow"),
			new("honeycomb.run.id", run.RunId.Value.ToString()),
			new("honeycomb.question.ref", "q-" + Math.Abs(run.Question.GetHashCode())),
		};

		List<ActivityLink>? links = null;
		if (resumed is not null)
		{
			links = [];
			if (ActivityContext.TryParse(resumed.RootTraceparent, resumed.RootTracestate, out var rc)) links.Add(new ActivityLink(rc));
			// Link to the cells that were actually waiting on a human, not to the planning cells above them.
			foreach (var n in resumed.Tree.Where(n => n.State == "checkpointed" && !resumed.Tree.Any(c => c.ParentCellId == n.CellId && c.State == "checkpointed")))
				if (ActivityContext.TryParse(n.Traceparent, n.Tracestate, out var cc)) links.Add(new ActivityLink(cc));
		}

		using var rootActivity = Telemetry.Source.StartActivity("invoke_workflow hive", ActivityKind.Internal, parentContext: default, tags: tags, links: links);
		run.RootTraceparent = rootActivity?.Id; run.RootTracestate = rootActivity?.TraceStateString;
		if (resumed is not null)
			Telemetry.Event(rootActivity, "checkpoint.cleared", ("waited_seconds", (time.GetUtcNow() - resumed.WrittenAt).TotalSeconds));

		await using var scope = root.CreateAsyncScope();
		var ctx = scope.ServiceProvider.GetRequiredService<CellContext>();
		var rootId = resumed?.Tree.First(n => n.ParentCellId is null).CellId ?? CellId.From(Guid.NewGuid());
		var goalText = resumed?.Tree.First(n => n.ParentCellId is null).Goal ?? run.Question;
		ctx.Init(run.RunId, rootId, null, 0, new Goal(GoalId.From(Guid.NewGuid()), goalText), 0, null);
		var cell = scope.ServiceProvider.GetServices<ICell>().Single(c => c.Type == "planning");

		var result = await cell.RunAsync(ct);
		rootActivity?.SetTag("honeycomb.cell.outcome", result.Status);

		if (result.Status == "checkpointed")
		{
			var snap = new Snapshot(1, run.RunId, run.Question, time.GetUtcNow(), run.RootTraceparent, run.RootTracestate,
				run.Nodes.Values.Select(n => new NodeSnap(n.CellId, n.ParentCellId, n.CellType, n.Depth, n.Goal, n.State, n.Result, n.RespinAttempt, n.Traceparent, n.Tracestate, n.HumanQuestion, n.HumanAnswer, n.Superseded)).ToList(),
				counters.Snapshot());
			store.Save(snap);
		}
		return result;
	}
}
