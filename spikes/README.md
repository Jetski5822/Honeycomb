# Spikes

Throwaway programs that check claims in `drafts/stack.md` and `drafts/telemetry.md` against the real packages. None of this is the product. They use scripted fake models, except where a test says it calls the local Docker Model Runner.

## telemetry-spike

`Microsoft.Agents.AI` 1.26.0 and `Microsoft.Extensions.AI` 10.11.0.

| Command | Checks |
|---|---|
| `dotnet run` | Span names, tags, token usage, metric names, content capture, default source names, MAF Workflows (shared `Id`, nested workflow), and one real call to Docker Model Runner |
| `dotnet run -- nested` | A nested workflow's telemetry, resuming an inner checkpoint with fresh objects, and the resume that never returns |
| `dotnet run -- sharedid` | 20 concurrent agents sharing one `Id` |
| `dotnet run -- json 3` | Result-format reliability with a local model (simple schema) |
| `dotnet run -- json2 6` | The same with a larger schema, three context sizes, and unanswerable inputs |
| `dotnet run -- json3 12` | Whether `maxItems` is enforced by the local server |
| `dotnet run -- json4` | Which schema constraints the local server enforces |
| `dotnet run -- json5 10` | Ten kinds of cell (planner, depth judge, re-spin decider, router, goal folder, specialist, reviewer, classifier, injection, neighbour tool call), each case repeated 10 times |
| `dotnet run -- fold 10` | The goal body design: contribution fields, the planning cell judging a proposed criterion, and a folded body against the full transcript. Environment variables `FOLD_PROMPT=2` and `FOLD_ORDER=2` select the second prompt wording and `status` after the contributions. `foldraw` prints a few raw replies |

| `dotnet run -- validate format 8` | Four ways of asking a specialist for contributions: status first, status last, two passes, evidence as a sentence number |
| `dotnet run -- validate concurrency 3` | The local server with 1, 4 and 8 identical requests at once. Run it with nothing else using the model |
| `dotnet run -- validate decisions 20` | Two isolated sibling cells design two endpoints. Do their conventions agree, with and without a decisions part |
| `dotnet run -- validate partselect 10` | Which parts of the goal body a planner gives a child, against fixed rules |
| `dotnet run -- validate judge 3` | A judge settling eight invented disputes in both orders, with and without a code check on the quotes |
| `dotnet run -- validate injection 5` and `injection2 5` | Eight hand-written payloads in a ticket and in a neighbour's reply. `injection2` separates obeyed from reported and prints sample replies |
| `dotnet run -- validate cappedraw 8` | The same capped schema through the library path and through the SDK's own response format |
| `dotnet run -- validate dumpschema` and `capped1` | Print the schema the library generates, and make one call (point `HIVE_ENDPOINT` at `logproxy.py` to see what is sent) |

The `maxitems*.py` scripts call the server over plain HTTP, with no client library: `maxitems.py` (a capped list, a control and a grammar), `maxitems2.py` (other schema shapes) and `maxitems3.py` (the schema the .NET client generates, in `net-schema.json`, with one feature removed at a time). `logproxy.py` forwards requests to the server and logs each body. Run them with `python -I`.

The `json*` commands need Docker Model Runner on `http://localhost:12434/engines/v1` with `docker.io/ai/qwen2.5:7B-Q4_0`.

## hive-spike

A hive skeleton with no model: planning cells as `AIAgent`s, cells created in child DI scopes, re-spin counters on a fake clock, a checkpoint snapshot written through `IFileSystem`, and a resume in a fresh container.

| Command | Checks |
|---|---|
| `dotnet run` | Everything below that needs no network |
| `dotnet run -- values` | `Vogen` value objects round-trip through JSON |
| `dotnet run -- counters` | The three-scope re-spin counter, with windows, on `FakeTimeProvider` |
| `dotnet run -- tokens` | The tokenizer, and its count against the local model's own |
| `dotnet run -- store` | The checkpoint store: atomic replace, stale temporary file, corrupt file |
| `dotnet run -- hive` | Run to a checkpoint, then resume: spans, links, events, counters, snapshot |
| `dotnet run -- cancel` | Cancellation from the hive to every cell |
| `dotnet run -- atomic`, `fresh` | How reliably a file can be replaced on Windows, with and without the retry |
| `dotnet run -- anthropic` | The Anthropic adapter, against a fake server on localhost |
| `dotnet run -- compaction` | MAF compaction strategies |
| `dotnet run -- mcp` | An in-process MCP server's tool bound to a cell |
| `dotnet run -- compaction2 10` | A cell with a long tool-call history compacted four ways (sliding window, truncation, tool result collapse, summarising), against the local model. Needs Docker Model Runner. `COMPACT_TRIGGER` and `COMPACT_SHOW=1` set the trigger and print one reply |
| `dotnet run -- compaction3 10` | The same cell with three ways to keep the evidence: notes taken as each result arrives, a fuzzy quote check, and collapse strategies that keep result text. `COMPACT_TAIL=1` moves the answer to the end of each result, `COMPACT_DUMP=1` writes what the model was sent to the temp folder |
| `dotnet run -- idsharing` | Cells sharing an agent `Id` in MAF's session store and `ChatHistoryMemoryProvider`, and the cell-id key that isolates them |

To run on Linux: `dotnet publish -c Release -r linux-x64 --self-contained -o <dir>`, then run `<dir>/HiveSpike` in any Linux container with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` (the Amazon Linux 2023 image worked), and set `TMPDIR` to choose the file system `atomic` and `fresh` test.
