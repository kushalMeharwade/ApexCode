# ApexCode AI Assistant

ApexCode brings a full agentic AI coding assistant into Visual Studio — no
subscription required. Bring your own API key from any supported provider and
start chatting, planning, and shipping code without leaving the IDE.

## Why ApexCode

- **100% free, Bring-Your-Own-Key (BYOK).** No subscription, no markup on
  tokens. Plug in a key from OpenAI, Azure OpenAI, OpenRouter, DeepSeek,
  Gemini, NVIDIA, or Vercel and you're running.
- **Plan before it acts.** Switch into Plan Mode and ApexCode explores your
  workspace read-only, proposes a plan, and waits for your approval before
  any file is touched — tool access itself is restricted, not just prompted.
- **Built for real codebases.** Reads files in paginated line ranges instead
  of dumping entire documents into context, caches by last-write-time, and
  automatically evicts older conversation history to stay within your
  provider's token limit.
- **Talks to your database — safely.** Connect to SQL Server to inspect
  schemas and run queries. Writes and stored procedures are blocked outright;
  only validated read-only SELECT/CTE queries run, capped at 100 rows.
- **Deep IDE awareness.** Uses Roslyn to read diagnostics, validate builds,
  and apply fixes directly to your syntax tree — not just paste-and-hope
  suggestions.
- **Checkpoints, not regret.** Every AI turn that uses tools takes an
  automatic workspace snapshot first. Review a diff, revert with one click.
- **Custom personas.** Define roles like "Angular Developer" or "SQL
  Reviewer" that shape how the assistant behaves, injected straight into
  its context.

## Core Features

| Feature | What it does |
|---|---|
| Multi-provider chat (BYOK) | Connect any of 7 LLM providers with your own API key |
| Plan / Act modes | Read-only exploration and approval before any mutating action |
| Paginated file reading | Large-file-safe context handling, capped and cached |
| SQL Server integration | Read-only schema inspection and query execution |
| Roslyn-powered fixes | Compile validation and AI fixes applied to the syntax tree |
| Checkpoints & diffs | Snapshot and revert any AI-driven change |
| Custom personas | Tailor the assistant's behavior per project or task |
| Token budget management | Automatic history eviction to fit provider context limits |

## Requirements

- Visual Studio 2022 or Visual Studio 2026
- An API key from a supported LLM provider (OpenAI, Azure OpenAI, OpenRouter,
  DeepSeek, Gemini, NVIDIA, or Vercel)
- SQL Server connectivity is optional and only required for the database
  features
