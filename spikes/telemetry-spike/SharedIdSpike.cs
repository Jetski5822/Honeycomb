using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Tests whether instances that share an agent Id interfere with each other outside MAF Workflows:
// sessions, chat history, responses and spans, with 20 same-Id agents running concurrently.

static class SharedIdSpike
{
	public static async Task RunAsync(List<Activity> activities)
	{
		const int count = 20;

		AIAgent Make(int i)
		{
			var seen = new EchoClient();
			var chat = seen.AsBuilder().UseOpenTelemetry(sourceName: "Honeycomb").Build();
			var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { Id = "jira-analysis", Name = "jira-analysis", ChatOptions = new ChatOptions { Instructions = "Echo." } });
			return agent.AsBuilder().UseOpenTelemetry(sourceName: "Honeycomb").Build();
		}

		// Baseline: one agent, two turns, to learn how much history the model sees on turn two.
		var solo = Make(-1);
		var soloSession = await solo.CreateSessionAsync();
		await solo.RunAsync("A-solo", soloSession);
		var soloSecond = (await solo.RunAsync("B-solo", soloSession)).Text;
		Console.WriteLine($"baseline, one agent, turn two: {soloSecond}");
		lock (activities) { activities.Clear(); }

		var agents = Enumerable.Range(0, count).Select(Make).ToList();
		var results = await Task.WhenAll(agents.Select((a, i) => Task.Run(async () =>
		{
			var session = await a.CreateSessionAsync();
			var first = (await a.RunAsync($"A{i}", session)).Text;
			var second = (await a.RunAsync($"B{i}", session)).Text;
			return (i, first, second);
		})));

		var wrongEcho = results.Where(r => !r.second.Contains($"last=B{r.i}|") || !r.first.Contains($"last=A{r.i}|")).ToList();
		var baselineHistory = soloSecond.Split('|').First(p => p.StartsWith("seen="));
		var wrongHistory = results.Where(r => !r.second.Contains(baselineHistory)).ToList();
		Console.WriteLine($"agents run concurrently, all with Id 'jira-analysis': {count}");
		Console.WriteLine($"responses that did not echo their own input: {wrongEcho.Count}");
		Console.WriteLine($"second turns whose history differed from the baseline ({baselineHistory}): {wrongHistory.Count}");

		List<Activity> snap; lock (activities) { snap = activities.ToList(); }
		var agentSpans = snap.Where(a => a.OperationName.StartsWith("invoke_agent") || a.DisplayName.StartsWith("invoke_agent")).ToList();
		var ids = agentSpans.Select(a => a.GetTagItem("gen_ai.agent.id")?.ToString()).Distinct().ToList();
		var names = agentSpans.Select(a => a.DisplayName).Distinct().ToList();
		Console.WriteLine($"invoke_agent spans: {agentSpans.Count} (expected {count * 2}); distinct span ids: {agentSpans.Select(a => a.SpanId).Distinct().Count()}");
		Console.WriteLine($"distinct gen_ai.agent.id values: [{string.Join(", ", ids)}]");
		Console.WriteLine($"distinct span names: [{string.Join(", ", names)}]");
		Console.WriteLine($"distinct trace ids across the run (each task started its own trace): {agentSpans.Select(a => a.TraceId).Distinct().Count()}");
	}

	sealed class EchoClient : IChatClient
	{
		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
		{
			var list = messages.ToList();
			var last = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
			var text = $"last={last}|seen={list.Count}";
			return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
			{
				ModelId = "fake-model",
				Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 },
			});
		}

		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public object? GetService(Type serviceType, object? serviceKey = null)
			=> serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake-provider", new Uri("http://localhost/"), "fake-model") : null;

		public void Dispose() { }
	}
}
