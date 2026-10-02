# SkillOpt tests

Own deterministic regression coverage. Test-only `IChatClient` and `IEvaluator` implementations are allowed. Cover exact edit anchors, held-out rejection, malformed optimizer output, cancellation, budgets, and resume-state validation. Verify with `dotnet test ../../ManagedCode.SkillOpt.sln --configuration Release`.
