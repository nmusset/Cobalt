# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Cobalt is a research project exploring a programming language that combines C#/Rust semantics on the .NET runtime. The core idea is to bring Rust's ownership and borrow-checking guarantees to a C#-like language targeting .NET.

Two approaches are under consideration:

1. **Augmented C#**: Add borrow-checker-style static analysis on top of standard C#.
2. **New language**: Design a new language with C#-like syntax and a built-in borrow checker, compiled to .NET IL.

An additional goal is seamless interop with both Rust and C#/.NET ecosystems.

## Current State

The project is in the research/design phase with no code yet. Work should start with comparative analysis of C# and Rust type systems, ownership models, and runtime semantics before any prototyping.

## Repository Structure

- `docs/research-roadmap.md` — Master roadmap with three phases
- `docs/research/` — Phase 1 knowledge base (01 through 08, one doc per topic)
- `docs/feasibility/` — Phase 2 feasibility assessments (01-augmented-csharp, 02-new-language, 03-cross-cutting)
- `docs/decision/` — Phase 3 decision documents

## Research Conclusion

The recommended approach is: **build a new language targeting .NET IL**, using a Roslyn analyzer as a stepping stone to validate the ownership model first. See `docs/decision/01-approach-decision.md` for full rationale.

## Conventions

- Research documents are numbered sequentially (01-08). When adding new documents, verify numbering doesn't conflict with existing files.
- Each research phase uses a separate folder under `docs/`.
