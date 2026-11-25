using System.Text.Json;
using Microsoft.Extensions.AI;

// Does the local server enforce the response format at all? Use schemas the model would not produce on its own.
static class JsonSpike4
{
	public static async Task RunAsync()
	{
		var openAi = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("x"), new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://localhost:12434/engines/v1") });
		IChatClient client = openAi.GetChatClient("docker.io/ai/qwen2.5:7B-Q4_0").AsIChatClient();

		async Task Try(string label, string schemaJson, string prompt)
		{
			var schema = JsonDocument.Parse(schemaJson).RootElement;
			var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 150, ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "result") };
			var text = (await client.GetResponseAsync(prompt, options)).Text;
			Console.WriteLine($"{label}\n   -> {text.Replace("\n", " ")}");
		}

		await Try("1. a required constant field the prompt never mentions",
			"""{"type":"object","properties":{"zebra":{"const":"stripes"},"answer":{"type":"string"}},"required":["zebra","answer"],"additionalProperties":false}""",
			"What is 2 plus 2? Reply in JSON.");
		await Try("2. an enum that excludes the answer the prompt asks for",
			"""{"type":"object","properties":{"colour":{"enum":["red","green"]}},"required":["colour"],"additionalProperties":false}""",
			"Pick the colour blue. Reply in JSON with the field colour.");
		await Try("3. an array capped at 1 item, asked for three",
			"""{"type":"object","properties":{"items":{"type":"array","items":{"type":"string"},"maxItems":1}},"required":["items"],"additionalProperties":false}""",
			"List three fruits. Reply in JSON with the field items.");
		await Try("4. a string with maxLength 3, asked for a long word",
			"""{"type":"object","properties":{"word":{"type":"string","maxLength":3}},"required":["word"],"additionalProperties":false}""",
			"Give the word 'extraordinary'. Reply in JSON with the field word.");
		await Try("5. an integer with maximum 5, asked for 100",
			"""{"type":"object","properties":{"n":{"type":"integer","maximum":5}},"required":["n"],"additionalProperties":false}""",
			"Give the number 100. Reply in JSON with the field n.");
	}
}
