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
- Live and manual preview modes. Manual mode keeps the preview still until Refresh is clicked.
- Publish button for the target blog repository. It can save the current post, stage the current post/media or all detected blog changes, commit, fetch/rebase, and push `origin main`.
- If the current Markdown file is outside the target blog repository, Publish can import it into `_posts/YYYY-MM-DD-slug.md` before committing.
- Left pane: dark live preview using site-like article typography.
- Right pane: dark Markdown/HTML source editor with automatic line wrapping, GitHub-like monospace typography, and lightweight syntax highlighting.
- New/Open/Save/Save As for `_posts/*.md`.
- Drag/drop or button insert images.
- Image insertion copies files to `assets/img/posts/<date-title>/`.
- Image insertion adds or reuses `media_subpath`.
- Inserts Markdown image syntax such as `![alt](image.png)`.
- Inline Image button copies files to the post media folder and inserts the Neutriverse `{% include inline-image.html %}` floating illustration helper, with optional small captions.
- Guide button opens a quick editor for the Neutriverse roam guide page (`_tabs/about.md`), covering Info, Personal Orbit, Signal/Status/Roadmap, and reading/media stacks.
- Grouped toolbar menus for files, headings, lists, tables, inline code, code blocks, configurable soft text blocks, bold, italic, underline, strikethrough, and horizontal rules.
- Text Block options cover hover translation, hover text, English/Chinese display fonts, alignment, and block background color. The default uses the current bilingual script style and `#18191b`.
- Dropdowns for Neutriverse text colors, underline styles, mark/background helpers, keys, spoilers, and common HTML inline tags.
- Preview support for Markdown tables, inline-image includes, and Neutriverse custom `nv-*` color/mark helpers.

Inline Image inserts a right-aligned include using the copied file name. The post's `media_subpath` supplies the image folder:

```liquid
{% include inline-image.html
  src="demo.jpg"
  alt="demo"
  caption="Optional small caption"
  align="right"
%}
```

Shortcut: `Ctrl+Shift+I`.

The Guide panel has two write actions:

- `Save`: updates only `_tabs/about.md` in the target blog repository.
- `Save && Publish`: stages only `_tabs/about.md`, runs `git diff --cached --check`, commits, fetches/rebases `origin/main`, and pushes `origin main`.
- The guide publish flow shows a progress/log window, retries fetch on transient connection resets, and can continue pushing an already-created local commit after a previous network failure.
- The regular post Publish flow uses the same progress/log window, fetch retry behavior, and recovery path for already-created local commits.

## Version

Current source snapshot: v1.1 in progress.

The preview is intended as a close writing preview, not a byte-for-byte Jekyll renderer.

## Publishing

The Publish action runs against the target blog repository passed to the app at launch. It does not store credentials or tokens; it relies on the local Git/GitHub authentication already configured on the machine.

Before pushing, it runs `git diff --cached --check`. If `git rebase origin/main` reports conflicts, the push stops and no force push is attempted.

If the current file is outside the blog repository, Publish asks whether to import it into `_posts`. The default imported filename is generated from front matter `date` and `title`.
