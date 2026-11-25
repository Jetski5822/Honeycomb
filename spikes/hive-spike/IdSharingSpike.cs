using CommunityToolkit.VectorData.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

// Two cells of different types that end up with the same agent Id: what do MAF's session store and memory provider do?
// The hive gives each cell its cell type as the agent Id (see drafts/telemetry.md), so two cells of one type share an Id by design.
static class IdSharingSpike
{
	// Replies "ok" and remembers every message list it was sent.
	sealed class RecordingClient : IChatClient
	{
		public List<List<string>> Calls { get; } = [];
		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
		{
			var list = messages.Select(m => $"{m.Role}:{m.Text}").ToList();
			if (options?.Instructions is { } ins) list.Insert(0, $"instructions:{ins}");
			Calls.Add(list);
			return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
		}
		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
		public object? GetService(Type serviceType, object? serviceKey = null) => null;
		public void Dispose() { }
	}

	// Bag-of-words embedding: no model needed, similar words give similar vectors.
	sealed class HashEmbedder : IEmbeddingGenerator<string, Embedding<float>>
	{
		public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
		{
			var result = new GeneratedEmbeddings<Embedding<float>>();
			foreach (var v in values)
			{
				var vec = new float[16];
				foreach (var w in v.ToLowerInvariant().Split([' ', '.', ',', ':'], StringSplitOptions.RemoveEmptyEntries))
					vec[(w.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % 16] += 1f;
				if (vec.All(x => x == 0)) vec[0] = 1;
				result.Add(new Embedding<float>(vec));
			}
			return Task.FromResult(result);
		}
		public object? GetService(Type serviceType, object? serviceKey = null) => null;
		public void Dispose() { }
	}

	static ChatClientAgent Agent(IChatClient client, string id, string instructions, params AIContextProvider[] providers) =>
		new(client, new ChatClientAgentOptions
		{
			Id = id,
			Name = id,
			ChatOptions = new() { Instructions = instructions },
			AIContextProviders = providers,
		});

	public static async Task RunAsync(Action<string, bool, string> check1)
	{
		void check(string n, bool ok, string d = "") => check1(n, ok, d);
		Console.WriteLine("--- session store (InMemoryAgentSessionStore)");
		var store = new InMemoryAgentSessionStore();
		var key = new AgentSessionStoreKey("conversation-1");

		// Writer #1 learns a secret, and its session is saved under the key.
		var clientA = new RecordingClient();
		var writer1 = Agent(clientA, "writer", "You are writer one.");
		var s1 = await writer1.CreateSessionAsync();
		await writer1.RunAsync("The password is swordfish.", s1);
		await store.SaveSessionAsync(writer1, key, s1);

		// Writer #2: a different instance with the SAME Id, asking for the same key.
		var clientB = new RecordingClient();
		var writer2 = Agent(clientB, "writer", "You are writer two.");
		var s2 = await store.GetOrCreateSessionAsync(writer2, key);
		await writer2.RunAsync("What did we say before?", s2);
		var seenBySameId = clientB.Calls[0].Any(m => m.Contains("swordfish"));
		check("same agent Id + same key: second instance receives the first one's history", seenBySameId, string.Join(" | ", clientB.Calls[0]));

		// A different Id with the same key is isolated.
		var clientC = new RecordingClient();
		var other = Agent(clientC, "analyst", "You are an analyst.");
		var s3 = await store.GetOrCreateSessionAsync(other, key);
		await other.RunAsync("What did we say before?", s3);
		check("different agent Id + same key: isolated", !clientC.Calls[0].Any(m => m.Contains("swordfish")));

		// Same Id, but the key carries the durable cell id: isolated. This is the fix the hive uses.
		var clientD = new RecordingClient();
		var writer3 = Agent(clientD, "writer", "You are writer three.");
		var cellKey = new AgentSessionStoreKey("conversation-1", new Dictionary<string, string> { ["cell"] = "cell-0007" });
		var s4 = await store.GetOrCreateSessionAsync(writer3, cellKey);
		await writer3.RunAsync("What did we say before?", s4);
		check("same agent Id + key partitioned by cell id: isolated", !clientD.Calls[0].Any(m => m.Contains("swordfish")));

		// And a cell resumed after a checkpoint, with a fresh instance and the same cell key, gets its own history back.
		await store.SaveSessionAsync(writer3, cellKey, s4);
		var clientE = new RecordingClient();
		var writer3Again = Agent(clientE, "writer", "You are writer three.");
		var s5 = await store.GetOrCreateSessionAsync(writer3Again, cellKey);
		await writer3Again.RunAsync("Continue.", s5);
		check("same cell key, fresh instance: own history restored", clientE.Calls[0].Any(m => m.Contains("What did we say before?")), string.Join(" | ", clientE.Calls[0]));

		// Instructions are not stored in the session: writer two's instructions, not writer one's, were used.
		check("the session carries history only, not the first instance's instructions",
			clientB.Calls[0].Any(m => m.Contains("writer two")) && !clientB.Calls[0].Any(m => m.Contains("writer one")));

		// Concurrent same-Id cells, each under its own cell key, saving at once.
		var tasks = Enumerable.Range(0, 20).Select(async i =>
		{
			var c = new RecordingClient();
			var a = Agent(c, "writer", "w");
			var k = new AgentSessionStoreKey("conversation-2", new Dictionary<string, string> { ["cell"] = $"cell-{i}" });
			var s = await store.GetOrCreateSessionAsync(a, k);
			await a.RunAsync($"private note {i}", s);
			await store.SaveSessionAsync(a, k, s);
			return (i, ok: true);
		}).ToArray();
		await Task.WhenAll(tasks);
		var mixups = 0;
		foreach (var i in Enumerable.Range(0, 20))
		{
			var c = new RecordingClient();
			var a = Agent(c, "writer", "w");
			var k = new AgentSessionStoreKey("conversation-2", new Dictionary<string, string> { ["cell"] = $"cell-{i}" });
			var s = await store.GetOrCreateSessionAsync(a, k);
			await a.RunAsync("again", s);
			var notes = c.Calls[0].Count(m => m.Contains("private note"));
			if (notes != 1 || !c.Calls[0].Any(m => m.Contains($"private note {i}"))) mixups++;
		}
		check("20 concurrent same-Id cells with cell keys: no history mixed", mixups == 0, $"mixups={mixups}");

		// The hosting wrapper: SessionStorageIdentity is internal and only changes the partition when set.
		var host1 = new AIHostAgent(Agent(new RecordingClient(), "writer", "w"), store);
		var host2 = new AIHostAgent(Agent(new RecordingClient(), "writer", "w"), store);
		var hs = await host1.GetOrCreateSessionAsync("conv-host");
		await host1.RunAsync("host secret: marigold", hs);
		await host1.SaveSessionAsync("conv-host", hs);
		var hs2 = await host2.GetOrCreateSessionAsync("conv-host");
		var rec = new RecordingClient();
		var host3 = new AIHostAgent(Agent(rec, "writer", "w"), store);
		var hs3 = await host3.GetOrCreateSessionAsync("conv-host");
		await host3.RunAsync("recall", hs3);
		check("AIHostAgent over the same Id also shares the stored session", rec.Calls[0].Any(m => m.Contains("marigold")));

		Console.WriteLine("--- memory provider (ChatHistoryMemoryProvider over an in-memory vector store)");
		var vectors = new InMemoryVectorStore(new InMemoryVectorStoreOptions { EmbeddingGenerator = new HashEmbedder() });

		ChatHistoryMemoryProvider Provider(ChatHistoryMemoryProviderScope storage, ChatHistoryMemoryProviderScope? search = null) =>
			new(vectors, "chathistory", 16, _ => new ChatHistoryMemoryProvider.State(storage, search),
				new ChatHistoryMemoryProviderOptions { EnableSensitiveTelemetryData = true });

		// Search scope that only names the user (the MAF sample's configuration): every agent sharing the collection sees everything.
		var recW = new RecordingClient();
		var writerM = Agent(recW, "writer", "w", Provider(new() { UserId = "u1", AgentId = "writer", SessionId = "s-w" }, new() { UserId = "u1" }));
		await writerM.RunAsync("The launch codename is Zephyr.", await writerM.CreateSessionAsync());

		var recA = new RecordingClient();
		var analystM = Agent(recA, "analyst", "a", Provider(new() { UserId = "u1", AgentId = "analyst", SessionId = "s-a" }, new() { UserId = "u1" }));
		await analystM.RunAsync("What is the launch codename?", await analystM.CreateSessionAsync());
		check("search scope = user only: a different agent type sees the first one's messages", recA.Calls[0].Any(m => m.Contains("Zephyr")), string.Join(" | ", recA.Calls[0]));

		// Search scope that includes the agent Id: isolates types, but not two cells of the same type.
		var recW2 = new RecordingClient();
		var writerCell1 = Agent(recW2, "writer", "w", Provider(new() { UserId = "u2", AgentId = "writer", SessionId = "s-1" }, new() { UserId = "u2", AgentId = "writer" }));
		await writerCell1.RunAsync("Cell one note: the budget is 40 percent over.", await writerCell1.CreateSessionAsync());
		var recW3 = new RecordingClient();
		var writerCell2 = Agent(recW3, "writer", "w", Provider(new() { UserId = "u2", AgentId = "writer", SessionId = "s-2" }, new() { UserId = "u2", AgentId = "writer" }));
		await writerCell2.RunAsync("What is the budget?", await writerCell2.CreateSessionAsync());
		check("search scope = user + agent Id: a second cell of the same type sees the first one's messages", recW3.Calls[0].Any(m => m.Contains("40 percent")), string.Join(" | ", recW3.Calls[0]));

		var recA2 = new RecordingClient();
		var analystM2 = Agent(recA2, "analyst", "a", Provider(new() { UserId = "u2", AgentId = "analyst", SessionId = "s-3" }, new() { UserId = "u2", AgentId = "analyst" }));
		await analystM2.RunAsync("What is the budget?", await analystM2.CreateSessionAsync());
		check("search scope = user + agent Id: a different type is isolated", !recA2.Calls[0].Any(m => m.Contains("40 percent")));

		// Search scope = the durable cell id carried in SessionId: isolates every cell.
		var recX = new RecordingClient();
		var cellX = Agent(recX, "writer", "w", Provider(new() { UserId = "u3", AgentId = "writer", SessionId = "cell-0001" }));
		await cellX.RunAsync("Private: the vendor is Acme.", await cellX.CreateSessionAsync());
		var recY = new RecordingClient();
		var cellY = Agent(recY, "writer", "w", Provider(new() { UserId = "u3", AgentId = "writer", SessionId = "cell-0002" }));
		await cellY.RunAsync("Who is the vendor?", await cellY.CreateSessionAsync());
		check("search scope = storage scope with cell id as SessionId: same-type cells isolated", !recY.Calls[0].Any(m => m.Contains("Acme")));
		var recX2 = new RecordingClient();
		var cellX2 = Agent(recX2, "writer", "w", Provider(new() { UserId = "u3", AgentId = "writer", SessionId = "cell-0001" }));
		await cellX2.RunAsync("Who is the vendor?", await cellX2.CreateSessionAsync());
		check("the same cell id, fresh instance: finds its own earlier messages", recX2.Calls[0].Any(m => m.Contains("Acme")), string.Join(" | ", recX2.Calls[0]));

		// What the memory provider writes into the traced request (relevant to the telemetry draft's content rules).
		check("injected memory text is part of the chat request (so content capture covers it)", recX2.Calls[0].Any(m => m.Contains("Acme")));
	}
}
