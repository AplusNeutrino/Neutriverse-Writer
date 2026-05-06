using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace NeutriverseWriter
{
    public class MainForm : Form
    {
        private readonly string repoRoot;
        private readonly string postsDir;
        private readonly string imageRoot;
        private readonly SplitContainer split;
        private readonly WebBrowser preview;
        private readonly RichTextBox editor;
        private readonly StatusStrip statusStrip;
        private readonly ToolStripStatusLabel status;
        private readonly Timer previewTimer;
        private readonly Timer highlightTimer;
        private bool applyingHighlight;
        private string currentFile;

        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogFatal(e.ExceptionObject as Exception);
            };

            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                LogFatal(e.Exception);
                MessageBox.Show(e.Exception.ToString(), "Neutriverse Writer failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string root = args.Length > 0 ? args[0] : FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
                Application.Run(new MainForm(root));
            }
            catch (Exception ex)
            {
                LogFatal(ex);
                MessageBox.Show(ex.ToString(), "Neutriverse Writer failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void LogFatal(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NeutriverseWriter-error.log");
                File.AppendAllText(path, DateTime.Now.ToString("s") + Environment.NewLine + (ex == null ? "Unknown exception" : ex.ToString()) + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string FindRepoRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "_config.yml")) && Directory.Exists(Path.Combine(dir.FullName, "_posts")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return @"C:\Users\ZFY\Documents\Codex\2026-04-29\github-blog\repo";
        }

        public MainForm(string root)
        {
            repoRoot = root;
            postsDir = Path.Combine(repoRoot, "_posts");
            imageRoot = Path.Combine(repoRoot, "assets", "img", "posts");

            Text = "Neutriverse Writer";
            Width = 1360;
            Height = 860;
            StartPosition = FormStartPosition.CenterScreen;

            var toolbar = BuildToolbar();
            Controls.Add(toolbar);

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;

            preview = new WebBrowser();
            preview.Dock = DockStyle.Fill;
            preview.AllowWebBrowserDrop = false;
            preview.ScriptErrorsSuppressed = true;
            split.Panel1.Controls.Add(preview);

            editor = new RichTextBox();
            editor.Dock = DockStyle.Fill;
            editor.ScrollBars = RichTextBoxScrollBars.Vertical;
            editor.AcceptsTab = true;
            editor.WordWrap = true;
            editor.BorderStyle = BorderStyle.None;
            editor.BackColor = Color.FromArgb(13, 17, 23);
            editor.ForeColor = Color.FromArgb(230, 237, 243);
            editor.SelectionColor = editor.ForeColor;
            editor.Font = new Font("Consolas", 11f);
            editor.HideSelection = false;
            editor.AllowDrop = true;
            editor.TextChanged += delegate
            {
                if (!applyingHighlight)
                {
                    QueuePreview();
                    QueueHighlight();
                }
            };
            editor.DragEnter += EditorDragEnter;
            editor.DragDrop += EditorDragDrop;
            split.Panel2.Controls.Add(editor);

            Controls.Add(split);
            split.BringToFront();

            statusStrip = new StatusStrip();
            status = new ToolStripStatusLabel();
            statusStrip.Items.Add(status);
            Controls.Add(statusStrip);

            previewTimer = new Timer();
            previewTimer.Interval = 350;
            previewTimer.Tick += delegate
            {
                previewTimer.Stop();
                RenderPreview();
            };

            highlightTimer = new Timer();
            highlightTimer.Interval = 450;
            highlightTimer.Tick += delegate
            {
                highlightTimer.Stop();
                ApplyEditorHighlight();
            };

            Shown += delegate
            {
                split.Panel1MinSize = 320;
                split.Panel2MinSize = 360;
                split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Width / 2);
            };

            NewDraft();
        }

        private ToolStrip BuildToolbar()
        {
            var toolbar = new ToolStrip();
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Dock = DockStyle.Top;

            AddButton(toolbar, "New", delegate { NewDraft(); });
            AddButton(toolbar, "Open", delegate { OpenPost(); });
            AddButton(toolbar, "Save", delegate { SavePost(false); });
            AddButton(toolbar, "Save As", delegate { SavePost(true); });
            toolbar.Items.Add(new ToolStripSeparator());
            AddButton(toolbar, "Insert Image", delegate { InsertImagesFromDialog(); });
            AddButton(toolbar, "Refresh", delegate { RenderPreview(); });
            toolbar.Items.Add(new ToolStripSeparator());

            AddButton(toolbar, "H1", delegate { PrefixLines("# "); });
            AddButton(toolbar, "H2", delegate { PrefixLines("## "); });
            AddButton(toolbar, "H3", delegate { PrefixLines("### "); });
            AddButton(toolbar, "Quote", delegate { PrefixLines("> "); });
            AddButton(toolbar, "UL", delegate { PrefixLines("- "); });
            AddButton(toolbar, "OL", delegate { NumberLines(); });
            AddDropdown(toolbar, "Table", new Dictionary<string, EventHandler>
            {
                { "2 columns", delegate { InsertTable(2, 2); } },
                { "3 columns", delegate { InsertTable(3, 3); } },
                { "4 columns", delegate { InsertTable(4, 3); } }
            });
            AddButton(toolbar, "Code", delegate { WrapSelection("`", "`"); });
            AddButton(toolbar, "Code Block", delegate { WrapSelection("```" + Environment.NewLine, Environment.NewLine + "```"); });
            toolbar.Items.Add(new ToolStripSeparator());

            AddButton(toolbar, "B", delegate { WrapSelection("**", "**"); });
            AddButton(toolbar, "I", delegate { WrapSelection("*", "*"); });
            AddButton(toolbar, "U", delegate { WrapSelection("<u>", "</u>"); });
            AddButton(toolbar, "S", delegate { WrapSelection("~~", "~~"); });
            toolbar.Items.Add(new ToolStripSeparator());

            AddDropdown(toolbar, "Text Color", new Dictionary<string, EventHandler>
            {
                { "Red", delegate { WrapSpan("nv-red"); } },
                { "Orange", delegate { WrapSpan("nv-orange"); } },
                { "Gold", delegate { WrapSpan("nv-gold"); } },
                { "Green", delegate { WrapSpan("nv-green"); } },
                { "Cyan", delegate { WrapSpan("nv-cyan"); } },
                { "Blue", delegate { WrapSpan("nv-blue"); } },
                { "Purple", delegate { WrapSpan("nv-purple"); } },
                { "Pink", delegate { WrapSpan("nv-pink"); } },
                { "Muted", delegate { WrapSpan("nv-muted"); } }
            });

            AddDropdown(toolbar, "Underline", new Dictionary<string, EventHandler>
            {
                { "Plain", delegate { WrapSpan("nv-underline"); } },
                { "Wavy", delegate { WrapSpan("nv-wavy"); } },
                { "Dotted", delegate { WrapSpan("nv-dotted"); } },
                { "Red Plain", delegate { WrapSpan("nv-red nv-underline"); } },
                { "Blue Wavy", delegate { WrapSpan("nv-blue nv-wavy"); } },
                { "Gold Dotted", delegate { WrapSpan("nv-gold nv-dotted"); } }
            });

            AddDropdown(toolbar, "Mark", new Dictionary<string, EventHandler>
            {
                { "Gold Mark", delegate { WrapSpan("nv-mark"); } },
                { "Red Mark", delegate { WrapSpan("nv-mark-red"); } },
                { "Blue Mark", delegate { WrapSpan("nv-mark-blue"); } },
                { "Green Mark", delegate { WrapSpan("nv-mark-green"); } },
                { "Key", delegate { WrapSpan("nv-key"); } },
                { "Spoiler", delegate { WrapSpan("nv-spoiler"); } }
            });

            AddDropdown(toolbar, "HTML", new Dictionary<string, EventHandler>
            {
                { "Superscript", delegate { WrapSelection("<sup>", "</sup>"); } },
                { "Subscript", delegate { WrapSelection("<sub>", "</sub>"); } },
                { "Small", delegate { WrapSelection("<small>", "</small>"); } },
                { "Keyboard", delegate { WrapSelection("<kbd>", "</kbd>"); } },
                { "Break", delegate { InsertAtCursor("<br>"); } },
                { "Horizontal Rule", delegate { InsertAtCursor(Environment.NewLine + "---" + Environment.NewLine); } }
            });

            return toolbar;
        }

        private void AddButton(ToolStrip toolbar, string text, EventHandler click)
        {
            var button = new ToolStripButton(text);
            button.Click += click;
            toolbar.Items.Add(button);
        }

        private void AddDropdown(ToolStrip toolbar, string text, Dictionary<string, EventHandler> items)
        {
            var dropdown = new ToolStripDropDownButton(text);
            foreach (var item in items)
            {
                var menuItem = new ToolStripMenuItem(item.Key);
                menuItem.Click += item.Value;
                dropdown.DropDownItems.Add(menuItem);
            }
            toolbar.Items.Add(dropdown);
        }

        private void NewDraft()
        {
            currentFile = null;
            string date = DateTime.Now.ToString("yyyy-MM-dd");
            string nl = Environment.NewLine;
            editor.Text = string.Join(nl, new[]
            {
                "---",
                "title: \"新文章\"",
                "date: " + date,
                "categories: [记忆碎片]",
                "tags: []",
                "description: \"\"",
                "---",
                "",
                "## 开始写作",
                "",
                "这里写正文。",
                ""
            });
            SetStatus("New draft created.");
            RenderPreview();
        }

        private void OpenPost()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.InitialDirectory = postsDir;
                dialog.Filter = "Markdown posts (*.md)|*.md|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                currentFile = dialog.FileName;
                editor.Text = File.ReadAllText(currentFile, Encoding.UTF8);
                SetStatus("Opened " + currentFile);
                RenderPreview();
            }
        }

        private void SavePost(bool saveAs)
        {
            string text = editor.Text;
            if (!HasFrontMatter(text))
            {
                MessageBox.Show(this, "The post needs YAML front matter.", "Neutriverse Writer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (saveAs || string.IsNullOrEmpty(currentFile))
            {
                string date = GetFrontMatterValue(text, "date");
                if (date.Length >= 10)
                {
                    date = date.Substring(0, 10);
                }
                else
                {
                    date = DateTime.Now.ToString("yyyy-MM-dd");
                }

                string title = GetFrontMatterValue(text, "title");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "new-post";
                }

                currentFile = Path.Combine(postsDir, date + "-" + SanitizeFileName(title) + ".md");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(currentFile));
            File.WriteAllText(currentFile, editor.Text, new UTF8Encoding(false));
            SetStatus("Saved " + currentFile);
        }

        private void InsertImagesFromDialog()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Multiselect = true;
                dialog.Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                InsertImageFiles(dialog.FileNames);
            }
        }

        private void EditorDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void EditorDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null)
            {
                InsertImageFiles(files);
            }
        }

        private void InsertImageFiles(IEnumerable<string> files)
        {
            string text = EnsureMediaSubpath(editor.Text);
            editor.Text = text;

            string mediaSubpath = GetFrontMatterValue(editor.Text, "media_subpath");
            string relativeFolder = mediaSubpath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
            string targetDir = Path.Combine(repoRoot, relativeFolder);
            Directory.CreateDirectory(targetDir);

            var inserted = new StringBuilder();
            foreach (string source in files)
            {
                if (!File.Exists(source) || !IsImage(source))
                {
                    continue;
                }

                string name = UniqueFileName(targetDir, SanitizeFileName(Path.GetFileName(source)));
                File.Copy(source, Path.Combine(targetDir, name), false);
                string alt = Path.GetFileNameWithoutExtension(name);
                inserted.AppendLine("![" + alt + "](" + name.Replace("\\", "/") + ")");
                inserted.AppendLine();
            }

            if (inserted.Length > 0)
            {
                InsertAtCursor(inserted.ToString());
                SetStatus("Inserted image(s) into " + targetDir);
            }
        }

        private string EnsureMediaSubpath(string text)
        {
            string existing = GetFrontMatterValue(text, "media_subpath");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return text;
            }

            string date = GetFrontMatterValue(text, "date");
            if (date.Length >= 10)
            {
                date = date.Substring(0, 10);
            }
            else
            {
                date = DateTime.Now.ToString("yyyy-MM-dd");
            }

            string title = GetFrontMatterValue(text, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "post";
            }

            string folder = "/assets/img/posts/" + date + "-" + SanitizeFolderSegment(title);
            int close = FindFrontMatterClose(text);
            if (close < 0)
            {
                return text;
            }

            return text.Insert(close, "media_subpath: " + folder + Environment.NewLine);
        }

        private void InsertAtCursor(string text)
        {
            int start = editor.SelectionStart;
            editor.Text = editor.Text.Insert(start, text);
            editor.SelectionStart = start + text.Length;
            editor.Focus();
        }

        private void WrapSpan(string classes)
        {
            WrapSelection("<span class=\"" + classes + "\">", "</span>");
        }

        private void WrapSelection(string before, string after)
        {
            string selected = editor.SelectedText;
            int start = editor.SelectionStart;
            editor.SelectedText = before + selected + after;
            editor.SelectionStart = start + before.Length;
            editor.SelectionLength = selected.Length;
            editor.Focus();
        }

        private void PrefixLines(string prefix)
        {
            string selected = editor.SelectedText;
            int start = editor.SelectionStart;

            if (string.IsNullOrEmpty(selected))
            {
                editor.SelectedText = prefix;
                editor.Focus();
                return;
            }

            string normalized = selected.Replace("\r\n", "\n");
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                {
                    lines[i] = prefix + lines[i];
                }
            }

            string replacement = string.Join(Environment.NewLine, lines);
            editor.SelectedText = replacement;
            editor.SelectionStart = start;
            editor.SelectionLength = replacement.Length;
            editor.Focus();
        }

        private void NumberLines()
        {
            string selected = editor.SelectedText;
            int start = editor.SelectionStart;

            if (string.IsNullOrEmpty(selected))
            {
                editor.SelectedText = "1. ";
                editor.Focus();
                return;
            }

            string normalized = selected.Replace("\r\n", "\n");
            string[] lines = normalized.Split('\n');
            int number = 1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                {
                    lines[i] = number + ". " + lines[i];
                    number++;
                }
            }

            string replacement = string.Join(Environment.NewLine, lines);
            editor.SelectedText = replacement;
            editor.SelectionStart = start;
            editor.SelectionLength = replacement.Length;
            editor.Focus();
        }

        private void InsertTable(int columns, int bodyRows)
        {
            var builder = new StringBuilder();
            builder.Append("|");
            for (int i = 1; i <= columns; i++)
            {
                builder.Append(" Column ").Append(i).Append(" |");
            }
            builder.AppendLine();

            builder.Append("|");
            for (int i = 1; i <= columns; i++)
            {
                builder.Append(" --- |");
            }
            builder.AppendLine();

            for (int row = 0; row < bodyRows; row++)
            {
                builder.Append("|");
                for (int col = 0; col < columns; col++)
                {
                    builder.Append("  |");
                }
                builder.AppendLine();
            }

            InsertAtCursor(Environment.NewLine + builder.ToString() + Environment.NewLine);
        }

        private void QueuePreview()
        {
            previewTimer.Stop();
            previewTimer.Start();
        }

        private void QueueHighlight()
        {
            highlightTimer.Stop();
            highlightTimer.Start();
        }

        private void ApplyEditorHighlight()
        {
            if (editor.IsDisposed)
            {
                return;
            }

            int start = editor.SelectionStart;
            int length = editor.SelectionLength;
            applyingHighlight = true;

            try
            {
                editor.SuspendLayout();
                editor.SelectAll();
                editor.SelectionColor = Color.FromArgb(230, 237, 243);
                editor.SelectionFont = new Font(editor.Font, FontStyle.Regular);

                ColorPattern("(?m)^---\\s*$", Color.FromArgb(139, 148, 158), false);
                ColorPattern("(?m)^(title|date|categories|tags|description|media_subpath|image|hidden|display_date|display_updated_at):", Color.FromArgb(121, 192, 255), true);
                ColorPattern("(?m)^#{1,6}\\s+.*$", Color.FromArgb(88, 166, 255), true);
                ColorPattern("(?m)^\\s*[-*+]\\s+", Color.FromArgb(255, 171, 112), true);
                ColorPattern("(?m)^\\s*\\d+\\.\\s+", Color.FromArgb(255, 171, 112), true);
                ColorPattern("<[^>]+>", Color.FromArgb(139, 148, 158), false);
                ColorPattern("class=\\\"[^\\\"]*nv-[^\\\"]*\\\"", Color.FromArgb(210, 168, 255), false);
                ColorPattern("`[^`]+`", Color.FromArgb(126, 231, 135), false);
                ColorPattern("\\*\\*[^*]+\\*\\*", Color.FromArgb(255, 255, 255), true);
                ColorPattern("~~[^~]+~~", Color.FromArgb(255, 123, 114), false);

                editor.SelectionStart = Math.Min(start, editor.TextLength);
                editor.SelectionLength = Math.Min(length, editor.TextLength - editor.SelectionStart);
                editor.SelectionColor = Color.FromArgb(230, 237, 243);
            }
            finally
            {
                editor.ResumeLayout();
                applyingHighlight = false;
            }
        }

        private void ColorPattern(string pattern, Color color, bool bold)
        {
            foreach (Match match in Regex.Matches(editor.Text, pattern))
            {
                editor.Select(match.Index, match.Length);
                editor.SelectionColor = color;
                if (bold)
                {
                    editor.SelectionFont = new Font(editor.Font, FontStyle.Bold);
                }
            }
        }

        private void RenderPreview()
        {
            string html = BuildHtml(editor.Text);
            preview.DocumentText = html;
        }

        private string BuildHtml(string source)
        {
            string body = StripFrontMatter(source);
            string mediaSubpath = GetFrontMatterValue(source, "media_subpath");
            string cssPath = Path.Combine(repoRoot, "assets", "css", "ChirpyDefault.css");
            string css = File.Exists(cssPath) ? File.ReadAllText(cssPath, Encoding.UTF8) : "";
            string title = WebUtility.HtmlEncode(GetFrontMatterValue(source, "title"));
            string baseCss =
                "html,body{background:#1b1b1e!important;color:#c9d1d9!important;}" +
                "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Noto Sans SC','Microsoft YaHei',sans-serif;line-height:1.75;padding:2rem 2.35rem;max-width:860px;margin:0 auto;}" +
                ".content{font-size:1.03rem;letter-spacing:0;}" +
                ".content p{margin:0 0 1rem;}" +
                ".content h1,.content h2,.content h3,.content h4{color:#dce8f7!important;font-weight:500;line-height:1.35;margin:2.1rem 0 1rem;}" +
                ".content>h1:first-child{margin-top:0;font-size:2.05rem;font-weight:500;}" +
                ".content h1{font-size:2.05rem}.content h2{font-size:1.65rem}.content h3{font-size:1.35rem}.content h4{font-size:1.18rem}" +
                "strong{font-weight:700;color:#dce8f7;} em{font-style:italic;} del{text-decoration:line-through;} u{text-decoration:underline;}" +
                "a{color:#8fb7ff;} img{max-width:100%;height:auto;border-radius:.35rem;}" +
                "hr{height:1px;margin:2rem 0;border:0;background:#30363d;}" +
                ".content ul,.content ol{margin:0 0 1rem 1.35rem;padding-left:1.25rem}.content li{margin:.35rem 0}.content li::marker{color:#9aa4b2;}" +
                ".content blockquote{margin:1.2rem 0 1.2rem 1rem;padding:.1rem 0 .1rem 1rem;border-left:.22rem solid #4f6fff!important;background:transparent!important;color:#c9d1d9!important;}" +
                ".content blockquote p{margin:.45rem 0;color:#c9d1d9!important;}" +
                ".content code{font-family:Consolas,'Cascadia Mono',monospace;font-size:.92em;background:#2a2a2d!important;color:#d7e3f0!important;border-radius:0;padding:.13rem .34rem;}" +
                ".content pre{margin:1rem 0 1.25rem;padding:1rem;background:#242426!important;color:#d7e3f0!important;border-radius:0;overflow:auto;white-space:pre-wrap;word-wrap:break-word;}" +
                ".content pre code{display:block;padding:0;background:transparent!important;color:inherit!important;border:0;}" +
                ".content table{border-collapse:collapse;width:100%;margin:1rem 0 1.25rem;display:table}.content th,.content td{border:1px solid #34383f;padding:.45rem .65rem}.content th{background:#242426!important;color:#dce8f7!important;font-weight:700}.content td{background:#1b1b1e!important}" +
                ".nv-red{color:#ff7d7d!important}.nv-orange{color:#ffab70!important}.nv-gold{color:#d8b76a!important}.nv-green{color:#7ee787!important}.nv-cyan{color:#76e3ea!important}.nv-blue{color:#79c0ff!important}.nv-purple{color:#d2a8ff!important}.nv-pink{color:#ff9bd2!important}.nv-muted{color:#8b949e!important}" +
                ".nv-underline{display:inline;border-bottom:.08em solid currentColor!important;text-decoration:none!important;padding-bottom:.04em}.nv-dotted{display:inline;border-bottom:.1em dotted currentColor!important;text-decoration:none!important;padding-bottom:.04em}.nv-wavy{display:inline;border-bottom:.1em solid currentColor!important;text-decoration:none!important;padding-bottom:.04em}" +
                ".nv-mark,.nv-mark-gold{background:rgba(216,183,106,.20)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}.nv-mark-red{background:rgba(255,125,125,.18)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}.nv-mark-blue{background:rgba(143,183,255,.18)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}.nv-mark-green{background:rgba(130,217,130,.16)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}" +
                ".nv-key{border:0;border-bottom:2px solid currentColor;background:transparent!important;color:#d8b76a!important;padding:.02em .18em;font-weight:600}.nv-spoiler{background:currentColor!important;color:#c9d1d9!important;border-radius:.18em;padding:.05em .22em}.nv-spoiler:hover{background:rgba(255,255,255,.08)!important;color:#c9d1d9!important}";

            return "<!doctype html><html data-mode=\"dark\"><head><meta charset=\"utf-8\"><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><style>" +
                   css +
                   baseCss +
                   "</style></head><body><article class=\"content\"><h1>" + title + "</h1>" +
                   RenderMarkdown(body, mediaSubpath) +
                   "</article></body></html>";
        }

        private string RenderMarkdown(string body, string mediaSubpath)
        {
            var output = new StringBuilder();
            var paragraph = new StringBuilder();
            bool inCode = false;
            bool inList = false;
            bool inOrderedList = false;
            var tableRows = new List<string>();
            var quoteLines = new List<string>();

            Action flushParagraph = delegate
            {
                if (paragraph.Length > 0)
                {
                    output.Append("<p>").Append(RenderInline(paragraph.ToString().Trim(), mediaSubpath)).AppendLine("</p>");
                    paragraph.Length = 0;
                }
            };

            Action closeList = delegate
            {
                if (inList)
                {
                    output.AppendLine("</ul>");
                    inList = false;
                }
                if (inOrderedList)
                {
                    output.AppendLine("</ol>");
                    inOrderedList = false;
                }
            };

            Action flushQuote = delegate
            {
                if (quoteLines.Count == 0)
                {
                    return;
                }

                output.AppendLine("<blockquote>");
                foreach (string quoteLine in quoteLines)
                {
                    output.Append("<p>").Append(RenderInline(quoteLine.Trim(), mediaSubpath)).AppendLine("</p>");
                }
                output.AppendLine("</blockquote>");
                quoteLines.Clear();
            };

            Action flushTable = delegate
            {
                if (tableRows.Count == 0)
                {
                    return;
                }

                output.AppendLine("<table>");
                bool headerWritten = false;
                bool bodyOpen = false;
                foreach (string tableLine in tableRows)
                {
                    string[] cells = SplitTableCells(tableLine);
                    if (cells.Length == 0 || IsTableSeparator(cells))
                    {
                        continue;
                    }

                    if (!headerWritten)
                    {
                        output.AppendLine("<thead><tr>");
                        foreach (string cell in cells)
                        {
                            output.Append("<th>").Append(RenderInline(cell.Trim(), mediaSubpath)).AppendLine("</th>");
                        }
                        output.AppendLine("</tr></thead>");
                        headerWritten = true;
                    }
                    else
                    {
                        if (!bodyOpen)
                        {
                            output.AppendLine("<tbody>");
                            bodyOpen = true;
                        }
                        output.AppendLine("<tr>");
                        foreach (string cell in cells)
                        {
                            output.Append("<td>").Append(RenderInline(cell.Trim(), mediaSubpath)).AppendLine("</td>");
                        }
                        output.AppendLine("</tr>");
                    }
                }

                if (bodyOpen)
                {
                    output.AppendLine("</tbody>");
                }
                output.AppendLine("</table>");
                tableRows.Clear();
            };

            foreach (string raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```"))
                {
                    flushParagraph();
                    closeList();
                    flushTable();
                    flushQuote();
                    if (!inCode)
                    {
                        output.AppendLine("<pre><code>");
                        inCode = true;
                    }
                    else
                    {
                        output.AppendLine("</code></pre>");
                        inCode = false;
                    }
                    continue;
                }

                if (inCode)
                {
                    output.AppendLine(WebUtility.HtmlEncode(line));
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    flushParagraph();
                    closeList();
                    flushTable();
                    flushQuote();
                    continue;
                }

                if (IsTableLine(trimmed))
                {
                    flushParagraph();
                    closeList();
                    flushQuote();
                    tableRows.Add(trimmed);
                    continue;
                }

                flushTable();
                flushQuote();

                if (trimmed == "---" || trimmed == "***")
                {
                    flushParagraph();
                    closeList();
                    flushQuote();
                    output.AppendLine("<hr>");
                    continue;
                }

                Match heading = Regex.Match(trimmed, "^(#{1,6})\\s+(.+)$");
                if (heading.Success)
                {
                    flushParagraph();
                    closeList();
                    flushQuote();
                    int level = heading.Groups[1].Value.Length;
                    output.Append("<h").Append(level).Append(">")
                        .Append(RenderInline(heading.Groups[2].Value, mediaSubpath))
                        .Append("</h").Append(level).AppendLine(">");
                    continue;
                }

                if (trimmed.StartsWith(">"))
                {
                    flushParagraph();
                    closeList();
                    quoteLines.Add(trimmed.TrimStart('>', ' '));
                    continue;
                }

                Match bullet = Regex.Match(trimmed, "^[-*+]\\s+(.+)$");
                if (bullet.Success)
                {
                    flushParagraph();
                    flushQuote();
                    if (inOrderedList)
                    {
                        output.AppendLine("</ol>");
                        inOrderedList = false;
                    }
                    if (!inList)
                    {
                        output.AppendLine("<ul>");
                        inList = true;
                    }
                    output.Append("<li>").Append(RenderInline(bullet.Groups[1].Value, mediaSubpath)).AppendLine("</li>");
                    continue;
                }

                Match ordered = Regex.Match(trimmed, "^\\d+\\.\\s+(.+)$");
                if (ordered.Success)
                {
                    flushParagraph();
                    flushQuote();
                    if (inList)
                    {
                        output.AppendLine("</ul>");
                        inList = false;
                    }
                    if (!inOrderedList)
                    {
                        output.AppendLine("<ol>");
                        inOrderedList = true;
                    }
                    output.Append("<li>").Append(RenderInline(ordered.Groups[1].Value, mediaSubpath)).AppendLine("</li>");
                    continue;
                }

                if (paragraph.Length > 0)
                {
                    paragraph.Append(" ");
                }
                paragraph.Append(trimmed);
            }

            flushParagraph();
            closeList();
            flushTable();
            flushQuote();
            if (inCode)
            {
                output.AppendLine("</code></pre>");
            }

            return output.ToString();
        }

        private static bool IsTableLine(string line)
        {
            return line.StartsWith("|") && line.EndsWith("|") && line.Count(c => c == '|') >= 2;
        }

        private static string[] SplitTableCells(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("|"))
            {
                trimmed = trimmed.Substring(1);
            }
            if (trimmed.EndsWith("|"))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed.Split('|');
        }

        private static bool IsTableSeparator(string[] cells)
        {
            if (cells.Length == 0)
            {
                return false;
            }

            foreach (string cell in cells)
            {
                string value = cell.Trim();
                if (!Regex.IsMatch(value, "^:?-{3,}:?$"))
                {
                    return false;
                }
            }

            return true;
        }

        private string RenderInline(string text, string mediaSubpath)
        {
            string result = WebUtility.HtmlEncode(text);

            result = Regex.Replace(result, "!\\[([^\\]]*)\\]\\(([^\\)]+)\\)", delegate(Match m)
            {
                string alt = m.Groups[1].Value;
                string src = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                string resolved = ResolveImageSource(src, mediaSubpath);
                return "<img src=\"" + WebUtility.HtmlEncode(resolved) + "\" alt=\"" + alt + "\">";
            });

            result = Regex.Replace(result, "\\[([^\\]]+)\\]\\(([^\\)]+)\\)", "<a href=\"$2\">$1</a>");
            result = Regex.Replace(result, "\\*\\*([^*]+)\\*\\*", "<strong>$1</strong>");
            result = Regex.Replace(result, "~~([^~]+)~~", "<del>$1</del>");
            result = Regex.Replace(result, "`([^`]+)`", "<code>$1</code>");

            result = WebUtility.HtmlDecode(result);
            return result;
        }

        private string ResolveImageSource(string src, string mediaSubpath)
        {
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return src;
            }

            string local;
            if (src.StartsWith("/"))
            {
                local = Path.Combine(repoRoot, src.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            }
            else if (!string.IsNullOrWhiteSpace(mediaSubpath))
            {
                local = Path.Combine(repoRoot, mediaSubpath.Trim('/').Replace('/', Path.DirectorySeparatorChar), src.Replace('/', Path.DirectorySeparatorChar));
            }
            else if (!string.IsNullOrEmpty(currentFile))
            {
                local = Path.Combine(Path.GetDirectoryName(currentFile), src.Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                local = Path.Combine(repoRoot, src.Replace('/', Path.DirectorySeparatorChar));
            }

            return new Uri(local).AbsoluteUri;
        }

        private static bool HasFrontMatter(string text)
        {
            return text.StartsWith("---");
        }

        private static int FindFrontMatterClose(string text)
        {
            if (!text.StartsWith("---"))
            {
                return -1;
            }

            int firstLine = text.IndexOf('\n');
            if (firstLine < 0)
            {
                return -1;
            }

            Match close = Regex.Match(text.Substring(firstLine + 1), "(?m)^---\\s*$");
            if (!close.Success)
            {
                return -1;
            }

            return firstLine + 1 + close.Index;
        }

        private static string StripFrontMatter(string text)
        {
            int close = FindFrontMatterClose(text);
            if (close < 0)
            {
                return text;
            }

            int after = text.IndexOf('\n', close);
            return after >= 0 ? text.Substring(after + 1) : "";
        }

        private static string GetFrontMatterValue(string text, string key)
        {
            if (!text.StartsWith("---"))
            {
                return "";
            }

            int close = FindFrontMatterClose(text);
            if (close < 0)
            {
                return "";
            }

            string front = text.Substring(0, close);
            Match match = Regex.Match(front, "(?m)^" + Regex.Escape(key) + ":\\s*(.+?)\\s*$");
            if (!match.Success)
            {
                return "";
            }

            string value = match.Groups[1].Value.Trim();
            if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '-');
            }
            return Regex.Replace(name.Trim(), "\\s+", "-");
        }

        private static string SanitizeFolderSegment(string name)
        {
            string cleaned = SanitizeFileName(name).ToLowerInvariant();
            cleaned = Regex.Replace(cleaned, "-+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(cleaned) ? "post" : cleaned;
        }

        private static bool IsImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".bmp";
        }

        private static string UniqueFileName(string dir, string name)
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            string candidate = baseName + ext;
            int index = 2;
            while (File.Exists(Path.Combine(dir, candidate)))
            {
                candidate = baseName + "-" + index + ext;
                index++;
            }

            return candidate;
        }

        private void SetStatus(string message)
        {
            status.Text = message;
        }
    }
}
