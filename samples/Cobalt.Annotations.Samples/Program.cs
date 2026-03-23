// Cobalt.Annotations sample project.
//
// This project demonstrates all ownership annotation attributes.
// Each file illustrates a specific concept:
//
//   OwnershipTransfer.cs  — [Owned] for transferring value ownership
//   BorrowingPatterns.cs  — [Borrowed] / [MutBorrowed] for shared and exclusive access
//   MustDisposePatterns.cs — [MustDispose] for types requiring disposal
//   ScopedReferences.cs   — [Scoped] for references that must not escape
//   NoAliasPatterns.cs    — [NoAlias] for unaliased parameters and fields
//   MoveSemantics.cs      — Move semantics: aliasing, implicit sharing
//   CombinedPatterns.cs   — Combining annotations in realistic scenarios
//
// Building this project produces analyzer warnings (CB0001-CB0005) on
// intentional bugs. Run 'dotnet build' to see them in action.

using Cobalt.Annotations.Samples;

Console.WriteLine("Cobalt.Annotations samples — see source code for usage examples.");
Console.WriteLine("Build this project to see analyzer warnings on intentional bugs.");

// Run the safe examples (no warnings).
BorrowingPatterns.SharedBorrowsCoexist();
MustDisposePatterns.CorrectUsage();
MustDisposePatterns.TransferOwnership();
