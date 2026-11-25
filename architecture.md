# Architecture

> Honeycomb architecture uses an interlocking hexagonal cell pattern inspired by natural beehives to maximize strength while minimizing weight and material use

This strikes similar tones when dealing with software engineering, often with component based development, you tend to create componets that interlock together, but these components are often top down, with one way communication, "do this, and give me a result back", never "do this, and ask for more information".

Honeycomb architecture starts with a Hive, the hive is essentially a set of Hive cells.

If you apply this to software, think of the Hive as a set of agents, where each agent (cell) can interact with each neighbouring agent (cell).

Each cell is a specialized agent, good at doing one thing, and one thing only, from analyzing jira tickets, to writing unit tests.

Small describes that job, not the model. A cell can run on any model the hive has available, local or frontier. The planning cell chooses the model when it creates the cell. See Specialist routing.

This document is the architecture. How it is built (libraries, projects, checkpoints, testing) is in the [Tech stack](docs/stack.md), how it is instrumented is in [Telemetry](docs/telemetry.md), and what has been tested, and what has not, is in [Evidence](docs/evidence.md).

## How the story starts

To begin with interacting with a hive, you post it a question, it could be as simple as, "Please analyse Jira ticket JIRA-12345".

The hive take that, turns it in to a goal, the goal being "Analyse a JIRA ticket, and return a result of the analysis", and then returns it. Each cells goal is the same, and the end product is a transcript of each analysis from each cell to achieve the same goal.

This works "ok" when you are using huge context windows, but when you are trying to keep the context small, and specialized, this doesnt work.

On top of this, it never breaks the problem down in to chunks, one goal might contain multiple questions and all of these end up getting asked within the same context, which can lead to hallucinations.

This is where the Planning cell comes in to play. A highly specialised cell that is the router for cells within the hive.

## Planning cell

[The SDLC Isn't a workflow anymore](ref/the-sdlc-isnt-a-workflow-anymore.md)

The initial call is about taking the goal, "Analyse JIRA-12345", and breaking that down in to goals.

Each goal is then routed to neighbouring Planning cells.

A planning cell can route to another Planning cell. Every planning cell is the same persona, at every level of the hive.

A planning cell can then determine if that goal needs to be broken down further, and what specialist cells are required to complete that goal.

The Planning cell also determines if the goal is synchronous, or can be worked on in parallel.

The Planning cell then creates the specialist cells that will be involved, and then passes goal down to them.

Each planning cell tells each other specialist cell when providing the goal, what cells are neighbours to that cell. The judge cell is a neighbour to all of them.

If a specialist's result to a planning cell is that it cannot answer the question, then the planning cell must determine which cell it should spin up to answer that question.

### Re-spin

Re-spining is where a specialist cell returns a result to a planning cell, where it cannot answer.

The planning cell can then determine if it should re-spin

The planning cell can create a the same fresh specialist cell that failed to answer the first time, but can only re-spin to a desired configured amount, this is to prevent constant re-spin.

The guidence to a cell that was spun up due to re-spein, must also state that a previous specialist cell could not answer the question.

Re-spins do not count to depth.

Re-spins have a number of settings to prevent constant re-spinning across the hive.

The first is a total re-spin counter preventing the planning cell from creating the same cell multple times.

The second is a total counter within a planning cell to prevent the planning cell round robining. 

The third is a total counter within the hive.

Each counter has three settings, one a total maximum, two a total maximum within a alloted period of time, and thrid a period of time to reset the counter.

The time based counters allow the ability for the hive to reset, so one bad planning cell does not affect the hive long term.

The counters are tested on a fake clock, see [Evidence](docs/evidence.md). Each re-spin and each refusal is recorded as a span event and a counter, see [Telemetry](docs/telemetry.md).

## Specialist cells

A specialist cell takes a goal, and return a result.

Specialists are highly specialised personas. The specialist catalogue to begin with is built from existing persona sets, such as agency-agents, copied in and edited to fit this architecture, see [Specialist cells](drafts/specialists.md).

There are three types of cell: the Planning cell, the Specialist cell and the Judge cell.

If a specialist cannot answer a question, it must say why, thus informing the planning cell.

Whilst the specialist must return a result, the specialist when performing updates to documents, or updating pull requests, or writing code, can do so, and still return a result of what it has done.

### Specialist neighbours

A neighbour is a cell that is attached to the parent planning cell.

Neighbours are not part of a cell's definition. The planning cell creates the cells for a goal, and which of them are neighbours is decided at run time from that goal and its context. The same specialist can have different neighbours in the next hive run.

A specialist, if it deems needs to consult a neighbour, can reach out directly, without needing to talk through the planning cell.

A cell can only communicate with its direct planning cells specialist cells, i.e. its direct neighbours. If a specialist cell is a planning cell, it cannot reach out to that cell.

A neighbour is given to a specialist as a tool the hive builds, so the rule is enforced by which tools a cell has. See the tech stack under [Cells](docs/stack.md#cells). A consult appears in a trace as a tool call with the neighbour's own span under it, see [Telemetry](docs/telemetry.md).

### Result Format

Specialist cells must follow a set JSON schema when replying - using JSON prevents the need to interpert format between cells.

The schema, the validation of every limit in code, and the re-ask when a reply is invalid are in the tech stack under [Results](docs/stack.md#results). Tested against a small local model across ten kinds of cell, see [Evidence](docs/evidence.md).

## Judge cell

The judge cell settles disputes, and other issues, between cells. If two cells have conflicting information, the judge determines which way to go.

The judge is a specialist cell. Each planning cell has its own judge, created with its specialists, and the judge is a neighbour to all cells bordering that planning cell. A specialist can reach out to the judge directly, in the same way it can reach any other neighbour, without needing to talk through the planning cell.

The planning cell reaches the judge the same way, because the judge is a specialist cell. It asks, for example, when it wants to break a goal down further past the point where it must ask (see Questions, 1).

The judge takes the dispute as its goal, and returns a result in the same JSON format as any other cell. The judge is the last resort. It either decides, or it escalates to a human. It is never re-spun, and a call to the judge does not count to depth.

When the judge escalates, the cell is checkpointed as normal, but the planning cell does not first look for a path that avoids the human, because the judge is already the last resort.

## Agentic oriented feedback

Feedback from specialists is handled differently within a hive.

When a specialist is asked to achieve a goal, such as answer a question, or investigate a problem, they will return that data to the planning cell.

The planning cell will take that, and instead of storing that as a line in the transcript, and provide it as context to the next agent, the planning cell will incorporate that feedback in to the goal.

The key here, is that the transcript can be stored, but the entire transcript is not constantly reused, removing the cost of large context.

## Specialist routing

A cell does one thing. That is what small means. The model that runs the cell is a separate choice, and it can be any model the hive has available, local or frontier.

The hive has a known set of providers, declared for the run, and uses only those. Docker can offer a local model. Anthropic can offer a frontier model. A run may have either, or both, and more than one model on a provider.

The planning cell chooses the provider and model when it creates a cell. That includes the specialists, the judge, and a nested planning cell. The same kind of cell can run on a local model for one goal and a frontier model for the next. A cell definition may hint at a model class. The hint does not decide. The planner does.

The root planning cell has no parent, so the hive chooses its model from the same declared set when the run starts.

Providers, the local runtime, and how a cell is routed are in the tech stack under [Providers and routing](docs/stack.md#providers-and-routing).

## Human in the hive

All cells, be that planning, or specialist can run in to issues, where they cannot complete the goal - that's a fact. When that happens, they need to be able to escalate to the human.

To do this, each cell can checkpoint - this means that the entire path to that cell is effectively checkpointed.

When a planning cell has been checkpointed, then it does two things, the first, it asks if there is a path where the goal can be achieved without involving a human, this is similar to re-spin, but will consult the parent cell.

If there is no path out, and the cell is permanently checkpointed, then all cells in that path, within the hive cannot continue - and must wait for the checkpoint to be cleared.

What a checkpoint saves, and the rules for resuming after a human has answered, are in the tech stack under [Checkpoints](docs/stack.md#checkpoints). Resuming starts a new trace linked to the first, see [Telemetry](docs/telemetry.md).

## Implementation details

### Telemetry

Open Telemetry (OTEL) will instument the Hive. From the part the Hive starts, to when a cell starts.

Each new cell will start its own scope, so for example

hive -> Parent Activity Being
hive-to-planning-cell -> Activity Begin from Hive Activity
planning-cell-to-specialist -> Activity Begin from Planning Activity
Direct specialist-to-neighbour -> Activity Begin from Specialist Activity, the Neighbour will start its own Activity too, so the client calling the neighbour can be tracked.

Each Activity should contain a bag of information, including From and To names of the cells, and what the purpose of the call was.

The design in [Telemetry](docs/telemetry.md) refines this. The libraries' own GenAI names are used, there are no From and To attributes because the parent span and the span itself already say who called whom, and each cell has a durable id. The purpose is `honeycomb.cell.purpose`, treated as content and off by default.

### DI

Dependnency injection is extremily important - as it helps to facilitate testing.

Each Hive will start a new DI scope, the Parent scope will be the Hive Scope.

Each new cell will start its own scope, so for example

Start Hive -> Parent Scope Being
Create Planning Cell -> Scope Begin from Hive scope
Create Specialist Cell -> Scope Begin from Planning scope

How cells are created and matched is in the tech stack under [Orchestration](docs/stack.md#orchestration).

### Cancellation

Cancellation tokens will flow from Hive to each cell, mirroring that of DI.

Each Hive will start a new CancellationToken source, linked to the parent, the Parent scope will be the Hive CancellationToken.

Each new cell will start its own CancellationToken, so for example

Start Hive -> Parent CancellationToken Being
Create Planning Cell -> CancellationToken Begin from Hive CancellationToken
Create Specialist Cell -> CancellationToken Begin from Planning CancellationToken

Doing so means that the application can gracefully degrade.

A cancelled cell returns a cancelled result and does not throw, so its span is not marked as an error. See [Orchestration](docs/stack.md#orchestration) and [Evidence](docs/evidence.md).

## Questions

1. Who decides a goal is atomic enough for a specialist?

This is two fold. There should be two levels.

The first is the maximum degrees of distance from the core Planning cell for an LLM Judge to determine.

What happens here is that if the setting is 3, and the planning cell determines it needs to go to another planning cell because it can break down the goal more, then it first asks the judge cell, and the judge will decide.

The second is the maximum degrees of distance from the core Planning cell overall.

So, you have hit the judge, and you are six levels removed for example, the system will say no more - and tell the planning cell to get on with it.

2. Is a planning cell a specialist cell?

Yes, a planning cell is a form of specialist cell, the difference is that the planning cell takes on more responsibilities within the overall hive, where as specialist cells are very discrete. The judge cell is the same, it takes a goal (the dispute) and returns a result.