# ADR-0001: License PbxAdmin under MIT

- **Status:** Accepted
- **Date:** 2026-03-24
- **Deciders:** Project lead
- **Related:** `docs/specs/2026-03-24-pbxadmin-architecture-overview.md`

## Context

PbxAdmin is a Blazor Server admin panel that demonstrates the `Asterisk.Sdk` NuGet packages on a live Asterisk PBX. The project's goals are to (a) serve as a working showcase of the SDK, (b) be adoptable by Asterisk integrators with minimal friction, and (c) attract external contributors who can extend it for their own deployments. A licensing choice that introduces any compliance review, copyleft obligations, or attribution burden would undercut all three goals.

## Decision

PbxAdmin is released under the **MIT License**. It depends only on MIT-licensed packages — notably `Asterisk.Sdk` consumed via NuGet. Any code or dependency that cannot be shipped under MIT is out of scope for this repository.

## Consequences

- **Positive:** Contributors can fork, modify, and ship PbxAdmin — including inside commercial products — with no legal review beyond preserving the copyright notice.
- **Positive:** Integrators and downstream distributions (Docker Hub image, Linux distro packages, cloud marketplaces) carry no attribution burden beyond the standard MIT notice.
- **Negative:** PbxAdmin cannot incorporate code from more restrictively-licensed sources, even when that code would otherwise be a good fit.
- **Trade-off:** Some functionality that would be easier to reuse from non-MIT sources has to be implemented from scratch or omitted.

## Alternatives considered

- **Apache-2.0:** rejected — adds a patent grant that MIT lacks, but in exchange introduces NOTICE-file obligations that marginally increase friction for embedders. MIT's minimalism wins for a project meant to be embedded and redistributed.
- **GPL / AGPL:** rejected — the copyleft obligations would block the primary use case (integrators embedding PbxAdmin into proprietary deployments and distributions).
- **Dual-license (MIT + commercial):** rejected — dual licensing is only worth the overhead when there is a specific commercial extension being sold from the same codebase; keeping this repository purely MIT avoids the dual-track compliance burden.
