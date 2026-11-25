using System.Diagnostics;
using System.Net;
using System.Text;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// The Anthropic adapter, against a fake Anthropic server on localhost. No key and no network are needed.
static class AnthropicSpike
{
	public static async Task RunAsync(Action<string, bool, string> check)
	{
		var port = 18000 + Random.Shared.Next(1000);
		using var listener = new HttpListener();
		listener.Prefixes.Add($"http://localhost:{port}/");
		listener.Start();
		var seen = new List<(string Path, string? Key, string Body)>();
		_ = Task.Run(async () =>
		{
			while (listener.IsListening)
			{
				HttpListenerContext c;
				try { c = await listener.GetContextAsync(); } catch { break; }
				using var reader = new StreamReader(c.Request.InputStream);
				var body = await reader.ReadToEndAsync();
				lock (seen) seen.Add((c.Request.Url!.AbsolutePath, c.Request.Headers["x-api-key"], body));
				var reply = """{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"hello from the fake server"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":11,"output_tokens":7}}""";
				var bytes = Encoding.UTF8.GetBytes(reply);
				c.Response.ContentType = "application/json"; c.Response.StatusCode = 200;
				await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
			}
		});

		var spans = new List<Activity>();
		using var al = new ActivityListener
		{
			ShouldListenTo = s => s.Name == Telemetry.Name,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = a => { lock (spans) spans.Add(a); },
		};
		ActivitySource.AddActivityListener(al);

		var client = new AnthropicClient { ApiKey = "test-key", BaseUrl = $"http://localhost:{port}" };

		// (a) MAF's own helper
		var helperAgent = client.AsAIAgent(model: "claude-haiku-5-5", instructions: "Be brief.", name: "anth-helper");
		var wrapped = new OpenTelemetryAgent(helperAgent, sourceName: Telemetry.Name);
		var response = await wrapped.RunAsync("hi");
		check("MAF's AsAIAgent completes a chat through the Anthropic SDK", response.Text == "hello from the fake server", response.Text);
		(string Path, string? Key, string Body) req; lock (seen) req = seen.Last();
		check("the request went to /v1/messages with the api key header", req.Path == "/v1/messages" && req.Key == "test-key", $"{req.Path} key={req.Key}");
		check("the request body names the model and carries the user text", req.Body.Contains("claude-haiku-5-5") && req.Body.Contains("hi"), "");
		Activity top; lock (spans) top = spans.Last(a => a.DisplayName.StartsWith("invoke_agent"));
		string Tag(Activity a, string k) => a.GetTagItem(k)?.ToString() ?? "";
		check("the span reports provider anthropic and the usage from the response", Tag(top, "gen_ai.provider.name") == "anthropic" && Tag(top, "gen_ai.usage.input_tokens") == "11" && Tag(top, "gen_ai.usage.output_tokens") == "7", $"provider={Tag(top, "gen_ai.provider.name")} in={Tag(top, "gen_ai.usage.input_tokens")} out={Tag(top, "gen_ai.usage.output_tokens")}");
		var helperId = Tag(top, "gen_ai.agent.id");
		check("AsAIAgent gives a random agent id (so the cell Id cannot be set through the helper)", Guid.TryParseExact(helperId, "N", out _), helperId);

		// (b) IChatClient from the SDK, with the hive's own agent and Id
		IChatClient chat = client.AsIChatClient("claude-haiku-5-5");
		var own = new ChatClientAgent(chat, new ChatClientAgentOptions { Id = "writer", Name = "writer" });
		var wrapped2 = new OpenTelemetryAgent(own, sourceName: Telemetry.Name);
		var response2 = await wrapped2.RunAsync("hi again");
		Activity top2; lock (spans) top2 = spans.Last(a => a.DisplayName.StartsWith("invoke_agent"));
		check("the SDK's IChatClient works under the hive's own ChatClientAgent with Id set to the cell type", response2.Text == "hello from the fake server" && Tag(top2, "gen_ai.agent.id") == "writer", $"id={Tag(top2, "gen_ai.agent.id")} name={top2.DisplayName}");
		listener.Stop();
	}
}
