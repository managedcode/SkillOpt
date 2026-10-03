# ManagedCode.SkillOpt

[![NuGet](https://img.shields.io/nuget/v/ManagedCode.SkillOpt)](https://www.nuget.org/packages/ManagedCode.SkillOpt) [![.NET CI](https://github.com/managedcode/SkillOpt/actions/workflows/dotnet.yml/badge.svg?branch=main)](https://github.com/managedcode/SkillOpt/actions/workflows/dotnet.yml) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

ManagedCode.SkillOpt is a C#/.NET port of Microsoft's original Python [SkillOpt](https://github.com/microsoft/SkillOpt) project. This repository is a fork that preserves the upstream Git history and license while maintaining an in-process .NET library. The upstream Python source remains the reference for future port work; Python engines, benchmarks, CLI tools, plugins, and WebUI are not shipped here.

The library targets .NET 10 and uses caller-provided [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/overview) `IChatClient` instances and [Microsoft.Extensions.AI.Evaluation](https://learn.microsoft.com/dotnet/ai/evaluation) `IEvaluator`. Hosts built with Microsoft Agent Framework can pass their normal MEAI client. The library does not start Python, launch a subprocess, call a provider SDK, create an HTTP model transport, or require an optimizer service.

## Install

```xml
<PackageReference Include="ManagedCode.SkillOpt" Version="0.1.3" />
```

## What it implements

The port optimizes one Markdown skill document while keeping the target client fixed. It runs scenarios, evaluates typed per-case evidence, reflects on success and failure, aggregates and selects bounded edits, validates candidates on a held-out selection split, and retains the best skill. It includes supported patch/rewrite modes, edit-budget schedules, rejected-edit memory, epoch meta/slow updates, optional final test reporting, candidate gates, and awaited typed checkpoints. See the [port coverage and known deviations](docs/Parity.md); this is a documented C# implementation scope, not a claim of complete Python feature or benchmark parity.

## Example

The exact request contract and persistence guidance are in the [C# package guide](src/ManagedCode.SkillOpt/README.md). In outline, the host provides frozen, disjoint cases and role clients:

```csharp
var result = await SkillOptOptimizer.OptimizeAsync(new SkillOptRequest
{
    InitialSkill = skillMarkdown,
    TrainingCases = trainingCases,
    SelectionCases = validationCases,
    TestCases = testCases,
    TargetChatClient = targetClient,
    OptimizerChatClient = optimizerClient,
    Evaluator = evaluator,
    Options = options,
    Checkpoint = async (state, token) => await SaveCheckpointAsync(state, token)
}, cancellationToken);

var bestSkillMarkdown = result.BestSkill;
```

The caller owns model/client construction, evaluator policy, case collection, durable checkpoint storage, billing, and saving the returned artifact. The checkpoint callback must persist its state before it returns. The library never modifies a product draft or publishes a skill.

## Build and verify

```bash
dotnet restore ManagedCode.SkillOpt.sln
dotnet format ManagedCode.SkillOpt.sln --verify-no-changes
dotnet build ManagedCode.SkillOpt.sln --configuration Release
dotnet test ManagedCode.SkillOpt.sln --configuration Release
dotnet pack src/ManagedCode.SkillOpt/ManagedCode.SkillOpt.csproj --configuration Release
```

The package version is maintained in `Directory.Build.props`. Published .NET releases use `dotnet-v<version>` tags so they cannot collide with the preserved upstream `v*` tags. The `dotnet.yml` workflow runs package checks and publishes the versioned NuGet package.

## Upstream source and license

- Original project and research: [microsoft/SkillOpt](https://github.com/microsoft/SkillOpt)
- C# port base: upstream commit [`fa4ca184573e42ec11472959dd57422381418096`](https://github.com/microsoft/SkillOpt/commit/fa4ca184573e42ec11472959dd57422381418096)
- License: [MIT](LICENSE)
