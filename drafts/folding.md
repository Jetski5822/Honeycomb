# Folding results into the goal (draft)

Proposed replacement for the "Agentic oriented feedback" section of `architecture.md`. The text between the lines is the section. The notes after it are for the tech stack, telemetry and specialist documents and are not part of the section.

---

## Folding results into the goal

This is feedback in the sense of results flowing back up the hive. It has nothing to do with training or improving a model: no model is changed.

A specialist cell does a job and reports on that job. It never edits the goal. The planning cell takes what the specialist reported and incorporates it into the goal's body, so that the next cell starts from a goal that already knows what has been found. The transcript is stored, but it is not passed on, which keeps each cell's context small.

### The body of a goal

A goal has an aim, which never changes, and a body of five parts that the planning cell updates as results come back.

| Part | What it holds | Example, for the goal "raise a pull request that fixes the slow checkout query" |
|---|---|---|
| Aim | What is to be achieved. Fixed for the life of the goal | The sentence above |
| Acceptance criteria | What must be true for the goal to be done | "p95 under 200 ms", later extended with "on both queries" |
| Context | Background a cell needs that is not in dispute | "The orders table has no index on `customer_id`" |
| Confirmed | Things established by a cell, with which cell | "The query runs 14 times per page load (analyst, cell 0004)" |
| Evidence | What supports a confirmed item: quotes, file and line references, or a reference to a transcript | The query text and the file it is in |
| Open questions | Things not yet known, removed when answered | "Is the replica allowed to serve this read?" |

### How a result is folded

- A specialist's result carries its contributions as well as its status: what it confirmed, the evidence, context it found, criteria it proposes, and questions it could not answer. They are fields of the result, so the hive can apply them without asking a model to rewrite the goal.
- **The specialist proposes and the planning cell decides.** A proposed criterion, or a proposed change to one, is only a proposal. The planning cell accepts it, changes it or rejects it. Criteria are what "done" means, so they are the planning cell's to set.
- Confirmed items, evidence, context and open questions are appended, each tagged with the cell it came from, so a later cell can see where a fact came from and say it doubts it.
- A cell that could not answer is folded too. The reason is added to the context or as an open question, which is what the re-spin guidance (a previous cell could not answer) relies on.
- The planning cell checks the acceptance criteria before it returns its own result. The criteria, not "all the children have finished", say when it is done.
- Each part has a size limit, set for the hive like the other limits. When a part passes its limit, the planning cell compresses that part. Compression is the only point where a model rewrites the body.

### What a child cell receives

A cell receives the aim and the parts of the body the planning cell chooses to give it. It does not receive the whole body by default. A planning cell passes a child planning cell a body that child can use, and what the child's own specialists return is folded into the child's body first, then up into its parent's.

---

## Notes for the other documents

These follow from the section above. All of them are now applied, except the one under Evidence.

- **Tech stack, Results and Cells.** Applied. The result has the contributions as fields, validated in code, and `Goal` is a record with the aim and the five parts. Folding is a deterministic application of the contributions, with a model only to compress.
- **Tech stack, Checkpoints.** Applied. The snapshot holds the whole body, including who contributed each entry.
- **Telemetry.** Applied, differently from the first proposal: there is no `goal.folded` event at all, because a fold is deterministic and everything the event carried is on a span. There is a `goal.compressed` event for the one step where a model rewrites the body.
- **Specialist cells draft.** Applied. The envelope has the five contribution fields beside `summary` and `payload`.
- **Evidence.** A test of the new design is in `docs/evidence.md`. The earlier test of a model rewriting the goal no longer describes the design.

## Open decisions

1. The size limits for each part, and whether any part is exempt (for instance, confirmed items, which are short).
2. How the planning cell chooses which parts of the body a child receives. Proposed: it is told which parts the specialty uses, with all of the aim and criteria always included.
3. Whether the planning cell uses a model to judge a proposed criterion, or applies a rule (for example, accept if no existing criterion contradicts it). A rule is cheaper and testable, and a model handles wording.
4. Whether an item may be marked confirmed on one cell's word, or needs a second source, or a judge, when the cost of a wrong fact is high.
5. Whether evidence is stored in the body or only as a reference. Storing quotes keeps the body self-contained, and a reference keeps it small.
