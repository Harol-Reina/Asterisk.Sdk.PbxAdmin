# ADR-0002: Build the entire ecosystem on .NET Native AOT

- **Status:** Accepted
- **Date:** 2026-03-24
- **Deciders:** Harol Reina (project lead)
- **Related:** ADR-0001

## Context

PbxAdmin runs inside a Docker container alongside Asterisk in customer deployments that often include small edge devices (SBCs, on-prem appliances) with tight memory budgets and slow cold starts. The Blazor Server model also amplifies startup time into perceived latency on the first request after a container restart.

Across the four-repo Asterisk ecosystem, a single runtime choice has to serve SDK libraries, a load-test binary, a Blazor Server app, and future platform services. Mixing JIT and AOT across packages would force consumers to choose and would pull reflection-heavy patterns back into libraries that are supposed to be AOT-clean.

## Decision

We will target **.NET 10 Native AOT across every repo in the ecosystem** — SDK, Pro, Platform, and PbxAdmin. No reflection-based serialization, no runtime code generation, no dynamic proxies. Dapper is used in place of EF Core; `System.Text.Json` source generators handle every DTO.

## Consequences

- **Positive:** Cold-start measured in tens of milliseconds; container images are smaller; memory footprint is predictable — all critical for edge deployments.
- **Positive:** Every SDK package is trim-safe and AOT-safe by construction, so downstream consumers inherit the guarantees.
- **Negative:** We give up the ecosystem's most popular ORM (EF Core still has AOT gaps) and a long tail of reflection-based libraries.
- **Negative:** Every new dependency has to be AOT-audited before adoption; some third-party NuGet packages are simply off-limits.
- **Trade-off:** Developer ergonomics (source generators, manual DI wiring) cost more upfront in exchange for runtime characteristics that match the deployment target.

## Alternatives considered

- **JIT (CoreCLR) with ReadyToRun:** rejected — faster cold start than pure JIT but still far slower than AOT, and does not deliver the size/trimming wins.
- **Mixed: JIT for the app, AOT for libraries:** rejected — would still require the libraries to be AOT-clean, and introduces two test matrices without simplifying anything.
- **Go or Rust for the SDK:** rejected — .NET is the dominant stack in the Asterisk integration community we target; switching languages would cut the addressable audience for no runtime win that AOT doesn't also deliver.
