# Neutriverse Writer

Neutriverse Writer is a local Windows writing tool for the Neutriverse Jekyll/Chirpy site. It is designed for posts that mix Markdown, inline HTML, and Neutriverse custom `nv-*` writing helpers.

## Build

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Output:

```text
D:\Cyberia Cafe\Neutriverse Writer\NeutriverseWriter.exe
```

## Usage

The app works best when launched with the target blog repository path:

```powershell
& "D:\Cyberia Cafe\Neutriverse Writer\NeutriverseWriter.exe" "C:\Users\ZFY\Documents\Codex\2026-04-29\github-blog\repo"
```

When opened without an argument, it tries to discover a nearby Jekyll repository and then falls back to the original Neutriverse blog path.

## Features

- Neutriverse-styled dark UI with logo blue and gold accents.
- Application icon based on the Neutriverse site logo.
- Left pane: dark live preview using site-like article typography.
- Right pane: dark Markdown/HTML source editor with automatic line wrapping, GitHub-like monospace typography, and lightweight syntax highlighting.
- New/Open/Save/Save As for `_posts/*.md`.
- Drag/drop or button insert images.
- Image insertion copies files to `assets/img/posts/<date-title>/`.
- Image insertion adds or reuses `media_subpath`.
- Inserts Markdown image syntax such as `![alt](image.png)`.
- Toolbar buttons for headings, blockquotes, unordered lists, ordered lists, tables, inline code, code blocks, bold, italic, underline, strikethrough, and horizontal rules.
- Dropdowns for Neutriverse text colors, underline styles, mark/background helpers, keys, spoilers, and common HTML inline tags.
- Preview support for Markdown tables and Neutriverse custom `nv-*` color/mark helpers.

## Version

Current source snapshot: v1.1 in progress.

The preview is intended as a close writing preview, not a byte-for-byte Jekyll renderer.
