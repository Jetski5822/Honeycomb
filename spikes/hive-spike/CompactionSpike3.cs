#pragma warning disable CA1707, IDE1006
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

// Three ways to keep the evidence rule after compaction, against the same cell as compaction2:
//   1. notes: after each tool result the cell records facts with a quote; code checks each quote against that result;
//      the notebook is pinned as a system message, which compaction does not touch.
//   2. a quote check that resolves a reworded quote against the stored tool results (fuzzy), instead of requiring it to be verbatim.
//   3. compaction that keeps the text of the tool results (collapse), at different budgets.
static partial class CompactionSpike2
{
	sealed record Note(string statement, string quote);
	sealed record Notes(List<Note> notes);

	const string NoteInstructions =
		"You are a cell investigating a goal. The aim is: " + Aim + " You are given one tool result. " +
		"notes: each fact in the tool result that bears on the aim, with the fact in statement and an exact quote copied word for word from the tool result in quote. " +
		"If the tool result has nothing that bears on the aim, return an empty list. Never state a fact the tool result does not state. Reply only with JSON.";

	static string[] Words(string s) => Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);

	// Fuzzy resolve: some window of the stored results, a few words longer than the quote, must contain at least 80% of the quote's words.
	static bool Resolves(string quote, List<string[]> results)
	{
		var q = Words(quote);
		if (q.Length < 3) return false;
		var qset = q.ToHashSet();
		var need = 0.8 * qset.Count;
		foreach (var words in results)
			for (var i = 0; i < words.Length; i++)
			{
				var w = words.Skip(i).Take(q.Length + 3).ToList();
				if (qset.Count(w.Contains) >= need) return true;
			}
		return false;
	}

	static string Head(CompactionMessageGroup g, int chars)
	{
		var sb = new StringBuilder("[tool results, shortened]");
		foreach (var m in g.Messages)
			foreach (var c in m.Contents)
				if (c is FunctionResultContent r) { var t = r.Result?.ToString() ?? ""; sb.Append($"\n{(t.Length <= chars ? t : t[..chars] + " ...")}"); }
		return sb.ToString();
	}

	sealed class Tally
	{
		public int Parsed, All5, Confirmed, Quotes, Verbatim, Seen, Fuzzy, Invented, Cause; public double Facts; public long Tokens, ExtraTokens; public int Runs;
	}

	public static async Task RunOptionsAsync(int repeats)
	{
		var openAi = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("not-needed"), new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
		var model = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();
		var original = string.Join("\n", Calls.Select(c => c.Result));
		var originalNorm = Norm(original);
		var originalNumbers = Numbers(original).ToHashSet();
		var sentences = Calls.Select(c => Words(c.Result)).ToList();
		var trigger = CompactionTriggers.TokensExceed(int.Parse(Environment.GetEnvironmentVariable("COMPACT_TRIGGER") ?? "400"));

		// name, whether the notebook is pinned, strategy factory
		var conditions = new List<(string Name, bool Notebook, Func<IChatClient, CompactionStrategy?> Make)>
		{
			("none", false, _ => null),
			("sliding window", false, _ => new SlidingWindowCompactionStrategy(trigger, minimumPreservedTurns: 1)),
			("truncation", false, _ => new TruncationCompactionStrategy(trigger, minimumPreservedGroups: 3)),
			("summarise, default prompt", false, c => new SummarizationCompactionStrategy(c, trigger, minimumPreservedGroups: 3)),
			("1 notes, no compaction", true, _ => null),
			("1 notes + sliding window", true, _ => new SlidingWindowCompactionStrategy(trigger, minimumPreservedTurns: 1)),
			("1 notes + truncation", true, _ => new TruncationCompactionStrategy(trigger, minimumPreservedGroups: 3)),
			("1 notes + summarise", true, c => new SummarizationCompactionStrategy(c, trigger, minimumPreservedGroups: 3)),
			("3 collapse, default, keep 3", false, _ => new ToolResultCompactionStrategy(trigger, minimumPreservedGroups: 3)),
			("3 collapse, default, keep 1", false, _ => new ToolResultCompactionStrategy(trigger, minimumPreservedGroups: 1)),
			("3 collapse, first 300 chars, keep 1", false, _ => new ToolResultCompactionStrategy(trigger, minimumPreservedGroups: 1) { ToolCallFormatter = g => Head(g, 300) }),
			("3 collapse, first 150 chars, keep 1", false, _ => new ToolResultCompactionStrategy(trigger, minimumPreservedGroups: 1) { ToolCallFormatter = g => Head(g, 150) }),
		};
		var tallies = conditions.ToDictionary(c => c.Name, _ => new Tally());
		// notebook quality
		int noteRuns = 0, notesTotal = 0, notesVerbatim = 0, noteFacts = 0, noteAll5 = 0; long noteTokens = 0;

		for (var r = 0; r < repeats; r++)
		{
			// build the notebook once per repeat, one model call per tool result
			var notebook = new List<(string Call, Note Note)>();
			long tokensForNotes = 0;
			for (var i = 0; i < Calls.Length; i++)
			{
				var rec = new Recorder(model);
				var opts = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 500, ResponseFormat = ChatResponseFormat.ForJsonSchema<Notes>(Json) };
				try
				{
					var resp = await rec.GetResponseAsync([new(ChatRole.System, NoteInstructions), new(ChatRole.User, $"Tool: {Calls[i].Tool}({Calls[i].Arg})\n{Calls[i].Result}")], opts);
					tokensForNotes += rec.LastInputTokens;
					var parsed = JsonSerializer.Deserialize<Notes>(resp.Text, Json);
					foreach (var n in parsed?.notes ?? [])
					{
						notesTotal++;
						// code check: the quote must be word for word in this tool result
						if (Norm(Calls[i].Result).Contains(Norm(n.quote))) { notesVerbatim++; notebook.Add(($"call{i}", n)); }
					}
				}
				catch (Exception) { }
			}
			noteRuns++; noteTokens += tokensForNotes;
			var nbStatements = string.Join(" ", notebook.Select(n => n.Note.statement.ToLowerInvariant()));
			var nbGot = ScoreFacts(nbStatements);
			noteFacts += nbGot; if (nbGot == 5) noteAll5++;
			var notebookText = "Notes already taken, each checked against its tool result. Quotes in evidence may be copied from these notes.\n" +
				string.Join("\n", notebook.Select((n, k) => $"n{k + 1} [{n.Call}]: {n.Note.statement} — \"{n.Note.quote}\""));

			foreach (var (name, pinned, make) in conditions)
			{
				var t = tallies[name]; t.Runs++;
				var cell = new Recorder(model); var summariser = new Recorder(model);
				var strategy = make(summariser);
				var options = new ChatClientAgentOptions
				{
					Id = "cell", Name = "cell",
					ChatOptions = new ChatOptions { Instructions = Instructions, Temperature = 0.2f, MaxOutputTokens = 900, ResponseFormat = ChatResponseFormat.ForJsonSchema<Result>(Json) },
				};
				if (strategy is not null) options.AIContextProviders = [new CompactionProvider(strategy)];
				var agent = new ChatClientAgent(cell, options);
				var session = await agent.CreateSessionAsync();
				var messages = pinned ? new[] { new ChatMessage(ChatRole.System, notebookText) }.Concat(History()) : History();
				string text;
				try { text = (await agent.RunAsync(messages, session)).Text; } catch (Exception) { continue; }
				t.Tokens += cell.LastInputTokens;
				if (r == 0 && Environment.GetEnvironmentVariable("COMPACT_DUMP") == "1") File.WriteAllText(Path.Combine(Path.GetTempPath(), $"compaction3-{name.Replace(' ', '_').Replace(',', '_')}.txt"), cell.LastText);
				Result? res;
				try { res = JsonSerializer.Deserialize<Result>(text, Json); } catch (JsonException) { continue; }
				if (res is null) continue;
				t.Parsed++;
				var seenNorm = Norm(cell.LastText);
				var joined = string.Join(" ", res.confirmed.Select(f => f.statement.ToLowerInvariant()));
				var got = ScoreFacts(joined);
				t.Facts += got; if (got == 5) t.All5++;
				if (res.status == Status.answered && joined.Contains("3.2.1") && joined.Contains("tenant")) t.Cause++;
				t.Confirmed += res.confirmed.Count;
				foreach (var q in res.evidence)
				{
					t.Quotes++;
					if (originalNorm.Contains(Norm(q.quote))) t.Verbatim++;
					if (seenNorm.Contains(Norm(q.quote))) t.Seen++;
					if (Resolves(q.quote, sentences)) t.Fuzzy++;
				}
				foreach (var f in res.confirmed) foreach (var n in Numbers(f.statement)) if (!originalNumbers.Contains(n) && !Regex.IsMatch(f.id, "^c" + n + "$")) t.Invented++;
			}
		}

		Console.WriteLine($"    notes step ({noteRuns} notebooks, 8 model calls each): {notesTotal / (double)noteRuns:0.0} notes per notebook, {notesVerbatim}/{notesTotal} quotes verbatim in their tool result, expected facts in notebook {noteFacts / (double)noteRuns:0.0}/5 (all 5 in {noteAll5}/{noteRuns}), {noteTokens / noteRuns} input tokens across the 8 calls");
		Console.WriteLine($"    {"condition",-38} {"parsed",6} {"input tok",9} {"facts/5",8} {"all 5",6} {"cause",6} {"verbatim",9} {"fuzzy",8} {"in view",8} {"invented",8}");
		foreach (var (name, _, _) in conditions)
		{
			var t = tallies[name];
			Console.WriteLine($"    {name,-38} {t.Parsed + "/" + t.Runs,6} {t.Tokens / Math.Max(1, t.Runs),9} {(t.Parsed == 0 ? "-" : (t.Facts / t.Parsed).ToString("0.0")),8} {t.All5 + "/" + t.Parsed,6} {t.Cause + "/" + t.Parsed,6} {t.Verbatim + "/" + t.Quotes,9} {t.Fuzzy + "/" + t.Quotes,8} {t.Seen + "/" + t.Quotes,8} {t.Invented,8}");
		}
	}

	static int ScoreFacts(string joined) => Expected.Count(e => e.Name.StartsWith("3.2.1 shipped") ? e.Words.Any(w => joined.Contains(w)) && joined.Contains("3.2.1") : e.Name.StartsWith("18,300") || e.Name.StartsWith("61") ? e.Words.Any(w => joined.Contains(w)) : e.Words.All(w => joined.Contains(w)));
}
