using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// MCP tools bound to a cell, with the server in the same process. Checks tool binding and what telemetry the SDK adds.
static class McpSpike
{
	[McpServerToolType]
	public sealed class EchoTools
	{
		public static string? ServerSideTraceParent;
		public static string? ServerSideParentOperation;

		[McpServerTool(Name = "echo"), Description("Echoes the text back.")]
		public static string Echo([Description("text to echo")] string text)
		{
			ServerSideTraceParent = Activity.Current?.Id;
			ServerSideParentOperation = Activity.Current?.Parent?.OperationName ?? Activity.Current?.Parent?.DisplayName;
			return "echo: " + text;
		}
	}

	public static async Task RunAsync(Action<string, bool, string> check)
	{
		var all = new List<Activity>();
		using var al = new ActivityListener
		{
			ShouldListenTo = _ => true,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = a => { lock (all) all.Add(a); },
		};
		ActivitySource.AddActivityListener(al);

		var c2s = new Pipe(); var s2c = new Pipe();
		await using var server = McpServer.Create(
			new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream()),
			new McpServerOptions { ServerInfo = new Implementation { Name = "spike", Version = "1" }, ToolCollection = [McpServerTool.Create(typeof(EchoTools).GetMethod(nameof(EchoTools.Echo))!)] });
		var serverTask = server.RunAsync();
		await using var client = await McpClient.CreateAsync(new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));

		var tools = await client.ListToolsAsync();
		check("the MCP client lists the server's tool", tools.Any(t => t.Name == "echo"), string.Join(",", tools.Select(t => t.Name)));

		// A scripted model asks for the tool, then answers.
		var chat = new McpScripted();
		var agent = new ChatClientAgent(chat.AsBuilder().UseOpenTelemetry(sourceName: "Honeycomb").Build(), new ChatClientAgentOptions
		{
			Id = "mcp-cell", Name = "mcp-cell", ChatOptions = new ChatOptions { Tools = [.. tools] },
		});
		var wrapped = new OpenTelemetryAgent(agent, sourceName: "Honeycomb");
		Activity root;
		using (var src = new ActivitySource("Honeycomb"))
		{
			root = src.StartActivity("invoke_workflow hive")!;
			var response = await wrapped.RunAsync("please echo hello");
			root.Dispose();
			check("the model's tool call reached the MCP server and the result came back", response.Text.Contains("echo: hello"), response.Text);
		}

		List<Activity> sp; lock (all) sp = all.ToList();
		Console.WriteLine("    spans seen (name | kind | source):");
		foreach (var a in sp.Where(a => a.TraceId == root.TraceId || a.Source.Name.Contains("ModelContext")).OrderBy(a => a.StartTimeUtc))
			Console.WriteLine($"      {a.DisplayName,-45} {a.Kind,-9} {a.Source.Name}");

		var exec = sp.FirstOrDefault(a => a.DisplayName.StartsWith("execute_tool"));
		check("an execute_tool span exists for the MCP tool, named after the tool", exec is not null && exec.DisplayName == "execute_tool echo", exec?.DisplayName);
		var mcpSpans = sp.Where(a => a.Source.Name.Contains("ModelContext", StringComparison.OrdinalIgnoreCase)).ToList();
		check("the MCP SDK emits its own spans", mcpSpans.Count > 0, $"{mcpSpans.Count} spans from: {string.Join(",", mcpSpans.Select(a => a.Source.Name).Distinct())}");
		Console.WriteLine($"    the tool ran with trace parent {EchoTools.ServerSideTraceParent} (parent operation: {EchoTools.ServerSideParentOperation})");
		check("the tool handler runs inside the same trace as the cell (context reached the server)", EchoTools.ServerSideTraceParent is not null && EchoTools.ServerSideTraceParent.Contains(root.TraceId.ToString()), EchoTools.ServerSideTraceParent);
	}

	sealed class McpScripted : IChatClient
	{
		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
		{
			var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().FirstOrDefault();
			var reply = result is not null
				? new ChatMessage(ChatRole.Assistant, "tool said: " + result.Result)
				: new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "echo", new Dictionary<string, object?> { ["text"] = "hello" })]);
			return Task.FromResult(new ChatResponse(reply) { ModelId = "scripted", Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 } });
		}
		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", new Uri("http://localhost/"), "scripted") : null;
		public void Dispose() { }
	}
}
