#pragma warning disable CA1707, IDE1006
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

// The goal body design: specialists return typed contributions, the hive folds them into the body with code, a later cell reads the body.
// Part 1: do specialists fill the contribution fields honestly (no invented facts, quotes really in the source)?
// Part 2: can the planning cell judge a proposed acceptance criterion?
// Part 3: after folding, does a later cell still get the facts it needs, with fewer tokens than the full transcript?
static class JsonSpike6
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

	enum Status { answered, cannot_answer }
	enum Decision { accept, change, reject }

	sealed record Fact(string id, string statement);
	sealed record Quote(string supports, string quote);
	sealed record Contribution(Status status, string summary, List<Fact> confirmed, List<Quote> evidence, List<string> context, List<string> proposed_criteria, List<string> open_questions, string? reason);
	// The same fields with the status after the contributions. FOLD_ORDER=2 asks for this one.
	sealed record ContributionStatusLast(List<Fact> confirmed, List<Quote> evidence, List<string> context, List<string> proposed_criteria, List<string> open_questions, Status status, string summary, string? reason);
	sealed record CriterionReply(Decision decision, string? criterion, string reason);
	sealed record Answer1(string table, List<string> columns, int p95_target_ms, string blocker);
	sealed record Answer2(string header, string first_bad_version, int http_status);

	static IChatClient _client = null!;

	static async Task<(T? Value, long InTokens, string Raw, string? Error)> Ask<T>(string system, string user, int maxTokens = 900) where T : class
	{
		var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = maxTokens, ResponseFormat = ChatResponseFormat.ForJsonSchema<T>(Json) };
		try
		{
			var r = await _client.GetResponseAsync([new(ChatRole.System, system), new(ChatRole.User, user)], options);
			try { return (JsonSerializer.Deserialize<T>(r.Text, Json), r.Usage?.InputTokenCount ?? 0, r.Text, null); }
			catch (JsonException) { return (null, r.Usage?.InputTokenCount ?? 0, r.Text, "not parseable"); }
		}
		catch (Exception ex) { return (null, 0, "", "call failed: " + ex.GetType().Name); }
	}

	static async Task<(Contribution? Value, long InTokens, string Raw, string? Error)> AskContribution(string system, string user)
	{
		if (Environment.GetEnvironmentVariable("FOLD_ORDER") != "2") return await Ask<Contribution>(system, user);
		var (v, tokens, raw, error) = await Ask<ContributionStatusLast>(system, user);
		return (v is null ? null : new Contribution(v.status, v.summary, v.confirmed, v.evidence, v.context, v.proposed_criteria, v.open_questions, v.reason), tokens, raw, error);
	}

	const string SourceQuery =
		"Query review, checkout service, 14 Oct.\n" +
		"The checkout page runs the query SELECT id, total FROM orders WHERE customer_id = @c AND created_at > @since ORDER BY created_at DESC once per page load. " +
		"The execution plan shows a full scan of the orders table, 41 million rows. The orders table has a primary key on id and no other index. " +
		"The payments table was also reviewed and is fine: it has an index on order_id. The search service is not part of this review. " +
		"A second query on the page, which loads the shopping cart, takes 4 ms and is not a concern.";

	const string SourcePerf =
		"Performance report, week 42.\n" +
		"Checkout p95 latency was 840 ms on Monday and 910 ms on Friday. Search p95 was 310 ms. " +
		"The checkout latency objective in the service level agreement is p95 under 200 ms. The objective for search is p95 under 400 ms. " +
		"Load tests were run with 500 concurrent users. CPU on the web tier stayed below 40 percent throughout.";

	const string SourceOps =
		"Database operations note.\n" +
		"Creating an index on the orders table takes about 25 minutes and must be run between 01:00 and 04:00. " +
		"Any schema change on orders needs written approval from the DBA team before it is merged. " +
		"The read replica can serve checkout reads, so the load test can run there. The users table is due for archiving next quarter, which is unrelated.";

	const string SourceLog =
		"Log analysis, auth gateway, 20 Oct.\n" +
		"Between 08:00 and 12:00 the login endpoint returned HTTP 500 for 18,300 requests. Every failing request had no X-Tenant header. " +
		"Requests from the web client always include it. The password reset endpoint returned 200 for all requests in the same period. CPU and memory on the gateway were normal.";

	const string SourceClient =
		"Mobile client review.\n" +
		"In the mobile app the shared HTTP client adds headers from a settings object. Version 3.2.1 replaced the settings object with a new configuration loader, " +
		"and the loader does not copy the tenant setting into the headers. The desktop client is a separate codebase and was not checked. " +
		"The app's push notification module was also changed in 3.2.1 but does not make HTTP calls.";

	const string SourceRelease =
		"Release notes.\n" +
		"Mobile 3.2.0 shipped 2 September. Mobile 3.2.1 shipped 12 October and is installed on 61 percent of devices. Mobile 3.3.0 is planned for 3 November. " +
		"App store review takes up to 2 days. The web client was last released on 9 October.";

	// FOLD_PROMPT=2 selects the second wording, written after the first one failed on three of the five cases.
	static readonly string SpecialistSystem = Environment.GetEnvironmentVariable("FOLD_PROMPT") == "2" ? SpecialistSystem2 : SpecialistSystem1;

	const string SpecialistSystem2 =
		"You are an analyst cell. You are given the aim of a goal and a source. Report what the source says that bears on the aim, and nothing else. " +
		"confirmed: every fact in the source that bears on the aim, one per entry, each with an id (c1, c2, ...). A fact belongs in confirmed even if it does not finish the aim on its own. Do not put such facts in context. " +
		"evidence: one entry per confirmed fact, with supports set to the fact's id and quote set to the exact words copied from the source. " +
		"context: only background that does not bear on the aim. " +
		"proposed_criteria: a requirement or objective the source states that the finished work must meet, such as a target or a rule. Empty if the source states none. " +
		"open_questions: what the aim needs that the source does not say, or that the source says has not been checked. " +
		"status: answered if the source has at least one fact that bears on the aim, otherwise cannot_answer with a reason. Never state a fact the source does not state. Reply only with JSON.";

	const string SpecialistSystem1 =
		"You are an analyst cell. You are given the aim of a goal and a source. Report only what the source states. " +
		"confirmed: facts the source states plainly, each with an id (c1, c2, ...). " +
		"evidence: for each confirmed fact, a verbatim quote copied from the source, with supports set to the fact's id. " +
		"context: useful background that is not a finding. " +
		"proposed_criteria: acceptance criteria the source says must hold, or an empty list if the source states none. " +
		"open_questions: what the aim needs that the source does not say, or only hedges. " +
		"Never state a fact the source does not state. If the source has nothing relevant to the aim, set status to cannot_answer, give a reason, and leave the lists empty. Reply only with JSON.";

	static string Norm(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", " "), "\\s+", " ").Trim();
	static IEnumerable<string> Numbers(string s) => Regex.Matches(s.Replace(",", ""), "\\d+(\\.\\d+)?").Select(m => m.Value);
	static bool Mentions(IEnumerable<string> texts, string word) => texts.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));

	// Problems that apply to every contribution, whatever the case.
	static List<string> Honesty(Contribution c, string source)
	{
		var problems = new List<string>();
		var src = Norm(source);
		var srcNumbers = Numbers(source).ToHashSet();
		var ids = c.confirmed.Select(f => f.id).ToHashSet();
		foreach (var q in c.evidence)
		{
			if (!src.Contains(Norm(q.quote))) problems.Add("quote not in source");
			if (!ids.Contains(q.supports)) problems.Add("evidence points at a missing fact");
		}
		foreach (var f in c.confirmed)
		{
			if (!c.evidence.Any(q => q.supports == f.id)) problems.Add("confirmed fact without evidence");
			if (Numbers(f.statement).Any(n => !srcNumbers.Contains(n))) problems.Add("number not in source: " + f.statement);
		}
		return problems;
	}

	sealed record SpecialistCase(string Name, string Aim, string Source, Func<Contribution, List<string>> Check);

	static string Trunc(string s) => s.Length > 110 ? s[..110] + "..." : s;

	public static async Task RunAsync(int repeats)
	{
		var openAi = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("not-needed"),
			new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
		_client = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();
		await _client.GetResponseAsync("Say ok.", new ChatOptions { MaxOutputTokens = 5 });

		await Part1(repeats);
		await Part2(repeats);
		await Part3(repeats);
	}

	// Debug: print one raw reply for the first, second and fifth specialist cases.
	public static async Task RawAsync()
	{
		_client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("not-needed"),
			new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") }).GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();
		foreach (var (aim, source) in new[] { ("Raise a pull request that fixes the slow checkout query.", SourceQuery), ("Find who owns the checkout service and what its p95 latency is.", "The checkout service is owned by the Payments team. It is deployed twice a week."), ("Raise a pull request that fixes the slow checkout query.", SourcePerf) })
		{
			var (_, tokens, raw, _) = await AskContribution(SpecialistSystem, $"Aim: {aim}\n\nSource:\n{source}");
			Console.WriteLine($"--- {Trunc(aim)} ({tokens} input tokens)\n{raw}\n");
		}
	}

	// ---------- Part 1 ----------
	static async Task Part1(int repeats)
	{
		Console.WriteLine("=== Part 1: a specialist fills the contribution fields");
		Console.WriteLine("schema sent: " + ChatResponseFormat.ForJsonSchema<Contribution>(Json).Schema);
		var cases = new SpecialistCase[]
		{
			new("findings", "Raise a pull request that fixes the slow checkout query.", SourceQuery, c =>
			{
				var p = Honesty(c, SourceQuery);
				if (c.status != Status.answered) p.Add("status " + c.status);
				if (!Mentions(c.confirmed.Select(f => f.statement), "orders")) p.Add("missed: orders table");
				if (!Mentions(c.confirmed.Select(f => f.statement), "index") && !Mentions(c.confirmed.Select(f => f.statement), "scan")) p.Add("missed: no index / full scan");
				if (c.proposed_criteria.Count > 0) p.Add("invented a criterion: " + c.proposed_criteria[0]);
				return p;
			}),
			new("silent on part", "Find who owns the checkout service and what its p95 latency is.", "The checkout service is owned by the Payments team. It is deployed twice a week.", c =>
			{
				var p = Honesty(c, "The checkout service is owned by the Payments team. It is deployed twice a week.");
				if (!Mentions(c.confirmed.Select(f => f.statement), "Payments")) p.Add("missed: owner");
				if (!Mentions(c.open_questions.Concat(c.context).Append(c.reason ?? ""), "latency") && !Mentions(c.open_questions.Concat(c.context).Append(c.reason ?? ""), "p95")) p.Add("did not say p95 is unknown");
				return p;
			}),
			new("hedged claim", "Find out whether the gateway cache is enabled.", "I think the cache might be disabled on the gateway, but I have not checked. The gateway restarts nightly at 02:00.", c =>
			{
				var p = Honesty(c, "I think the cache might be disabled on the gateway, but I have not checked. The gateway restarts nightly at 02:00.");
				if (Mentions(c.confirmed.Select(f => f.statement), "cache")) p.Add("confirmed a hedged claim: " + c.confirmed.First(f => f.statement.Contains("cache", StringComparison.OrdinalIgnoreCase)).statement);
				if (!Mentions(c.open_questions.Concat(c.context).Append(c.reason ?? ""), "cache")) p.Add("lost the cache question");
				return p;
			}),
			new("irrelevant source", "Find the retry limit for webhook delivery.", "The office kitchen is restocked on Tuesdays and the coffee machine is descaled monthly.", c =>
			{
				var p = Honesty(c, "The office kitchen is restocked on Tuesdays and the coffee machine is descaled monthly.");
				if (c.status != Status.cannot_answer) p.Add("status " + c.status);
				if (c.confirmed.Count > 0) p.Add("confirmed something: " + c.confirmed[0].statement);
				if (string.IsNullOrWhiteSpace(c.reason)) p.Add("no reason");
				return p;
			}),
			new("states a requirement", "Raise a pull request that fixes the slow checkout query.", SourcePerf, c =>
			{
				var p = Honesty(c, SourcePerf);
				if (!Mentions(c.proposed_criteria, "200")) p.Add("did not propose the 200 ms objective");
				return p;
			}),
		};

		var perCase = new Dictionary<string, (int Calls, int Parsed, int Clean)>();
		var categories = new Dictionary<string, int>();
		var examples = new Dictionary<string, string>();
		foreach (var c in cases)
		{
			var t = (Calls: 0, Parsed: 0, Clean: 0);
			for (var r = 0; r < repeats; r++)
			{
				t.Calls++;
				var (value, _, raw, error) = await AskContribution(SpecialistSystem, $"Aim: {c.Aim}\n\nSource:\n{c.Source}");
				if (value is null) { Count(categories, error ?? "null"); examples.TryAdd(c.Name + "/" + (error ?? "null"), Trunc(raw)); continue; }
				t.Parsed++;
				var problems = c.Check(value);
				if (problems.Count == 0) t.Clean++;
				foreach (var problem in problems.Select(p => p.Contains(':') ? p[..p.IndexOf(':')] : p).Distinct()) Count(categories, c.Name + ": " + problem);
				foreach (var problem in problems) examples.TryAdd(c.Name + "/" + (problem.Contains(':') ? problem[..problem.IndexOf(':')] : problem), problem);
			}
			perCase[c.Name] = t;
		}
		Console.WriteLine($"{"case",-20} {"calls",5} {"parsed",6} {"clean",6}");
		foreach (var (name, t) in perCase) Console.WriteLine($"{name,-20} {t.Calls,5} {t.Parsed,6} {t.Clean,6}");
		Console.WriteLine("problems (number of replies with the problem):");
		foreach (var (k, v) in categories.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {v,3}x {k}   e.g. {examples.GetValueOrDefault(k.Replace(": ", "/"), "")}");
	}

	static void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

	const string CriterionSystem = "You are a planning cell. A specialist proposed an acceptance criterion for a goal. The aim never changes and the criteria say what must be true for the goal to be done. " +
		"accept: the proposal is relevant to the aim, measurable, and not already covered or contradicted by an existing criterion. " +
		"change: relevant but too vague, so give a measurable version in criterion. " +
		"reject: not relevant to the aim, already covered, or it contradicts an existing criterion. Reply only with JSON.";

	// ---------- Part 2 ----------
	static async Task Part2(int repeats)
	{
		Console.WriteLine();
		Console.WriteLine("=== Part 2: the planning cell judges a proposed acceptance criterion");
		const string sys = CriterionSystem;
		const string aim = "Raise a pull request that fixes the slow checkout query.";
		var cases = new (string Name, string Existing, string Proposed, Decision[] Wanted)[]
		{
			("new and measurable", "- A pull request is raised.", "Checkout p95 latency is under 200 ms (from the performance report).", [Decision.accept]),
			("already covered", "- A pull request is raised.\n- Checkout p95 latency is under 200 ms.", "The checkout query is faster than it is now.", [Decision.reject, Decision.change]),
			("contradicts", "- A pull request is raised.\n- Checkout p95 latency is under 200 ms.", "Checkout p95 latency is under 500 ms.", [Decision.reject]),
			("out of scope", "- A pull request is raised.", "The whole checkout page is rewritten in React.", [Decision.reject]),
			("vague", "- A pull request is raised.", "The checkout is good and fast.", [Decision.change, Decision.reject]),
			("process requirement", "- A pull request is raised.", "The DBA team has given written approval before the pull request is merged (from the database operations note).", [Decision.accept]),
		};
		Console.WriteLine($"{"case",-22} {"calls",5} {"parsed",6} {"right",6}   decisions");
		foreach (var c in cases)
		{
			int parsed = 0, right = 0;
			var seen = new Dictionary<Decision, int>();
			for (var r = 0; r < repeats; r++)
			{
				var (v, _, _, _) = await Ask<CriterionReply>(sys, $"Aim: {aim}\nExisting criteria:\n{c.Existing}\nProposed by a specialist: {c.Proposed}", 300);
				if (v is null) continue;
				parsed++;
				seen[v.decision] = seen.GetValueOrDefault(v.decision) + 1;
				if (c.Wanted.Contains(v.decision) && (v.decision == Decision.reject || !string.IsNullOrWhiteSpace(v.criterion))) right++;
			}
			Console.WriteLine($"{c.Name,-22} {repeats,5} {parsed,6} {right,6}   {string.Join(", ", seen.Select(kv => $"{kv.Key} {kv.Value}"))}   (wanted {string.Join(" or ", c.Wanted)})");
		}
	}

	// ---------- Part 3 ----------
	sealed record Scenario(string Name, string Aim, string Criteria, (string Cell, string Source)[] Sources, string Question, string[] MustKeep, Func<string, (bool Right, string Why)> CheckFinal);

	static string Body(string aim, string criteria, List<(string Cell, Contribution C)> parts, bool withEvidence)
	{
		var sb = new StringBuilder();
		sb.AppendLine("Aim: " + aim);
		sb.AppendLine("Acceptance criteria:\n- " + criteria);
		sb.AppendLine("Context:");
		foreach (var (cell, c) in parts) foreach (var x in c.context) sb.AppendLine($"- {x} ({cell})");
		sb.AppendLine("Confirmed:");
		foreach (var (cell, c) in parts)
			foreach (var f in c.confirmed)
			{
				sb.AppendLine($"- {f.statement} ({cell})");
				if (withEvidence) foreach (var q in c.evidence.Where(q => q.supports == f.id)) sb.AppendLine($"    evidence: \"{q.quote}\"");
			}
		sb.AppendLine("Open questions:");
		foreach (var (cell, c) in parts) foreach (var x in c.open_questions) sb.AppendLine($"- {x} ({cell})");
		return sb.ToString();
	}

	static async Task Part3(int repeats)
	{
		Console.WriteLine();
		Console.WriteLine("=== Part 3: after folding, does a later cell still get what it needs?");
		var scenarios = new Scenario[]
		{
			new("checkout query", "Raise a pull request that fixes the slow checkout query.", "A pull request is raised that fixes the slow checkout query.",
				[("query-analyst", SourceQuery), ("performance-analyst", SourcePerf), ("database-ops-analyst", SourceOps)],
				"Fill in: table (the table to change), columns (the columns to index), p95_target_ms (the latency target in milliseconds) and blocker (what must happen before the pull request is merged).",
				["orders", "customer_id", "created_at", "200", "approval"],
				text =>
				{
					Answer1? a; try { a = JsonSerializer.Deserialize<Answer1>(text, Json); } catch (JsonException) { return (false, "not parseable"); }
					if (a is null) return (false, "null");
					if (!a.table.Contains("orders", StringComparison.OrdinalIgnoreCase)) return (false, "table " + a.table);
					if (!a.columns.Any(c => c.Contains("customer_id", StringComparison.OrdinalIgnoreCase)) || !a.columns.Any(c => c.Contains("created_at", StringComparison.OrdinalIgnoreCase))) return (false, "columns " + string.Join("/", a.columns));
					if (a.p95_target_ms != 200) return (false, "target " + a.p95_target_ms);
					if (!a.blocker.Contains("approv", StringComparison.OrdinalIgnoreCase) && !a.blocker.Contains("DBA", StringComparison.OrdinalIgnoreCase)) return (false, "blocker " + Trunc(a.blocker));
					return (true, "");
				}),
			new("mobile login", "Write a fix plan for the mobile login failures.", "A fix plan is written for the mobile login failures.",
				[("log-analyst", SourceLog), ("client-analyst", SourceClient), ("release-analyst", SourceRelease)],
				"Fill in: header (the request header that is missing), first_bad_version (the first mobile app version with the fault) and http_status (the status code the endpoint returns for the failing requests).",
				["X-Tenant", "3.2.1", "500"],
				text =>
				{
					Answer2? a; try { a = JsonSerializer.Deserialize<Answer2>(text, Json); } catch (JsonException) { return (false, "not parseable"); }
					if (a is null) return (false, "null");
					if (!a.header.Contains("X-Tenant", StringComparison.OrdinalIgnoreCase)) return (false, "header " + a.header);
					if (!a.first_bad_version.Contains("3.2.1")) return (false, "version " + a.first_bad_version);
					if (a.http_status != 500) return (false, "status " + a.http_status);
					return (true, "");
				}),
		};

		foreach (var s in scenarios)
		{
			var modes = new[] { "body with evidence", "body without evidence", "full transcript" };
			var right = modes.ToDictionary(m => m, _ => 0);
			var tokens = modes.ToDictionary(m => m, _ => new List<long>());
			var whys = new List<string>();
			var keepWith = s.MustKeep.ToDictionary(k => k, _ => 0);
			var keepWithout = s.MustKeep.ToDictionary(k => k, _ => 0);
			var specialistsFailed = 0;
			var runs = 0;
			int proposed = 0, taken = 0;
			for (var r = 0; r < repeats; r++)
			{
				var parts = new List<(string Cell, Contribution C)>();
				var transcript = new StringBuilder();
				var ok = true;
				foreach (var (cell, source) in s.Sources)
				{
					var user = $"Aim: {s.Aim}\n\nSource:\n{source}";
					var (v, _, raw, _) = await AskContribution(SpecialistSystem, user);
					if (v is null) { ok = false; break; }
					parts.Add((cell, v));
					transcript.AppendLine($"[{cell} was given]\n{user}\n[{cell} replied]\n{raw}\n");
				}
				if (!ok) { specialistsFailed++; continue; }
				runs++;
				// The planning cell decides each proposed criterion, as designed, before the body is given to the next cell.
				var criteria = new List<string> { s.Criteria };
				foreach (var proposal in parts.SelectMany(x => x.C.proposed_criteria).Distinct())
				{
					proposed++;
					var (cr, _, _, _) = await Ask<CriterionReply>(CriterionSystem, $"Aim: {s.Aim}\nExisting criteria:\n{string.Join("\n", criteria.Select(c => "- " + c))}\nProposed by a specialist: {proposal}", 300);
					if (cr is null) continue;
					if (cr.decision == Decision.accept) { criteria.Add(proposal); taken++; }
					else if (cr.decision == Decision.change && !string.IsNullOrWhiteSpace(cr.criterion)) { criteria.Add(cr.criterion); taken++; }
				}
				var criteriaText = string.Join("\n- ", criteria);
				var withEv = Body(s.Aim, criteriaText, parts, true);
				var withoutEv = Body(s.Aim, criteriaText, parts, false);
				foreach (var k in s.MustKeep)
				{
					if (withEv.Contains(k, StringComparison.OrdinalIgnoreCase)) keepWith[k]++;
					if (withoutEv.Contains(k, StringComparison.OrdinalIgnoreCase)) keepWithout[k]++;
				}
				const string finalSys = "You are a writer cell. You are given a goal and what is known about it. Answer using only what is known. Reply only with JSON.";
				var inputs = new Dictionary<string, string>
				{
					["body with evidence"] = withEv + "\n" + s.Question,
					["body without evidence"] = withoutEv + "\n" + s.Question,
					["full transcript"] = $"Aim: {s.Aim}\nTranscript of the cells that worked on it:\n{transcript}\n{s.Question}",
				};
				foreach (var m in modes)
				{
					var (tok, raw) = s.Name == "checkout query"
						? await AskFinal<Answer1>(finalSys, inputs[m])
						: await AskFinal<Answer2>(finalSys, inputs[m]);
					tokens[m].Add(tok);
					var (isRight, why) = s.CheckFinal(raw);
					if (isRight) right[m]++; else whys.Add($"{m}: {why}");
				}
			}
			Console.WriteLine();
			Console.WriteLine($"-- {s.Name}: {runs} complete runs of {repeats} (specialist replies unparseable in {specialistsFailed})");
			Console.WriteLine($"   criteria proposed by specialists: {proposed}, taken into the goal by the planning cell: {taken}");
			Console.WriteLine("   facts still in the folded body, of " + runs + ": " + string.Join(", ", s.MustKeep.Select(k => $"{k} {keepWithout[k]}/{keepWith[k]}")) + "   (without evidence / with evidence)");
			foreach (var m in modes)
			{
				var t = tokens[m].OrderBy(x => x).ToList();
				Console.WriteLine($"   {m,-22} right {right[m],2}/{runs}   input tokens median {(t.Count == 0 ? 0 : t[t.Count / 2])}");
			}
			foreach (var g in whys.GroupBy(w => w).OrderByDescending(g => g.Count()).Take(8)) Console.WriteLine($"   wrong {g.Count()}x  {g.Key}");
		}
	}

	static async Task<(long InTokens, string Raw)> AskFinal<T>(string system, string user) where T : class
	{
		var (_, tokens, raw, _) = await Ask<T>(system, user, 300);
		return (tokens, raw);
	}
}
