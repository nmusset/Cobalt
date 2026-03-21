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
