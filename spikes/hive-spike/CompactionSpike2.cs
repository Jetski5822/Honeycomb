#pragma warning disable CA1707, IDE1006
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

// A cell with a long tool-call history. After compaction can it still return confirmed facts that each have a quote
// copied from a tool result? The history is seeded (the tool calls are scripted); only the last turn calls the local model.
static partial class CompactionSpike2
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
	enum Status { answered, cannot_answer }
	sealed record Fact(string id, string statement);
	sealed record Quote(string supports, string quote);
	sealed record Result(List<Fact> confirmed, List<Quote> evidence, List<string> open_questions, Status status, string summary);

	// Records what the model was sent and what it reported as input tokens.
	sealed class Recorder(IChatClient inner) : IChatClient
	{
		public string LastText = ""; public int LastMessages; public long LastInputTokens;
		public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
		{
			var list = messages.ToList();
			LastMessages = list.Count;
			LastText = string.Join("\n", list.Select(m => string.Join(" ", m.Contents.Select(c => c switch
			{
				TextContent t => t.Text,
				FunctionResultContent r => r.Result?.ToString() ?? "",
				FunctionCallContent f => $"{f.Name}({string.Join(",", f.Arguments?.Select(a => a.Value) ?? [])})",
				_ => ""
			}))));
			var r = await inner.GetResponseAsync(list, options, cancellationToken);
			LastInputTokens = r.Usage?.InputTokenCount ?? 0;
			return r;
		}
		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
		public void Dispose() { }
	}

	const string Aim = "Find out why the login endpoint returned HTTP 500 between 08:00 and 12:00 on 20 October, and which release introduced the fault.";

	// Eight tool results. The ones that bear on the aim come first, so compaction reaches them first.
	static readonly (string Tool, string Arg, string Result)[] CallsRaw =
	[
		("read_log", "auth-gateway 20 Oct",
			"Log analysis, auth gateway, 20 Oct.\n" +
			"Between 08:00 and 12:00 the login endpoint returned HTTP 500 for 18,300 requests. Every failing request had no X-Tenant header. " +
			"Requests from the web client always include it. The password reset endpoint returned 200 for all requests in the same period. CPU and memory on the gateway were normal.\n" +
			"Sample of the first failing request: POST /login, status 500, latency 12 ms, user agent HoneycombMobile/3.2.1, header X-Tenant absent. " +
			"Sample of a succeeding request: POST /login, status 200, latency 48 ms, user agent Mozilla/5.0, header X-Tenant present."),
		("read_code", "mobile http client",
			"Mobile client review.\n" +
			"In the mobile app the shared HTTP client adds headers from a settings object. Version 3.2.1 replaced the settings object with a new configuration loader, " +
			"and the loader does not copy the tenant setting into the headers. The desktop client is a separate codebase and was not checked. " +
			"The app's push notification module was also changed in 3.2.1 but does not make HTTP calls.\n" +
			"The change was made in pull request 4412, reviewed by one person, with no tests for the headers."),
		("read_release_notes", "mobile",
			"Release notes.\n" +
			"Mobile 3.2.0 shipped 2 September. Mobile 3.2.1 shipped 12 October and is installed on 61 percent of devices. Mobile 3.3.0 is planned for 3 November. " +
			"App store review takes up to 2 days. The web client was last released on 9 October.\n" +
			"Mobile 3.2.1 contents: new configuration loader, push notification fixes, updated onboarding screens, minor translation updates."),
		("read_dashboard", "gateway week 43",
			"Gateway dashboard, week 43.\n" +
			"Request volume was 4.1 million per day on weekdays and 2.2 million at weekends. Median latency was 41 ms and p95 was 180 ms. " +
			"Error rate was 0.2 percent apart from 20 October. Certificates renewed on 18 October with no incident. The rate limiter rejected 310 requests in the week, all from one test account. " +
			"Autoscaling added two instances on Tuesday at 09:10 and removed them at 13:40."),
		("read_code", "desktop http client",
			"Desktop client review.\n" +
			"The desktop client adds headers in a middleware class. The class reads the tenant from the signed-in session and has done so since version 2.0. " +
			"Its last release was 30 September. It has 14 unit tests for headers. No change was made to the middleware in the last six months."),
		("read_tickets", "login label",
			"Support tickets labelled login, 19 to 21 October.\n" +
			"Ticket 8812: user cannot log in on a phone, reported 20 October 09:05. Ticket 8815: forgotten password, resolved by reset link. Ticket 8819: user asks to change email address. " +
			"Ticket 8821: login screen shows a blank page on a tablet, reported 20 October 10:30. Ticket 8830: request for single sign-on with a new provider, planned for next quarter. " +
			"Ticket 8833: two-factor code arrived late, carrier delay."),
		("read_runbook", "login 500",
			"Runbook, login failures.\n" +
			"Step 1: check the gateway dashboard for error rate. Step 2: check the gateway log for the status and the request headers. Step 3: if one client version is affected, ask the mobile team for a hotfix. " +
			"Step 4: if all clients are affected, roll back the gateway. The on-call rota for the mobile team is kept in the staffing sheet, not in this runbook."),
		("read_log", "web client login 20 Oct",
			"Web client log, 20 October.\n" +
			"Login requests from the web client: 212,400, of which 212,400 returned 200. Each request included the X-Tenant header. The web client's last release was 9 October. " +
			"No errors were recorded in the web client between 08:00 and 12:00."),
	];

	// COMPACT_TAIL=1 reverses the sentences after the title line of each result, so the answer is at the end and not at the start.
	static readonly (string Tool, string Arg, string Result)[] Calls = Environment.GetEnvironmentVariable("COMPACT_TAIL") != "1" ? CallsRaw : CallsRaw.Select(c =>
	{
		var parts = Regex.Split(c.Result.Replace('\n', ' '), @"(?<=[.!?])\s+").Where(x => x.Length > 0).ToList();
		return (c.Tool, c.Arg, string.Join(" ", parts.Take(1).Concat(Enumerable.Reverse(parts.Skip(1)))));
	}).ToArray();

	const string Instructions =
		"You are a cell investigating a goal. The aim is: " + Aim + " You have already called your tools. Report what the tool results state. " +
		"confirmed: facts the tool results state plainly, each with an id (c1, c2, ...). " +
		"evidence: for each confirmed fact, a quote copied word for word from one tool result, with supports set to the fact's id. " +
		"open_questions: what the aim needs that the tool results do not say. status: answered if you found the cause, otherwise cannot_answer. " +
		"Never state a fact the tool results do not state. Reply only with JSON.";

	const string SummaryPrompt =
		"Summarise the conversation so far. Keep every fact, number, name, identifier and version exactly as written. " +
		"Copy short exact quotes for the facts that bear on this aim: " + Aim + " Leave out anything that does not bear on the aim.";

	// Expected facts, matched by keywords in a confirmed statement.
	static readonly (string Name, string[] Words)[] Expected =
	[
		("18,300 failures", ["18300", "18,300"]),
		("no X-Tenant header", ["tenant", "header"]),
		("3.2.1 loader omits tenant", ["3.2.1", "loader"]),
		("3.2.1 shipped 12 October", ["12 oct", "october 12", "12th"]),
		("61 percent of devices", ["61"]),
	];

	static string Norm(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", " "), "\\s+", " ").Trim();
	static IEnumerable<string> Numbers(string s) => Regex.Matches(s.Replace(",", ""), "\\d+(\\.\\d+)?").Select(m => m.Value);

	static IEnumerable<ChatMessage> History()
	{
		yield return new(ChatRole.User, "Begin.");
		var i = 0;
		foreach (var (tool, arg, result) in Calls)
		{
			var id = $"call{i++}";
			yield return new(ChatRole.Assistant, [new FunctionCallContent(id, tool, new Dictionary<string, object?> { ["query"] = arg })]);
			yield return new(ChatRole.Tool, [new FunctionResultContent(id, result)]);
		}
		yield return new(ChatRole.User, "Report your findings.");
	}

	public static async Task RunAsync(int repeats)
	{
		var openAi = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("not-needed"), new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
		var model = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();
		var original = string.Join("\n", Calls.Select(c => c.Result));
		var originalNorm = Norm(original);
		var originalNumbers = Numbers(original).ToHashSet();
		var allText = string.Join("\n", History().Select(m => string.Join(" ", m.Contents.Select(c => c is FunctionResultContent r ? r.Result?.ToString() : (c as TextContent)?.Text))));
		Console.WriteLine($"    history: {History().Count()} messages, about {Encoding.UTF8.GetByteCount(original) / 4} tokens of tool results out of about {Encoding.UTF8.GetByteCount(allText) / 4} (bytes / 4)");

		// trigger as in a safety net: fire once the history is over roughly half its full size
		var trigger = CompactionTriggers.TokensExceed(int.Parse(Environment.GetEnvironmentVariable("COMPACT_TRIGGER") ?? "400"));
		var conditions = new List<(string Name, Func<IChatClient, CompactionStrategy?> Make)>
		{
			("none", _ => null),
			("sliding window", _ => new SlidingWindowCompactionStrategy(trigger, minimumPreservedTurns: 1)),
			("truncation", _ => new TruncationCompactionStrategy(trigger, minimumPreservedGroups: 3)),
			("tool result collapse", _ => new ToolResultCompactionStrategy(trigger, minimumPreservedGroups: 3)),
			("summarise, default prompt", c => new SummarizationCompactionStrategy(c, trigger, minimumPreservedGroups: 3)),
			("summarise, keep-quotes prompt", c => new SummarizationCompactionStrategy(c, trigger, minimumPreservedGroups: 3, summarizationPrompt: SummaryPrompt)),
		};

		Console.WriteLine($"    {"condition",-30} {"parsed",6} {"input tok",9} {"facts/5",8} {"all 5",6} {"confirmed",9} {"quote in source",16} {"quote in what model saw",24} {"invented no.",12} {"cause found",11}");
		foreach (var (name, make) in conditions)
		{
			int parsed = 0, all5 = 0, confirmed = 0, quotes = 0, quotesInSource = 0, quotesSeen = 0, noEvidence = 0, invented = 0, cause = 0, facts = 0, sums = 0;
			long tokens = 0;
			string? firstSeen = null;
			for (var r = 0; r < repeats; r++)
			{
				var cell = new Recorder(model);
				var summariser = new Recorder(model);
				var strategy = make(summariser);
				var options = new ChatClientAgentOptions
				{
					Id = "cell", Name = "cell",
					ChatOptions = new ChatOptions { Instructions = Instructions, Temperature = 0.2f, MaxOutputTokens = 900, ResponseFormat = ChatResponseFormat.ForJsonSchema<Result>(Json) },
				};
				if (strategy is not null) options.AIContextProviders = [new CompactionProvider(strategy)];
				var agent = new ChatClientAgent(cell, options);
				var session = await agent.CreateSessionAsync();
				string text;
				try { text = (await agent.RunAsync(History(), session)).Text; } catch (Exception ex) { Console.WriteLine($"      run failed: {ex.GetType().Name} {ex.Message[..Math.Min(120, ex.Message.Length)]}"); continue; }
				tokens += cell.LastInputTokens;
				if (summariser.LastText.Length > 0) sums++;
				firstSeen ??= $"{cell.LastMessages} messages";
				Result? res;
				try { res = JsonSerializer.Deserialize<Result>(text, Json); } catch (JsonException) { continue; }
				if (res is null) continue;
				parsed++;
				if (Environment.GetEnvironmentVariable("COMPACT_SHOW") == "1" && parsed == 1) { Console.WriteLine($"      [{name}] model saw {cell.LastMessages} messages, {cell.LastInputTokens} tokens"); foreach (var f in res.confirmed) Console.WriteLine($"        {f.id}: {f.statement}"); }
				var seenNorm = Norm(cell.LastText);
				var statements = res.confirmed.Select(f => f.statement.ToLowerInvariant()).ToList();
				// a fact counts if its keywords all appear somewhere in the confirmed statements taken together
				var joined = string.Join(" ", statements);
				var got = Expected.Count(e => e.Name.StartsWith("3.2.1 shipped") ? e.Words.Any(w => joined.Contains(w)) && joined.Contains("3.2.1") : e.Name.StartsWith("18,300") || e.Name.StartsWith("61") ? e.Words.Any(w => joined.Contains(w)) : e.Words.All(w => joined.Contains(w)));
				facts += got; if (got == 5) all5++;
				if (res.status == Status.answered && statements.Any(s => s.Contains("3.2.1") && s.Contains("tenant"))) cause++;
				confirmed += res.confirmed.Count;
				var ids = res.confirmed.Select(f => f.id).ToHashSet();
				noEvidence += res.confirmed.Count(f => !res.evidence.Any(e => e.supports == f.id));
				foreach (var q in res.evidence) { quotes++; if (originalNorm.Contains(Norm(q.quote))) quotesInSource++; if (seenNorm.Contains(Norm(q.quote))) quotesSeen++; }
				foreach (var f in res.confirmed) foreach (var n in Numbers(f.statement)) if (!originalNumbers.Contains(n) && !Regex.IsMatch(f.id, "^c" + n + "$")) invented++;
			}
			var avgTokens = parsed + (repeats - parsed) > 0 ? tokens / Math.Max(1, repeats) : 0;
			Console.WriteLine($"    {name,-30} {parsed + "/" + repeats,6} {avgTokens,9} {(parsed == 0 ? "-" : (facts / (double)parsed).ToString("0.0")),8} {all5 + "/" + parsed,6} {confirmed,9} {quotesInSource + "/" + quotes,16} {quotesSeen + "/" + quotes,24} {invented,12} {cause + "/" + parsed,11}   ({firstSeen}; summariser called in {sums} runs; confirmed without evidence {noEvidence})");
		}
	}
}
