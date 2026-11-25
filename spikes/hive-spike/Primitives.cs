using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Vogen;

// Value objects, as proposed in the stack draft.
[ValueObject<Guid>] public readonly partial struct CellId { }
[ValueObject<Guid>] public readonly partial struct RunId { }
[ValueObject<Guid>] public readonly partial struct GoalId { }
[ValueObject<string>] public readonly partial struct CellType { }

public sealed record Goal(GoalId Id, string Text);

// The result a cell must return. Status: answered | cannot_answer | checkpointed.
public sealed record CellResult(string Status, string? Answer, string? Reason, string? Question);

public interface ICellContext
{
	RunId RunId { get; }
	CellId CellId { get; }
	CellId? ParentCellId { get; }
	int Depth { get; }
	Goal Goal { get; }
	int RespinAttempt { get; }
	ActivityContext? PreviousAttempt { get; }
}

public sealed class CellContext : ICellContext
{
	public RunId RunId { get; private set; }
	public CellId CellId { get; private set; }
	public CellId? ParentCellId { get; private set; }
	public int Depth { get; private set; }
	public Goal Goal { get; private set; } = null!;
	public int RespinAttempt { get; private set; }
	public ActivityContext? PreviousAttempt { get; private set; }

	public void Init(RunId runId, CellId cellId, CellId? parent, int depth, Goal goal, int attempt, ActivityContext? previous)
	{
		RunId = runId; CellId = cellId; ParentCellId = parent; Depth = depth; Goal = goal; RespinAttempt = attempt; PreviousAttempt = previous;
	}
}

public interface ICell
{
	string Type { get; }
	Task<CellResult> RunAsync(CancellationToken ct);
}

public static class Telemetry
{
	public const string Name = "Honeycomb";
	public static readonly ActivitySource Source = new(Name, "1.0.0");
	public static readonly Meter Meter = new(Name, "1.0.0");
	public static readonly Counter<long> RespinCount = Meter.CreateCounter<long>("honeycomb.respin.count", "{respin}");
	public static readonly Counter<long> RespinRefusedCount = Meter.CreateCounter<long>("honeycomb.respin.refused.count", "{respin}");
	public static readonly Counter<long> CheckpointRaisedCount = Meter.CreateCounter<long>("honeycomb.checkpoint.raised.count", "{checkpoint}");

	public static void Event(Activity? activity, string name, params (string Key, object? Value)[] tags)
	{
		if (activity is null) return;
		var t = new ActivityTagsCollection();
		foreach (var (k, v) in tags) t[k] = v;
		activity.AddEvent(new ActivityEvent(name, tags: t));
	}
}

// ---------- re-spin counters: total max, windowed max, window length, on a TimeProvider ----------
public sealed record CounterState(string Scope, string Key, int Total, int InWindow, DateTimeOffset WindowStart);

public sealed class ReSpinCounter(string scope, string key, int totalMax, int windowMax, TimeSpan window, TimeProvider time)
{
	private int total, inWindow;
	private DateTimeOffset windowStart = time.GetUtcNow();

	public string Scope => scope;
	public string Key => key;
	public int Total => total;
	public int InWindow => inWindow;

	public bool CanAcquire(out string why)
	{
		Roll();
		if (total >= totalMax) { why = $"total {total}/{totalMax}"; return false; }
		if (inWindow >= windowMax) { why = $"window {inWindow}/{windowMax}"; return false; }
		why = ""; return true;
	}

	public void Acquire() { Roll(); total++; inWindow++; }

	private void Roll()
	{
		var now = time.GetUtcNow();
		if (now - windowStart >= window) { windowStart = now; inWindow = 0; }
	}

	public CounterState Snapshot() => new(scope, key, total, inWindow, windowStart);
	public void Restore(CounterState s) { total = s.Total; inWindow = s.InWindow; windowStart = s.WindowStart; }
}

public sealed record HiveOptions(int JudgeDepth = 1, int HardCap = 2, int ReAsk = 2, int CellTotal = 1, int PlannerTotal = 5, int HiveTotal = 10, int WindowMax = 3, int WindowMinutes = 60);

public sealed class CounterRegistry(HiveOptions o, TimeProvider time)
{
	private readonly Dictionary<string, ReSpinCounter> counters = new();
	private readonly object gate = new();

	private ReSpinCounter Get(string scope, string key, int total)
	{
		var id = scope + "|" + key;
		lock (gate)
		{
			if (!counters.TryGetValue(id, out var c))
				counters[id] = c = new ReSpinCounter(scope, key, total, o.WindowMax, TimeSpan.FromMinutes(o.WindowMinutes), time);
			return c;
		}
	}

	// A re-spin must be allowed by all three scopes. Returns the first scope that refuses, or null.
	public (bool Ok, string? Scope, string Why, string[] Scopes) TryRespin(CellId planner, string cellType)
	{
		var set = new[]
		{
			Get("cell", $"{planner.Value}:{cellType}", o.CellTotal),
			Get("planner", planner.Value.ToString(), o.PlannerTotal),
			Get("hive", "hive", o.HiveTotal),
		};
		lock (gate)
		{
			foreach (var c in set) if (!c.CanAcquire(out var why)) return (false, c.Scope, why, set.Select(s => s.Scope).ToArray());
			foreach (var c in set) c.Acquire();
		}
		return (true, null, "", set.Select(s => s.Scope).ToArray());
	}

	public List<CounterState> Snapshot() { lock (gate) { return counters.Values.Select(c => c.Snapshot()).ToList(); } }

	public void Restore(IEnumerable<CounterState> states)
	{
		foreach (var s in states)
		{
			var total = s.Scope switch { "cell" => o.CellTotal, "planner" => o.PlannerTotal, _ => o.HiveTotal };
			Get(s.Scope, s.Key, total).Restore(s);
		}
	}
}

// ---------- scripted models ----------
public sealed class Models
{
	private readonly Dictionary<string, Queue<string>> replies = new();
	private readonly Dictionary<string, string> last = new();
	public readonly Dictionary<string, int> Calls = new();
	private readonly object gate = new();

	public void Script(string key, params string[] texts) { lock (gate) { replies[key] = new Queue<string>(texts); } }
	public void Append(string key, params string[] texts) { lock (gate) { if (!replies.TryGetValue(key, out var q)) replies[key] = q = new(); foreach (var t in texts) q.Enqueue(t); } }

	public string Next(string key)
	{
		lock (gate)
		{
			Calls[key] = Calls.GetValueOrDefault(key) + 1;
			if (replies.TryGetValue(key, out var q) && q.Count > 0) return last[key] = q.Dequeue();
			return last.GetValueOrDefault(key) ?? "{}";
		}
	}

	public readonly Dictionary<string, TimeSpan> Delays = new();

	public IChatClient Client(string key, string model = "scripted-model", string provider = "scripted", TimeSpan? delay = null)
		=> new ScriptedClient(this, key, model, provider, delay ?? (Delays.TryGetValue(key, out var d) ? d : null));
}

public sealed class ScriptedClient(Models models, string key, string model, string provider, TimeSpan? delay) : IChatClient
{
	public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		if (delay is { } d) await Task.Delay(d, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		var text = models.Next(key);
		return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
		{
			ModelId = model,
			Usage = new UsageDetails { InputTokenCount = 100 + messages.Count() * 10, OutputTokenCount = 20, TotalTokenCount = 120 + messages.Count() * 10 },
		};
	}

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
	}

	public object? GetService(Type serviceType, object? serviceKey = null)
		=> serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(provider, new Uri("http://localhost/"), model) : null;

	public void Dispose() { }
}

public static class Json
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
