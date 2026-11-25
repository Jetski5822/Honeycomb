using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

// MAF compaction as a safety net inside one cell: does it cap how much history the model sees?
static class CompactionSpike
{
	sealed class CountingClient : IChatClient
	{
		public readonly List<int> Seen = new();
		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
		{
			Seen.Add(messages.Count());
			return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
		}
		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public object? GetService(Type serviceType, object? serviceKey = null) => null;
		public void Dispose() { }
	}

	public static async Task RunAsync(Action<string, bool, string> check)
	{
		async Task<List<int>> Turns(CompactionStrategy? strategy)
		{
			var client = new CountingClient();
			var options = new ChatClientAgentOptions { Id = "cell", Name = "cell", ChatOptions = new ChatOptions { Instructions = "x" } };
			if (strategy is not null) options.AIContextProviders = [new CompactionProvider(strategy)];
			var agent = new ChatClientAgent(client, options);
			var session = await agent.CreateSessionAsync();
			for (var i = 0; i < 12; i++) await agent.RunAsync($"turn {i} " + new string('x', 200), session);
			return client.Seen;
		}

		var none = await Turns(null);
		var sliding = await Turns(new SlidingWindowCompactionStrategy(CompactionTriggers.MessagesExceed(8), minimumPreservedTurns: 2));
		var truncation = await Turns(new TruncationCompactionStrategy(CompactionTriggers.MessagesExceed(8), minimumPreservedGroups: 2));
		Console.WriteLine($"    messages the model saw, per turn, no compaction:   {string.Join(",", none)}");
		Console.WriteLine($"    messages the model saw, per turn, sliding window:  {string.Join(",", sliding)}");
		Console.WriteLine($"    messages the model saw, per turn, truncation:      {string.Join(",", truncation)}");
		check("without compaction the history grows every turn", none.Last() > none.First() + 15, $"{none.First()} -> {none.Last()}");
		check("a sliding window stops the history growing", sliding.Last() <= 12 && sliding.Last() < none.Last(), $"{sliding.First()} -> {sliding.Last()}");
		check("truncation stops the history growing", truncation.Last() <= 12 && truncation.Last() < none.Last(), $"{truncation.First()} -> {truncation.Last()}");
	}
}
