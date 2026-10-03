# Contributing to ManagedCode.SkillOpt

ManagedCode.SkillOpt is the C#/.NET port maintained in this fork of Microsoft's
original Python [SkillOpt](https://github.com/microsoft/SkillOpt) project. Keep
changes within the documented .NET port scope; do not reintroduce Python engines,
subprocesses, provider SDKs, custom model transports, or required cloud services.

## Prerequisites

- .NET 10 SDK
- Git

## Build and test

```bash
dotnet restore ManagedCode.SkillOpt.sln
dotnet format ManagedCode.SkillOpt.sln --verify-no-changes
dotnet build ManagedCode.SkillOpt.sln --configuration Release
dotnet test ManagedCode.SkillOpt.sln --configuration Release
```

The test project is `tests/ManagedCode.SkillOpt.Tests`. Put prompts and authored
instruction text in tracked resource files; keep public contracts typed and async
work cancellable. Update `docs/Parity.md` when the implemented scope or upstream
coverage changes.

## Pull requests and releases

Keep pull requests focused and run the checks above. CI executes the canonical
.NET workflow at `.github/workflows/dotnet.yml`. NuGet releases use the version in
`Directory.Build.props` and an immutable `dotnet-v<version>` tag; never reuse or
move upstream Python release tags.

## License

Contributions are licensed under the [MIT License](LICENSE).
