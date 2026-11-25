using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

// Tests the "not tested" nested-workflow items:
//  - the inner workflow's own telemetry, nested under the outer one
//  - a checkpoint taken while the nested run is parked on a human request, resumed later by fresh objects
//  - resuming the outer workflow from its own checkpoint, and whether the outer handler runs again

static class NestedResumeSpike
{
	public static int OuterHandlerCalls;
	public static CheckpointInfo? InnerCheckpoint;
	public static ExternalRequest? InnerRequest;
	public static string InnerStatus = "";
	public static string Dir = "";
	public static ActivitySource? Source;

	public static Workflow BuildInner()
	{
		var ask = new AskExecutor();
		var port = RequestPort.Create<string, string>("human");
		var finish = new FinishExecutor();
		return new WorkflowBuilder(ask)
			.AddEdge(ask, port)
			.AddEdge(port, finish)
			.WithOutputFrom(finish)
			.WithOpenTelemetry(activitySource: Source)
			.Build();
	}

	public static async Task RunAsync(ActivitySource hive, Action<string> printTree)
	{
		Source = hive;
		Dir = Path.Combine(Path.GetTempPath(), "honeycomb-spike-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Dir);
		Console.WriteLine($"checkpoint directory: {Dir}");

		// ---- Part A: the outer workflow runs an inner workflow in its handler. The inner one parks on a human request. ----
		Console.WriteLine();
		Console.WriteLine("=== Part A: outer workflow (in-memory checkpoints) runs inner workflow (file checkpoints) that parks on a human request");
		var outerExec = new OuterExecutor("outer");
		var outer = new WorkflowBuilder(outerExec).WithOutputFrom(outerExec).WithOpenTelemetry(activitySource: hive).Build();
		var outerMgr = CheckpointManager.CreateInMemory();
		CheckpointInfo? outerCheckpoint;
		await using (var outerRun = await InProcessExecution.RunAsync(outer, "the question", outerMgr))
		{
			Console.WriteLine($"  outer status: {await outerRun.GetStatusAsync()}");
			foreach (var e in outerRun.OutgoingEvents.OfType<WorkflowOutputEvent>()) Console.WriteLine($"  outer output: {e.Data}");
			outerCheckpoint = outerRun.LastCheckpoint;
			Console.WriteLine($"  outer checkpoints: {outerRun.Checkpoints.Count}");
		}
		Console.WriteLine($"  inner run status when the outer handler returned: {InnerStatus}");
		Console.WriteLine($"  inner pending request captured: {(InnerRequest is null ? "NO" : "yes, id " + InnerRequest.RequestId)}");
		Console.WriteLine($"  inner checkpoint captured: {(InnerCheckpoint is null ? "NO" : "yes, " + InnerCheckpoint.CheckpointId)}");
		Console.WriteLine($"  outer handler calls so far: {OuterHandlerCalls}");
		printTree("Part A spans (inner workflow telemetry enabled on the same source)");

		// ---- Part B: resume the outer workflow from its own checkpoint. Does the handler run again? ----
		Console.WriteLine();
		Console.WriteLine("=== Part B: resume the outer workflow from its checkpoint");
		var before = OuterHandlerCalls;
		try
		{
			// The same executor id must be used for the resumed workflow to match the checkpoint.
			var e2 = new OuterExecutor("outer");
			var outer2 = new WorkflowBuilder(e2).WithOutputFrom(e2).Build();
			var noToken = Environment.GetEnvironmentVariable("NO_TOKEN") == "1";
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			var token = noToken ? CancellationToken.None : cts.Token;
			Console.WriteLine($"  calling ResumeAsync (cancellation token: {(noToken ? "none" : "20s")}) ...");
			await using var resumed = await InProcessExecution.ResumeAsync(outer2, outerCheckpoint!, outerMgr, token);
			Console.WriteLine("  ResumeAsync returned");
			Console.WriteLine($"  handler calls caused by the resume so far: {OuterHandlerCalls - before}");
			Console.WriteLine($"  resumed status: {await resumed.GetStatusAsync(token)}");
			Console.WriteLine($"  outgoing events: {string.Join(", ", resumed.OutgoingEvents.Select(e => e.GetType().Name))}");
			Console.WriteLine($"  handler calls caused by the resume: {OuterHandlerCalls - before}");
		}
		catch (OperationCanceledException)
		{
			Console.WriteLine($"  TIMED OUT after 20s. handler calls caused by the resume so far: {OuterHandlerCalls - before}");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}

		// ---- Part C: a "new process": fresh objects, same checkpoint directory. Resume the inner checkpoint and answer. ----
		Console.WriteLine();
		Console.WriteLine("=== Part C: fresh objects resume the inner checkpoint taken during the nested run, then the human answers");
		try
		{
			using var freshStore = new FileSystemJsonCheckpointStore(new DirectoryInfo(Dir));
			var freshMgr = CheckpointManager.CreateJson(freshStore);
			var freshInner = BuildInner();
			await using var run = await InProcessExecution.ResumeAsync(freshInner, InnerCheckpoint!, freshMgr);
			Console.WriteLine($"  status after restore: {await run.GetStatusAsync()}");
			var reRaised = run.OutgoingEvents.OfType<RequestInfoEvent>().ToList();
			Console.WriteLine($"  pending request re-raised on restore: {reRaised.Count}");
			var request = reRaised.FirstOrDefault()?.Request ?? InnerRequest!;
			Console.WriteLine($"  request id matches the original: {request.RequestId == InnerRequest!.RequestId}");
			var ok = await run.ResumeAsync([request.CreateResponse("42")]);
			Console.WriteLine($"  response accepted: {ok}");
			Console.WriteLine($"  status after the answer: {await run.GetStatusAsync()}");
			foreach (var o in run.OutgoingEvents.OfType<WorkflowOutputEvent>()) Console.WriteLine($"  output: {o.Data}");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		printTree("Parts B and C spans");
	}
}

sealed class AskExecutor() : Executor<string, string>("ask")
{
	public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
	{
		await context.QueueStateUpdateAsync("note", "state-set-before-the-pause", "shared", cancellationToken);
		return $"Please answer: {message}";
	}
}

sealed class FinishExecutor() : Executor<string, string>("finish")
{
	public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
	{
		var note = await context.ReadStateAsync<string>("note", "shared", cancellationToken);
		var text = $"human said '{message}', restored state '{note}'";
		await context.YieldOutputAsync(text, cancellationToken);
		return text;
	}
}

sealed class OuterExecutor(string id) : Executor<string, string>(id)
{
	public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
	{
		NestedResumeSpike.OuterHandlerCalls++;
		using var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(NestedResumeSpike.Dir));
		var mgr = CheckpointManager.CreateJson(store);
		await using var run = await InProcessExecution.RunAsync(NestedResumeSpike.BuildInner(), message, mgr, cancellationToken: cancellationToken);
		NestedResumeSpike.InnerStatus = (await run.GetStatusAsync()).ToString();
		NestedResumeSpike.InnerRequest = run.OutgoingEvents.OfType<RequestInfoEvent>().FirstOrDefault()?.Request;
		NestedResumeSpike.InnerCheckpoint = run.LastCheckpoint;
		var text = "inner parked";
		await context.YieldOutputAsync(text, cancellationToken);
		return text;
	}
}
