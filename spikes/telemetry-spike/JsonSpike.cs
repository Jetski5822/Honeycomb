using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

// Tests how reliably a local model returns a valid result object over many calls.
// Two modes: a JSON schema passed as the response format, and instructions only.

sealed record CellResult(string Status, string? Answer, string? Reason);

static class JsonSpike
{
	private static readonly string[] AllowedStatus = ["answered", "cannot_answer", "checkpointed"];

	// (prompt, expected status). Clear answerable questions and questions that cannot be answered without data the model is not given.
	private static readonly (string Prompt, string Expected)[] Cases =
	[
		("What is 6 times 7?", "answered"),
		("What is the capital of France?", "answered"),
		("Name the first month of the year.", "answered"),
		("How many days are in a week?", "answered"),
		("What colour do you get by mixing blue and yellow?", "answered"),
		("What is the current balance of bank account 83920-A?", "cannot_answer"),
		("Which pull request fixed ticket PLNNET-48211?", "cannot_answer"),
		("What did the customer say in yesterday's support call?", "cannot_answer"),
		("What is the password for the production database?", "cannot_answer"),
		("What is the status of build number 1894?", "cannot_answer"),
	];

	private const string Instructions =
		"You are a cell in a larger system. Reply with ONLY a JSON object with the fields: " +
		"\"status\" (one of: answered, cannot_answer, checkpointed), \"answer\" (a string, or null), \"reason\" (a string, or null). " +
		"Use status answered with an answer when you can answer from general knowledge. " +
		"Use status cannot_answer with a reason when the question needs information you were not given. Never guess.";

	public static async Task RunAsync(int repeats)
	{
		var openAi = new OpenAI.OpenAIClient(
			new System.ClientModel.ApiKeyCredential("not-needed"),
			new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
		IChatClient client = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();

		// Warm-up, so model load time is not counted.
		await client.GetResponseAsync("Say ok.", new ChatOptions { MaxOutputTokens = 5 });

		foreach (var withSchema in new[] { true, false })
		{
			var label = withSchema ? "JSON schema response format" : "instructions only";
			var times = new List<double>();
			int total = 0, parsed = 0, validStatus = 0, correct = 0, reasonOk = 0, answerOk = 0, tokensIn = 0, tokensOut = 0;
			var failures = new List<string>();

			for (var r = 0; r < repeats; r++)
			{
				foreach (var (prompt, expected) in Cases)
				{
					total++;
					var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 200 };
					if (withSchema) options.ResponseFormat = ChatResponseFormat.ForJsonSchema<CellResult>();
					var messages = new List<ChatMessage> { new(ChatRole.System, Instructions), new(ChatRole.User, prompt) };

					var sw = Stopwatch.StartNew();
					string text;
					try
					{
						var response = await client.GetResponseAsync(messages, options);
						text = response.Text;
						tokensIn += (int)(response.Usage?.InputTokenCount ?? 0);
						tokensOut += (int)(response.Usage?.OutputTokenCount ?? 0);
					}
					catch (Exception ex)
					{
						failures.Add($"call failed: {ex.GetType().Name}");
						continue;
					}
					sw.Stop();
					times.Add(sw.Elapsed.TotalSeconds);

					CellResult? result = null;
					try { result = JsonSerializer.Deserialize<CellResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
					catch (JsonException) { failures.Add($"not JSON: {Trunc(text)}"); continue; }
					if (result is null) { failures.Add($"null result: {Trunc(text)}"); continue; }
					parsed++;
					if (AllowedStatus.Contains(result.Status)) validStatus++; else failures.Add($"bad status '{result.Status}'");
					if (result.Status == expected) correct++;
					else failures.Add($"expected {expected}, got {result.Status}: \"{prompt}\" -> {Trunc(result.Reason ?? result.Answer ?? "")}");
					if (result.Status == "cannot_answer" && !string.IsNullOrWhiteSpace(result.Reason)) reasonOk++;
					if (result.Status == "answered" && !string.IsNullOrWhiteSpace(result.Answer)) answerOk++;
				}
			}

			times.Sort();
			double P(double p) => times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)Math.Ceiling(p * times.Count) - 1)];
			Console.WriteLine();
			Console.WriteLine($"=== {label}: {total} calls");
			Console.WriteLine($"  parsed as the result type:      {parsed}/{total}");
			Console.WriteLine($"  status in the allowed set:      {validStatus}/{total}");
			Console.WriteLine($"  expected status (answerable vs not): {correct}/{total}");
			Console.WriteLine($"  cannot_answer carried a reason: {reasonOk}, answered carried an answer: {answerOk}");
			Console.WriteLine($"  latency p50 {P(0.5):0.0}s  p95 {P(0.95):0.0}s  max {P(1.0):0.0}s");
			Console.WriteLine($"  tokens: {tokensIn} in, {tokensOut} out");
			foreach (var f in failures.Take(5)) Console.WriteLine($"  failure: {f}");
		}
	}

	private static string Trunc(string s) => s.Length > 80 ? s[..80] + "..." : s;
}
