# Telemetry

How the hive is instrumented, for the Telemetry section under "Implementation details" in [architecture.md](../architecture.md). Items marked **Decision** record a choice and the reason for it. What was tested is in [evidence.md](evidence.md).

The approach is to use OpenTelemetry as it is, and only add what is specific to a hive. Where OTEL or its GenAI conventions already have a concept, the hive uses it. Anything that can be answered by querying traces is not made into a metric.

---

### Telemetry

Open Telemetry (OTEL) will instrument the Hive, from the point the Hive starts, to when the last cell finishes.

Telemetry has one job in Honeycomb: making the hive's own rules observable. A trace should show not only which cells ran, but why the tree has the shape it does: how large the context was when each cell started, which cells were re-spun, where the judge was asked, and where a human was needed.

#### Source

The Hive has one OTEL source, `Honeycomb`, with one `ActivitySource` and one `Meter`. Anything built on top of the hive (for example a Jira analysis cell) adds its own source under its own name.

The libraries have their own defaults: `Experimental.Microsoft.Agents.AI` for MAF's `OpenTelemetryAgent`, and `Experimental.Microsoft.Extensions.AI` for the chat client. Both accept a source name, so the hive passes `Honeycomb` to both and keeps one source. If that proves awkward, the host subscribes to the three names instead. The MCP SDK has its own source, `Experimental.ModelContextProtocol`, which the host must subscribe to as well if it wants MCP spans.

#### Identity

The hive does not invent identifiers where OTEL has one.

- **A trace is one process run of the hive.** Every span in it ends normally. A run that is checkpointed and later resumed is two traces, joined as described in "Checkpoint and resume".
- **The run is `honeycomb.run.id`.** It is generated when a question is first posted, stored with any checkpoint, and set on the root span of every trace that belongs to the run. This is how the traces of one run are found together.
- **The cell is an agent.** Cells use the OTEL GenAI agent attributes, so existing tooling understands them:

| Attribute | Meaning |
|---|---|
| `gen_ai.agent.name` | The cell type, for example `planning` or `jira-analysis`. Low cardinality. |
| `gen_ai.operation.name` | `invoke_agent` for a cell, `plan` for a planning cell's decomposition, `chat` for a model call, `execute_tool` for a tool call |
| `gen_ai.provider.name` | Required on the model call (`chat`) span |
| `gen_ai.request.model` | The model chosen for the call |

- **The cell has a durable id, `honeycomb.cell.id`.** The tech stack document keys the checkpoint tree on a durable `CellId`, because span ids do not survive a resume. That value is recorded on the cell's span. It is not the GenAI `gen_ai.agent.id`: the conventions do not recommend a transient instance id there, and MAF's `OpenTelemetryAgent` always sets `gen_ai.agent.id` to the agent's `Id` and puts it in the span name, with a new GUID per instance by default (see "Library check"). So each cell's MAF `Id` is set to its cell type, which keeps `gen_ai.agent.id` and the span name stable and low-cardinality, and the unique id lives in `honeycomb.cell.id`.
- **Who created whom** is the parent span. There is no parent id attribute.

The hive adds only the attributes OTEL has no concept for, under the `honeycomb.` prefix:

| Attribute | Meaning |
|---|---|
| `honeycomb.run.id` | The run. On the root span only. |
| `honeycomb.question.ref` | A reference to the stored question that started the run, never the text. On the root span only. |
| `honeycomb.cell.id` | The cell's durable id, the same one the checkpoint tree uses. It survives a resume. Span attribute only, never a metric attribute. |
| `honeycomb.cell.depth` | Distance from the root planning cell. Re-spins do not change it. |
| `honeycomb.goal.id` | The goal this cell is working on |
| `honeycomb.cell.purpose` | Why the cell was created, in a short phrase. Treated as content, see "Content". |
| `honeycomb.cell.outcome` | `answered`, `cannot_answer`, `checkpointed` or `cancelled`. Failures are not an outcome, see "Status". |
| `honeycomb.respin.attempt` | For a re-spun cell, which attempt this is |

The values of `honeycomb.cell.outcome` must match the status values in the Result Format section.

The GenAI names were checked against the OpenTelemetry GenAI semantic conventions repository (`open-telemetry/semantic-conventions-genai`, `main`, checked 2026-10-27). Every page there is marked **Development**, so names can still change. They have already moved once: the conventions left the main semantic-conventions repository, and `gen_ai.client.token.usage` and `gen_ai.token.type` are no longer defined there.

**Decision:** where the libraries and the new spec differ, the hive follows the libraries, and does not add a mapping layer. The differences are listed under "Library check".

#### Library check

Checked on 2026-10-27 against:

- the local MAF clone at commit `1168c001d` (2026-10-26), in `dotnet/src/Microsoft.Agents.AI/OpenTelemetryAgent.cs` and `ChatClientAgent.cs`. Its version prefix is 1.26.0, which is also the latest `Microsoft.Agents.AI` on NuGet;
- the `Microsoft.Extensions.AI` package. The clone pins 10.10.0 and the latest on NuGet is 10.11.0. The names below were read from the assemblies of both in the local NuGet cache, and the `gen_ai.*`, source-name and environment-variable strings are identical in the two. They also agree with `dotnet/extensions` `main` on GitHub.

The hive uses the latest versions, so this table describes the versions it will use today. It still has to be re-run on each upgrade, because the GenAI conventions are at Development status and the library names have already fallen behind the spec once.

| Area | Library | Spec | Effect |
|---|---|---|---|
| Layering | `OpenTelemetryAgent` delegates to `OpenTelemetryChatClient`, and says so, to avoid reimplementing the conventions | | Spans and metrics come from `Microsoft.Extensions.AI`, so its version decides the names |
| Spec version | The chat client says it implements GenAI conventions v1.41 | `main` of the new repository | The library follows an older version |
| Token metric | `gen_ai.client.token.usage` with `gen_ai.token.type` (`input`, `output`, and audio and text variants) | `gen_ai.client.inference.usage.*` and `.operation.*` with `gen_ai.token.modality` | Different names. This document uses the library's names |
| Duration metric | `gen_ai.client.operation.duration` | Same | Matches |
| Other metrics | `gen_ai.client.operation.time_to_first_chunk` and `.time_per_output_chunk` | Server metrics use other names | No `gen_ai.invoke_agent.*`, `gen_ai.invoke_workflow.duration` or `gen_ai.execute_tool.duration` was found |
| Span name | `invoke_agent {Name}({Id})`, or `invoke_agent {Id}` with no name | `invoke_agent {gen_ai.agent.name}` | The id is in the span name, so a per-instance GUID makes the name high-cardinality unless `Id` is set to the cell type |
| `gen_ai.agent.id` | Always set to the agent's `Id`, default a new GUID per instance | Not recommended for transient ids | Set `Id` to the cell type |
| Operation names | `chat`, `invoke_agent`, `execute_tool`, `invoke_workflow` and others are defined. `plan` was not seen | `plan` is defined | The hive writes `plan` and `invoke_workflow` spans itself |
| Content capture | The `EnableSensitiveData` property, default false. Its comment says the environment variable `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` turns it on | `OTEL_INSTRUMENTATION_GENAI_CAPTURE_CONTENT` with four values | Different variable and values |
| Source name | `Experimental.Microsoft.Agents.AI` and `Experimental.Microsoft.Extensions.AI`, both overridable | | See "Source" |
| Extra metric | `gen_ai.client.operation.exception` is also present in the 10.10.0 package | Not seen in the spec pages read | Counts failed model calls. Not used here |
| Setting `Id` | `ChatClientAgentOptions.Id` is settable, and `ChatClientAgent` returns it from `IdCore`, so the cell type can be the `Id` | | Confirmed. The `AIAgent` documentation says an `Id` is a unique identifier that should stay stable for the agent's lifetime, so a shared `Id` across cell instances departs from its intent. See "Where MAF uses the agent `Id`" below |
| Neighbour as tool | `AsAIFunction()` exists, but neither MAF nor the 10.10.0 package contains `gen_ai.transfer.target.name` or `gen_ai.transfer.mode` | Defined for agent-as-a-tool | A consult is a plain `execute_tool` span. See "Spans" |

#### Where MAF uses the agent `Id`

Setting every instance of a cell type to the same `Id` is safe only if nothing treats the `Id` as unique. Checked in the MAF clone (1.26.0):

| Where | Use of the agent `Id` | Effect on the hive |
|---|---|---|
| `ChatClientAgent`, `OpenTelemetryAgent`, logging | Written to log messages, `AgentResponse.AgentId` and the `gen_ai.agent.id` tag, and used in the span name | Safe. This is the intent |
| `ChatHistoryMemoryProvider` | Stored as an `AgentId` field and used to filter memory searches | Instances of one type would share memory. The hive does not use this provider, because folding replaces it |
| `Microsoft.Agents.AI.Workflows` (`AIAgentExtensions`, handoff builders and executors) | The workflow executor id is `{Name}_{Id}`, and handoff targets are addressed by `Id` | **Two cells of the same type in one MAF workflow would get the same executor id and collide.** Applies if the hive uses MAF Workflows or Handoff |
| `Microsoft.Agents.AI.Hosting` (`InMemoryAgentSessionStore`) | Session store key `id:{agent.Id}` | Not used. The hive has no hosting layer |

Only the Workflows row matters for the plan. Running a planner's goals in parallel in one MAF workflow would put two cells of one type, such as two `jira-analysis` cells, into one workflow, where they cannot bind. **Decision:** the hive runs its own fan-out and fan-in (see the tech stack document), so cells of one type never share a workflow and each cell's `Id` can stay its cell type.

The Workflows row was confirmed by running (see below): two cells with the same type and `Id` in one concurrent MAF workflow fail to build with `Cannot bind executor with ID 'jira_analysis_jira_analysis' because an executor with the same ID but different instance is already bound`.

#### Verified by running

A throwaway test program (not kept; see [evidence.md](evidence.md)) ran the packages at `Microsoft.Agents.AI` 1.26.0 and `Microsoft.Extensions.AI` 10.11.0 with a scripted fake model, and once against a real local model. It listened to every `ActivitySource` and `Meter`. Results, on 2026-10-27:

| Claim | Result |
|---|---|
| Span name is `invoke_agent {Name}({Id})`, with `gen_ai.agent.id`, `gen_ai.agent.name`, `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.request.model` | Confirmed. The span kind is `CLIENT`, not the spec's `INTERNAL` |
| Token usage is on the `invoke_agent` span | Confirmed. It is the total across the cell's model calls, and `chat` spans carry per-call values |
| A neighbour consulted as a tool shows as `execute_tool` with the neighbour's `invoke_agent` span as its child | Confirmed, with no transfer attributes |
| The hive's own `Honeycomb` spans nest the library's spans correctly | Confirmed |
| Passing `Honeycomb` as the source name puts spans **and** metrics under `Honeycomb` | Confirmed. Without it the sources are `Experimental.Microsoft.Agents.AI` and `Experimental.Microsoft.Extensions.AI` |
| Content off records no messages, and on records them | Confirmed, with the two exceptions under "Content" |
| Metric names are `gen_ai.client.operation.duration` and `gen_ai.client.token.usage` with `gen_ai.token.type` | Confirmed, plus the two time-per-chunk metrics when streaming |
| Those metrics can be used for totals | **Refuted.** They double-count with the agent wrapper, see "Metrics" |
| Two cells of one type and `Id` cannot share a MAF workflow | Confirmed |
| MAF workflow telemetry is off by default, has its own names, and can use the hive's source | Confirmed: `.WithOpenTelemetry(activitySource: ...)` emits `workflow.build`, `workflow.session`, `workflow_invoke`, `executor.process` and `message.send`, none of them GenAI names |
| An executor can build and run a nested workflow at run time inside its handler | Confirmed. The inner workflow ran and returned its output, and the outer run took one in-memory checkpoint |
| The inner workflow's telemetry nests under the outer one | Confirmed. With `.WithOpenTelemetry(activitySource: ...)` on both workflows, the inner `workflow.build`, `workflow_invoke` and `executor.process` spans sit under the outer `executor.process outer` span. Each workflow has to enable it separately |
| A checkpoint taken while the nested run is parked on a human request can be resumed later by fresh objects | Confirmed in 4 of 4 runs. The inner run, given its own file checkpoint store, stopped with status `PendingRequests`. A new `Workflow` and a new store over the same directory restored it, the request was raised again with the same id, the answer was accepted and the run completed. State written before the pause was restored, but only when written to a **shared** scope. State in an executor's default scope is private to that executor, and the executor that read it saw nothing |
| Resuming the outer workflow from its own checkpoint | The checkpoint was taken after the nested run had finished. Resuming did not run the handler again, and the resumed run had status `NotStarted` with no events. The outer checkpoint holds nothing of the inner run, which has its own session and store, so the hive would have to keep the inner checkpoint ids itself. The earlier hang is explained in the `ResumeAsync` row below |
| Two `FileSystemJsonCheckpointStore` instances over one directory | The second fails with `The store ... is already in use by another process` until the first is disposed. One store instance per directory per process |
| `Microsoft.Extensions.AI.OpenAI` completes a chat against Docker Model Runner and honours a JSON schema response format | Confirmed for one call with `docker.io/ai/qwen2.5:7B-Q4_0`. The reply parsed as the requested type, and the span carried the real token counts (35 in, 18 out) with provider name `openai`. Over 30 calls in each of two modes (a JSON schema response format, and instructions only) every reply parsed and had a valid status, and 27 of 30 had the expected status. All three misses were one question, "What colour do you get by mixing blue and yellow?", which the model refused as `cannot_answer`. Latency was a median of 1.4s and 95th percentile of 1.9s. The two modes gave the same results, so this does not show the schema format helping. The sample is small: 10 prompts, one model, one simple result shape, short contexts |
| 20 agents with the same `Id` running concurrently (not in a MAF workflow) | No interference. Every reply echoed its own input, each session's history was its own, and there were 40 `invoke_agent` spans with distinct span ids, one `gen_ai.agent.id` and one span name. The hosting session store and `ChatHistoryMemoryProvider` do share state by `Id` unless keyed by cell id; see evidence.md |
| `ResumeAsync` on a MAF checkpoint with nothing pending | It does not return. With a 20 second cancellation token every run took the full 20 seconds and then returned quietly with status `NotStarted` and no error. With no token it waits forever. This explains the earlier hang, which was deterministic and not flaky. It matters little to the hive, which keeps its own checkpoints, but any code that resumes a MAF workflow must pass a token |
| A hive skeleton: planning cells as `AIAgent`s, cells created in child DI scopes, run to a checkpoint with 8 cells | The skeleton's checks pass (33 at the time; 54 with later additions). Every span that started was stopped. A planning cell's `invoke_agent` span is the parent of its children's spans, because the planning loop runs inside the agent's `RunCoreAsync`. The root `invoke_workflow` span ends last. Every cell span carries `honeycomb.cell.id`, depth and goal id, and `gen_ai.agent.id` equals the cell type |
| The span events | `judge.asked`, `depth.capped`, `respin.decided`, `respin.refused`, `result.invalid`, `checkpoint.raised`, `routing.selected` and `goal.folded` all fire the expected number of times, and `goal.folded` carries the goal size in tokens. `goal.folded` has since been dropped (see "Span events"), so that part of the result no longer describes the design |
| A re-spun cell links to the attempt it replaces | Confirmed, with `Activity.AddLink` after the span starts (.NET 9 and later). The link is not visible to a sampler, because the span already exists |
| The three counters | `honeycomb.respin.count` recorded 6 times for 2 re-spins (once per scope), `honeycomb.respin.refused.count` once, `honeycomb.checkpoint.raised.count` once. The snapshot held 5 counter states, not 6, because the hive-scope counter is shared |
| Checkpoint and resume across two traces | Confirmed. After a simulated two-hour wait, a fresh container resumed from the snapshot as a new trace. The resumed root kept `honeycomb.run.id`, and linked to the first trace's root and to the checkpointed cell. The resumed cell kept its `honeycomb.cell.id` and linked to its earlier span. `checkpoint.cleared` recorded 7,200 s. Only the writer cell ran again: no other cell's model was called. Every span ended. Root links go to the cells that were waiting on a human, not to the planning cells above them |
| Parent cancellation reaches every cell | Confirmed. A hive cancelled after 0.4 s with 10 s model delays ended in 0.4 s, every span stopped, and every unfinished cell had `honeycomb.cell.outcome = cancelled` |
| MCP tools bind to a cell, and the MCP SDK's own telemetry | A tool from an MCP server (`ModelContextProtocol` 2.3.0) worked as a cell tool and appeared as `execute_tool echo`. The SDK emits its own spans from a source named `Experimental.ModelContextProtocol` (`tools/call echo` with kind `Server`), so a host must subscribe to that name too. The server-side handler ran in the cell's trace, and it runs on a loop that started before the trace, so the context reached it through the protocol |
| The Anthropic adapter | MAF's adapter completed a chat against a fake local Anthropic server, with the API key header and model in the request, provider name `anthropic` on the span, and the token usage mapped. MAF's `AsAIAgent` helper gives a random agent id, so the hive builds its own `ChatClientAgent` over the SDK's `IChatClient` to set `Id` |

Not checked: how much of the above holds for later package versions.

#### Spans

Each cell starts its own span, parented by the span of whoever created it. A cell's span is its whole life, from creation to its result being returned or a failure.

```
invoke_workflow hive                            (root; INTERNAL, written by the hive)
  invoke_agent planning                         (child of the hive; CLIENT, from MAF)
    plan planning                               (the decomposition; INTERNAL, written by the hive)
      chat <model>                              (the model call; CLIENT)
    chat <model>                                (the judge, when asked)
    invoke_agent jira-analysis                  (child of its planning cell; CLIENT)
      chat <model>
      execute_tool consult_code_search          (INTERNAL; the neighbour tool)
        invoke_agent code-search                (the neighbour's span; CLIENT)
```

- **Root.** The whole hive is `invoke_workflow {gen_ai.workflow.name}`, which the conventions define for processes that coordinate multiple agents. The workflow name is one fixed, low-cardinality name for the hive.
- **Cells.** The spec's in-process span is `invoke_agent {gen_ai.agent.name}` with kind INTERNAL. MAF emits kind `CLIENT` even for an in-process agent (confirmed by running), and this document follows the library. MAF names the span `invoke_agent {Name}({Id})`, so with each cell's `Id` set to its cell type, the span reads `invoke_agent jira-analysis(jira-analysis)`. That is accepted as the library's form, and it stays low-cardinality. See "Library check".
- **Decomposition.** A planning cell's breaking down of a goal is a `plan {gen_ai.agent.name}` span. The conventions say to use it when planning can be told apart from generic reasoning, which it can here. The number of goals, and whether they run sync or parallel, are attributes of this span.
- **Model calls.** `chat {gen_ai.request.model}`. CLIENT is recommended, and INTERNAL is allowed for a model that runs in the same process. This includes the judge and any model-selection call a planning cell makes. They are ordinary model calls under the planning cell, so their tokens and latency are counted like any other.
- **Neighbour consult.** The tech stack document exposes a neighbour to a specialist as a tool that the hive builds itself, so a consult is an `execute_tool {tool name}` span, and the neighbour's own `invoke_agent` span is its child. Both were confirmed by running. The conventions also define `gen_ai.transfer.target.name` and `gen_ai.transfer.mode` (`return_to_caller` for a consult that waits) for a tool that hands work to another agent. Neither MAF nor `Microsoft.Extensions.AI` sets them. **Decision:** do not add them. The parent and child spans already show the call, and the tool name is the neighbour's cell type. Add the two attributes later if a tool needs to tell a consult from an ordinary tool call.
- **Tokens and context size.** `gen_ai.usage.input_tokens` and `gen_ai.usage.output_tokens` are set on each `invoke_agent` span and on each `chat` span. Confirmed by running, with two details. On the `invoke_agent` span they are the **total across every model call in that cell's run** (a tool-calling cell with calls of 150 and 300 input tokens showed 450), and they exclude the neighbour's own calls. On the `chat` span they are per call. So the design's main claim is checked with queries and no custom metric: the total tokens a cell type uses is the `invoke_agent` value, and the size of the largest context a cell ever sent is the maximum over its `chat` spans.
- There are no From and To attributes. The parent span says who the caller is, and the span itself says who the callee is.

Everything above assumes cells run in the same process. If cells ever run out of process, or tools are called over MCP, the GenAI conventions define `CLIENT` and `SERVER` spans for A2A and MCP calls, and MCP carries the trace context inside the request. Neither is needed yet.

#### Status

Use OTEL's span status, and do not define a separate failure outcome:

- A cell that returns normally, including `cannot_answer`, is not an error. Its status is unset, and `honeycomb.cell.outcome` says what it returned. `cannot_answer` is a valid result that the planning cell acts on, not a failure. The same holds for `partial`, which a cell returns when it reaches its context limit (the `context.limit` event says so, see stack.md under Context and tokens).
- A cell that throws, or returns a result that never matches the result schema, ends with status `Error`, `error.type` set, and the exception recorded using OTEL's standard `exception` event.
- A cancelled cell ends normally with `honeycomb.cell.outcome = cancelled`. **A cell must return a cancelled result and must not let `OperationCanceledException` escape.** Confirmed by running: when the exception passed through MAF's `OpenTelemetryAgent`, the span ended with status `Error` and `error.type = System.Threading.Tasks.TaskCanceledException`. When the cell's own layer caught it and returned a cancelled result instead, the span status was unset and `honeycomb.cell.outcome` was `cancelled`. A planning cell reports `cancelled` if any child did, and the root does too.

#### Span events

Not everything that matters is a call. These are recorded as events on the span of the cell that did them:

| Event | Recorded when | Key attributes |
|---|---|---|
| `goal.compressed` | A planning cell compresses a part of the goal body that passed its size limit | which part, size before and after (tokens) |
| `context.limit` | A cell reaches its context limit and returns `partial` | tokens counted, limit |
| `judge.asked` | The depth judge is consulted | depth, decision (`decompose` or `proceed`) |
| `depth.capped` | The hard depth limit forces the planner to proceed | depth, limit |
| `respin.decided` | A planning cell decides to re-spin | attempt number, which counters were consumed |
| `respin.refused` | A re-spin was wanted but a counter blocked it | which counter, current value, limit |
| `routing.selected` | A model was chosen for a new specialist | provider, model, reason |
| `result.invalid` | A cell's JSON did not match the result schema | parse error, attempt |
| `checkpoint.raised` | A cell checkpoints | reason |
| `checkpoint.cleared` | A human clears a checkpoint and the hive resumes | time waited |

**There is no event for an ordinary fold.** Folding is a deterministic append of a child's contributions to the goal body, and everything an event would say is already on a span. Which cell contributed, and what it returned, is the child cell's span (its `honeycomb.cell.outcome`, and its transcript reference), a child of the planning cell's span. How big the goal has become is the input tokens of the next cell's `chat` span, and the largest context a cell type ever sent is the maximum over its `chat` spans, as under "Spans". A fold is also the same on every run, so there is nothing in it to explain. Compression is different: it is the one point where a model rewrites the body and can lose something, so it is recorded.

#### Re-spin

A re-spun specialist is a new cell, but it is a retry. Its span has a span link to the span of the attempt it replaces, and `honeycomb.respin.attempt` set. This uses OTEL's own idiom for retries.

#### Metrics

**Emitted by the libraries.** The hive does not write these. The names are the library's (see "Library check"):

| Metric | Answers |
|---|---|
| `gen_ai.client.operation.duration` (histogram, `s`) | How long do model calls take, by provider and model? Is a local model faster or slower than a frontier one? |
| `gen_ai.client.token.usage` (histogram), with `gen_ai.token.type` | How many tokens does each provider and model use, split into input and output? |
| `gen_ai.client.operation.time_to_first_chunk` and `.time_per_output_chunk` | How quickly does a model start and keep streaming? |

The spec also defines duration metrics for agents, workflows and tools (`gen_ai.invoke_agent.duration`, `gen_ai.invoke_workflow.duration`, `gen_ai.execute_tool.duration`), but none was found in the libraries. How long a cell type or a hive run takes is answered from the span durations, with no metric. Add a metric only if a dashboard needs a rate that a trace query cannot give.

The token metric has no cell type dimension, which is why context size is read from the spans instead.

**These client metrics double-count when the agent wrapper is used.** Confirmed by running. Each agent run adds a second record to `gen_ai.client.token.usage` and `gen_ai.client.operation.duration`, carrying the whole run's total, and it is labelled `gen_ai.operation.name = chat` like the per-call records, so the two cannot be told apart by attribute. A cell that made model calls of 150 and 300 tokens, plus a neighbour call of 80, recorded 1,060 input tokens, which is twice the true 530. One call of 120 tokens recorded 240, with the agent wrapper alone or with both wrappers. With only the chat client wrapper it was 120, but then there is no `invoke_agent` span. So these two metrics are not used for totals or cost. Use the span attributes instead. This is worth reporting upstream.

**Written by the hive.** Three counters, for the hive's own rules that a trace query cannot show as a rate. They are named in OTEL style: lowercase, dot-delimited, no `_total` suffix, and the unit is set on the instrument and not put in the name.

| Metric (unit) | Attributes | Answers |
|---|---|---|
| `honeycomb.respin.count` (`{respin}`) | `gen_ai.agent.name`, `honeycomb.respin.scope` | How much retrying happens, and where? |
| `honeycomb.respin.refused.count` (`{respin}`) | `gen_ai.agent.name`, `honeycomb.respin.scope` | Which counters are blocking? |
| `honeycomb.checkpoint.raised.count` (`{checkpoint}`) | `gen_ai.agent.name`, `honeycomb.checkpoint.reason` | Where do humans get involved? |

`honeycomb.respin.scope` is one of the three re-spin counters: `cell` (the same cell created more than once), `planner` (within a planning cell) or `hive`.

Everything else, such as how often the judge stops decomposition, how deep goals go, or how often a model is escalated from local to frontier, is answered by querying the span events and attributes above. A new custom metric is added only when a question cannot be answered that way.

Metric tags are low-cardinality only: cell type, outcome, scope and reason. Ids, goals and free text stay on spans.

#### Content

By default a trace holds metadata only: which cells ran, how long each took, how many tokens a call used, and which model answered. **Content** is the actual text behind that: the prompts sent to a model, the system instructions, the model's replies, and the arguments and results of tool calls. With content capture off, a trace says "the jira-analysis cell called model X and used 3,200 input tokens", but not what it asked or what came back.

Goals, results and prompts may contain ticket text, code, names and secrets, and a telemetry backend is often kept longer and seen by more people than the hive itself. So content is off by default and turned on only when someone needs to debug a prompt.

- Content capture uses the library's setting, the `EnableSensitiveData` property on the agent and chat client. It is **off by default**, and is only turned on for a development hive. The library's documentation also names an environment variable, `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`, to turn it on. The newer spec defines a different variable, `OTEL_INSTRUMENTATION_GENAI_CAPTURE_CONTENT`, with the values `NO_CONTENT`, `SPAN_ONLY`, `EVENT_ONLY` and `SPAN_AND_EVENT`. The hive follows the library, and does not read the spec's variable.
- The content attributes are `gen_ai.input.messages`, `gen_ai.output.messages` and `gen_ai.system_instructions`. Function-call arguments and results are covered by the same setting. With it on, the messages appear on both the `invoke_agent` and the `chat` span, and the system instructions on the `chat` span. With it off, none of them appear (confirmed by running).
- Two things are recorded even with content off, so they are redacted or reviewed like content. `gen_ai.tool.definitions` appears on `chat` spans, with the tool names. And `gen_ai.response.model` is whatever the server reports: Docker Model Runner reported a path on the developer's machine (`C:\Users\...\.docker\models\bundles\...`), which is a user path.
- Free text the hive adds, which means `honeycomb.cell.purpose` and error messages, is treated as content. It is only recorded when content capture is on.
- A span carries a reference to the stored transcript entry (an id) and a size in tokens, not the text.
- Secrets and user paths are redacted before anything reaches a log, event or exporter. Keys and tokens are never recorded, only a hash if one is needed.

#### Logging

Logs use the standard structured logging with source-generated message delegates. The OTEL logs SDK adds the trace id and span id automatically while a span is active, so logs join to traces with nothing custom.

#### Checkpoint and resume

A checkpoint can outlive the process that raised it. Each process run is its own complete trace, so a checkpoint ends a trace, and a resume starts a new one.

- **At a checkpoint.** A cell that checkpoints ends its span with `honeycomb.cell.outcome = checkpointed`. The architecture lets sibling paths keep running, so the planning cells above it end when their other children have finished, with the same outcome. The root span ends last, also `checkpointed`. Every span ends normally, so nothing is left open and lost.
- **What is stored.** The checkpoint stores `honeycomb.run.id` and the checkpointed cell's span context, in the standard W3C `traceparent` and `tracestate` format using the OTEL propagators.
- **At resume.** The hive starts a new trace. Its root span has the same `honeycomb.run.id` and span links to the first trace's root span and to each cell that was waiting on a human (not to the planning cells above it), and each resumed cell has a link to its own checkpointed span. Confirmed by running. Cells are created as normal, and the span tree of the new trace shows who created whom within it. A resumed cell keeps its durable `honeycomb.cell.id`, so the cells of a run can be followed across its traces even though span ids differ.
- **The human's wait** is not part of any span's duration. The resumed hive records `checkpoint.cleared` with the time waited.

**Decision:** resume starts a new linked trace. The alternative, continuing the original trace, would need spans to stay open across a human delay and would force sampling to be decided by trace id. Linked traces keep every trace short and complete. This matches the one nearby precedent in the GenAI conventions, where work picked up later in another process is a separate trace correlated by an identifier.

#### Export and sampling

Telemetry is exported using OTLP, and the host decides the endpoint. A local run needs no collector: with no endpoint configured, the standard OTEL console exporter prints the trace, so a developer can see one hive run's tree.

Sampling is a deployment choice and not part of the hive. Because every trace is complete when it ends, ordinary sampling, including keeping failed traces, works.

#### Resolved

- **Libraries and versions.** Checked against the MAF clone and the `Microsoft.Extensions.AI` version it pins (10.10.0). See "Library check". Re-check when either is upgraded.
- **Setting each cell's `Id` to its cell type** is possible (`ChatClientAgentOptions.Id`).
- **Parallel goals** are run by the hive's own fan-out and fan-in, not by MAF Workflows. Cells of one type therefore never share a workflow, and sharing an `Id` per cell type is safe. See "Where MAF uses the agent `Id`".
- **A neighbour consult** is a plain `execute_tool` span. The libraries do not emit the transfer attributes, and this document does not add them.
- **The root `invoke_workflow` span** carries a reference to the stored question (`honeycomb.question.ref`), never the question text.
- **Content capture** is off by default, and free-text attributes are treated as content. See "Content".
- **Names.** `honeycomb.call.purpose` is now `honeycomb.cell.purpose`. Outcome values are snake_case (`cannot_answer`). The custom counters follow OTEL's naming guidance (checked on 2026-10-27): `honeycomb.respin.count`, `honeycomb.respin.refused.count` and `honeycomb.checkpoint.raised.count`. The source name `Honeycomb`, the `honeycomb.` attribute prefix and the dotted event names stay. The guidance has no rule for enum value casing, so snake_case outcomes follow the GenAI conventions' own style and are not a requirement.
- **What a checkpoint saves.** Agreed in principle, as below. It is specified under Checkpoints in [stack.md](stack.md), not here. Telemetry needs only the run id and the span context.
  - One JSON file per run, behind an interface.
  - The run id, the reference to the question, and the path from the root planning cell down to the checkpointed cell. For each cell on the path: cell type, depth, goal id, and the current goal (the aim and the whole body).
  - For the checkpointed cell: why it checkpointed, what was tried, what it had already changed, and the question for the human.
  - The re-spin counter values with their window start times, and the span context.
  - Sibling paths finish before the process ends, and anything unfinished is saved as a goal to re-create.
  - Transcripts are referenced by id, not copied. Cell instances and model clients are re-created from the cell type, goal and configuration.
  - At resume, the human's answer is folded into the checkpointed cell's goal, and that cell is run again as a retry that does not count against the re-spin limits.

#### Follow-up

No open decisions. Remaining work:

1. The checkpoint contents above are specified in the Checkpoints section of [stack.md](stack.md) and pointed to from "Human in the hive" in `architecture.md`.
2. Re-running the "Library check" table whenever `Microsoft.Extensions.AI` or MAF is upgraded. It is currently correct for the latest versions of both (`Microsoft.Extensions.AI` 10.11.0, `Microsoft.Agents.AI` 1.26.0).
3. The parallel-goals decision is recorded in the tech stack document: the hive runs its own fan-out and fan-in, and does not use MAF Workflows for it (see "Resolved"). A test run confirmed the `Id` collision, and also shows nested workflows do work, so the choice is about fit, not about a limit.
4. The doubled `gen_ai.client.*` metrics (see "Metrics"): report upstream, and meanwhile take totals and cost from span attributes.
