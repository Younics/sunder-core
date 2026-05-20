# Security Policy

Sunder Core includes a desktop app, local runtime host, package installation logic, package archive validation, local HTTP APIs, and SDK/build tooling. Please report security issues privately.

## Reporting A Vulnerability

Use GitHub Security Advisories when possible:

https://github.com/Younics/sunder-core/security/advisories/new

If GitHub Security Advisories are unavailable, contact the maintainers privately before publishing details.

Please include:

- Affected component, version, commit, or package.
- Operating system and environment.
- Reproduction steps or proof of concept.
- Impact assessment.
- Any relevant logs with secrets removed.

## Please Do Not Report Publicly

Do not open public issues for vulnerabilities involving package installation, archive validation bypasses, local runtime APIs, credential handling, package asset serving, or arbitrary code execution.

## Supported Versions

Security fixes target the active development branch and maintained public releases. If a fix affects package compatibility or archive validation, release notes will call that out explicitly.
