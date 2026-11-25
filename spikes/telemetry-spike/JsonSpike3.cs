using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.AI;

// Does a maxItems constraint in the JSON schema reach the model, and does the local server enforce it?

sealed record CappedResult(
	[property: Description("One of: answered, cannot_answer")] string Status,
	[property: Description("Why the question cannot be answered. Null when answered")] string? Reason,
	[property: Description("Findings"), MaxLength(3)] List<Finding> Findings,
	[property: Description("Questions to ask next. May be empty")] List<string> NextQuestions,
	[property: Description("A number from 0 to 1")] double Confidence,
	[property: Description("One sentence summary")] string Summary);

static class JsonSpike3
{
	public static async Task RunAsync(int repeats)
	{
		var schemaCapped = AIJsonUtilities.CreateJsonSchema(typeof(CappedResult));
		var schemaPlain = AIJsonUtilities.CreateJsonSchema(typeof(AnalysisResult));
		Console.WriteLine($"generated schema mentions maxItems: capped={schemaCapped.ToString().Contains("maxItems")} plain={schemaPlain.ToString().Contains("maxItems")}");

		var openAi = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("x"), new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
		IChatClient client = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();
		await client.GetResponseAsync("ok", new ChatOptions { MaxOutputTokens = 3 });

		foreach (var capped in new[] { true, false })
		{
			int total = 0, parsed = 0, within = 0; var counts = new List<int>();
			for (var r = 0; r < repeats; r++)
			{
				// seeds 1..6 give 4,5,3,4,5,3 facts in the ticket, so a model that lists every fact exceeds three on four of six.
				var ticket = JsonSpike2.Ticket(true, 0, (r % 6) + 1);
				var options = new ChatOptions
				{
					Temperature = 0.2f, MaxOutputTokens = 700,
					ResponseFormat = capped ? ChatResponseFormat.ForJsonSchema<CappedResult>() : ChatResponseFormat.ForJsonSchema<AnalysisResult>(),
				};
				var messages = new List<ChatMessage> { new(ChatRole.System, JsonSpike2.Instructions), new(ChatRole.User, ticket + "\nQuestion: list the authentication and session problems.") };
				var text = (await client.GetResponseAsync(messages, options)).Text;
				total++;
				try
				{
					var res = JsonSerializer.Deserialize<AnalysisResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
					parsed++; counts.Add(res!.Findings.Count); if (res.Findings.Count <= 3) within++;
				}
				catch (JsonException) { counts.Add(-1); }
			}
			Console.WriteLine($"schema with maxItems={capped,-5}: parsed {parsed}/{total}, findings<=3 in {within}/{total}, counts per call: {string.Join(",", counts)}");
		}
	}
}
