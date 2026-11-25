# Specialist cells (draft)

Draft on the cells of a hive: the specialist catalogue, and the two cell types that are not in it, the planning cell and the judge cell. Items marked **Decision** are proposals for you to confirm or change, unless they say they were confirmed.

This is about what the cells are and what they must do. How they are built (projects, packages, DI) is in `docs/stack.md`. Anything here that touches it is collected under "For the stack document" at the end, with what has been applied. What has been tested on a small local model is in `docs/evidence.md`.

## Cell types

A hive has three types of cell.

| Type | Does | Where it comes from |
|---|---|---|
| Specialist | Takes a goal, returns a result. Does one thing | The catalogue (below) |
| Planning | Decomposes goals, routes them, creates cells, folds results into the goal | Written for Honeycomb |
| Judge | Settles disputes between cells, and decides whether a goal is broken down further | Written for Honeycomb |

`architecture.md` says a planning cell is a form of specialist cell. The judge is a specialist cell too, so every cell takes a goal and returns a result in the common envelope (below), and any cell reaches the judge the way it reaches any other neighbour.

Confirmed:

- **One persona for every planning cell.** The root planner and a nested planner are the same persona.
- **One judge per planning cell.** It is created with that planner's specialists.

---

## The catalogue

Specialists are highly specialised personas, copied into Honeycomb and edited to match the architecture. For now they come from one source, agency-agents. It is not a ready fit, and what it needs is below.

### Source: agency-agents

[agency-agents](https://github.com/msitarzewski/agency-agents), cloned at `C:\code\personal\frameworks\agency-agents`. Checked against commit `b37b5bb` (2026-10-24), MIT licence.

- 283 persona files, one markdown file each, in 18 division directories (`engineering`, `testing`, `security`, `specialized`, `marketing`, and so on). `strategy/`, `integrations/`, `examples/` and `scripts/` are not divisions and hold no personas. I counted the 18 directories from the directory listing, and did not check them against `divisions.json`, the repository's own list.
- Every file starts with YAML frontmatter. `name`, `description`, `color` and `emoji` are in all 283, `vibe` in 282. `services` is in 4 files and `tools` in 17 (counted as a line starting `tools:`, which could also match body text). No file has a `model` field.
- The body follows the repository's template: identity and memory, core mission, critical rules, deliverables, workflow, communication style, success metrics.
- Size: median 1,885 words, 90th percentile 3,305, largest 4,918. These are words, not tokens.

I read five of them: Code Reviewer, Agents Orchestrator, Master Plan Architect, Reality Checker and Jira Workflow Steward. The rest is from counts over all files.

### Against what a hive cell needs

| A hive cell needs | agency-agents |
|---|---|
| A JSON result in a fixed schema | Markdown output formats. Only 11 of 283 contain a JSON code block |
| To be told its neighbours at run time | Nothing. Personas do not know about each other |
| Only the tools it is given | Shell commands, screenshot capture and file paths. 4 of the testing personas mention screenshots |
| Not to create other agents | 13 files mention spawning or sub-agents. The Agents Orchestrator spawns a whole pipeline |
| A way for the planner to choose it | A one-line `description`, and the division it sits in. No tags |
| A small context | Median 1,885 words, so most will need trimming |
| Free of another system | Written for a general coding assistant |

### Coverage of the two examples in `architecture.md`

- **Analysing a Jira ticket.** None. The only Jira persona (Jira Workflow Steward) enforces a Git workflow, which is not analysing a ticket. A ticket-analysis specialist would be written.
- **Writing unit tests.** None. There is no unit-test or TDD persona by name or description. The Test Automation Engineer is end-to-end tests with Playwright and Cypress. A unit-test specialist would be written, to Honeycomb's test rules (xunit v3, `dotnet-coverage` only, 90%).

So both of the examples the architecture opens with need a persona written, and the catalogue cannot be only copies.

### Not in agency-agents: a planner and a judge

No persona's filename or name matches judge, arbiter, referee, mediator, planner or coordinator, apart from two that are not substitutes. The Agents Orchestrator runs a fixed pipeline by spawning other agents, which is a workflow, where the planning cell decides the shape at run time. The Master Plan Architect writes a human-facing implementation plan in Markdown. The Reality Checker defaults to "NEEDS WORK", and a judge between two cells must be neutral.

**A judge persona in your own agents folder.** `~/.claude/agents/orchestration-judge.md` is a persona that reads a group-chat transcript after each turn and chooses Continue, Terminate or Escalate. It answers in strict JSON only, never invents content, cites turn numbers for every decision, and prefers Escalate to guessing. I do not know where it came from. It depends on a shared transcript, which the hive does not use, so it is a model for the judge's rules and not a copy.

---

## Specialists in a hive

### Decision (yours): copy the personas in and edit them

The personas are copied into Honeycomb and edited to match this architecture, for instance to return the JSON result. After the copy they are Honeycomb's own files, and there is no upstream to pull from.

- **Licence.** MIT, which allows copying and modifying. The condition is that the copyright and permission notice (`Copyright (c) 2025 AgentLand Contributors`) is included in all copies or substantial portions.
- **Provenance.** Record the source and commit each file was copied from, and for each file the upstream path, so a later difference from upstream is a known edit.
- **Definition in the persona's frontmatter.** There is no separate definition file. The Honeycomb fields go in the copied file's own frontmatter, next to the fields it already has, so each persona is one file.

| Field | Meaning |
|---|---|
| `cell` | The durable name of the cell type (becomes the `CellType`). Taken from the persona's `name`, as a slug |
| `specialties` | A short list used to choose the persona (see "How the planning cell chooses"). agency-agents has none, so they are added |
| `purpose` | One line the planning cell sees when choosing a cell. Defaults to `description` |
| `model class` | `local`, `frontier` or `any`. A hint only. The planning cell may put any available model on the cell, and the same cell type can take a different model on the next goal. The class does not fix the size |
| `tools` | The tools the cell may be given. This replaces upstream's `tools` |
| `repeatable` | Default `false` (see Checkpoints in the stack document) |
| `source` | The upstream path and commit the file was copied from |

It does not list neighbours. `architecture.md` has the planning cell tell each cell its neighbours when it hands over the goal, because the neighbours depend on which cells that planner created for this goal.

### Decision: what the edit to each persona is

Each copied persona is edited so that, with only the tools it is given, it can be a cell:

1. **Output.** Its output section becomes the common envelope below, with a `payload` that suits the persona. This is a large change: agency-agents output is markdown, and only 11 of 283 contain JSON at all.
2. **Tools.** Shell commands, screenshot steps and file paths with no equivalent are removed or replaced, and the tool list moves to the `tools` field.
3. **No other agents.** Steps that spawn or direct other agents are removed. Creating cells is the planning cell's job.
4. **Neighbours.** A persona that names other personas to hand work to is changed to consult a neighbour, if it has one, and otherwise to say it cannot answer.
5. **Size.** Trimmed to fit a local model's context with the goal, the neighbour list and the envelope added.

Not every upstream persona can be edited into a cell. The Agents Orchestrator is a pipeline of spawning, so nothing is left once the spawning is removed.

### Decision: an explicit starting set

The catalogue is the set of personas that have been copied, edited and checked. It is not "everything in the repository". Editing 283 files is a lot of work, and an unedited copy would break the architecture.

Proposal: pick a small starting set from agency-agents for the software-delivery cells (for example the Code Reviewer and a security persona), and write the two the architecture's examples need, the ticket-analysis specialist and the unit-test specialist. Which personas go in the set is open.

### How the planning cell chooses (two stages)

Confirmed as possible, and proposed as the approach. The planning cell does not read the whole catalogue. It chooses in two steps:

1. **Specialties.** It is shown a short list of specialties, each with a line saying what it covers, and picks the ones a sub-goal needs.
2. **Personas.** It is shown only the personas tagged with those specialties (name and `purpose`) and picks the cell.

Each step's choice is checked in code: it must be one of the options it was shown. That is a prompt design and needs nothing from a framework. It can be two model calls, or one call where the second step is a tool the planner calls.

Two things need deciding:

- **The first-stage list.** agency-agents has 18 divisions, which are coarse, and no tags, so the first stage needs a curated list of specialties and each persona is tagged with one or more. The list is not written.
- **Cost.** It adds a model call to each planning decision, though each prompt is short. It has not been tried.

### How a persona becomes a cell prompt

The cell's system prompt is the edited persona text, followed by a section the hive adds at run time, because it differs for every goal:

- the goal: the aim, and the parts of the body the planning cell chose to give it;
- the neighbours the cell was given, and the rule that they are the only cells it can consult;
- the result envelope, repeated so the model has it last;
- a plain statement that the cell cannot create other agents, and that if it cannot answer it must say why.

Anything fixed about the persona (its output, its tools) is in the persona. The added text goes after the persona, so a stray conflicting instruction left in the persona is overridden by the later one. Whether a small model follows that order has not been tested.

### Decision: one result envelope for all three cell types

`architecture.md` says specialists follow a set JSON schema, so that cells never interpret each other's format. The same envelope is proposed for all three cell types:

| Field | Meaning |
|---|---|
| `status` | `answered`, `cannot_answer` or `checkpointed` |
| `summary` | A short statement of the outcome, for the trace and for a person reading the result. It is not what is folded into the goal |
| `confirmed` | Facts the cell established, each with an id local to the reply and a statement |
| `evidence` | What supports a confirmed fact: a verbatim quote or a reference, with `supports` set to the fact's id |
| `context` | Useful background that is not a finding |
| `proposed_criteria` | Acceptance criteria the cell proposes. Proposals only: the planning cell sets the criteria |
| `open_questions` | What the goal needs and the cell's input did not say, or only hedged |
| `payload` | The cell's own result. Its schema comes from the cell definition |
| `reason` | Required when `cannot_answer`: why, and what would be needed |
| `question` | Required when `checkpointed`: what the human is being asked |
| `actions` | What the cell changed (documents, pull requests, code), for cells that act. Empty otherwise |

The five contribution fields are what the hive folds into the goal body, with code and not a model, see [Results](../docs/stack.md#results) and "The goal" under it. The cell does a job and reports on it, and never edits the goal. A planning cell's contributions are not written by a model: the hive builds them from what its body now holds beyond what it was given.

The three statuses follow the states in the stack document's checkpoint tree, and the local-model test in `docs/evidence.md`, which had specialists reply `answered`, `cannot_answer` or `checkpointed`, with a question on every checkpoint. `checkpointed` means a human answer or decision is needed. `evidence.md` first reported that the local server enforces a schema's structure and enums but not its bounds (`maxItems`, `maxLength`). Its seventh round corrected that: the server does enforce them, and the client library had been moving them into the field descriptions. Every limit is still checked in code, with a bounded re-ask.

**What `payload` looks like.** The question is whether every cell must give `payload` a schema, or a report-style cell may leave it as markdown. Take a ticket analyst, a persona that would be written (see coverage above), whose job is to close gaps in a ticket and cite sources.

*Markdown payload:*

```json
{
  "status": "answered",
  "summary": "Two of three gaps closed. The retry limit is not documented in any source searched.",
  "confirmed": [
    { "id": "c1", "statement": "The dead-letter queue is owned by the Platform team." },
    { "id": "c2", "statement": "The callback timeout is 10 seconds." }
  ],
  "evidence": [
    { "supports": "c1", "quote": "Dead-letter queue: owned by Platform.", "ref": "confluence:Webhook operations" },
    { "supports": "c2", "quote": "CallbackTimeoutSeconds = 10", "ref": "repo:delivery-service/Config.cs" }
  ],
  "context": [],
  "proposed_criteria": [],
  "open_questions": ["What is the retry limit for webhook delivery? Not in Confluence (webhooks space) or the delivery service repo."],
  "payload": "## Ticket Analysis\n\n### Gap 1: retry limit\nNot found. Searched Confluence (webhooks space) and the delivery service repo.\n\n### Gap 2: dead-letter owner\nPlatform team. Source: Confluence 'Webhook operations'.\n...",
  "actions": []
}
```

*Schema payload:*

```json
{
  "status": "answered",
  "summary": "Two of three gaps closed. The retry limit is not documented in any source searched.",
  "confirmed": [
    { "id": "c1", "statement": "The dead-letter queue is owned by the Platform team." },
    { "id": "c2", "statement": "The callback timeout is 10 seconds." }
  ],
  "evidence": [
    { "supports": "c1", "quote": "Dead-letter queue: owned by Platform.", "ref": "confluence:Webhook operations" },
    { "supports": "c2", "quote": "CallbackTimeoutSeconds = 10", "ref": "repo:delivery-service/Config.cs" }
  ],
  "context": [],
  "proposed_criteria": [],
  "open_questions": ["What is the retry limit for webhook delivery?"],
  "payload": {
    "gaps": [
      { "gap": "Retry limit for webhook delivery", "state": "open", "searched": ["confluence:webhooks", "repo:delivery-service"], "citations": [] },
      { "gap": "Owner of the dead-letter queue", "state": "closed", "answer": "Platform team", "citations": ["confluence:Webhook operations"] },
      { "gap": "Timeout on the callback", "state": "closed", "answer": "10 seconds", "citations": ["repo:delivery-service/Config.cs"] }
    ],
    "recommended_next": "human"
  },
  "actions": []
}
```

The contributions are the same in both, and they are all the planning cell needs to fold the outcome into the goal: the same facts and the same open question reach the goal whichever payload is used. What differs is what another cell can do with `payload`. With the schema, the planner can read `recommended_next` and `state` directly and, for example, checkpoint on `open` gaps. With markdown it would have to ask a model to read the prose again, which is the interpretation the JSON rule exists to avoid. The cost of the schema is one to write per persona, and the larger the schema, the more a small model gets wrong: `evidence.md` found 11 of 18 answerable replies broke an unenforced "at most 3 findings" limit.

The proposal: a schema is required when another cell reads fields of `payload`, and markdown is allowed when the result is a report for a person. The planner's plan and the judge's verdict always have schemas.

The stack document's Results section now has the five contribution fields, and also an `Error` status for a reply that fails validation after the re-asks. An evidence item has a `quote`, a `ref`, or both: a quote is for text the cell was given, and a reference is for a file, line or page it had to fetch. The tested schema has `quote` only, see `docs/evidence.md`.

---

## The planning cell

Not written yet, and not from the source. It is the cell the whole hive depends on, so it is the first persona to write. It is one persona, used at every level of the tree. What it must do, from `architecture.md`:

- take a goal and decide whether it is atomic enough for a specialist or must be broken down;
- pick the cells for each sub-goal from the catalogue, in the two stages above, or route a sub-goal to another planning cell;
- decide whether sub-goals run in sequence or in parallel;
- choose a provider and model for each specialist;
- give each cell its goal and its neighbours, and say when a cell is a re-spin and why the earlier one failed;
- on `cannot_answer` from a specialist, decide whether to re-spin, pick a different cell, or checkpoint (a judge is never re-spun, see below);
- fold each result's contributions into the goal body, and not append it to a transcript. Specialists only propose acceptance criteria. The planning cell accepts, changes or rejects them, and checks the criteria before it returns its own result;
- ask the judge when results conflict, or when it wants to go deeper. It reaches the judge as a neighbour, the same way a specialist does.

**Decision:** its `payload` is a plan, as data and not prose. The hive reads the plan and creates the cells. The planning cell does not create cells itself by calling tools, so the depth and re-spin rules sit in code and not in a prompt.

An example, for "Analyse JIRA-12345". The cell names are illustrative: no persona with these names exists yet, and the catalogue is not decided.

```json
{
  "status": "answered",
  "summary": "Three steps. Readiness first. Research and impact then run in parallel.",
  "payload": {
    "steps": [
      { "id": "s1", "cell": "ticket-readiness-reviewer",
        "goal": "Review JIRA-12345 against the Definition of Ready and list what is missing.",
        "after": [], "model_class": "local" },
      { "id": "s2", "cell": "ticket-analyst",
        "goal": "Find answers in existing sources for the gaps step s1 reports.",
        "after": ["s1"], "model_class": "local" },
      { "id": "s3", "cell": "impact-and-risk-analyst",
        "goal": "Estimate the impact and risk of JIRA-12345 from the ticket and its linked sources.",
        "after": ["s1"], "model_class": "frontier" }
    ]
  }
}
```

- **Order and parallel.** A step runs when everything in its `after` has finished. `s2` and `s3` both wait only for `s1`, so they run in parallel. Steps with an empty `after` start together. A planner that sends a step to another planner gives it `"planner": true` in place of `cell`.
- **What the hive checks in code:** every `cell` is in the catalogue, every `after` refers to a step that exists, there are no cycles, the depth cap holds, and nothing in the plan is a judge (each planning cell already has one).
- **The plan is a first draft.** `s2`'s goal was written before `s1` had returned. When `s1` finishes, its contributions are folded into the goal body, and the planner chooses which parts of the body the steps that depend on it are given. So the planner is called again after results, not once. How the plan is revised is open.
- **A known weak spot.** In the local-model test in `docs/evidence.md`, the planner scored 47/60 under a placeholder prompt, and one of its errors was giving three independent documents as sequential goals and not parallel ones. The `after` field makes that choice explicit, and the prompt will need work.

---

## The judge cell

Not written yet, and not from the source. A specialist cell, one per planning cell, created with that planner's specialists.

### What it does

The judge settles disputes and other issues between cells. When two cells hold conflicting information, the judge decides which way to go. It also answers the depth question in `architecture.md`: when a planning cell wants to break a goal down further past the configured depth, it asks the judge first.

### Where it sits

The judge is a neighbour to every cell that borders the planning cell. Within one planning cell's group:

- every specialist has the judge among its neighbours, and can consult it directly like any other neighbour, without going through the planner;
- the planning cell reaches the judge the same way, as a neighbour, because the judge is a specialist cell (confirmed).

The planner creates the judge alongside the specialists, and gives it to each of them as a tool.

### What a judge is given and returns

- **Given:** the dispute as its goal: the two or more conflicting claims, who made each, and the evidence each cell gave. It is not given the cells' transcripts, in keeping with small contexts.
- **Returns:** the common envelope. `payload` holds the verdict: which claim to go with (or that neither holds, or that they can be combined), and why.
- **When it cannot decide:** it escalates to a human, with `status` `checkpointed` and the `question`, using the checkpoint mechanism. The judge is the last resort, so it never returns `cannot_answer`: that would invite a re-spin, and a judge is not re-spun. A judge does not guess. Its statuses are `answered` or `checkpointed`.

### Confirmed decisions

- **A judge has no neighbours.** It decides from what it is given and does not consult other cells. This stops a dispute turning into a chain of consults. A judge that needs more evidence escalates to a human and does not ask the planning cell to fetch it.
- **A verdict is binding on the cells in that group.** The planning cell folds it into the goal body as a confirmed entry with the judge as its source. A cell that disagrees with a verdict does not call the judge again for the same dispute. It can only say it cannot answer, which leads to a human.
- **Default to a frontier model.** A wrong verdict is folded into the goal and carried down the tree. The planning cell's routing makes the call, as for any specialist.
- **A judge call does not count towards depth.** Like a re-spin.
- **There is no counter for judge calls.** The judge either decides or escalates to a human, so there is nothing to re-spin or limit.
- **A judge escalation skips the "path without a human" step.** `architecture.md` has a checkpointed planning cell first look for a way to finish without a human, by consulting its parent. A judge is the last resort, so the escalation goes straight to the human.
- **One cell type for both depth and disputes.**
- **Neutral by design.** The persona must not default to approval or to rejection. It settles between claims, which is not the Reality Checker's job of doubting a single claim.

### What to take from the existing judge persona

The `orchestration-judge` persona above is a model for how the judge writes, not for what it reads. Take from it: strict JSON and nothing else, never invent content, cite the evidence behind every verdict (it cites turn numbers; the judge would cite which claim and which piece of evidence), and prefer escalation to a guess. It differs in that it has no transcript to read and no discussion to steer, so there is no Continue.

`docs/evidence.md` shows the local 7B model got the depth-judge decision right 60/60 on clear cases. Settling a real dispute was not tested, and the confirmed default is still the frontier model.

---

## Checked and not checked

Checked by reading or counting:

- In agency-agents at `b37b5bb`: the file counts, frontmatter fields and sizes over all 283 files; the five personas named above; the contributing guide; the licence text; that no persona's filename or name matches judge, arbiter, referee, mediator, planner or coordinator, and none matches unit tests or TDD by name or description.
- The `orchestration-judge` persona.
- The examples above were written by me for this document and were not run against any model.

Not checked:

- How any persona behaves in a hive, or with a local 7B model. No persona has been edited, and nothing was run on one.
- The other 278 agency-agents personas by reading, so more of them may assume tools, shells or spawning than the counts show.
- Whether a small model follows a result contract placed after a long persona.
- Whether a two-stage choice works with a small model, and what the specialty list should be.
- How much of any one persona survives the edit.

## For the stack document

Applied to `docs/stack.md`: the result's contribution fields (the third bullet, in part). Nothing else here has been changed there.

- The Cells section lists "neighbours" as a field of the cell definition. Here neighbours are given by the planning cell at run time and are not in the definition.
- There are three cell types, and the judge is a specialist cell with its own entry. The `Cells.Abstractions` project would hold the `CellType` values.
- The result envelope above overlaps the Results section. The spelling is decided: `cannot_answer`, with an underscore, everywhere. `docs/stack.md` still has the hyphenated form in the checkpoint tree states, and needs changing.
- The personas are copied into the repository and edited, with the cell definition fields in their frontmatter. That affects where cell definitions live and what loads them, and a licence notice has to be kept with them.
- A new definition field, `specialties`, is used to choose a persona, in two stages.
- The planner is called again after results to fold them into the goal body and choose what dependent steps are given.

## Open decisions

1. The starting set. Proposed: a small set from agency-agents for software delivery, plus a ticket-analysis specialist and a unit-test specialist written for Honeycomb. Which agency-agents personas go in is open.
2. The result envelope: the three statuses, the `question` field, the five contribution fields, and the proposal that a schema is required when another cell reads `payload`, with markdown allowed for a report.
3. The plan as data (proposed above), and how a plan is revised after results: call the planner again after each result, or only when a result changes something.
4. The two-stage choice: confirm it, and decide the specialty list. agency-agents has 18 divisions and no tags.
5. Where the copied personas live in the repository, and how the notice and upstream commit are recorded (proposed: a notice file beside them, and `source` in each file's frontmatter).
6. Who writes the ticket-analysis and unit-test specialists. agency-agents has neither, so both are written, and the question is whether each starts from the nearest persona or from nothing.

Decided: the status is spelled `cannot_answer` everywhere.
