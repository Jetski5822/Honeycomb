using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

// Reliability of a local model with a larger result schema, longer contexts, and answerable vs unanswerable inputs.

sealed record Finding(
	[property: Description("Short title")] string Title,
	[property: Description("One of: low, medium, high")] string Severity,
	[property: Description("A quote copied from the ticket that supports the finding")] string Evidence);

sealed record AnalysisResult(
	[property: Description("One of: answered, cannot_answer")] string Status,
	[property: Description("Why the question cannot be answered. Null when answered")] string? Reason,
	[property: Description("Up to 3 findings. Empty when cannot_answer")] List<Finding> Findings,
	[property: Description("Questions to ask next. May be empty")] List<string> NextQuestions,
	[property: Description("A number from 0 to 1")] double Confidence,
	[property: Description("One sentence summary")] string Summary);

static class JsonSpike2
{
	private static readonly string[] Facts =
	[
		"The login endpoint /api/v2/login returns HTTP 500 when the X-Tenant header is missing.",
		"Session tokens are stored in the browser's localStorage and are not cleared on logout.",
		"Password reset links remain valid for 30 days and can be reused.",
		"The admin role check in AuditController is performed in the client and not on the server.",
		"Failed login attempts are not rate limited for service accounts.",
	];

	private static string Padding(int paragraphs, int seed)
	{
		var sb = new StringBuilder();
		for (var i = 0; i < paragraphs; i++)
		{
			sb.AppendLine($"Note {seed}-{i}: the weekly planning meeting on day {(i % 5) + 1} covered capacity for sprint {20 + i}, " +
				$"with {3 + i} engineers available and {100 + i * 7} story points committed. The retrospective action number {i + 1} was to review " +
				$"the build pipeline duration, currently {12 + i} minutes, and to agree on a target of {8 + (i % 3)} minutes.");
		}
		return sb.ToString();
	}

	internal static string Ticket(bool hasAuthContent, int paragraphs, int seed)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"TICKET PLN-{4000 + seed}");
		sb.AppendLine(hasAuthContent
			? "Summary: Customers report problems signing in and staying signed in."
			: "Summary: Change the colour of the secondary button on the settings page.");
		sb.AppendLine("Description:");
		if (hasAuthContent) foreach (var f in Facts.Take(3 + seed % 3)) sb.AppendLine("- " + f);
		else sb.AppendLine("- The secondary button should use the theme's accent colour, not grey.\n- The change applies to the settings page only.");
		sb.Append(Padding(paragraphs, seed));
		return sb.ToString();
	}

	internal const string Instructions =
		"You are an analysis cell. Reply with ONLY a JSON object with the fields: status (answered or cannot_answer), reason (string or null), " +
		"findings (array of {title, severity (low|medium|high), evidence}), nextQuestions (array of strings), confidence (number 0 to 1), summary (string). " +
		"Findings must be about authentication and session problems only, with evidence quoted from the ticket. " +
		"If the ticket contains nothing about authentication or sessions, use status cannot_answer with a reason and an empty findings array. Never invent evidence.";

	public static async Task RunAsync(int repeats)
	{
		var openAi = new OpenAI.OpenAIClient(
			new System.ClientModel.ApiKeyCredential("not-needed"),
			new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
		IChatClient client = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();
		await client.GetResponseAsync("Say ok.", new ChatOptions { MaxOutputTokens = 5 });

		// (label, paragraphs of padding)
		var sizes = new[] { ("short", 0), ("medium", 12), ("long", 40) };

		foreach (var withSchema in new[] { true, false })
		{
			Console.WriteLine();
			Console.WriteLine($"##### {(withSchema ? "JSON schema response format" : "instructions only")}");
			foreach (var (label, paragraphs) in sizes)
			{
				foreach (var hasAuth in new[] { true, false })
				{
					int total = 0, parsed = 0, schemaOk = 0, statusExpected = 0, grounded = 0, withEvidence = 0, failedCalls = 0, tokensIn = 0, tokensOut = 0;
					var times = new List<double>();
					var problems = new List<string>();
					for (var r = 0; r < repeats; r++)
					{
						total++;
						var ticket = Ticket(hasAuth, paragraphs, r + 1);
						var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 700 };
						if (withSchema) options.ResponseFormat = ChatResponseFormat.ForJsonSchema<AnalysisResult>();
						var messages = new List<ChatMessage> { new(ChatRole.System, Instructions), new(ChatRole.User, ticket + "\nQuestion: list the authentication and session problems.") };
						var sw = Stopwatch.StartNew();
						string text;
						try
						{
							var response = await client.GetResponseAsync(messages, options);
							text = response.Text;
							tokensIn += (int)(response.Usage?.InputTokenCount ?? 0);
							tokensOut += (int)(response.Usage?.OutputTokenCount ?? 0);
						}
						catch (Exception ex) { failedCalls++; problems.Add($"call failed: {ex.GetType().Name}: {Trunc(ex.Message)}"); continue; }
						times.Add(sw.Elapsed.TotalSeconds);

						AnalysisResult? result;
						try { result = JsonSerializer.Deserialize<AnalysisResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
						catch (JsonException) { problems.Add($"not JSON: {Trunc(text)}"); continue; }
						if (result is null) { problems.Add("null result"); continue; }
						parsed++;

						var ok = (result.Status is "answered" or "cannot_answer")
							&& result.Findings is not null && result.NextQuestions is not null
							&& result.Confidence is >= 0 and <= 1
							&& !string.IsNullOrWhiteSpace(result.Summary)
							&& result.Findings.Count <= 3
							&& result.Findings.All(f => f.Severity is "low" or "medium" or "high" && !string.IsNullOrWhiteSpace(f.Evidence));
						if (ok) schemaOk++; else problems.Add($"schema violation: status={result.Status} findings={result.Findings?.Count} conf={result.Confidence}");

						var expected = hasAuth ? "answered" : "cannot_answer";
						if (result.Status == expected) statusExpected++; else problems.Add($"expected {expected}, got {result.Status}");

						if (result.Findings is not null)
							foreach (var f in result.Findings)
							{
								withEvidence++;
								if (EvidenceInTicket(f.Evidence, ticket)) grounded++;
							}
					}
					times.Sort();
					double P(double p) => times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)Math.Ceiling(p * times.Count) - 1)];
					Console.WriteLine($"[{label,-6} ticket, {(hasAuth ? "auth content   " : "no auth content")}] calls={total} failed={failedCalls} parsed={parsed} schemaOk={schemaOk} statusExpected={statusExpected} " +
						$"evidenceGrounded={grounded}/{withEvidence} median={P(0.5):0.0}s p95={P(0.95):0.0}s avgTokensIn={(total - failedCalls == 0 ? 0 : tokensIn / (total - failedCalls))}");
					foreach (var p in problems.Distinct().Take(3)) Console.WriteLine($"      problem: {p}");
				}
			}
		}
	}

	// Evidence counts as grounded if it appears in the ticket after loose normalisation, or most of its words do.
	private static bool EvidenceInTicket(string evidence, string ticket)
	{
		static string Norm(string s) => new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Replace("  ", " ");
		var e = Norm(evidence); var t = Norm(ticket);
		if (e.Length == 0) return false;
		if (t.Contains(e)) return true;
		var words = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return words.Length > 0 && words.Count(w => t.Contains(w)) >= words.Length * 0.8;
	}

	private static string Trunc(string s) => s.Length > 90 ? s[..90] + "..." : s;
}
