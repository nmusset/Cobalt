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
//   CombinedPatterns.cs   — Combining annotations in realistic scenarios
//
// Commented-out lines marked "BUG" show violations that the Cobalt analyzer
// (Phase A.2/A.3) will detect once implemented.

using Cobalt.Annotations.Samples;

Console.WriteLine("Cobalt.Annotations samples — see source code for usage examples.");

// Run the safe examples.
OwnershipTransfer.UseAfterMoveExample();
BorrowingPatterns.SharedBorrowsCoexist();
MustDisposePatterns.CorrectUsage();
MustDisposePatterns.TransferOwnership();
