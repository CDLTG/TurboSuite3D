# Security Policy

## Reporting a Vulnerability

Please **do not open a public GitHub issue** for security bugs.

Use GitHub's private reporting instead: on the repo page, click **Security → Report a vulnerability**. This routes the report privately to the maintainer.

Only the latest released version is supported. Fixes ship through the standard auto-update channel.

## Trust Model

- **Auto-update channel.** On Revit launch, TurboSuite reads the server share path from `%LOCALAPPDATA%\TurboSuite\config.json` and copies files from that share into `%LOCALAPPDATA%\TurboSuite\Staging\`. After Revit closes, `TurboSuiteUpdater.exe` overwrites the installed add-in from staging. Anyone with write access to the server share — or to `config.json` — can run code inside Revit on next launch. Restrict share write access to the maintainer. Staged files are not signature-verified in v1.0.0.
- **User-supplied files.** TurboDocs reads PDFs via `PdfSharpCore` and images via `SixLabors.ImageSharp` (transitive). TurboName reads DWG/DXF via `ACadSharp`. All files are user-selected from their own filesystem; there is no network ingestion.
- **`SixLabors.ImageSharp 1.0.4` CVEs.** Known unfixed in the 1.x line. Accepted for v1.0.0 — only processes user-chosen logo files. Upgrade tracked post-1.0.
