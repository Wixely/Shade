# 2026-09-10: Shade foundation

- Status: Accepted for explicitly requested constraints; technical hypotheses remain provisional
- Source: User requests on 2026-09-10

## Decision

Name the project Shade. Start a new local Git repository. Target C#/.NET 10 with CupriFace. Investigate ScreenDimmer-MultiScreen's documented behavior without copying its implementation.

## Consequences

- Independently authored code and UI assets; no fork, code translation, or imported history.
- Initial deliverables are planning and investigation documents, not an application build.
- Windows-first and conditional NativeAOT/single-executable publishing follow the user's existing development defaults. Linux remains a secondary feasibility task.
- CupriFace for controls plus native Windows overlays is a hypothesis to test, not an accepted architectural commitment.
- Remote hosting, disclosure, and license are not decided by referring to an existing public repository.
- Add later decisions to supersede or refine this record; preserve the original constraints.

## Follow-up clarification: 2026-09-10

The user explicitly requires multi-screen variable shading, Home Assistant integration, and all setup/control through the UI, done correctly. Windows and Linux support are mandatory, with Windows primary. This supersedes the provisional feature scope and the characterization of Linux as merely a secondary feasibility task: investigation must now establish how to deliver and validate both platforms. Existing Home Assistant entity compatibility and the supported Linux desktop/session matrix remain open details.
