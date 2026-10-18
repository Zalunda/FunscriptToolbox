# Agent Working Guidelines & Constraints

This document defines core rules, workflows, and constraints for AI assistants working on **FunscriptToolBox**.

---

## 1. Application Overview & Architecture Context

Please read PROJECT.md for more information.

---

## 2. Golden Rules & Hard Constraints

* **GIT RULE**: **NEVER run `git add` under any circumstance.** The user inspects and stages changes manually.
* **DOCUMENTATION INTEGRITY**: Preserve existing comments, docstrings, and formatting unrelated to current edits.
* **FILE LINKS**: When referencing code files in responses, use clickable Markdown links with `file:///` and forward slashes.
* **NO SILENT DEFAULTS / STRICT CONFIGURATION RULE**: If something is not configured, NEVER try to "default your way" out of the problem. Do NOT invent fallback values (e.g., fallback directories like `"data"`, fake drive letters, etc.). Instead: ask the user, surface a clear configuration error, or disable the operations that need that configuration until properly configured.
* The project does not have automated tests.
* If possible, try to use "git" commands (grep, etc) instead of Select-String/Get-ChildItem.
---
