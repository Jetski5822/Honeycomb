# Tech stack

The libraries and structure that implement the architecture in [architecture.md](../architecture.md). Items marked **Decision** record a choice and the reason for it. What was tested, and what was not, is in [evidence.md](evidence.md).

## Principle

Honeycomb is assembled from existing open source libraries. It writes only what is specific to a hive: the planning loop, the rules around it (depth, judge, re-spin counters, checkpoints), and the way cells are described. Everything else comes from a library, and a new in-house component needs a reason why no library covers it.

The main library is the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF). This document was checked against a local clone at commit `809cc04f2` (2026-10-19). What was and was not checked is listed at the end. NuGet packages are not pinned: every package is on its latest version (see Platform). Findings about MAF below describe that commit, and a later version may differ.

---

## Reuse map

| Need | Reuse | Honeycomb writes |
|---|---|---|
| Model abstraction | `Microsoft.Extensions.AI` (`IChatClient`) | Nothing |
| A cell as an agent | MAF `Microsoft.Agents.AI` (`AIAgent`, `ChatClientAgent`) | The hive-specific behaviour around it |
| Anthropic | MAF `Microsoft.Agents.AI.Anthropic`, on the `Anthropic` SDK | Nothing |
| Local model | Docker Model Runner, through its OpenAI-compatible endpoint with `Microsoft.Extensions.AI.OpenAI` | Nothing |
| Tools | `ModelContextProtocol` (MCP) | Nothing |
| A cell used as a tool | `AIFunctionFactory.Create` over the neighbour's `AIAgent` | The neighbour tool: result validation, the neighbour rule (which cells may call which), `execute_tool` recording |
| Structured results | `Microsoft.Extensions.AI` structured output, with JSON schema from the C# type | Validation, bounded re-ask |
| Context size | MAF `Compaction` strategies, `Microsoft.ML.Tokenizers` | Folding results into the goal |
| Model-call telemetry | `Microsoft.Extensions.AI` OpenTelemetry chat client | Nothing |
| Cell telemetry | MAF `OpenTelemetryAgent` (`invoke_agent`, `gen_ai.agent.*`) | The `honeycomb.*` attributes and events |
| Export | `OpenTelemetry` and its OTLP and console exporters | Nothing |
| Human approval | Nothing. MAF's request and response types are tied to a workflow graph (see Checkpoints) | The human request and response, and the checkpoint semantics |
| Parallel and sequential composition | Nothing. MAF Workflows were prototyped and are not used (see Orchestration) | The fan-out and fan-in |
| Planning loop, depth, judge, counters | Nothing exists | **All of it** |
| Cell definitions | `YamlDotNet` for frontmatter | The definition format |

## Platform

| Concern | Choice |
|---|---|
| Language / runtime | C# (latest), .NET 10, nullable enabled, implicit usings |
| Solution | `.slnx`, central package management |
| Package versions | Not pinned. Every NuGet package is on its latest stable version |
| Build strictness | `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild` |
| Host | A one-shot headless CLI. A hive run is a process. A checkpointed run ends the process, and a `resume` command starts a new one |

**Decision:** .NET 10, a single target framework. MAF multi-targets down to .NET Framework 4.7.2, and Honeycomb has no reason to. The architecture's DI scopes, linked cancellation tokens and per-cell telemetry map onto `IServiceScope`, `CancellationTokenSource.CreateLinkedTokenSource` and `ActivitySource`.

**Decision:** packages track the latest stable version. Pre-releases are excluded. A package that has only pre-release versions, such as `Microsoft.Agents.AI.Mcp` (alpha-only in the clone), is therefore not used until it has a stable release. The mechanism (for example floating versions in the central `Directory.Packages.props`) is not verified to work with central package management on the chosen feed, and it makes a build depend on the day it restores.

**Decision:** the hive core is a library with no host or HTTP surface. The CLI is one host of it.

## Cells

A cell is an `AIAgent`.

| Concern | Choice |
|---|---|
| Cell runtime | `ChatClientAgent` over a keyed `IChatClient` |
| Cell definition | A markdown file with YAML frontmatter: cell type, purpose, neighbours, an optional model-class hint (`local`, `frontier` or `any`), result schema reference, `repeatable` (default `false`, see Checkpoints). Parsed with `YamlDotNet`. The hint does not choose the model. See Providers and routing |
| Neighbours | The neighbour cells are given to a specialist as `AIFunction` tools built by the hive (see below), so the model decides when to consult one, with no planner in between |
| Planner | A planning cell is an `AIAgent` that also owns the hive rules below |
| Tools | `ModelContextProtocol` servers, loaded from a workspace `mcp.json` |

**Decision:** building cells on `AIAgent` gets agent telemetry, tool calling, sessions and the Anthropic and OpenAI adapters for free, and keeps the door open to MAF's hosting (A2A, AG-UI) later. The cost is a dependency on a large, fast-moving framework. In the clone, the core packages (`Microsoft.Agents.AI`, `.Abstractions`, `.Workflows`, `.Anthropic`, `.OpenAI`) are marked released at version prefix `1.26.0`, but `OpenTelemetryAgent` and parts of the API carry `[Experimental]` attributes, and `Microsoft.Agents.AI.Mcp` is `alpha`. What is actually on the package feed has not been checked.

**Decision:** a neighbour is exposed as a tool, which means the neighbour rule is enforced by what tools a cell is given. A cell cannot consult a cell it was not handed. This would settle open decision 3 in the telemetry document: a consult is a tool call, so it is `execute_tool` with the transfer attributes.

What the source of `AsAIFunction()` shows (read, not run):

- The generated tool takes one `query` string and returns `response.Text`, a string. A typed JSON result comes back as text, and the hive parses and validates it itself.
- With no session passed, `RunAsync` creates a fresh session for each call (`session ??= await this.CreateSessionAsync(...)` in `ChatClientAgent`). So a neighbour called this way starts with an empty history each time, which is the separate context the design needs. If a session is passed, the function is stateful, and the documentation warns against concurrent use.
- The parent's `AdditionalProperties` from its current function-invocation context are copied into the child's run options. This is a channel through which parent state could reach a neighbour, against the "small, separate context" rule. What those properties carry in practice has not been checked.

**Decision:** the hive builds its own neighbour tool and does not use `AsAIFunction()`. `AsAIFunction()` is a convenience that builds an `AIFunction` calling `agent.RunAsync`. The hive builds its own with `AIFunctionFactory.Create`, a few lines that:

- call the neighbour's `RunAsync` with a fresh session and run options the hive builds itself, so nothing from the parent's `AdditionalProperties` reaches the neighbour;
- parse and validate the reply against the neighbour's result schema, and return the validated JSON and not free text;
- record the consult as `execute_tool` with the transfer attributes, and refuse a neighbour the caller was not given.

Why not use `AsAIFunction()`: the hive needs all three behaviours above whatever it does, so wrapping it would add nothing. The neighbour still runs as an `AIAgent`, so MAF is reused for the agent and only the tool is the hive's.

## Orchestration

This is where nothing off the shelf matches, so it is the main in-house component.

| Concern | Choice |
|---|---|
| Planning loop | In-house: take a goal, decompose, create cells, fold their contributions into the goal body, check the acceptance criteria |
| Depth, judge, hard cap | In-house |
| Re-spin counters | In-house, taking a `TimeProvider` so the windowed reset can be tested with `FakeTimeProvider` |
| Scopes and cancellation | `IServiceScope` and linked `CancellationTokenSource` per cell, created by the planning cell from its own `IServiceProvider`, with a scoped `ICellContext` holder (see below) |

What the MAF source shows, and why the loop is in-house:

- **A workflow is fixed once built.** `WorkflowBuilder.Build()` returns an immutable `Workflow`, and edges and sub-workflows (`SubworkflowBinding`) are given to the builder beforehand. A hive's tree is decided at run time by planners, so it cannot be declared up front.
- **Handoff is not the hive's neighbour model.** The Handoff orchestration lets agents transfer control through `handoff_to_<agent>` tool calls, but over a shared conversation, which is the transcript-sharing the architecture is built to avoid.
- **Magentic is a single manager over participants,** and so does not describe a tree of planners.

**Decision:** the hive's loop, and its fan-out and fan-in for a planner's parallel goals, are written directly. MAF Workflows were prototyped for the parallel case, and are not used. The prototype showed that an executor can build and run a nested workflow at run time inside its handler, so that is not the reason. The reason is that MAF names a workflow executor `{Name}_{Id}`, and two cells of one type in one workflow fail to bind with `Cannot bind executor with ID 'jira_analysis_jira_analysis' because an executor with the same ID but different instance is already bound`. Giving each cell a unique `Id` to avoid this would put a per-instance value back into its span name (see the telemetry document).

**Decision:** cells are created at run time, each in its own scope. Implementations declare their `CellType` and are matched from `IEnumerable<ICell>` in the new scope. There is no factory.

A planning cell creates each child scope itself, from its own `IServiceProvider`:

1. `CreateAsyncScope()` for the child scope.
2. `CancellationTokenSource.CreateLinkedTokenSource` on the planner's token, for the child token.
3. Set the scoped `ICellContext` holder (run id, cell id, parent cell id, depth) before anything else is resolved in the new scope.
4. Dispose the child scopes and token sources when the planner finishes.

`ICellContext` is in `Hive.Abstractions`. It is how a child learns who its parent is, which the container does not do itself. This was checked with a small test on .NET 10: in `Microsoft.Extensions.DependencyInjection`, a scope created from inside another scope is **not nested**. It does not share the parent's scoped instances, and it is not disposed when the parent is. A child scope created from the planner's `IServiceProvider`, with the context holder set first, resolved a worker that saw the child's context, and the planner's own context was unchanged. There is one planning-cell class, so the four steps are written once.

## Results

| Concern | Choice |
|---|---|
| Schema | One C# result type per cell, JSON schema generated from it |
| Format request | Passed as the response format where the provider supports it |
| Validation | In code. A reply that fails is re-asked a bounded number of times, then the cell ends with status `Error` and records `result.invalid` |
| Contributions | Typed fields on every result, folded into the goal body by code (see below) |
| Serialisation | `System.Text.Json` |

**Decision: a result carries contributions.** Besides `status` and `summary`, every cell's result has five fields that the hive folds into the goal body, so no model is asked to rewrite the goal. A cell does a job and reports on it, and never edits the goal.

| Field | Meaning |
|---|---|
| `confirmed` | Facts the cell established, each with an id local to the reply (`c1`, `c2`) and a statement |
| `evidence` | What supports a fact: a verbatim quote or a reference, with `supports` set to the id of the fact |
| `context` | Background that is useful and not a finding |
| `proposed_criteria` | Acceptance criteria the cell proposes. Only proposals: the planning cell decides |
| `open_questions` | What the aim needs and the cell's input did not say, or only hedged |

A cell that cannot answer returns its `reason`, and may put what it learned along the way in `context` and `open_questions`. A cell that reaches its context limit returns `partial`, with its `reason` and the contributions it had established (see Context and tokens).

**Decision: the contributions come before `status` in the result schema.** A model writes the fields in order, and with `status` first the 7B model chose `cannot_answer` before it had written down any fact, then left `confirmed` empty and put everything in `context`. With `status` and `summary` last it was clean on 43 of 50 replies in each of two runs, against 18 to 20 of 50 (see [evidence.md](evidence.md), fifth round). The order is part of the contract, so the C# type declares the contributions first.

Every limit is validated in code, as above (the number of entries per field and the length of each). Two further checks are made in code and not left to the schema: every `supports` names a fact in the same reply, and every confirmed fact has at least one piece of evidence. A quote that does not appear in the cell's input could also be rejected, which is cheap to do wherever the input is text the hive holds.

**Finding: bounds reach the server only if the schema is sent as written.** `ChatResponseFormat.ForJsonSchema<T>()` through the `Microsoft.Extensions.AI` OpenAI adapter removes `maxItems` and the other bounds from the schema and writes them into the field's description as prose, because OpenAI's strict mode rejects them. The local server never sees them. Docker Model Runner does enforce `maxItems` and `maxLength` when they arrive (seventh round in [evidence.md](evidence.md)). Sending the schema through the OpenAI SDK's own response format, from `RawRepresentationFactory`, keeps it as written. Cap a list and its strings together, because a cap alone let the model run on inside the last string. This is a finding and not a decision: whether the hive sends schemas that way is open.

### The goal

`Goal` is a record in `Cells.Abstractions`: an `Aim` that never changes, and a body of five parts.

| Part | Written by |
|---|---|
| Acceptance criteria | The planning cell only. A specialist's `proposed_criteria` are proposals, which the planning cell accepts, changes or rejects |
| Context | Folded from `context` |
| Confirmed | Folded from `confirmed` |
| Evidence | Folded from `evidence`, kept with the fact it supports |
| Open questions | Folded from `open_questions`, and removed when answered |

Each entry carries the `CellId` of the cell that contributed it, so a later cell can see where a fact came from. Folding is a deterministic append in code. A part has a size limit set for the hive like the other limits, and the planning cell compresses a part, with a model, only when the part passes its limit. That is the one point where a model rewrites the body. The planning cell checks the acceptance criteria before it returns its own result, and "all children finished" is not the test for done.

A cell is given the aim and the parts of the body its planning cell chooses, and not the whole body by default. The transcripts are stored and referenced and are not passed on.

## Providers and routing

| Concern | Choice |
|---|---|
| Frontier | Anthropic, through MAF's Anthropic adapter |
| Local | Docker Model Runner, through its OpenAI-compatible endpoint with `Microsoft.Extensions.AI.OpenAI` |
| Selection | A planning cell picks a provider and model for each cell it creates, from keyed `IChatClient` services, and records `routing.selected`. The same cell type can be given a different model on the next goal |

**Decision:** a cell is not bound to a model size. Small is the goal the cell is given. When a planning cell creates a cell, including a specialist, the judge and a nested planning cell, it chooses the provider and model from the providers declared for the run. A definition may hint `local`, `frontier` or `any`, and the hint does not decide. The root planning cell has no parent, so the hive chooses its model from that same declared set when the run starts.

**Decision:** the local runtime is Docker Model Runner. The hive talks to it over HTTP and does not manage containers, so it needs only an endpoint and a model name, both configuration. Starting the runtime is the host's concern.

Checked on this machine: Docker Model Runner is running (llama.cpp backend) and `http://localhost:12434/engines/v1/models` returns an OpenAI-style model list (`qwen2.5:7B-Q4_0` and `qwen3-embedding:4b`). Docker reports a different endpoint for use from inside a container (`http://model-runner.docker.internal/v1/`), so the endpoint must be configuration. A test run (see [evidence.md](evidence.md)) completed a chat against it with `Microsoft.Extensions.AI.OpenAI`, using a JSON schema response format. Not verified: that host TCP access is on by default, and that the 7B model handles larger result schemas reliably.

## Context and tokens

| Concern | Choice |
|---|---|
| Counting | `Microsoft.ML.Tokenizers` |
| Compaction | MAF `Compaction` strategies, off by default. A safety net inside a single cell that is rarely reached, and not a way of keeping a cell going (see below) |
| Context limit | A token limit per cell, counted with the tokenizer. A cell that reaches it stops and returns `partial` (see below) |
| Folding | In-house. This is the hive's answer to growing context, and it works at the goal, not the message list. It is code applying typed contributions (see Results), with a model only to compress a part past its limit |

Compaction and folding do different jobs. Compaction trims one agent's own message history. Folding grows the goal's body between cells, so that a cell starts with a small context in the first place.

**Decision: a cell is not compacted. At its context limit it stops and returns `partial`.** The limit is a token count of the cell's message list, set below the model's window so that there is room for the reply, and with a margin because the tokenizer counts 8 to 10% under what the local model reports. A `partial` result carries the contributions the cell has already established (`confirmed`, `evidence`, `open_questions`) and a `reason`, and the planning cell folds them in like any other result and decides whether to re-spin with a narrower aim, through the counters it already has. MAF `Compaction` stays available for a cell type that needs it, and is configured per cell type. If it is configured, use the strategy that keeps the text of the tool results, which is the default tool result collapse, and not a summary or a window.

Why, from the sixth round in [evidence.md](evidence.md): a cell that returns evidence needs the words of its tool results to still be in its history when it writes the result, and compaction removes them. A local 7B cell with eight tool results (about 1,400 tokens), compacted past a low limit, 10 runs per condition, quotes checked word for word against the tool results:

| Strategy | Tokens sent | Facts found (of 5) | Quotes verbatim | What went wrong |
|---|---|---|---|---|
| None | 1,378 | 3.4 (5.0 with the answer at the end of each result) | 100% (84% with the answer at the end) | |
| Sliding window | 175 | 0.0 | 0% | The window kept only the last message, and the cell invented facts and numbers anyway |
| Truncation | 716 | 0.1 | 82% | Dropped the oldest results, which held the answer. Its quotes were real, about the wrong material |
| Summarising | 988 | 3.7 | 18% | Facts survive, quotes are reworded, and a quote check would reject about 80% |
| Default tool result collapse | 1,169 to 1,225 | 3.8 to 3.9 (4.6 to 5.0 with the answer at the end) | 88 to 94% (59% with the answer at the end) | Saves only 11 to 15%, because tool results are nearly all of the history |
| Collapse to the first 150 or 300 characters | 686 to 912 | 3.4 to 4.9 at the start, 1.0 to 2.0 at the end | 62 to 69% | Works only when the answer sits at the start of each result |

The two cheap strategies fail silently, and the cell answers confidently from what is left. A summary keeps the facts and loses the evidence, and the hive's rule is that every confirmed fact has evidence. The only strategy that kept most of the evidence saved 11 to 15%, which is little against stopping at a limit and letting the planner re-spin. Taking notes as each result arrives kept every quote (98 to 100%, including after a summary), but cost 8 extra model calls and about 2,060 input tokens for each cell, found fewer facts (2.2 to 3.0 of 5, against 3.4 and 5.0 with no notes) and sent more tokens than a plain summary (1,191 against 988), so it is not used. A fuzzy check that accepts a reworded quote when most of its words are in the stored results turned away 23 of 39 quotes after a summary and did not show that a quote supports its fact, so it is not used either.

What this does not show: one scenario, 18 messages, the local 7B model as cell and as summariser, a limit set low to force compaction, scripted tool calls, and a keyword match for facts. It gives no figure for a long history at a realistic limit, a frontier model, or a cell that picks its own tool calls. Where the answer sat in each result moved the baseline more than most strategies did, so a cell's behaviour on its own tool results should be tried before a strategy is chosen for it.

## Checkpoints

| Concern | Choice |
|---|---|
| Store | JSON on the filesystem, behind an interface in `Honeycomb.State.Abstractions`. All IO goes through `IFileSystem` |
| Implementation | Adapted from an existing `IFileSystem`-based JSON checkpoint store (save, load, clear, path segments sanitised), extended as described below |
| Human response | In-house. MAF's request and response types are tied to a workflow graph |

**Decision:** all filesystem access in the stack goes through `IFileSystem`, with no exceptions. MAF's `FileSystemJsonCheckpointStore` takes a real `DirectoryInfo` and so cannot be used. MAF's checkpoint manager is not used either.

How MAF checkpointing works (read in the clone):

- A checkpoint is taken at the end of every superstep, after all executors in that step have finished.
- It holds the step number, a description of the workflow (executors, edges, request ports and outputs), the runner state (which executors have been instantiated, the queued messages for each executor, and the outstanding external requests), the shared scoped state, the state of edges (such as fan-in buffers) and a link to the parent checkpoint.
- Resume restores that state into a workflow that must match the checkpointed description. `WorkflowInfo` refuses a workflow that differs, so a checkpoint cannot be restored into a different graph.
- `ICheckpointManager` and `Checkpoint` are `internal`. The stores (`JsonCheckpointStore`) are public.

What carries over to the hive: a checkpoint is a snapshot of everything needed to continue, with pending work and outstanding human requests included, and a parent link so there is a history. What does not: the hive's tree is not a fixed graph, so the snapshot must describe the tree itself.

**Decision: what a hive checkpoint stores.** A snapshot is written to JSON when a cell checkpoints, and not only at the moment it is raised, so that the paths that keep running are captured consistently. There is a single latest snapshot per run. A history of snapshots, which MAF keeps with a parent link, is deferred until something needs it.

| Part | Contents |
|---|---|
| Header | Schema version, `RunId`, time written, the original question |
| Tree | One node per cell: durable `CellId`, parent `CellId`, `repeatable` as declared, `CellType`, depth, the goal as it stands (the aim and the whole body, each entry with the `CellId` that contributed it; for a planning cell this includes what its children have returned so far), state (`pending`, `running`, `answered`, `cannot-answer`, `checkpointed`, `cancelled`), result or a reference to it, re-spin attempt, the provider and model chosen |
| Counters | For each of the three re-spin scopes: current value, window start, total |
| Transcript | References into the transcript store, not the text |
| Human request | For each checkpointed cell: the reason, what is being asked, and the answer once given |
| Trace link | The cell's W3C `traceparent` and `tracestate`, for the telemetry document's resume link |

Rules for resuming:

- A cell that was `running` when the process stopped, and is `repeatable`, is re-run from its goal. A cell is not resumed part way through a model call.
- A cell that was `running` and is not `repeatable` is not re-run. It becomes a checkpoint that asks the human to check what the cell had already done, before it continues. This reuses the human-in-the-hive mechanism. `repeatable` defaults to `false` in the cell definition, so a cell author opts in.
- Only the checkpointed cell's path waits. Cells that were `answered` are not run again, and their results are read from the snapshot.
- Counters are restored, and the time-window counters use the `TimeProvider`, so the time spent waiting for a human counts toward the windows like any other time.
- A snapshot is written to a temporary file and then moved into place, using `IFileSystem`. A checkpoint is what a human is waiting on, so a half-written file must never replace a good one. A corrupt snapshot is an error and is not treated as "no checkpoint", because starting afresh would silently lose the human's pending request.

**Decision: a durable cell id.** The tree is keyed on a `CellId`, a `Vogen` value object in `Cells.Abstractions`, and not on span ids. The telemetry document says the span id identifies a cell, but span ids do not survive a resume. This needs a change in the telemetry document: a `honeycomb.cell.id` attribute on each cell's span, so a snapshot and a trace can be joined. That change belongs to the telemetry document and has not been made.

**Decision: non-repeatable work.** Re-running a `running` cell is only safe if its work is repeatable. A specialist that has already edited a document or opened a pull request would repeat that, which is why a cell declares `repeatable` and a non-repeatable one is handed to the human on resume.

## Observability

Specified in [telemetry.md](telemetry.md).

| Concern | Choice |
|---|---|
| Tracing and metrics | `OpenTelemetry`. One `ActivitySource` and one `Meter`, named `Honeycomb` |
| Cell spans | MAF's `OpenTelemetryAgent`, which emits `invoke_agent` with `gen_ai.agent.name` |
| Model-call spans and metrics | The `Microsoft.Extensions.AI` OpenTelemetry chat client |
| Hive spans, events, metrics | In-house: `invoke_workflow` root, `plan`, the `honeycomb.*` attributes, span events and three counters |
| Export | OTLP, with the console exporter when no endpoint is set |
| Logging | `Microsoft.Extensions.Logging` with source-generated `[LoggerMessage]` delegates |

What the MAF source shows about telemetry, which bears on open decision 2 in the telemetry document:

- `OpenTelemetryAgent` delegates to `Microsoft.Extensions.AI`'s `OpenTelemetryChatClient`, and then relabels the resulting span as `invoke_agent` with `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.agent.id` and `gen_ai.agent.name`. In a test run, token usage appeared on the `invoke_agent` span as the total across the cell's model calls, with `chat` spans carrying the per-call values.
- The span display name is `invoke_agent {Name}({Id})`, which differs from the telemetry document's `invoke_agent {gen_ai.agent.name}`.
- It sets `gen_ai.agent.id` from the agent's `Id`, which defaults to a new GUID per instance. The telemetry document sets `Id` to the cell type, so the attribute holds the type and not an instance.
- The default `ActivitySource` name is `Experimental.Microsoft.Agents.AI`, not `Honeycomb`, so the host must subscribe to both. The member is marked `[Experimental]`.
- MAF's own workflow telemetry does not use the GenAI names. It uses its own (`workflow_invoke`, `executor.process`), so a hive that used MAF Workflows would have two naming schemes in one trace.

## Conventions

| Concern | Choice |
|---|---|
| Module pattern | Every subsystem is `Honeycomb.X` (implementation) plus `Honeycomb.X.Abstractions` (contracts only), as in `Microsoft.Extensions`. Implementations reference other subsystems through their abstractions only (see Proposed projects) |
| DI | Constructor injection, one constructor per class, no nullable dependencies, no service locator |
| DI registration | Explicit `Add{Subsystem}` extension methods |
| Value objects | `Vogen`: `RunId`, `GoalId`, `CellId`, `CellType`, `ModelId`, `ProviderKey`. Each lives in the `.Abstractions` project of the subsystem it belongs to |
| Filesystem and time | `System.IO.Abstractions`, `TimeProvider` |
| Analysers | `Meziantou.Analyzer`, `Microsoft.VisualStudio.Threading.Analyzers` |
| Errors | Exceptions for programming errors. A cell that cannot answer returns a result and does not throw |
| Dependencies | Check for an existing library before writing a component. A new package needs explicit approval |

## Integrations

Not part of the hive core. A cell that talks to Jira, Confluence, GitHub or SonarQube lives in its own project, so the core carries no integration dependencies. MCP servers cover the tool side where one exists.

| Concern | Choice |
|---|---|
| HTTP | `Flurl` and `Microsoft.Extensions.Http.Resilience`, when a cell needs them |
| External processes | `CliWrap`, when a cell needs them |

## Console

| Concern | Choice |
|---|---|
| Rendering | `Spectre.Console` |
| Human in the hive | The CLI prompts when a checkpoint is raised in an interactive run, and otherwise exits with the checkpoint saved for `resume` |
| Configuration | `IOptions<T>` with `ValidateOnStart()`, appsettings plus `HONEYCOMB_*` environment variables |

## Testing

| Concern | Choice |
|---|---|
| Framework | xunit v3 (`xunit.v3.mtp-v2`, the package MAF's tests use) |
| Mocking | Moq |
| Filesystem and time | `MockFileSystem`, `FakeTimeProvider` |
| Model calls | A scripted fake `IChatClient` that returns queued replies. No test calls a real model |
| Hive behaviour | Scenario tests over a whole hive with the fake client: re-spin limits, judge, depth cap, checkpoint propagation |
| Trace assertions | Tests listen on the `ActivitySource` and assert the span tree and events |
| Coverage | 90% target, collected with the `dotnet-coverage` tool only. Cobertura is never used, as a collector or as an output format |
| Test report | JUnit XML from `Microsoft.Testing.Extensions.JUnitReport` (alpha only, see below). `dotnet test` on the .NET 10 SDK needs the Microsoft Testing Platform opt-in in `global.json`: `{ "test": { "runner": "Microsoft.Testing.Platform" } }` |
| Quality gate | SonarQube, `dotnet format` |

The command is `dotnet-coverage collect "dotnet test" -f xml -o test-results/coverage.xml`, and SonarQube imports the result through `sonar.cs.vscoveragexml.reportsPaths=test-results/coverage.xml`. Everything under `test-results/` is git-ignored and is never committed. The installed `dotnet-coverage` (18.12.0, rechecked after an upgrade from 18.4.1) supports only `coverage`, `xml` and `cobertura` as output formats, for both `collect` and `merge`. JUnit is a test-result format and not a coverage format, so it is not an option. A coverage settings file (`--settings`) excludes generated code and types marked `ExcludeFromCodeCoverage`. Not verified: that the project's SonarQube server and scanner accept it, which is open decision 15.

**JUnit.** `dotnet test -- --report-junit --report-junit-filename results.xml` writes JUnit XML with passed, failed (with the message) and skipped tests, and runs inside `dotnet-coverage collect`, so one run gives both files. The extension has alpha versions only, which is the one exception to "latest stable". Xunit theory cases are named `Theory [attempt 1]`, `[attempt 2]`, with the arguments in a `testpath` property. See evidence.md.

The hive's rules are deterministic code around a non-deterministic model, so all of them can be tested without a model.

## Proposed projects

The layout follows the `Microsoft.Extensions` pattern: each subsystem is a pair of packages, an implementation and its own `.Abstractions`, in the way `Microsoft.Extensions.Logging` has `Microsoft.Extensions.Logging.Abstractions`. There is no single shared abstractions project.

```
src/
  Honeycomb.Cells.Abstractions       CellId, CellType, ICell, goal, result and status
  Honeycomb.Cells                    cell definitions, loading, neighbour wiring

  Honeycomb.Providers.Abstractions   ProviderKey, ModelId, model routing contract
  Honeycomb.Providers                keyed IChatClient registration, routing

  Honeycomb.State.Abstractions       checkpoint and transcript store contracts
  Honeycomb.State                    filesystem checkpoint and transcript stores

  Honeycomb.Telemetry.Abstractions   source, meter, span, event and attribute names
  Honeycomb.Telemetry                OpenTelemetry registration, counters, emit helpers

  Honeycomb.Hive.Abstractions        RunId, IHive, ICellContext, planning-cell and counter contracts
  Honeycomb.Hive                     hive, planning cell, re-spin and depth control, scopes

  Honeycomb.Cli                      host: run, resume, checkpoint prompts
tests/
  Honeycomb.*.Tests                  one per implementation project
  Honeycomb.Hive.ScenarioTests       whole-hive tests over a fake client
```

Rules, as in `Microsoft.Extensions`:

- An implementation project references other subsystems only through their `.Abstractions`, never their implementation.
- Only the host (`Honeycomb.Cli`, or any embedding application) references implementations. It composes them with the `Add{Subsystem}` extension methods, which live in each implementation project.
- An `.Abstractions` project holds interfaces, records, enums, value objects and constants, and no behaviour beyond what a value object needs. It depends on `Microsoft.Extensions.*.Abstractions` packages and on other `Honeycomb.*.Abstractions` only.
- `Honeycomb.Cli` is an application and has no `.Abstractions`.

Proposed dependencies between the abstractions, which must stay acyclic:

```
Cells.Abstractions      (no Honeycomb dependencies)
Providers.Abstractions  (no Honeycomb dependencies)
Telemetry.Abstractions  (no Honeycomb dependencies)
Hive.Abstractions       -> Cells.Abstractions
State.Abstractions      -> Cells.Abstractions, Hive.Abstractions
```

| Implementation | References (abstractions only) |
|---|---|
| `Honeycomb.Cells` | `Cells`, `Hive`, `Providers`, `Telemetry` |
| `Honeycomb.Providers` | `Providers`, `Telemetry` |
| `Honeycomb.State` | `State`, `Hive`, `Cells` |
| `Honeycomb.Telemetry` | `Telemetry` |
| `Honeycomb.Hive` | `Hive`, `Cells`, `Providers`, `State`, `Telemetry` |

A type lives in the `.Abstractions` project of the subsystem whose concept it is. Identifiers and other scalar values are `Vogen` value objects, and structured types are records. `Goal` and the result type (with its status) are cell concepts, because every cell receives and returns them, so they are in `Cells.Abstractions` with `GoalId` and `CellType`. `RunId` is a hive concept and is in `Hive.Abstractions`. `ModelId` and `ProviderKey` are in `Providers.Abstractions`. A checkpoint holds cell and hive types, so `State.Abstractions` depends on both.

Abstractions projects are tested only where they contain logic, for example value object validation. Behaviour is tested through the implementation projects and the scenario tests.

## Checked and not checked

Checked by reading the MAF clone (commit `809cc04f2`). What was compiled and run is in [evidence.md](evidence.md).

- A `Workflow` is built once with `WorkflowBuilder.Build()`, and sub-workflows (`SubworkflowBinding`) are bound at build time.
- Handoff is tool-call based, and its builder and instructions refer to a shared workflow conversation.
- Magentic is an LLM manager that plans, selects agents, tracks progress and replans over a set of participants, with an optional round limit.
- `OpenTelemetryAgent` delegates to `OpenTelemetryChatClient` and relabels the span `invoke_agent`, with the tags listed under Observability. MAF's workflow telemetry uses its own names.
- `AsAIFunction()` takes a `query` string, returns `response.Text`, and with no session creates a fresh session per call.
- MAF checkpoints are taken at the end of each superstep, and hold the workflow description, queued messages, outstanding requests, scoped state, edge state and a parent link. `ICheckpointManager` and `Checkpoint` are internal. A checkpoint is validated against the workflow graph it is resumed into. `FileSystemJsonCheckpointStore` and `JsonCheckpointStore` are public, and the filesystem one takes a `DirectoryInfo`.
- The core packages are marked released at `1.26.0`, and `Microsoft.Agents.AI.Mcp` is `alpha`.
- Anthropic and compaction projects exist, and `Directory.Packages.props` lists `Anthropic`, `OllamaSharp`, `ModelContextProtocol`, `OpenTelemetry`, `Microsoft.Extensions.AI` and `Microsoft.ML.Tokenizers`.
- The test packages in MAF are `xunit.v3.mtp-v2` and `Moq` (pinned to `[4.18.4]`).

The results of everything that was compiled and run are in [evidence.md](evidence.md): package versions on the feed, the hive skeleton, resume after a checkpoint, cancellation, file replacement on Windows and Linux, the Anthropic adapter, compaction, the tokenizer, MCP, what the local server enforces in a JSON schema, reliability across ten kinds of cell, and sharing an agent `Id` in MAF's session store and memory provider.

Rules the results added to this document: cells must return a cancelled result and never let `OperationCanceledException` escape; checkpoint saves are serialised and the file move is retried; every schema bound is validated in code; sessions and memory, if ever used, are keyed by the durable cell id.

Still unverified:

- That folding saves context on long transcripts, with a frontier model, and through nested planners. The fifth round showed a body 30 to 50% of the transcript's size on three short sources, with no loss a later cell needed when the specialist replies were usable.
- Compaction on a long history at a realistic limit, with a frontier model, with a summariser other than the local 7B, and with a cell that chooses its own tool calls. The sixth round tried one short scripted history.
- What happens to a proposed criterion the planning cell rejects. It is dropped from the body today, which is how a fact could be lost.
- Whether the project's SonarQube server and scanner import `dotnet-coverage` XML in practice. The XML has the expected shape, but nothing was uploaded. Parked by decision.
- Whether a frontier model needs the same validation, and how the local model does on cell types and prompts beyond the ten tried.
- Anthropic against the real API. The adapter was tested against a fake server only, so authentication, streaming, rate limits and real token counts are untested. Parked by decision.
- File replacement on macOS. Windows and Linux were tested.

## Decisions made

Each is reflected in the sections above.

1. .NET 10 and C#.
2. Cells are `AIAgent`s.
3. The hive writes its own fan-out and fan-in for parallel goals, and does not use MAF Workflows. MAF Workflows were prototyped first, and the nested-workflow question was answered (see Orchestration).
4. The hive builds its own neighbour tool and does not use `AsAIFunction()` (see Cells).
5. All filesystem access goes through `IFileSystem`. The checkpoint store is adapted from the existing `IFileSystem`-based one, and MAF's `FileSystemJsonCheckpointStore` is not used.
6. The local runtime is Docker Model Runner.
7. There is no scope factory. A planning cell creates child scopes from its own `IServiceProvider` and sets a scoped `ICellContext` first (see Orchestration).
8. The coverage target is 90%, collected with `dotnet-coverage` only. Cobertura is never used.
9. The checkpoint contents, resume rules, durable `CellId` and `repeatable` flag are as under Checkpoints. The `honeycomb.cell.id` attribute is in the telemetry document.
10. Packages are on the latest stable version.
11. Shared types are `Vogen` value objects where scalar, and each lives in the `.Abstractions` project of the subsystem whose concept it is.

12. Test results are written as JUnit XML, alongside the `dotnet-coverage` XML.
13. Results carry typed contributions, and a goal is a fixed aim with a five-part body that the planning cell folds them into with code. A specialist proposes, the planning cell decides the acceptance criteria (see Results).
14. A cell is not compacted. At its context limit it stops and returns `partial` with the contributions it has, and the planning cell decides whether to re-spin (see Context and tokens).

## Open decisions

15. Parked. Confirm that the project's SonarQube server and scanner import `dotnet-coverage` XML through `sonar.cs.vscoveragexml.reportsPaths`. Sonar's current docs for this say they do, and the format is the one from the proposal under Testing. Not tried against the real server.
