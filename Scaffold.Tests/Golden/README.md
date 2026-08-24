Golden files. Committed on purpose.

Each is the exact output of one template for the fixture entity in GoldenTests.
A template change shows up here as a reviewable diff instead of a silent
behaviour change.

To regenerate after an intended change:

    $env:SCAFFOLD_UPDATE_GOLDEN = "1"
    dotnet test
    $env:SCAFFOLD_UPDATE_GOLDEN = $null

Then read the diff. If it is not obviously an improvement, it is a regression.

A missing golden file is written and the test PASSES. Git is the review gate:
the new file shows up in `git status`, and a changed one shows up as a diff.
Failing the run would add a red build without adding information.

Only a MISMATCH fails, because that is the case where committed output changed
without anyone saying so. On a mismatch the new output is written next to the
golden file as `<name>.actual`, so the two can be diffed in an editor instead of
read out of console escaping. A stale `.actual` is deleted once the test passes
again.

These files are excluded from compilation in Scaffold.Tests.csproj. Several are
.cs and .cshtml, and without the exclusion the SDK default globs compile them
into the test project against ASP.NET and EF types it does not reference.
