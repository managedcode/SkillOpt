# ManagedCode.SkillOpt

This fork preserves the official `microsoft/SkillOpt` history and adds a reusable C# implementation of SkillOpt's text-space optimizer. The upstream Python package remains available for research benchmarks and host-specific adapters; this package is the in-process .NET implementation.

## Boundaries

- Target .NET 10 and use only Microsoft.Extensions.AI `IChatClient` and Microsoft.Extensions.AI.Evaluation `IEvaluator` for model/evaluation integrations.
- All package execution is embedded and in-process through caller-provided clients. Never add Python, subprocesses, required cloud services, direct provider SDKs, or custom HTTP model transports.
- Keep prompts and authored instructions in tracked resource files. Keep contracts typed and async operations cancellable.
- Preserve upstream's text optimization behavior and document gaps precisely in `docs/Parity.md`.

## Commands

- `dotnet restore ManagedCode.SkillOpt.sln`
- `dotnet format ManagedCode.SkillOpt.sln --verify-no-changes`
- `dotnet build ManagedCode.SkillOpt.sln --configuration Release`
- `dotnet test ManagedCode.SkillOpt.sln --configuration Release`
- `dotnet pack src/ManagedCode.SkillOpt/ManagedCode.SkillOpt.csproj --configuration Release`

## Package release

- Set the single NuGet version in the root `Directory.Build.props` `<Version>` property.
- Release only from a tag named `dotnet-v<Version>` (for example, `dotnet-v0.1.0`). This namespace avoids the upstream Python project's existing `v*` tags; never move or overwrite upstream tags.
- `.github/workflows/dotnet.yml` runs format, build, tests, and pack for branch/PR changes. On a `dotnet-v*` tag it additionally publishes the matching package to NuGet.org, verifies the flat-container package URL, and creates a GitHub release on the same C# commit.
- Before tagging, verify the package contents, passing CI, and the exact version/repository commit metadata. A local pack is not a release.
