# Phase 2: Feasibility Study

Stress-tests both candidate approaches against the hardest technical questions, using Phase 1 findings as evidence. Each question is assessed as **Viable**, **Viable with constraints**, or **Blocker**.

| # | Document | Verdict |
|---|----------|---------|
| 01 | [Augmented C#](01-augmented-csharp.md) | Viable as stepping stone; hits ceiling on inter-procedural analysis and lifetime expressiveness |
| 02 | [New Language](02-new-language.md) | Viable with constraints; no hard blockers, risk concentrated in engineering effort |
| 03 | [Cross-Cutting Concerns](03-cross-cutting.md) | Rust interop FFI-only for now; GC coexistence manageable; ownership yields real performance gains |
