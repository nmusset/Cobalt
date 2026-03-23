# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Cobalt is a research project exploring a programming language that combines C#/Rust semantics on the .NET runtime. The core idea is to bring Rust's ownership and borrow-checking guarantees to a C#-like language targeting .NET.

The chosen approach is: **build a new language targeting .NET IL**, using a Roslyn analyzer as a stepping stone to validate the ownership model first. See `docs/decision/01-approach-decision.md` for full rationale.

An additional goal is seamless interop with both Rust and C#/.NET ecosystems.

## Current State

Research phase complete. The project is now in early implementation, following `docs/implementation-roadmap.md`:
- **Phase A** (current): Roslyn analyzer with ownership annotations
- **Phase B, Milestone 1** (next): Core language compiler

## Repository Structure

- `docs/implementation-roadmap.md` — Implementation plan for the MVP
- `docs/research/` — Phase 1 knowledge base (01 through 08, one doc per topic)
- `docs/feasibility/` — Phase 2 feasibility assessments (01-augmented-csharp, 02-new-language, 03-cross-cutting)
- `docs/decision/` — Phase 3 decision documents

## Conventions

- Research documents are numbered sequentially (01-08). When adding new documents, verify numbering doesn't conflict with existing files.
- Each research phase uses a separate folder under `docs/`.
