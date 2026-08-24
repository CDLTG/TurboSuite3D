# Security Policy

## Reporting a Vulnerability

Please **do not open a public GitHub issue** for security bugs.

Use GitHub's private reporting instead: on the repo page, click **Security → Report a vulnerability**. This routes the report privately to the maintainer.

Only the latest released version is supported. Fixes ship through the standard auto-update channel.

## Trust Model

- **Auto-update channel.** On Revit launch, TurboSuite reads the server share path from `%LOCALAPPDATA%\TurboSuite\config.json` and copies files from that share into `%LOCALAPPDATA%\TurboSuite\Staging\`. After Revit closes, `TurboSuiteUpdater.exe` overwrites the installed add-in from staging. Anyone with write access to the server share — or to `config.json` — can run code inside Revit on next launch. Restrict share write access to the maintainer. Staged files are not signature-verified in v1.0.0.
- **User-supplied files.** TurboDocs reads and renders PDFs/images via `PDFsharp` 6.x (native decoders; no `SixLabors.ImageSharp` dependency). TurboName reads DWG/DXF via `ACadSharp`. All files are user-selected from their own filesystem; there is no network ingestion.
- **`SixLabors.ImageSharp` CVEs — resolved.** The vulnerable `SixLabors.ImageSharp 1.0.4` was a transitive dependency of the former `PdfSharpCore`; migrating TurboDocs to `PDFsharp` 6.x dropped it entirely, and the `NuGetAuditSuppress` block in `Directory.Build.props` was removed. Stale copies on already-installed clients are swept on their next update via the updater's retire manifest (`retire.txt`).
