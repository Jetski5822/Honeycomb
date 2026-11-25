# **The SDLC Isn't a Workflow Anymore**

*Rethinking AI-assisted software development through adaptive planning, continuous feedback and small models.*

We've spent decades turning software development into increasingly well-defined processes.

Requirements become specifications. Specifications become plans. Plans become tasks. Developers implement those tasks, QA validates them, security reviews them, and eventually something gets released.

As we start to build AI systems for software development, we’re falling into a predictable trap: we are simply mapping human organizational charts onto AI architecture, rather than engineering for machine capabilities. Nowhere is this more apparent than in the standard JIRA workflow.

We look at a board—Backlog, Implement, Review, QA, Release—and our first instinct is to mirror it with agents: a Coding Agent here, a QA Agent there, and a Release Agent to finish. But this isn't innovation; it’s just automating our own legacy constraints.

A human engineer might spend hours understanding a requirement before making a change. A QA engineer might need to understand the implementation before deciding what to test. A security engineer might review the completed change days or weeks later.

AI doesn't necessarily need to work that way.

A software engineering agent doesn't need to be a "developer", or a dedicated “QA engineer”. It actually doesn't even need to own a phase of the SDLC.

It could instead be given **one small, well-defined problem**.

- Which classes are affected by this requirement?  
- Is this API change backwards compatible?  
- What are the security implications of this authentication change?

Or simply:

- Why did this test fail?

These are much smaller reasoning problems than:

- Implement this feature.

And that distinction becomes particularly interesting as local small language models become increasingly capable.

A 14B model may struggle to understand an entire codebase and implement a complex feature from a single prompt. But give it a narrowly scoped task, the relevant context, and a well-defined output, and the problem changes considerably.

So what happens if we stop thinking about the SDLC as a fixed workflow?

What if **Specify, Plan, Implement, Test, Review and Debug aren't phases at all?** What if they're simply **capabilities**?

And what if an AI planner can dynamically decide which capability should happen next, whether several capabilities can happen in parallel, whether the result needs another iteration, or whether the system should stop and ask a human for input.

The result looks less like a pipeline and more like a continuously evolving graph:

                        Intent  
                            │  
                            ▼  
                       ┌─────────┐  
                       │ Planner │  
                       └────┬────┘  
                            │  
                 ┌──────────┼──────────┐  
                 ▼          ▼          ▼  
              Specify    Analyse    Research  
                 │          │          │  
                 └──────────┼──────────┘  
                            ▼  
                         Evidence  
                            │  
                            ▼  
                       ┌─────────┐  
                       │ Planner │  
                       └────┬────┘  
                            │  
                       ┌────┴────┐  
                       ▼         ▼  
                   Implement   Security  
                       │         │  
                       └────┬────┘  
                            ▼  
                           Test  
                            │  
                     ┌──────┴──────┐  
                     ▼             ▼  
                   Pass           Fail  
                     │             │  
                     ▼             ▼  
                  Planner        Debug  
                                   │  
                                   └──────► Planner

The interesting part isn't the individual agents.

**It's the loop.**

The system continually observes what it knows, identifies what it doesn't know, performs the smallest useful piece of work, gathers evidence, and then decides what to do next.

The planner's goal is that it decides if it needs to go back, or forward.

In other words, I'm wondering whether the future of AI-assisted software development looks less like an automated software factory and more like **Agile applied at machine speed**.

---

## Breaking the SDLC Apart

If the SDLC isn't a fixed workflow, what happens to our established methodologies?

Modern processes like GitHub Spec Kit improve on the status quo by forcing a structured sequence (Specify \> Plan \> Implement). It’s a good guardrail—it prevents AI from jumping straight to writing code—but the steps are still too coarse.

Consider "Specify." It’s currently a catch-all container for a dozen distinct reasoning tasks: understanding intent, identifying actors, clarifying constraints, uncovering edge cases, and defining acceptance criteria. The same goes for "Planning," which bundles everything from dependency analysis and trade-off evaluation to risk assessment.

We don’t need to perform these as single, opaque blocks. They are actually a **collection of capabilities.**

Once you start looking at the SDLC this way, the existing phases begin to look less like discrete stages and more like **collections of capabilities**, where capabilities are the primitives from which the planner constructs the execution graph. You could even break these capabilities down further, into more discreet capabilities.

The key here is that we are **deconstructing a human-centric SDLC workflow**. The stages we have traditionally created — specification, planning, development, testing, and release — reflect the way humans organise and divide work. They don't necessarily represent the smallest useful units of work for AI.

By breaking those stages apart, we can give the planner a much finer-grained set of options. It can decide which capability is useful next, whether capabilities can run in parallel, whether an earlier capability needs to be revisited, or whether the work should take an entirely different path.

The resulting graph is therefore not necessarily linear:

A → B → C → D

It can fan out to contain branches:

      ┌──► B ──► D

A ─────┤

       └──► C ──► D

And, importantly, it can contain **loops**:

A ──► B ──► C

     ▲       │

     └───────┘

A node therefore doesn't necessarily have a single successor. Its output becomes evidence that the planner can use to determine the next transition. 

A failure, new piece of information, or conflicting piece of evidence can cause the planner to revisit an earlier capability rather than simply proceeding to the next stage.

**The graph isn't a workflow that we define upfront. It is a structure that emerges as the planner responds to the current state of the work.**

An Adaptive AI-SDLC of sources \- adapting based on the information presented by each node that precedes it.

---

## The plan becomes an emergent property

This is where the paradigm diverges from traditional AI workflows. Instead of pre-defining a workflow, we give the planner access to a set of capabilities and let it construct the execution graph on the fly. The planner evaluates the original intent, current state, accumulated evidence, and available capabilities against the ultimate objective to answer a single question: **Given the inputs from the previous steps, what should happen next to get us closer to the goal?**

This dynamic approach isn't just a hunch; it mirrors recent research into 'Tree of Thoughts' (Yao et al., 2023), where AI models explore multiple reasoning paths rather than committing to a single, linear trajectory.

This creates a dynamic, goal-oriented loop. The planner might analyze code, produce evidence, then decide security analysis is the necessary next step. It might spawn parallel investigations or—if the requirement is ambiguous—pause to consult a human. The path is never predetermined; it’s an evidence-based, goal-seeking process where the planner acts as an orchestrator fanning out to specialists rather than a monolithic researcher. 

The planner doesn't need to know the answer. It needs to know what it needs to know, and which capability is best placed to find out. 

---

## This is where Agile becomes interesting again

The contrast here is between two fundamentally different SDLC philosophies. Waterfall models rely on the premise that you can—and should—define the entire journey before taking the first step. You create a plan, you execute it, and any deviation is treated as a disruption.

But the original spirit of Agile wasn't about rigid ceremonies or documents; it was about the loop: work in small increments, inspect evidence, and adapt.

An AI-native SDLC pushes this to its logical extreme. Instead of the static "Plan → Execute" model, we move to a continuous cycle: **Determine the next useful step → Execute → Inspect evidence → Adapt.**

At this level of granularity, the entire development process becomes a high-speed feedback loop. This solves the classic problem of workflow inertia: you can change direction instantly because the system doesn't need to know the final destination—it only needs to know what to do next.

This feedback loop mimics the 'Reflexion' framework (Shinn et al., 2023), where the system treats past failures not as dead ends, but as evidence. Instead of blindly proceeding, the planner analyzes the failure and loops back to a prior capability to refine its approach.

---

## Why Small Models Change the Equation

The economic case for decomposition is just as compelling as the technical one. Much of the current industry conversation assumes the solution is always "bigger"—tasking massive frontier models with the entire SDLC, from architecture to release.

But asking one model to hold the whole lifecycle in its head is a bottleneck. It’s an unsustainable demand: we’re asking a single model to act as product manager, architect, engineer, QA, and DevOps lead simultaneously.

The alternative isn’t just to use smaller models; it’s to change the problem. If we break a complex feature into targeted tasks—identifying affected components, assessing backward compatibility, isolating test failures—we stop needing "frontier-level" intelligence for every step.

When inference is local, the economics change again. The cost of another reasoning step is no longer another API call. We can afford to be deliberately wasteful with inference: retry an analysis, ask several models the same question, run investigations in parallel, or revisit an earlier decision when new evidence appears.

Cheap inference makes continuous decomposition practical.

This aligns with the industry's shift toward 'Agentic Orchestration.' Tools like Microsoft’s AutoGen (Wu et al., 2023\) or LangChain’s LangGraph are proving that the most resilient systems aren't the ones using the largest models, but the ones orchestrating smaller, specialized agents that can be swapped, tested, and updated independently.

By isolating these tasks, we feed a model only what it needs, when it needs it. The economics shift from "one expensive, monolithic brain" to "specialized, inexpensive models for bounded tasks." Intelligence doesn't have to live inside one model; it can emerge from the orchestration of specialized agents, continuous verification, and rapid replanning.

---

## References

Yao, S., Yu, D., Zhao, J., Shafran, I., Griffiths, T.L., Cao, Y. and Narasimhan, K. (2023). 'Tree of Thoughts: Deliberate Problem Solving with Large Language Models'. arXiv preprint arXiv:2305.10601.

Shinn, N., Cassano, F., Gopinath, A., Narasimhan, K. and Yao, S. (2023). ‘Reflexion: Language Agents with Verbal Reinforcement Learning’. arXiv preprint arXiv:2303.11366.

Wu, Q., Bansal, G., Zhang, J., Wu, Y., Zhang, S., Zhu, E., Livio, B., Balog, K., Dey, S., Wu, E. and Oei, O. (2023). ‘AutoGen: Enabling Next-Gen LLM Applications via Multi-Agent Conversation Framework’. arXiv preprint arXiv:2308.08155.