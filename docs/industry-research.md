# Industry research

What industry practice and published research say about the Honeycomb architecture: what backs it, what challenges it, and what we could take from it. Written on 2026-10-30 against [architecture.md](../architecture.md), [stack.md](stack.md), [telemetry.md](telemetry.md), [evidence.md](evidence.md), [drafts/folding.md](../drafts/folding.md), [drafts/specialists.md](../drafts/specialists.md) and the essay in [ref/](../ref/the-sdlc-isnt-a-workflow-anymore.md).

This document is research, not a decision. Nothing in it changes the architecture. Where it suggests a change, that is listed under "What we could incorporate" for you to accept or reject.

## How to read this

Six strands were researched separately: orchestration patterns, the case against multi-agent, context and memory, small models and routing, judging and human-in-the-loop, and security, SDLC evidence and prior art. Two of the six were told to argue against the design.

Every source carries a status:

| Status | Meaning |
|---|---|
| ✔ | I checked it myself against the source, an abstract page or the full text |
| R | A research agent read it through a page-summary fetch, and I did not recheck it |
| S | Seen only in search results. Treat as unverified |

Evidence quality is stated for each strand. Many sources are vendor blogs, and many 2026 sources are preprints a few weeks old. Preprints and vendor posts are not peer-reviewed. Where numbers differ between versions of a paper, the latest version is used and the difference is noted.

**Limit of all of this.** No source tests an architecture like Honeycomb's end to end: run-time-created single-purpose cells, a planner-chosen model, neighbour consults, and typed folding. Everything below is analogy. The closest published system is the Six Sigma Agent (see 5.1).

**Model size, revised after this was written.** A cell's job is one thing. The model can be any model declared for the run, local or frontier, and the planning cell chooses it when it creates the cell. The "small models" rows below are evidence about one option the planner has. They are not a requirement that a cell run on a small model.

---

## 1. Verdict on each architecture claim

The table below reflects the published evidence. Seven retests on the local model were run afterwards, and they changed two earlier findings of ours (the server enforces schema bounds, and one simple injection test was far too kind). They are in section 13.1.

| Honeycomb claim | Verdict | Why, in one line |
|---|---|---|
| A planner that decomposes and routes to specialists | **Supported, with conditions** | Centralised orchestration holds up where work splits into independent parts. It loses on sequential, tightly coupled work (2.1, 3.1) |
| Cells start in small, fresh contexts | **Supported** | Length alone degrades models, even with perfect retrieval (4.1) |
| Results are typed state, folded by code, not transcripts | **Supported, little direct evidence** | The nearest prior art (ACE) merges typed deltas with non-LLM code, and found model rewriting loses detail (4.2) |
| Children receive only parts of the goal body | **Contested** | Cognition and others say conflicting implicit decisions follow (3.2) |
| Peer-to-peer neighbour consults | **Weakest evidence of any choice** | No source tests a bounded peer consult. Several found flat peer coordination failed (3.4) |
| Judge cell settles disputes | **Plausible, unvalidated** | No study tests a judge settling a conflict between agents' claims given evidence. Judges are weak on hard pairs (6.1) |
| Re-spin with counters and windows | **Supported by prior art** | Matches retry budgets and circuit breakers. Told-why retries only help if the reason is external (6.3) |
| Checkpoint to a human, resume only that path | **Supported** | Matches durable-execution practice. Side effects on resume need idempotency keys (6.4) |
| Small local models do narrow specialist work | **Plausible for classification, weak for judgement** | Evidence is thin and mostly from tasks unlike ours (5) |
| Local inference is cheap enough to be deliberately wasteful | **Qualified** | Errors are correlated, so repeated samples buy less than independent votes. Parallel throughput on the local runtime is unmeasured (5.3, 5.5) |
| Specialists must back facts with verbatim quotes | **Supported** | Models paraphrase when asked to copy, so code must check. Span references would remove the problem (4.3) |
| Status after the contributions in the schema | **Supported in direction** | Format requirements hurt open-weight models. Two-pass is an alternative to test (4.4) |
| OpenTelemetry GenAI conventions | **Supported** | The spec matches. It is still Development status (8.1) |
| The hive produces better answers than one agent | **Untested, and the central open question** | The matched-budget comparison has not been run (idea 1 in section 11, experiment 1 in section 13) |

---

## 2. Orchestration and decomposition

**Evidence quality:** mostly vendor engineering blogs and recent preprints. One controlled study (2.1).

### 2.1 When multi-agent helps, by task structure

- **Google, "Towards a Science of Scaling Agent Systems"** (Kim et al., 20 authors, arXiv 2512.08296, v1 9 Dec 2025, v3 8 Apr 2026) ✔. A controlled study: 260 configurations, six benchmarks, five architectures, three model families. Against a single agent, multi-agent changed results by **+80.8%** on decomposable financial reasoning and **−70.0%** on sequential planning. The paper says tasks where a single agent already exceeds about **45%** accuracy see negative returns from adding agents. Trace-level error amplification was 1.0 for a single agent, 17.2 independent, 7.8 decentralised, 5.1 hybrid and **4.4 centralised**. Caveat from the paper itself: error amplification as a main effect was not statistically significant (p = 0.658), and it says efficiency and overhead explain the differences better. The cross-validated R² is only 0.373. The blog version quotes 180 configurations and R² 0.513, so cite v3.
  - *Bears on:* **supports** a central planner and verifier over independent agents. **Challenges** any assumption that more cells help. The planner's sequential-versus-parallel call is the high-stakes decision, because a wrong choice cost up to 70%.
- **Anthropic, "How we built our multi-agent research system"** (13 Jun 2025) R. An Opus 4 lead with Sonnet 4 subagents beat single-agent Opus 4 by 90.2% on an internal research eval. Agents use about 4x the tokens of chat and multi-agent about 15x. Token usage alone explained about 80% of the variance on BrowseComp. Early versions spawned 50 subagents for simple queries, so they added effort-scaling rules (one agent for simple lookups, 2–4 for comparisons, more than 10 for complex research). Vague delegations led to duplicated work and gaps. Most coding tasks parallelise less than research. Subagents do not coordinate with each other.
  - *Bears on:* **supports** run-time cells, planner-written briefs, bounded spawning, and checkpoint-and-resume. **Challenges** the small-model premise (workers were mid-tier frontier models) and peer consults (Anthropic did not add them). Token spend is the lever, so the cost of the hive must be weighed against single-agent runs.
- **Anthropic, "Multi-agent coordination patterns"** (Phillips et al., 10 Apr 2026) R. Five patterns: generator-verifier, orchestrator-subagent, agent teams, message bus, shared state. It says to start with orchestrator-subagent. Its stated failure modes: the orchestrator becomes an information bottleneck, details are lost over repeated handoffs, and a verifier with vague criteria "approves nearly everything".
  - *Bears on:* **supports** typed folding as the answer to the bottleneck, and acceptance criteria as what makes a judge viable. Peer consults match none of the five patterns cleanly.
- **Microsoft, "AI Agent Orchestration Patterns"** (Azure Architecture Center, updated Sept 2026) R. "Use the lowest level of complexity that reliably meets your requirements." Group chat limited to three or fewer agents. Loops need an iteration cap and a human fallback. Magentic-style planning is "slow to converge" and "stalls on ambiguous goals".
  - *Bears on:* **supports** the bounded counters and human escalation. The three-agent limit is a cue to cap neighbours per planner.

### 2.2 Plan-and-execute, replanning and granularity

| Source | Finding | Bears on |
|---|---|---|
| ADaPT, Prasad et al., NAACL Findings 2024, arXiv 2311.05772 R | Decompose only when the executor fails. Up to +28.3% on ALFWorld, +27% on WebShop, +33% on TextCraft. Atomicity depends on the executor | Directly on open question 1 in architecture.md. **Extends:** try the cell, then decompose on failure |
| LLMCompiler, Kim et al., ICML 2024, arXiv 2312.04511 R | A planner emits a task DAG, tasks run in parallel. Up to 3.7x faster, 6.7x cheaper and about 9% more accurate than ReAct | **Supports** dependency-aware fan-out |
| ReWOO, Xu et al., arXiv 2305.18323 R | Decouple planning from observation. 5x token efficiency, +4% on HotpotQA, and the reasoning distilled to a 7B model | **Supports** planner/worker split. Plan-first limits adaptivity, which is why re-spin exists |
| Task-Decoupled Planning, arXiv 2601.07577 (Jan 2026) R | Confine replanning to the active sub-task. Up to 82% fewer tokens | **Extends** re-spin: scope the retry to the failing cell |
| Compositional Skill Routing, arXiv 2606.18051 (Jun 2026, single author) R | Decomposition quality is the main bottleneck. Plain LLM decomposition had 34.2% step-level recall. Catalogue-aware iteration raised accuracy from 51.0% to 67.7% | **Extends:** show the planner the catalogue while it decomposes |
| RAG-MCP, arXiv 2505.03275 R | Retrieve tool descriptions before the call. Selection accuracy 13.62% → 43.13%, prompt tokens down over 50% | **Supports** the two-stage choice of cell |
| Magentic-One, Microsoft, arXiv 2411.04468 R | One orchestrator plans, tracks progress with a ledger and re-plans. Flat, not recursive | **Supports** a structured ledger, which resembles the goal body |
| OpenAI Agents SDK, "agents as tools" R | A manager keeps control and calls specialists as tools. Orchestrating in code gives more predictable cost than in an LLM | **Supports** neighbour-as-tool and deterministic folding |
| LangGraph supervisor repository R | Supervisors can manage supervisors. By default the full history passes down. The maintainers now recommend building it directly with tools for "more control over context engineering" | **Supports** nested planners and context control |

No source gives a formal test for "is this goal atomic". It is an open problem.

### 2.3 Recursion and spawning (all 2026 preprints, read as abstracts)

- **Recursive Agent Harnesses** (arXiv 2606.13643) R. A parent scripts parallel sub-harnesses. Gains on one long-context benchmark with strong models. **Supports** recursion with one persona at every level.
- **Spawn Freely, Act Sparingly** (arXiv 2609.01035, theoretical) R. Proves a harm bound for adaptively generated agent trees, with a phase change when the branching rate passes one. Recommends searching broadly in a sandbox and granting irreversible authority sparingly. **Extends:** budget irreversible actions as well as spawns.
- **Rethinking Multi-Agent Collaboration: When More Is Less** (arXiv 2609.19759) R. Benefit is bounded by task structure. More agents or deeper recursion do not consistently help. Indirect support for a shallow depth cap.

---

## 3. The case against

**Evidence quality:** one large controlled study (2.1), several recent preprints, and practitioner posts without public data.

### 3.1 The strongest sceptical findings

- **Single agents under matched budgets.** Tran and Kiela, "Single-Agent LLMs Outperform Multi-Agent Systems on Multi-Hop Reasoning Under Equal Thinking Token Budgets" (arXiv 2604.02460, Apr 2026) ✔. Under equal reasoning tokens a single agent matches or beats multi-agent on multi-hop reasoning, and reported multi-agent gains often come from extra computation. They argue each handoff can only lose or preserve information. The abstract names an exception: multi-agent becomes competitive when a single agent's context is degraded, which is Honeycomb's own claim. The task is multi-hop QA, not SDLC work.
- **Generated topologies underperform.** Jwalapuram et al., "The Illusion of Multi-Agent Advantage" (arXiv 2606.13003, Jun 2026) ✔. Automatically generated multi-agent systems score below chain-of-thought with self-consistency, at up to 10x the cost, which the authors call "architectural bloat". Expert-designed systems do better on a diagnostic task. This is the closest hit on a planner that builds specialists at run time.
- **Failure taxonomy.** Cemri et al., "Why Do Multi-Agent LLM Systems Fail?" (arXiv 2503.13657, NeurIPS 2025 Datasets and Benchmarks) R. 14 failure modes over 1,600+ annotated traces. Failure rates ran from 41% to 86.7% across seven frameworks. Roughly 44% of failures were system design, 32% inter-agent misalignment and 23.5% task verification, but one secondary source gives a different split, so check the paper's table before quoting. Verification was where interventions paid most (+15.6% in one case). Failure to ask for clarification was 6.8%, which bears on neighbour consults.
- **Fixed pipelines.** Agentless, Xia et al., FSE 2025, arXiv 2407.01489 R. A fixed localise-repair-validate pipeline solved 32.00% of SWE-bench Lite at $0.70 per issue and beat agents that planned for themselves. Dated and the benchmark is now contaminated (7.1), but it is the baseline a run-time planner must beat.
- **Fewer tools.** Vercel, "We removed 80% of our agent's tools" (Dec 2025) R. A text-to-SQL agent went from 80% to 100% success, 274.8 s to 77.4 s, with a frontier model. Five test queries only. **Challenges** narrow specialists for capable models.

### 3.2 Cognition, and how its position moved

- **"Don't Build Multi-Agents"** (Walden Yan, 12 Jun 2025) R. Share full agent traces. Actions carry implicit decisions, and conflicting decisions give bad results. A Flappy Bird task split between subagents produced mismatched halves. Anecdote, no data.
- **"Multi-Agents: What's Actually Working"** (22 Apr 2026) ✔. Revised: setups work when "writes stay single-threaded" and extra agents contribute intelligence. A review agent that starts without the coder's context performs better (Devin Review averages 2 bugs per PR, about 58% severe). The "smart friend" pairing fails when the primary model is notably weaker. Children "often assume they share state they don't", and "the open problems are all communication problems", including how a child surfaces a discovery that should change its siblings' work.
  - *Bears on:* **supports** isolated clean-context review and the planner shape. **Challenges** children receiving only slices of context for write-type work. Sibling communication being unsolved means neighbour consults have no settled practice behind them.

### 3.3 Reliability arithmetic

- τ-bench, Yao et al., arXiv 2406.12045 R. A 2024 frontier agent succeeded on under 50% of tasks, with pass^8 under 25% in retail. pass^k is the chance all k trials succeed. Report per-cell pass^k, not pass@1, because a chain multiplies. A 10-cell chain at 95% per cell succeeds about 60% of the time (0.95¹⁰, my arithmetic).
- METR, Frontier Risk Report (19 May 2026) R. The 50% time horizon of public frontier models was about 12 hours but the 80% horizon about 1.5 hours, both with wide intervals. Reliability falls as work lengthens, which supports short pieces, each with a verified output.

### 3.4 Peer consultation specifically

| Source | Finding |
|---|---|
| Anthropic research system R | Subagents do not coordinate. No peer channel was added |
| Cursor, "Scaling long-running autonomous coding" (14 Jan 2026) R | Flat peer coordination failed: locks left twenty agents running at the throughput of two or three, and optimistic concurrency made agents risk-averse. Planners that spawn sub-planners, with workers that do not coordinate, worked. A judge decided each cycle whether to continue |
| Google scaling study ✔ | Decentralised setups amplified errors more than centralised (7.8 against 4.4), though the effect was not significant |
| A2A documentation R | Treats agents as opaque stateful peers. Wrapping an agent as a tool is fine for stateless skills and "undersells" the protocol |
| Liu and Li, arXiv 2503.23053 R | Subtasks with independent contexts lose information. Their fix, letting a subtask query completed subtasks, resembles a neighbour consult |

**Reading:** a consult that is single-shot, read-only and bounded is defensible. Anything that behaves like a negotiation has no support. No source shows a bounded peer consult helping.

### 3.5 What the criticism does not reach

The strongest negative results use frontier models on reasoning, planning or SWE-bench tasks, several on 2024-era systems. Honeycomb's cells are isolated by design, results are structured, and the planner never keeps a shared transcript. That avoids Cognition's shared-transcript cost model and matches its 2026 clean-context finding. It does not avoid information loss at each fold, planner decomposition error, cost multiples, or the weak evidence for small models.

---

## 4. Context, state and structured output

**Evidence quality:** the best-supported strand. Several large measured studies, plus vendor engineering practice.

### 4.1 Small fresh contexts

| Source | Finding |
|---|---|
| Anthropic, "Effective context engineering for AI agents" (29 Sep 2025) R | Context is a finite resource with diminishing returns. Subagents in clean windows return condensed summaries of about 1,000–2,000 tokens |
| Chroma, "Context Rot" (Jul 2025) R | 18 models degrade with length even on simple tasks. Focused prompts of about 300 tokens beat full prompts of about 113k on LongMemEval. Shuffled haystacks beat coherent ones in all 18, so a tidy folded body is not automatically easier to read |
| NoLiMa, Modarressi et al., ICML 2025, arXiv 2502.05167 R | With little lexical overlap, 11 of 13 models fell below 50% of their short-context score at 32K. GPT-4o fell from 99.3% to 69.7% |
| RULER, NVIDIA, COLM 2024, arXiv 2404.06654 R | About half of models claiming 32K kept satisfactory performance there |
| Du et al., arXiv 2510.05381 S | Length alone hurts by 13.9% to 85% even with perfect retrieval. Asking the model to recite the evidence first helped by up to 4% on RULER |
| Google ADK context article (Dec 2025) R | Context as "a compiled view over a richer stateful system", with scope the minimum by default and artifacts passed by reference |
| Long Context vs RAG, arXiv 2501.01880 S | Dissent: long context beat retrieval on self-contained material. Selecting parts for a child is a retrieval step that can lose connective detail |

### 4.2 Typed state and deterministic folding

- **Agentic Context Engineering (ACE)**, Zhang et al., arXiv 2510.04618, ICLR 2026 ✔. The closest prior art for folding. Itemised delta entries are "merged deterministically into the existing context by lightweight, non-LLM logic". It names *brevity bias* and *context collapse*: in one example a context fell from 18,282 tokens at accuracy 66.7 to 122 tokens at 57.1 after iterative rewriting. This independently matches our finding that model-written merging loses specifics, and supports the rule that only code folds and a model compresses only past a limit.
- Anthropic, "Effective harnesses for long-running agents" (26 Nov 2025) R. Fresh sessions bridge through a progress file and a JSON feature list in which the agent may change only a `passes` field. "The model is less likely to inappropriately change or overwrite JSON files compared to Markdown files." Practitioner claim. The nearest industry precedent for code controlling what each writer may change.
- PACT, arXiv 2606.05304 (Jun 2026) R. Compressing each agent output into an action-state record before shared history halved input tokens on SWE-agent. It also found no fixed strategy is best for every task, so the record schema must fit the work.
- AgentFold (arXiv 2510.24699), Context-Folding (arXiv 2510.11967), ACON (arXiv 2510.00615) R. Model-driven folding. Context-Folding reports matching ReAct with a 10x smaller active context. These are the opposite of deterministic folding, so they are a contrast and not prior art. ACON's failure-driven refinement of compression prompts is worth borrowing.
- **Gap:** no controlled study compares shared typed state with message passing, and none tracks a fact through several summarisation stages.

### 4.3 Quotes and attribution

- **Wright et al.**, "Unstructured Evidence Attribution for Long Context Query Focused Summarization", EMNLP 2025, arXiv 2502.14409 ✔. Exact-match rates for copying evidence (Table 3): GPT-4o mini **11.06%**, Llama 3.1 8B **43.93%**, Llama 3.2 1B **0.0%**, Llama 3.1 8B fine-tuned **78.36%**. Models paraphrase when asked to copy. **Supports** checking quotes in code, and warns a 7B model may fail the check often.
- Anthropic Citations documentation R. The model emits location indices and the API extracts `cited_text`, which is guaranteed to point into the supplied document. **Extends:** have cells return a span reference and let code copy the text, which makes a quote error structurally impossible.
- ALCE, Gao et al., EMNLP 2023 S. Even the best models lacked complete citation support about half the time.

### 4.4 Structured output and field order

- **Tam et al., "Let Me Speak Freely?"** (arXiv 2408.02442, EMNLP 2024 industry) R. Strict formats degrade reasoning. On GSM8K Claude 3 Haiku scored 86.99 with free JSON and 23.44 with a schema. GPT-3.5 placed `answer` before `reason` in 100% of JSON-mode replies. Contested: classification tasks improved with JSON, and a published response says the prompts differed between arms.
- **The Format Tax**, Lee, D'Antoni and Berg-Kirkpatrick (arXiv 2604.03616, Apr 2026) ✔. Structured output degrades reasoning and writing in open-weight models. "The dominant cost enters at the prompt": the format instructions cause most of the loss, and constrained decoding only a fraction. Generating free-form first and reformatting in a second pass recovers much of it. Recent closed-weight models show little or no tax.
  - *Bears on:* **supports** our status-last finding in direction, and **qualifies** it. Reordering fields addresses one part of the cost. A two-pass design (reason in free text, then reformat) is the alternative to test on the 7B.
- **Field-order evidence is thin.** Informal posts report large effects. We found no replicated study. Our own 18/50 → 43/50 result is the best evidence for our own schema.
- **JSONSchemaBench**, Geng et al., arXiv 2501.10868 R. Engine coverage varies widely (on one hard set: Guidance 0.41, llama.cpp 0.39, XGrammar 0.28, Outlines 0.03). Constrained decoding did not hurt accuracy on GSM8K with an 8B model.
- **`maxItems` is enforceable, and our earlier finding was wrong.** The llama.cpp grammar guide ✔ shows `minItems: 10, maxItems: 100` compiling to `("," space item){9,99}`, and says "Unsupported features are skipped silently." We retested (section 13.1): the local server enforces `maxItems` and `maxLength`, and the `Microsoft.Extensions.AI` OpenAI adapter had been moving the bounds into the description as prose before sending. A hard cap can force truncation or padding, so cap the strings as well and pair the list with an overflow flag.

### 4.5 Prompt caching

Anthropic's documentation R: cache reads cost 0.1x (0.05x on the newest models), writes 1.25x or 2x, and nothing below a minimum prefix is cached. Manus names cache hit rate "the single most important metric" for a long loop. **Qualifies** "fresh small contexts": each cell type can keep its system prompt, schema and tool definitions byte-stable above the minimum, with the goal body last, and keep caching. A cell that runs once pays the write premium for nothing. For the local server, prefix reuse in llama.cpp was not researched.

---

## 5. Small models, routing and local inference

**Evidence quality:** a position paper from a hardware vendor, several benchmarks, and almost nothing on tasks like ours.

### 5.1 The thesis and its support

- **Belcak et al. (NVIDIA), "Small Language Models are the Future of Agentic AI"** (arXiv 2506.02153, v3 22 Sep 2026) ✔. A position paper arguing small models are "sufficiently powerful, inherently more suitable, and necessarily more economical" for many agentic calls. It cites a 7B model as 10–30x cheaper than a 70–175B model. Its route to quality is **fine-tuning on logged agent traces**, which Honeycomb does not plan. No new experiments.
- **MAKER**, Meyerson et al., arXiv 2511.09030 (Nov 2025) ✔. Extreme decomposition into single-step microagents with voting, over a million steps with zero errors, on Towers of Hanoi. The full text R gives per-step error by model: gpt-4.1-mini about 0.22%, gpt-oss-20B about 3.6%, haiku-4.5 about 18.4%, gpt-4.1-nano about 35.7%, and a 3B model failed entirely. The decomposition was supplied, not discovered, the theory assumes independent errors, and only one task was fully evaluated. Strongest support for the bet, and a measure of its limits.
- **The Six Sigma Agent**, Patel et al., arXiv 2601.22290 (Jan 2026) ✔. Closest published description of Honeycomb: an atomic dependency tree, each atom run n times across diverse models and voted. Claims 5% per-action error becomes 0.11% with 5 agents, 14,700x better reliability, and 80% lower cost on three enterprise cases. The theory assumes independent errors, which 5.3 contradicts. Results are the authors' own.
- Requirements classification, Zadenoori et al., arXiv 2510.21443 R. Small models averaged only 2% lower F1 than large ones, not significant. **Supports** small models for classification-style cells.

### 5.2 Where small models were weak

- Our own tests (evidence.md): planner 47/60, re-spin decider 40/60, reviewer 50/60 under placeholder prompts.
- Code review, Cihan et al., arXiv 2505.20206 R. Even GPT-4o judged code correctness right only 68.5% of the time. A 7B reviewer scoring around 50/60 is not out of line, but it is not good.
- BFCL v4 (changelog, S) adds agentic and multi-turn categories, where small models fall. Single-turn function-calling scores overstate multi-step ability.
- "LLMs Get Lost In Multi-Turn Conversation", Laban et al., arXiv 2505.06120 R. Top models averaged a 39% drop from single-turn to multi-turn, mostly through unreliability, with early commitment and no recovery. **Supports** one-shot cells over repair loops, and resembles our finding that a model commits to `cannot_answer` before writing facts.
- Quantisation: no rigorous Q4-versus-Q8 tool-calling study was found. Our Q4 default is unvalidated.
- Tokenizer estimates ran 8–10% low in our test, and Docker Model Runner's default llama.cpp backend is documented as suited to single-user use. Red Hat measured vLLM at over 35x the throughput of llama.cpp at peak concurrency on one datacentre GPU, so parallel fan-out on a laptop may queue. That benchmark used full-precision weights on different hardware, so it does not transfer directly.

### 5.3 Ensembles and correlated errors

- **Correlated Errors in LLMs**, Kim et al., ICML 2025, arXiv 2506.07962 ✔. Across over 350 models, they agree about 60% of the time when both are wrong on one leaderboard. Larger, more accurate models are more correlated, as are models from one provider or architecture.
- Nine Judges, Two Effective Votes, arXiv 2605.29800 (May 2026) R. Nine frontier models from seven families behaved like about two independent votes on NLI tasks. The best single judge matched the panel.
- **Implication:** asking the same 7B several times buys less than independent voting would. Use disagreement as an escalation signal, and diversify prompts and models.
- CATTS, arXiv 2602.12276 R. Uniform extra samples saturate. Spending extra only on contentious steps (low vote margin) gave up to 11.8% better results than majority voting with fewer tokens. **Extends:** adaptive fan-out.
- Snell et al., arXiv 2408.03314 R. Compute-optimal test-time scaling beat best-of-N by over 4x, but with process verifiers on maths. Without a verifier the gain is not shown.

### 5.4 Routing

- FrugalGPT (arXiv 2305.05176) R: a cascade matched GPT-4 at up to 98% lower cost, in the GPT-4 versus GPT-3.5 era. RouteLLM, Hybrid LLM and AutoMix R report similar savings on chat or QA benchmarks.
- **LLMRouterBench** (arXiv 2601.07206, Jan 2026) R: 400K+ instances, 33 models, 10 routers. Many routers performed about the same, several (including a commercial one) did not reliably beat the best single model, and a large gap to the oracle remained. **Challenges** the hope that a planner can pick local or frontier from the task text. **Supports** a small curated pool and a conservative default.
- Calibration: a 2026 study of 11 small models (arXiv 2608.05064, single author, multiple-choice QA) R found verbalised confidence poorly calibrated, with 3 of 22 model-task pairs earning certified autonomy at a 20% risk budget. **Challenges** gating escalation on a 7B's stated confidence. Use validator failures, vote disagreement and log-probabilities, calibrated on a held-out set.

### 5.5 The economic premise

Prices for a fixed capability level have fallen about 5–10x a year, while frontier-run prices rose (Gundlach et al., arXiv 2511.23455 R). The local cost advantage for a fixed task narrows over time. Anthropic's finding that token spend drives quality also means cheap tokens enable the "wasteful" approach without guaranteeing gains. No source compares decomposed small models with one large model on an SDLC task, which is the gap Honeycomb could fill.

---

## 6. Judging, retries and the human

**Evidence quality:** a mature academic literature on LLM judges, but not on this use of one.

### 6.1 The judge

**No paper tests exactly "a judge settles a conflict between two agents' claims given verbatim evidence".** The nearest are Khan et al. and JudgeBench.

| Source | Finding | Bears on |
|---|---|---|
| JudgeBench, Tan et al., ICLR 2025, arXiv 2410.12784 ✔ | On hard pairs, strong models including GPT-4o score "just slightly better than random guessing" | **Challenges** the judge hardest. Our disputes are the hard, objective kind. Lean on evidence checks over the model's own reasoning |
| Zheng et al., MT-Bench, NeurIPS 2023, arXiv 2306.05685 R | Strong judges reach over 80% agreement with humans, with position, verbosity and self-preference bias | **Supports** a frontier default. Distilled claims reduce verbosity bias |
| Wang et al., "Not Fair Evaluators", arXiv 2305.17926 S | Swapping response order flips verdicts, e.g. 66 of 80 queries in one case. Fix by evaluating both orders | **Extends:** present claims in both orders and escalate on a flip |
| Panickssery et al., NeurIPS 2024, arXiv 2404.13076 S | Judges recognise and favour their own output (GPT-4 recognised its own at 73.5%) | **Challenges** same-family judges. Use a different family from the claimants |
| Kim et al., correlated errors ✔ | Same-provider models share errors | Same |
| Thakur et al., "Judging the Judges", arXiv 2406.12624 R | Best judges ran about 8 points below humans on chance-corrected agreement. Weak judges accept wrong answers and miss partly correct ones | **Challenges** a small judge. Measure with chance-corrected agreement |
| Verga et al., "Replacing Judges with Juries", arXiv 2404.18796 S | Three smaller models from different families agreed better with humans than one large judge, at over 7x lower cost | **Extends:** optional diverse panel for high stakes, with a split as the escalation trigger |
| Khan et al., ICML 2024, arXiv 2402.06782 S | With both sides arguing, weaker judges reached 76% against 48% for naive QA. Judging one advocate was the failure case | **Extends:** a short rebuttal round before judging |
| Wu et al., arXiv 2511.07784 R | Agents in a wrong majority corrected only 3.6–34.4% of the time. Final accuracy was bounded by the strongest reasoner | A weak judge follows the majority. Weigh claims by evidence, not by count |

### 6.2 Debate

- **Debate or Vote**, Choi, Zhu and Li, NeurIPS 2025, arXiv 2508.17536 ✔. Majority voting accounts for most of the gains attributed to multi-agent debate, and debate induces a martingale over beliefs, so it does not improve expected correctness. Interventions that bias updates toward correction helped a little. **Supports** Honeycomb's one-shot neutral judge over cells arguing with each other. When claims are independent samples, a vote may be cheaper than a judge.
- **Sparse topology**, Li et al., Google, arXiv 2406.11776 ✔. Debates over a sparse communication graph reach "comparable or superior performance while significantly reducing computational costs". The researcher reports about 41–44% lower cost on MATH and GSM8K with GPT-3.5 R. **Supports** neighbour-only communication as no worse than all-to-all.
- Should We Be Going MAD? (ICML 2024, arXiv 2311.17371) S and Stop Overvaluing Multi-Agent Debate (arXiv 2502.08788) S: debate often fails to beat self-consistency.

### 6.3 Re-spin and retry budgets

- **Self-correction** is weak without outside feedback. Huang et al., ICLR 2024, arXiv 2310.01798 R; Kamoi et al., TACL 2024, arXiv 2406.01297 S. Models fail mainly at locating the error. Given the location, correction works (Tyen et al., ACL Findings 2024, arXiv 2311.08516 R). **Extends:** the "why the last cell failed" hint should come from outside the model and say where it failed.
- **Google SRE, "Handling Overload"** R. Retry budgets: retry only while the retry ratio is under 10%, give up after three attempts, and retry only at the layer directly above the failure, because "if multiple layers retried, we'd have a combinatorial explosion". Per-client budgets cap amplification at about 1.1x against 3x. This is close prior art for the windowed counters.
  - *Bears on:* **supports** the counters. **Extends:** only the direct parent planner should re-spin. At three nested levels and three attempts each, a bad leaf could cost 27 runs.
- Fowler, "Circuit Breaker" R. Closed, open, half-open (one probe after a reset timeout). **Extends:** an exhausted hive counter is an open breaker. Add a half-open probe after the reset period, and log every trip.

### 6.4 The human, durability and side effects

- **Temporal, Activity definition** R. Completion is observed exactly once, but an activity "may be executed multiple times and may even partially complete more than once". Idempotency keys should be enforced by the called service. **Extends** the `repeatable` flag: add an "in flight at crash" state, because a side effect may or may not have happened. The saga pattern (microservices.io R) adds optional compensation notes.
- LangGraph interrupts R re-run the interrupted node from the start on resume. Honeycomb's rule (re-run only the checkpointed path, hand non-repeatable cells to a human) is more careful.
- **Autonomy.** Feng et al., "Levels of Autonomy for AI Agents" (arXiv 2506.12469) R places an agent that interacts only to resolve blockers at the "approver" level, and warns of rubber-stamping. Anthropic, "Measuring AI agent autonomy in practice" (Feb 2026) R: experienced users auto-approve more and interrupt more, and about 0.8% of public-API actions looked irreversible. **Extends:** favour cheap visibility and interrupt over mandatory approval, and track how fast humans approve resume prompts.
- Abstention (Wen et al., TACL 2025, arXiv 2407.18418 S) treats unanswerable queries, insufficient evidence and out-of-scope as different causes. **Extends:** `cannot_answer` could carry a reason class.

---

## 7. SDLC evidence

**Evidence quality:** mixed. Several vendor datasets, one randomised trial, and a benchmark whose own publisher has retired it.

### 7.1 Benchmarks

- **OpenAI stopped reporting SWE-bench Verified** (23 Feb 2026) R: of 138 audited hard problems, 59.4% had material test or description flaws, and frontier models could reproduce gold patches. It recommends SWE-bench Pro. SWE-Bench+ (arXiv 2410.06992 S) found 32.67% of successful patches had the solution leaked in the issue text.
  - *Bears on:* do not cite public benchmark scores to justify the design. Build a private evaluation set from real tickets.
- Open agents are mostly a single loop with specialists, not deep trees (OpenHands, ICLR 2025 S).

### 7.2 Does AI help developers

| Source | Finding |
|---|---|
| METR randomised trial (Jul 2025) R | 16 experienced developers, 246 issues: 19% slower with AI, while forecasting 24% faster |
| METR follow-up (24 Feb 2026) R | 57 developers, 800+ tasks: −18% and −4% time change, both inconclusive. 30–50% withheld tasks they would not do without AI. METR calls the data an unreliable signal |
| DORA 2025 (23 Sep 2025) R | About 5,000 respondents: 90% use AI and over 80% report productivity gains, 24% trust it a lot and 30% little or not at all. AI use now linked to higher delivery throughput |
| Faros 2025 S, GitClear 2025 S | More tasks and PRs, but larger PRs, longer review, more duplication. Vendor analytics, partly secondary |
| Spec-driven development, Böckeler on martinfowler.com (Oct 2025) R | Reviewing generated markdown can be harder than reviewing code. Tools showed non-determinism |

**Reading:** perceived gain is not measured gain. Plan internal measurement and keep output small and reviewable.

### 7.3 Task-specific

- Test generation: TestGen-LLM at Meta (FSE 2024, arXiv 2402.09171) S. 75% built, 57% passed reliably, 25% raised coverage, 73% of recommendations accepted. A deterministic filter that accepts only measurable improvement is the pattern for the unit-test specialist.
- Code review: automated comments are often resolved but add noise, and repository context limits trust (Cihan et al., arXiv 2505.16339 S). SWR-Bench best F1 about 19% S.
- Ticket analysis and impact analysis: only weak evidence. One user-story study found agreement of Krippendorff's alpha 0.491 S. **No published evaluation of agents doing change-impact analysis was found.** Build a golden set from past tickets with known actual impact before trusting the impact specialist.

---

## 8. Security

**Evidence quality:** strong design analysis and several empirical attack studies, mostly on older models.

### 8.1 Findings

- **Design patterns for securing agents**, Beurer-Kellner et al., arXiv 2506.08837 R. Six patterns: action-selector, plan-then-execute, LLM map-reduce, dual LLM, code-then-execute, context-minimisation. Protection holds only if outputs are constrained tightly enough, and a summary field can still be manipulated. Confirming every action is impractical in software engineering.
  - *Bears on:* **supports** the planner as the "privileged" cell that sees plans and typed results and never raw ticket text. **Qualifies** "JSON-only results stop propagation".
- **CaMeL**, Debenedetti et al. (Google, DeepMind, ETH), arXiv 2503.18813 ✔. Separating control flow from data flow with capability checks solves 77% of AgentDojo tasks with provable security, against 84% undefended. **Extends** "a cell only has the tools it is handed": CaMeL also tracks which values may reach which tool.
- **Agents Rule of Two**, Meta (31 Oct 2025) ✔. Until injection can be dependably blocked, an agent in one session must satisfy no more than two of: [A] processes untrustworthy inputs, [B] has access to sensitive systems or private data, [C] can change state or communicate externally. **Extends:** classify every cell A/B/C in its definition and check it when the plan is validated. A ticket-reading cell has A and B and should not have C. A PR-opening cell has C and should not read raw ticket text.
- **Multi-agent hijacking**, Triedman et al., arXiv 2503.12188 R. Web-based attacks hijacked multi-agent systems in about 58–90% of trials with GPT-4o. A fake error message passed through front-line agents tricked the orchestrator. "MAS have no real trust model": agents accept other agents' outputs at face value. **Challenges** the idea that only leaf cells see untrusted text. A neighbour's reply is untrusted input to the cell that consulted it.
- Prompt Infection, Lee and Tiwari, arXiv 2410.07283 R. Self-replicating injections spread agent to agent, and partial histories (local messaging) cut success by about 20%. Limited sharing helps but is not sufficient.
- **Evaluation realism.** NIST CAISI (Jan 2025, updated Dec 2025) R: tailored attacks raised success on AgentDojo from 11% to 81%, and repeating each attack 25 times raised average success from 57% to 80%. Its March 2026 red-team competition (250,000+ attempts, 13 frontier models) found a successful attack against every model R. **Challenges** the 30/30 result in evidence.md, which was single-shot, non-adaptive and on simple cases. Our retest with eight hand-written payloads confirmed the concern (section 13.1): most ticket payloads changed the reply.
- **No study measures injection propagation specifically through JSON-only inter-agent results.** Types, enums and short bounded strings leave little room. Free-text fields (summaries, rationales, risk descriptions) remain the channel.
- MCP security best practices (draft spec) R: no token passthrough, scope minimisation, and downstream audit trails that show the wrong identity if tokens are passed through. Invariant Labs' GitHub MCP "toxic agent flow" S: a public issue triggered an agent with a broad token to leak private repositories. **Supports** a per-cell, single-repository token for the pull-request cell.
- Checkpoint storage is a trust boundary. Microsoft Agent Framework documentation (updated Sep 2026) R: "Never load checkpoints from untrusted or potentially tampered sources." **Extends:** sign or hash the JSON snapshot and set directory permissions, since a tampered checkpoint is a persistent injection route. Memory poisoning (MINJA, arXiv 2503.03704 S) reports high injection success through ordinary queries.
- OWASP Top 10 for LLM Applications 2025 R and Top 10 for Agentic Applications 2026 (published 9 Dec 2025; item names only from vendor blogs, S). LLM06 excessive agency, LLM07 system prompt leakage (the persona body is a system prompt), and LLM10 unbounded consumption (a planner that creates cells needs a cell-count and token budget).

### 8.2 Governance

NIST's agent overlays for SP 800-53 are still in development, so there is no final control text. EU AI Act dates moved under the 2026 Digital Omnibus, and an internal SDLC assistant is unlikely to be high-risk, but both items are secondary or search-only and should be checked against primary text before anyone relies on them. A per-run audit record keyed by run id, cell id and the identity used at each tool call is good practice either way.

---

## 9. Observability, durability and evaluation

- **OpenTelemetry GenAI agent spans** R. Status is Development, and the conventions now live in `semantic-conventions-genai`. Operations include `invoke_agent`, `invoke_workflow` (for processes coordinating multiple agents), `plan` and `execute_tool`. Content is opt-in and may be stored by reference (`*_ref`). This matches telemetry.md. **Extends:** use `*_ref` for content so it can sit in a controlled store.
- **Vendors differ.** Datadog's LLM Observability ingests GenAI conventions from OTel 1.37 but is not supported on its US Government sites, which matters for FedRAMP, and exporting both the SDK and API spans double-counts tokens, as we saw R. Langfuse needs the root span and trace-level fields on every span R. Honeycomb.io's guide needs `gen_ai.agent.name` and says not to sample GenAI telemetry R. Arize, LangSmith, Logfire and OpenLLMetry were not verified.
- **Anthropic, "Demystifying evals for AI agents"** (Jan 2026) R: prefer deterministic graders, grade outcomes not paths, keep capability and regression evals apart, report pass^k (75% per trial is about 42% at pass^3), start from 20–50 tasks drawn from real failures, and calibrate LLM judges against humans. **Supports** the fake-model scenario tests, and **extends** them with pass^k and a private golden set.
- **Microsoft Agent Framework checkpoints** R. A rehydrated workflow "must preserve the topology and executor identities", and agents need a stable `Id`. For a planner that builds cells at run time, the checkpoint must hold the plan and cell ids because the topology is not in code. This matches the tree in stack.md.
- No standard for per-cell cost attribution was found beyond token attributes on spans.

---

## 10. Prior art and the name

**Evidence quality:** mostly unread. Treat this section as leads.

- **Contract Net Protocol** (Smith, 1980; FIPA) R: a manager announces a task, contractors bid, the manager awards. An alternative to creating cells at run time: let existing cells bid.
- **Erlang/OTP supervision trees** R: supervisors restart failed workers. Honeycomb's tree already resembles one. **Extends:** explicit restart policy, restart limits and escalation. "Let it crash" does not transfer to non-idempotent cells.
- **HTN planning**, blackboard systems (Hearsay-II), the actor model, stigmergy, holonic systems and BDI agents: not read. HTN planning is the most relevant, because a library of vetted decompositions is an alternative to free-form LLM planning.
- **Conway's law:** the essay argues against mirroring the org chart. We found only practitioner posts on the subject, and no study on whether mirroring or inverting the org chart improves agent systems.
- **Name collisions.**
  - **Honeycomb.io** is an observability vendor with agent-tracing documentation R. Searches for "honeycomb" with agents or OpenTelemetry will surface the vendor first, and this project uses OpenTelemetry.
  - **"HoneyComb: A Flexible LLM-Based Agent System for Materials Science"** (Zhang et al., arXiv 2409.00135, 2024) ✔ is an unrelated materials-science agent with a knowledge base and a tool hub.
  - **Hexagonal Architecture** (Cockburn, 2005) is the ports-and-adapters pattern. Honeycomb's hexagon is a metaphor and not that. The MCP tool boundary is port-like, so say so clearly if readers might mix them up.
  - Two searches found no multi-agent SDLC project using hexagonal cells. That is not an exhaustive search.

---

## 11. What we could incorporate

Ordered by how much the evidence supports it and how cheaply it can be done. "Lands in" says which document would change. Each is a proposal.

| # | Idea | Evidence | Lands in |
|---|---|---|---|
| 1 | **Run the missing baseline.** Same fixtures through one frontier agent with full context and tools, one frontier agent with summarise-and-restart, the hive on local models, and the hive on frontier models, with matched tokens, reporting quality, cost, latency and pass^k | 3.1, 3.3, 5.5, 9 | evidence.md |
| 2 | **Send schemas as written** so bounds reach the server (retested: they are enforced), cap lists and strings together, and add an overflow flag. Done as a finding, not yet a decision | 4.4, 13.1 | stack.md |
| 3 | **Span references for quotes.** A cell returns document id and offsets, and code copies the text. Tried on short sources: no better than verbatim quotes, see 13.1 | 4.3, 13.1 | stack.md Results |
| 4 | **Two-pass schema for hard cells:** free-text reasoning, then reformat. Tried: worse than status-last on the 7B, see 13.1 | 4.4, 13.1 | stack.md, evidence.md |
| 5 | **A/B/C classification per cell** (untrusted input, sensitive access, can act), enforced when the plan is validated | 8.1 | specialists.md, stack.md |
| 6 | **Treat neighbour replies as untrusted input.** Bounded typed fields, free text labelled as data and capped, optionally a quarantined extraction step | 8.1 | stack.md |
| 7 | **Judge protocol:** claims shown in both orders and a flip escalates, quotes checked in code before judging, a different model family from the claimants, a short rebuttal round, and optional diverse panel for high stakes | 6.1 | specialists.md |
| 8 | **Re-spin refinements:** only the direct parent re-spins, the failure hint names where it failed and comes from a check or tool, and a half-open probe after the reset period | 6.3 | architecture.md, stack.md |
| 9 | **Idempotency keys** from run id, cell id and step for cells with side effects, check-before-act, and an "in flight at crash" state | 6.4 | stack.md Checkpoints |
| 10 | **Sign or hash checkpoints** and restrict directory permissions. Store the plan and cell ids in them | 8.1, 9 | stack.md Checkpoints |
| 11 | **Mark each cell read-type or write-type.** Parallelise reads freely and keep writes under one owner | 3.2 | specialists.md |
| 12 | **A "decisions made" part in the goal body** so siblings share names, interfaces and conventions | 3.2 | folding.md |
| 13 | **Try then decompose.** A cell attempts the goal and decomposes on failure, and atomicity is judged relative to the executor | 2.2 | architecture.md question 1 |
| 14 | **A cheap default of not decomposing,** with effort-scaling rules in the planner prompt and a single-cell path | 2.1, 3.1 | specialists.md planner |
| 15 | **Cap neighbours per planner** and keep consults single-shot and read-only | 2.1, 3.4 | architecture.md |
| 16 | **Catalogue-aware planning:** the planner sees specialists by fine-grained capability when it decomposes | 2.2 | specialists.md |
| 17 | **Escalate on observed signals** (validator failures, vote disagreement, log-probabilities), calibrated on about 200 labelled cases per cell, and not on stated confidence | 5.4 | stack.md Providers |
| 18 | **Adaptive fan-out:** three local samples, more or a frontier model only when they disagree | 5.3 | stack.md |
| 19 | **Per-cell scorecard** (pass^k, cost, latency, disagreement) that sets the local, frontier or any hint, with a quantisation matrix (Q4, Q6, Q8) and a concurrency test at 1, 4 and 8 cells | 5.2, 5.4 | stack.md, evidence.md |
| 20 | **Budget irreversible actions** at hive scope beside the spawn counters, and add cell-count and token limits | 2.3, 8.1 | architecture.md, stack.md |
| 21 | **Log traces for later distillation,** with personal data scrubbed | 5.1 | telemetry.md |
| 22 | **Content by reference** (`*_ref`) and per-cell identity on every MCP call, so downstream audit logs tie back to a run and cell | 8.1, 9 | telemetry.md |
| 23 | **Byte-stable cell prefix** (system prompt, schema, tools) with the goal body last | 4.5 | stack.md |
| 24 | **Private golden set** from past tickets with known outcomes, for impact analysis and ticket analysis | 7.3 | evidence.md |

---

## 12. Questions already open in the documents that this research bears on

| Open question | What the research says |
|---|---|
| architecture.md question 1: who decides a goal is atomic | ADaPT: decide relative to the executor, and decompose on failure. No formal test exists |
| folding.md decision 4: confirmed on one cell's word, or a second source | Correlated errors mean a second cell of the same model adds little. Prefer a code check, a different family, or a judge |
| folding.md decision 5: evidence in the body or by reference | Span references from the source document give both: self-contained quotes copied by code |
| folding.md decision 2: which parts a child receives | Cognition and the long-context-versus-retrieval results warn that selection can lose connective detail. A "decisions made" part is always included |
| specialists.md: the two-stage choice of cell | RAG-MCP and tool-to-agent retrieval support it, with catalogue awareness |
| specialists.md: planner and judge prompts | The planner needs effort-scaling rules. The judge needs the protocol in idea 7 |
| stack.md open decision on frontier validation | The Format Tax says closed-weight models show little format tax, so a frontier model may not need the two-pass design |
| evidence.md "Not tested": hive versus a single agent | Idea 1 |

---

## 13. Experiments that would settle the main questions

1. **Matched-budget comparison** (idea 1), split by read-type and write-type tasks.
2. **Decomposition ablation:** no decomposition, a human-written one, a planner's, and one a judge reviewed. This separates planner error from coordination error.
3. **Implicit-decision conflict:** tasks where subtasks must agree on unstated conventions, measured with and without a decisions part and with on-demand access to a sibling's result.
4. **Fact survival through folding:** plant a fact at depth one and measure whether it survives to depths two and three.
5. **Small-model reliability curve:** per-cell error and pass^k for each candidate local model (3B, 8B, 20B and up), with and without voting, including measured error correlation.
6. **Chain-length curve:** success against the number of cells in sequence, with and without a judge.
7. **Judge accuracy on our own disputes:** a labelled set of real conflicts, with order-swap stability and chance-corrected agreement.
8. **Injection through the typed channel:** adaptive, repeated attacks aimed at free-text fields and at neighbour-to-neighbour paths.
9. **Cost and latency frontier** for 1, 2, 4 and 8 parallel specialists on the target machine.

### 13.1 What has been run

On 2026-10-30, with the local 7B Q4 model only, no frontier model and invented data. Full setup and numbers are in the seventh round of [evidence.md](evidence.md). Read each as a direction, since the counts are small.

| Question | Result |
|---|---|
| Does the server enforce `maxItems`? (idea 2) | **Yes. Our earlier finding was wrong.** The .NET adapter moved the bound into the description. 0 of 21 direct replies went over the cap against 10 of 10 over without it, and the SDK's own response format kept it as written |
| Status first, status last, two passes or sentence numbers? (ideas 3 and 4) | Status last was clean in 33 of 40 against 16 of 40 for status first, so the earlier result held. Two passes (16 of 40, 13 unparseable) and sentence numbers (24 of 40) did not beat it. Quotes were word for word in 32 of 32 on short sources |
| Parallel throughput on the local server? | About 2x at 4 at once (27 to 56 tokens/s) and no more at 8. Eight at once took about a minute against 15 s alone |
| Do isolated siblings agree on implicit choices? (experiment 3) | **No: 8 of 20 pairs agreed on all four conventions.** A decisions part gave 20 of 20, and so did showing the sibling's result |
| Does a planner pick the parts a child needs? | In 7 of 8 scenarios, every time. It gave 3.8 of 5 parts, so it saves little. Fixed rules covered 2 to 5 of 8 |
| Can a 7B judge settle disputes? (experiment 7) | 30 of 48 right alone, 40 of 48 with a code check on the quotes. It never chose the wrong claim in 96 runs, and every miss was an escalation. Eight invented cases |
| Does injection through the typed channel work? (experiment 8) | **Mostly yes against the 7B.** 24 of 40 ticket payloads obeyed by a strict count, and the payload text is often copied into `context`, which folds forward. Neighbour replies: 3 of 40 obeyed, with 13 unparsed. The automatic check errs both ways |
| Do errors correlate? (experiment 5, in part) | 40 of 43 wrong answers in the earlier ten-kind test came from five cases that failed every time, so repeating a cell at the same temperature would not fix them |

**Not yet run:** the matched-budget comparison with one agent (experiment 1), planner against human decomposition (2), fact survival through depth (4, only the part-selection step), the chain-length curve (6), the cost frontier beyond throughput (9), Q4 against Q6 and Q8 (only Q4_0 is installed), neighbour consults on and off, and anything on a frontier model.

---

## 14. Not checked

- Most sources were read by research agents through page-summary fetches, so exact wording and some numbers could not be confirmed. Items marked ✔ were rechecked against the source. Items marked S were not read.
- I did not read the full text of most arXiv papers. Where only an abstract was read, the numbers quoted came from the full text by the research agent and are marked R.
- Several 2026 preprints are single-author, weeks old, or abstract-only: arXiv 2606.18051, 2607.01942, 2609.01035, 2608.05064, 2605.29800, 2606.05304 and 2609.19759. Re-verify before quoting any of them outside this repository.
- Not researched: Restate and Azure Durable Task, Google ADK patterns (docs were unreachable), Phi-4, Gemma, Granite, LM Studio and Foundry Local, privacy and data-residency arguments for local models, NIST AI RMF and ISO 42001 control text, and llama.cpp prefix reuse.
- Actor model, stigmergy, holonic systems, BDI and Hearsay-II were named but not read.

## Source register: items checked myself

| Source | Where |
|---|---|
| Kim et al., Towards a Science of Scaling Agent Systems | arxiv.org/abs/2512.08296 (full text for the 45% threshold and Table 5) |
| Tran and Kiela, single-agent versus multi-agent at equal budgets | arxiv.org/abs/2604.02460 |
| Jwalapuram et al., The Illusion of Multi-Agent Advantage | arxiv.org/abs/2606.13003 |
| Meyerson et al., MAKER | arxiv.org/abs/2511.09030 |
| Zhang et al., Agentic Context Engineering | arxiv.org/abs/2510.04618 (full text for deterministic merge) |
| Wright et al., Unstructured Evidence Attribution | arxiv.org/abs/2502.14409 (full text for Table 3) |
| Lee et al., The Format Tax | arxiv.org/abs/2604.03616 |
| Tan et al., JudgeBench | arxiv.org/abs/2410.12784 |
| Kim et al., Correlated Errors in LLMs | arxiv.org/abs/2506.07962 |
| Patel et al., The Six Sigma Agent | arxiv.org/abs/2601.22290 |
| Choi et al., Debate or Vote | arxiv.org/abs/2508.17536 |
| Li et al., sparse communication topology | arxiv.org/abs/2406.11776 |
| Debenedetti et al., CaMeL | arxiv.org/abs/2503.18813 |
| Belcak et al., Small Language Models are the Future of Agentic AI | arxiv.org/abs/2506.02153 |
| Zhang et al., HoneyComb for materials science | arxiv.org/abs/2409.00135 |
| Yan, Multi-Agents: What's Actually Working | cognition.com/blog/multi-agents-working |
| Meta, Agents Rule of Two | ai.meta.com/blog/practical-ai-agent-security/ |
| llama.cpp grammar guide | github.com/ggml-org/llama.cpp, grammars/README.md |

Other sources named above were read by a research agent. Their URLs are the arXiv identifiers given, or these pages: Anthropic engineering (`anthropic.com/engineering/multi-agent-research-system`, `effective-context-engineering-for-ai-agents`, `effective-harnesses-for-long-running-agents`, `demystifying-evals-for-ai-agents`), `anthropic.com/research/measuring-agent-autonomy`, `claude.com/blog/multi-agent-coordination-patterns`, `trychroma.com/research/context-rot`, `cursor.com/blog/scaling-agents`, `cognition.com/blog/dont-build-multi-agents`, `sre.google/sre-book/handling-overload/`, `martinfowler.com/bliki/CircuitBreaker.html`, `docs.temporal.io/activity-definition`, `metr.org/blog/2025-07-10-early-2025-ai-experienced-os-dev-study/`, `metr.org/blog/2026-02-24-uplift-update/`, `openai.com/index/why-we-no-longer-evaluate-swe-bench-verified/`, `modelcontextprotocol.io/specification/draft/basic/security_best_practices` and `learn.microsoft.com/en-us/azure/architecture/ai-ml/guide/ai-agent-design-patterns`.
