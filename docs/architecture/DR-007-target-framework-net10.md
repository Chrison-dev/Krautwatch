# DR-007 — Target Framework: .NET 10 LTS

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-03-08 |
| **Deciders** | Architect |

## Context

.NET 9 was the initial default target during scaffolding. However .NET 10 shipped in
November 2025 as an LTS release supported until November 2028. Both .NET 8 and .NET 9
reach end of support simultaneously in November 2026, making .NET 10 the only viable
long-term target for a new project started in 2026.

## Decision

Target **net10.0** across all projects in the solution from day one.

- All `.csproj` files use `<TargetFramework>net10.0</TargetFramework>`
- All Docker base images use the `9.0` → `10.0` tagged variants
- C# 14 language features are available and encouraged

## Consequences

- ✅ 3 years of LTS support (until November 2028)
- ✅ No mid-project framework upgrade required
- ✅ Performance improvements in JIT, GC, and runtime vs .NET 9
- ✅ C# 14 — field-backed properties, extension members, cleaner code
- ⚠️ Requires .NET 10 SDK on all developer machines
  - macOS: `brew install --cask dotnet-sdk` or download from https://dotnet.microsoft.com/download/dotnet/10.0
