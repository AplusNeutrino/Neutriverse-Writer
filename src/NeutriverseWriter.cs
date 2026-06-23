using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace NeutriverseWriter
{
    public class MainForm : Form
    {
        private static readonly Color Surface = Color.FromArgb(18, 20, 25);
        private static readonly Color SurfaceRaised = Color.FromArgb(27, 29, 36);
        private static readonly Color SurfaceSoft = Color.FromArgb(36, 39, 48);
        private static readonly Color Border = Color.FromArgb(58, 64, 78);
        private static readonly Color TextPrimary = Color.FromArgb(221, 232, 247);
        private static readonly Color TextMuted = Color.FromArgb(154, 164, 178);
        private static readonly Color LogoBlue = Color.FromArgb(10, 72, 255);
        private static readonly Color LogoGold = Color.FromArgb(216, 183, 106);
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x04DD;
        private const int EM_SETSCROLLPOS = 0x04DE;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        private readonly string repoRoot;
        private readonly string postsDir;
        private readonly string imageRoot;
        private readonly SplitContainer split;
        private readonly WebBrowser preview;
        private readonly RichTextBox editor;
        private readonly StatusStrip statusStrip;
        private readonly ToolStripStatusLabel status;
        private readonly ToolStripStatusLabel documentStatus;
        private readonly ToolStripStatusLabel previewStatus;
        private readonly ToolStripStatusLabel caretStatus;
        private readonly ToolStripStatusLabel countStatus;
        private readonly Label previewHeader;
        private readonly Label sourceHeader;
        private readonly Timer previewTimer;
        private readonly Timer highlightTimer;
        private readonly Timer scrollSyncTimer;
        private ToolStripButton eyeButton;
        private ToolStripButton previewModeButton;
        private bool applyingHighlight;
        private bool comfortMode;
        private bool livePreview = true;
        private bool previewDirty;
        private bool restorePreviewScroll;
        private bool syncingScroll;
        private DateTime ignoreScrollSyncUntil = DateTime.MinValue;
        private Point pendingPreviewScroll;
        private int lastPreviewScrollTop = -1;
        private string currentFile;
        private string savedTextSnapshot = "";

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

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

        private void TryLoadAppIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
                if (File.Exists(iconPath))
                {
                    Icon = new Icon(iconPath);
                }
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
            MinimumSize = new Size(980, 620);
            BackColor = Surface;
            TryLoadAppIcon();

            var toolbar = BuildToolbar();
            Controls.Add(toolbar);

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.BackColor = Border;
            split.SplitterWidth = 3;
            split.Panel1.BackColor = Surface;
            split.Panel2.BackColor = GetEditorBackColor();
            split.Panel2.Padding = new Padding(14, 0, 0, 0);

            preview = new WebBrowser();
            preview.Dock = DockStyle.Fill;
            preview.AllowWebBrowserDrop = false;
            preview.ScriptErrorsSuppressed = true;
            preview.DocumentCompleted += delegate
            {
                if (restorePreviewScroll && preview.Document != null && preview.Document.Window != null)
                {
                    restorePreviewScroll = false;
                    preview.Document.Window.ScrollTo(pendingPreviewScroll.X, pendingPreviewScroll.Y);
                }
            };
            previewHeader = CreatePaneHeader("PREVIEW", "Live");
            var previewPanel = CreatePanePanel(previewHeader, preview);
            split.Panel1.Controls.Add(previewPanel);

            editor = new RichTextBox();
            editor.Dock = DockStyle.Fill;
            editor.ScrollBars = RichTextBoxScrollBars.Vertical;
            editor.AcceptsTab = true;
            editor.WordWrap = true;
            editor.BorderStyle = BorderStyle.None;
            editor.BackColor = GetEditorBackColor();
            editor.ForeColor = GetEditorTextColor();
            editor.SelectionColor = editor.ForeColor;
            editor.Font = new Font("Consolas", 11f);
            editor.HideSelection = false;
            editor.AllowDrop = true;
            editor.VScroll += delegate { SyncPreviewToEditorTopLine(); };
            editor.MouseWheel += delegate { BeginInvoke(new MethodInvoker(SyncPreviewToEditorTopLine)); };
            editor.TextChanged += delegate
            {
                if (!applyingHighlight)
                {
                    if (livePreview)
                    {
                        QueuePreview();
                    }
                    else
                    {
                        previewTimer.Stop();
                        previewDirty = true;
                        SetStatus("Manual preview mode: click Refresh to update preview.");
                    }
                    QueueHighlight();
                    UpdateWindowTitle();
                    UpdateUiState();
                }
            };
            editor.SelectionChanged += delegate { UpdateUiState(); };
            editor.DragEnter += EditorDragEnter;
            editor.DragDrop += EditorDragDrop;
            sourceHeader = CreatePaneHeader("SOURCE", "Untitled");
            var editorPanel = CreatePanePanel(sourceHeader, editor);
            split.Panel2.Controls.Add(editorPanel);

            Controls.Add(split);
            split.BringToFront();

            statusStrip = new StatusStrip();
            statusStrip.BackColor = SurfaceRaised;
            statusStrip.ForeColor = TextMuted;
            statusStrip.Renderer = new NeutriverseToolStripRenderer();
            statusStrip.SizingGrip = false;
            status = new ToolStripStatusLabel();
            status.ForeColor = TextMuted;
            status.Font = new Font("Segoe UI", 9f);
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            documentStatus = CreateStatusLabel();
            previewStatus = CreateStatusLabel();
            caretStatus = CreateStatusLabel();
            countStatus = CreateStatusLabel();
            documentStatus.ToolTipText = "Document save state";
            previewStatus.ToolTipText = "Preview mode and refresh state";
            caretStatus.ToolTipText = "Current editor line, column, and selection";
            countStatus.ToolTipText = "Document length summary";
            statusStrip.Items.Add(status);
            statusStrip.Items.Add(documentStatus);
            statusStrip.Items.Add(previewStatus);
            statusStrip.Items.Add(caretStatus);
            statusStrip.Items.Add(countStatus);
            Controls.Add(statusStrip);
            FormClosing += MainForm_FormClosing;

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

            scrollSyncTimer = new Timer();
            scrollSyncTimer.Interval = 250;
            scrollSyncTimer.Tick += delegate { PollPreviewScrollSync(); };
            scrollSyncTimer.Start();

            Shown += delegate
            {
                split.Panel1MinSize = 320;
                split.Panel2MinSize = 360;
                split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Width / 2);
            };

            NewDraft();
        }

        private Label CreatePaneHeader(string title, string detail)
        {
            var label = new Label();
            label.Dock = DockStyle.Top;
            label.Height = 30;
            label.Padding = new Padding(12, 0, 12, 0);
            label.BackColor = SurfaceRaised;
            label.ForeColor = TextMuted;
            label.Font = new Font("Segoe UI", 8.75f, FontStyle.Bold);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Text = FormatPaneHeader(title, detail);
            return label;
        }

        private Panel CreatePanePanel(Label header, Control content)
        {
            var panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Surface;
            panel.Padding = new Padding(0);
            content.Dock = DockStyle.Fill;
            panel.Controls.Add(content);
            panel.Controls.Add(header);
            return panel;
        }

        private ToolStripStatusLabel CreateStatusLabel()
        {
            var label = new ToolStripStatusLabel();
            label.ForeColor = TextMuted;
            label.Font = new Font("Segoe UI", 9f);
            label.BorderSides = ToolStripStatusLabelBorderSides.Left;
            label.BorderStyle = Border3DStyle.Flat;
            label.Padding = new Padding(10, 0, 8, 0);
            return label;
        }

        private static string FormatPaneHeader(string title, string detail)
        {
            return string.IsNullOrWhiteSpace(detail) ? title : title + "  /  " + detail;
        }

        private void UpdateUiState()
        {
            if (previewHeader != null)
            {
                previewHeader.Text = FormatPaneHeader("PREVIEW", (livePreview ? "Live" : "Manual") + (previewDirty ? " | Stale" : "") + (comfortMode ? " | Eye" : ""));
                previewHeader.ForeColor = previewDirty ? LogoGold : TextMuted;
            }

            if (sourceHeader != null)
            {
                string name = string.IsNullOrWhiteSpace(currentFile) ? "Untitled" : Path.GetFileName(currentFile);
                sourceHeader.Text = FormatPaneHeader("SOURCE", name + (HasUnsavedChanges() ? " | Unsaved" : " | Saved"));
                sourceHeader.ForeColor = HasUnsavedChanges() ? LogoGold : TextMuted;
            }

            if (documentStatus != null)
            {
                documentStatus.Text = HasUnsavedChanges() ? "Unsaved" : "Saved";
                documentStatus.ForeColor = HasUnsavedChanges() ? LogoGold : TextMuted;
            }

            if (previewStatus != null)
            {
                previewStatus.Text = livePreview ? "Live preview" : (previewDirty ? "Manual preview | stale" : "Manual preview");
                previewStatus.ForeColor = previewDirty ? LogoGold : TextMuted;
            }

            if (countStatus != null && editor != null)
            {
                int lineCount = editor.Lines.Length;
                int wordCount = CountReadableWords(editor.Text);
                int charCount = editor.Text.Count(c => !char.IsWhiteSpace(c));
                countStatus.Text = lineCount + " lines | " + wordCount + " words | " + charCount + " chars";
            }

            if (caretStatus != null && editor != null)
            {
                int safeSelectionStart = Math.Min(editor.SelectionStart, editor.TextLength);
                int lineIndex = editor.GetLineFromCharIndex(safeSelectionStart);
                int lineStart = editor.GetFirstCharIndexFromLine(lineIndex);
                int column = lineStart >= 0 ? safeSelectionStart - lineStart + 1 : 1;
                string selection = editor.SelectionLength > 0 ? " | Sel " + editor.SelectionLength : "";
                caretStatus.Text = "Ln " + (lineIndex + 1) + ", Col " + column + selection;
            }
        }

        private static int CountReadableWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            int latinWords = Regex.Matches(text, @"[A-Za-z0-9]+(?:[-'][A-Za-z0-9]+)*").Count;
            int cjkCharacters = Regex.Matches(text, @"[\u3400-\u9FFF]").Count;
            return latinWords + cjkCharacters;
        }

        private ToolStrip BuildToolbar()
        {
            var toolbar = new ToolStrip();
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Dock = DockStyle.Top;
            toolbar.AutoSize = false;
            toolbar.Height = 42;
            toolbar.Padding = new Padding(8, 5, 8, 5);
            toolbar.BackColor = SurfaceRaised;
            toolbar.ForeColor = TextPrimary;
            toolbar.Font = new Font("Segoe UI", 9.5f);
            toolbar.RenderMode = ToolStripRenderMode.Professional;
            toolbar.Renderer = new NeutriverseToolStripRenderer();
            toolbar.CanOverflow = true;
            toolbar.ShowItemToolTips = true;

            AddDropdown(toolbar, "File", new Dictionary<string, EventHandler>
            {
                { "New Draft", delegate { NewDraft(); } },
                { "Open Post", delegate { OpenPost(); } },
                { "Save", delegate { SavePost(false); } },
                { "Save As...", delegate { SavePost(true); } }
            }, "File actions. Shortcuts: Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Shift+S");
            toolbar.Items.Add(new ToolStripSeparator());
            AddButton(toolbar, "Insert Image", delegate { InsertImagesFromDialog(); }, "Copy image files into the post media folder and insert Markdown image links.");
            AddButton(toolbar, "Inline Image", delegate { InsertInlineImageFromDialog(); }, "Copy an image into the post media folder and insert a right-aligned inline illustration include. Shortcut: Ctrl+Shift+I");
            AddButton(toolbar, "Refresh", delegate { RenderPreview(); }, "Refresh the preview now. Shortcut: F5");
            AddButton(toolbar, "Publish", delegate { PublishSiteToGitHub(); }, "Commit and push the target blog repository to GitHub.");
            AddButton(toolbar, "Guide", delegate { OpenRoamGuidePanel(); }, "Quick edit Neutriverse roam guide info, orbit, and stack data.");
            previewModeButton = AddToggleButton(toolbar, "Live", delegate { TogglePreviewMode(); });
            previewModeButton.ToolTipText = "Toggle live preview. Shortcut: Ctrl+L";
            previewModeButton.Checked = true;
            eyeButton = AddToggleButton(toolbar, "Eye", delegate { ToggleComfortMode(); });
            eyeButton.ToolTipText = "Toggle eye comfort mode. Shortcut: Ctrl+E";
            toolbar.Items.Add(new ToolStripSeparator());

            AddDropdown(toolbar, "Heading", new Dictionary<string, EventHandler>
            {
                { "H1", delegate { PrefixLines("# "); } },
                { "H2", delegate { PrefixLines("## "); } },
                { "H3", delegate { PrefixLines("### "); } }
            }, "Heading levels");
            AddButton(toolbar, "Quote", delegate { PrefixLines("> "); }, "Turn selected lines into a quote block.");
            AddDropdown(toolbar, "List", new Dictionary<string, EventHandler>
            {
                { "Unordered", delegate { PrefixLines("- "); } },
                { "Ordered", delegate { NumberLines(); } }
            }, "List formatting");
            AddDropdown(toolbar, "Table", new Dictionary<string, EventHandler>
            {
                { "2 columns", delegate { InsertTable(2, 2); } },
                { "3 columns", delegate { InsertTable(3, 3); } },
                { "4 columns", delegate { InsertTable(4, 3); } }
            }, "Insert Markdown table");
            AddDropdown(toolbar, "Code", new Dictionary<string, EventHandler>
            {
                { "Inline Code", delegate { WrapSelection("`", "`"); } },
                { "Code Block", delegate { WrapSelection("```" + Environment.NewLine, Environment.NewLine + "```"); } },
                { "Text Block...", delegate { OpenTextBlockPanel(); } },
                { "Text Block - Default Hover", delegate { InsertConfiguredTextBlock(TextBlockOptions.Default(editor.SelectedText)); } },
                { "Text Block - Plain", delegate { WrapTextBlock("nv-text-block", "#18191b"); } },
                { "Text Block - Center", delegate { WrapTextBlock("nv-text-block nv-align-center", "#18191b"); } },
                { "Text Block - Right", delegate { WrapTextBlock("nv-text-block nv-align-right", "#18191b"); } }
            }, "Code formatting");
            toolbar.Items.Add(new ToolStripSeparator());

            AddButton(toolbar, "B", delegate { WrapSelection("**", "**"); }, "Bold");
            AddButton(toolbar, "I", delegate { WrapSelection("*", "*"); }, "Italic");
            AddButton(toolbar, "U", delegate { WrapSelection("<u>", "</u>"); }, "Underline");
            AddButton(toolbar, "S", delegate { WrapSelection("~~", "~~"); }, "Strikethrough");
            toolbar.Items.Add(new ToolStripSeparator());

            AddDropdown(toolbar, "Color", new Dictionary<string, EventHandler>
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
            }, "Neutriverse text colors");

            AddDropdown(toolbar, "Underline", new Dictionary<string, EventHandler>
            {
                { "Plain", delegate { WrapSpan("nv-underline"); } },
                { "Wavy", delegate { WrapSpan("nv-wavy"); } },
                { "Dotted", delegate { WrapSpan("nv-dotted"); } },
                { "Red Plain", delegate { WrapSpan("nv-red nv-underline"); } },
                { "Blue Wavy", delegate { WrapSpan("nv-blue nv-wavy"); } },
                { "Gold Dotted", delegate { WrapSpan("nv-gold nv-dotted"); } }
            }, "Underline styles");

            AddDropdown(toolbar, "Mark", new Dictionary<string, EventHandler>
            {
                { "Gold Mark", delegate { WrapSpan("nv-mark"); } },
                { "Red Mark", delegate { WrapSpan("nv-mark-red"); } },
                { "Blue Mark", delegate { WrapSpan("nv-mark-blue"); } },
                { "Green Mark", delegate { WrapSpan("nv-mark-green"); } },
                { "Key", delegate { WrapSpan("nv-key"); } },
                { "Spoiler", delegate { WrapSpan("nv-spoiler"); } }
            }, "Neutriverse marks and semantic inline helpers");

            AddDropdown(toolbar, "HTML", new Dictionary<string, EventHandler>
            {
                { "Superscript", delegate { WrapSelection("<sup>", "</sup>"); } },
                { "Subscript", delegate { WrapSelection("<sub>", "</sub>"); } },
                { "Small", delegate { WrapSelection("<small>", "</small>"); } },
                { "Keyboard", delegate { WrapSelection("<kbd>", "</kbd>"); } },
                { "Break", delegate { InsertAtCursor("<br>"); } },
                { "Horizontal Rule", delegate { InsertAtCursor(Environment.NewLine + "---" + Environment.NewLine); } }
            }, "Common inline HTML helpers");

            return toolbar;
        }

        private ToolStripButton AddButton(ToolStrip toolbar, string text, EventHandler click)
        {
            return AddButton(toolbar, text, click, text);
        }

        private ToolStripButton AddButton(ToolStrip toolbar, string text, EventHandler click, string tooltip)
        {
            var button = new ToolStripButton(text);
            StyleToolButton(button, toolbar, text);
            button.ToolTipText = tooltip;
            button.Click += click;
            toolbar.Items.Add(button);
            return button;
        }

        private ToolStripButton AddToggleButton(ToolStrip toolbar, string text, EventHandler click)
        {
            var button = new ToolStripButton(text);
            StyleToolButton(button, toolbar, text);
            button.CheckOnClick = false;
            button.Click += click;
            toolbar.Items.Add(button);
            return button;
        }

        private void StyleToolButton(ToolStripButton button, ToolStrip toolbar, string text)
        {
            button.AutoSize = false;
            button.Width = Math.Max(34, TextRenderer.MeasureText(text, toolbar.Font).Width + 20);
            button.Height = 30;
            button.Padding = new Padding(8, 0, 8, 0);
            button.Margin = new Padding(1, 0, 1, 0);
            button.DisplayStyle = ToolStripItemDisplayStyle.Text;
            button.ForeColor = TextPrimary;
        }

        private void AddDropdown(ToolStrip toolbar, string text, Dictionary<string, EventHandler> items)
        {
            AddDropdown(toolbar, text, items, text);
        }

        private void AddDropdown(ToolStrip toolbar, string text, Dictionary<string, EventHandler> items, string tooltip)
        {
            var dropdown = new ToolStripDropDownButton(text);
            dropdown.AutoSize = false;
            dropdown.Width = Math.Max(44, TextRenderer.MeasureText(text, toolbar.Font).Width + 26);
            dropdown.Height = 30;
            dropdown.Padding = new Padding(8, 0, 8, 0);
            dropdown.Margin = new Padding(1, 0, 1, 0);
            dropdown.DisplayStyle = ToolStripItemDisplayStyle.Text;
            dropdown.ForeColor = TextPrimary;
            dropdown.ToolTipText = tooltip;
            dropdown.DropDown.BackColor = SurfaceRaised;
            dropdown.DropDown.ForeColor = TextPrimary;
            dropdown.DropDown.Padding = new Padding(4);
            dropdown.DropDown.Renderer = new NeutriverseToolStripRenderer();
            foreach (var item in items)
            {
                var menuItem = new ToolStripMenuItem(item.Key);
                menuItem.BackColor = SurfaceRaised;
                menuItem.ForeColor = TextPrimary;
                menuItem.Font = new Font("Segoe UI", 9.5f);
                menuItem.Click += item.Value;
                dropdown.DropDownItems.Add(menuItem);
            }
            toolbar.Items.Add(dropdown);
        }

        private void NewDraft()
        {
            if (!ConfirmSaveChanges())
            {
                return;
            }

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
            savedTextSnapshot = editor.Text;
            UpdateWindowTitle();
            SetStatus("New draft created.");
            RenderPreview();
        }

        private void OpenPost()
        {
            if (!ConfirmSaveChanges())
            {
                return;
            }

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
                savedTextSnapshot = editor.Text;
                UpdateWindowTitle();
                SetStatus("Opened " + currentFile);
                RenderPreview();
            }
        }

        private bool SavePost(bool saveAs)
        {
            string text = editor.Text;
            if (!HasFrontMatter(text))
            {
                MessageBox.Show(this, "The post needs YAML front matter.", "Neutriverse Writer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
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

                using (var dialog = new SaveFileDialog())
                {
                    dialog.InitialDirectory = postsDir;
                    dialog.Filter = "Markdown posts (*.md)|*.md|All files (*.*)|*.*";
                    dialog.FileName = date + "-" + SanitizePostSlug(title) + ".md";
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        return false;
                    }

                    currentFile = dialog.FileName;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(currentFile));
            File.WriteAllText(currentFile, editor.Text, new UTF8Encoding(false));
            savedTextSnapshot = editor.Text;
            UpdateWindowTitle();
            SetStatus("Saved " + currentFile);
            return true;
        }

        private void PublishSiteToGitHub()
        {
            if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                MessageBox.Show(this, "The target blog folder is not a git repository:" + Environment.NewLine + repoRoot, "Neutriverse Writer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!EnsureCurrentPostIsInBlogRepository())
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(currentFile) || !File.Exists(currentFile))
            {
                MessageBox.Show(this, "Save the post before publishing.", "Neutriverse Writer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            GitResult statusResult = RunGit("status --short", repoRoot, 30000);
            if (!statusResult.Success)
            {
                ShowGitOutput("Could not read blog repository status.", statusResult);
                return;
            }

            string statusText = statusResult.Output.Trim();
            if (string.IsNullOrWhiteSpace(statusText))
            {
                DialogResult pushOnly = MessageBox.Show(this, "No local changes were found in the blog repository. Push existing commits anyway?", "Publish to GitHub", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (pushOnly != DialogResult.Yes)
                {
                    return;
                }

                RunPublishPushOnly();
                return;
            }

            DialogResult scope = MessageBox.Show(
                this,
                "Blog repository changes:" + Environment.NewLine + Environment.NewLine +
                statusText + Environment.NewLine + Environment.NewLine +
                "Yes: publish all listed changes." + Environment.NewLine +
                "No: publish only the current post and its media folder." + Environment.NewLine +
                "Cancel: stop.",
                "Publish to GitHub",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (scope == DialogResult.Cancel)
            {
                return;
            }

            string defaultMessage = "Publish " + Path.GetFileNameWithoutExtension(currentFile);
            string commitMessage = PromptForText("Commit message", "Commit message for AplusNeutrino/My_Blog:", defaultMessage);
            if (string.IsNullOrWhiteSpace(commitMessage))
            {
                return;
            }

            bool includeAll = scope == DialogResult.Yes;
            using (var progress = new PublishProgressForm("Publishing Blog Post"))
            {
                progress.Show(this);
                Cursor previousCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                UseWaitCursor = true;
                try
                {
                    SetStatus("Publishing blog repository...");

                    GitResult addResult = includeAll ? RunGitStep(progress, "Stage all blog changes", "add -A", repoRoot, 60000) : StageCurrentPostFiles(progress);
                    if (!addResult.Success)
                    {
                        ShowGitOutput("Failed to stage files.", addResult);
                        return;
                    }

                    GitResult cachedCheck = RunGitStep(progress, "Check staged diff", "diff --cached --check", repoRoot, 60000);
                    if (!cachedCheck.Success)
                    {
                        ShowGitOutput("git diff --cached --check failed. Fix whitespace/errors before publishing.", cachedCheck);
                        return;
                    }

                    GitResult cachedDiff = RunGitStep(progress, "Confirm staged changes", "diff --cached --quiet", repoRoot, 60000);
                    if (cachedDiff.ExitCode == 0)
                    {
                        if (HasLocalCommitsToPush(progress))
                        {
                            progress.AppendLog("No new staged post diff, but local commits are waiting to be pushed.");
                            if (!RunFetchRebasePush(progress))
                            {
                                return;
                            }
                            progress.MarkComplete("Pushed existing local commits.");
                            MessageBox.Show(this, "Pushed existing local commits to GitHub.", "Publish to GitHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            SetStatus("Pushed existing local commits.");
                            return;
                        }

                        MessageBox.Show(this, "No staged changes were found for publishing.", "Publish to GitHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    GitResult commitResult = RunGitStep(progress, "Commit blog update", "commit -m " + QuoteArg(commitMessage), repoRoot, 120000);
                    if (!commitResult.Success)
                    {
                        ShowGitOutput("Commit failed.", commitResult);
                        return;
                    }

                    if (!RunFetchRebasePush(progress))
                    {
                        return;
                    }

                    progress.MarkComplete("Published to GitHub.");
                    MessageBox.Show(this, "Published to GitHub." + Environment.NewLine + Environment.NewLine + commitResult.Output.Trim(), "Publish to GitHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus("Published to GitHub.");
                }
                finally
                {
                    UseWaitCursor = false;
                    Cursor.Current = previousCursor;
                }
            }
        }

        private void RunPublishPushOnly()
        {
            using (var progress = new PublishProgressForm("Pushing Blog Repository"))
            {
                progress.Show(this);
                Cursor previousCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                UseWaitCursor = true;
                try
                {
                    SetStatus("Pushing blog repository...");
                    if (RunFetchRebasePush(progress))
                    {
                        progress.MarkComplete("Pushed to GitHub.");
                        MessageBox.Show(this, "Pushed blog repository to GitHub.", "Publish to GitHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        SetStatus("Pushed to GitHub.");
                    }
                }
                finally
                {
                    UseWaitCursor = false;
                    Cursor.Current = previousCursor;
                }
            }
        }

        private bool RunFetchRebasePush()
        {
            return RunFetchRebasePush(null);
        }

        private bool RunFetchRebasePush(PublishProgressForm progress)
        {
            GitResult fetchResult = null;
            string[] fetchAttempts = new string[]
            {
                "-c http.version=HTTP/1.1 fetch origin main",
                "-c http.version=HTTP/1.1 -c http.lowSpeedLimit=0 fetch --prune origin main",
                "-c http.version=HTTP/1.1 fetch --no-tags origin main"
            };

            for (int attempt = 0; attempt < fetchAttempts.Length; attempt++)
            {
                fetchResult = RunGitStep(progress, "Fetch origin/main (" + (attempt + 1) + "/" + fetchAttempts.Length + ")", fetchAttempts[attempt], repoRoot, 120000);
                if (fetchResult.Success)
                {
                    break;
                }

                if (attempt + 1 < fetchAttempts.Length)
                {
                    if (progress != null)
                    {
                        progress.AppendLog("Fetch failed; retrying with a safer transport option.");
                    }
                    System.Threading.Thread.Sleep(1100);
                }
            }

            if (!fetchResult.Success)
            {
                ShowGitOutput("Fetch failed.", fetchResult);
                return false;
            }

            GitResult rebaseResult = RunGitStep(progress, "Rebase on origin/main", "rebase origin/main", repoRoot, 120000);
            if (!rebaseResult.Success)
            {
                ShowGitOutput("Rebase failed. Resolve conflicts in the blog repository before publishing again. No force push was attempted.", rebaseResult);
                return false;
            }

            GitResult pushResult = RunGitStep(progress, "Push origin main", "push origin main", repoRoot, 120000);
            if (!pushResult.Success)
            {
                ShowGitOutput("Push failed.", pushResult);
                return false;
            }

            return true;
        }

        private GitResult StageCurrentPostFiles()
        {
            return StageCurrentPostFiles(null);
        }

        private GitResult StageCurrentPostFiles(PublishProgressForm progress)
        {
            var paths = new List<string>();
            paths.Add(currentFile);

            string mediaSubpath = GetFrontMatterValue(editor.Text, "media_subpath");
            if (!string.IsNullOrWhiteSpace(mediaSubpath))
            {
                string mediaDir = Path.Combine(repoRoot, mediaSubpath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(mediaDir))
                {
                    paths.Add(mediaDir);
                }
            }

            var args = new StringBuilder("add --");
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                args.Append(" ").Append(QuoteArg(path));
            }

            return RunGitStep(progress, "Stage current post and media", args.ToString(), repoRoot, 60000);
        }

        private bool EnsureCurrentPostIsInBlogRepository()
        {
            if (string.IsNullOrWhiteSpace(currentFile))
            {
                return ImportCurrentPostToBlogRepository("This draft has not been saved into the blog repository.");
            }

            if (HasUnsavedChanges() && IsPathInsideDirectory(currentFile, repoRoot))
            {
                return SavePost(false);
            }

            if (IsPathInsideDirectory(currentFile, repoRoot))
            {
                return true;
            }

            return ImportCurrentPostToBlogRepository(
                "The current file is outside the blog repository:" + Environment.NewLine +
                currentFile + Environment.NewLine + Environment.NewLine +
                "Import it into _posts before publishing?");
        }

        private bool ImportCurrentPostToBlogRepository(string message)
        {
            DialogResult result = MessageBox.Show(
                this,
                message + Environment.NewLine + Environment.NewLine +
                "Writer will save a copy to the blog repository _posts folder and publish that copy.",
                "Import to blog repository",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                return false;
            }

            if (!HasFrontMatter(editor.Text))
            {
                MessageBox.Show(this, "The post needs YAML front matter before it can be imported.", "Import to blog repository", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string targetPath = BuildDefaultPostPath(editor.Text);
            if (File.Exists(targetPath))
            {
                using (var dialog = new SaveFileDialog())
                {
                    dialog.InitialDirectory = postsDir;
                    dialog.Filter = "Markdown posts (*.md)|*.md|All files (*.*)|*.*";
                    dialog.FileName = Path.GetFileName(targetPath);
                    dialog.Title = "Import post to blog repository";
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        return false;
                    }

                    targetPath = dialog.FileName;
                }
            }

            if (!IsPathInsideDirectory(targetPath, repoRoot))
            {
                MessageBox.Show(this, "The import target must be inside the blog repository.", "Import to blog repository", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            File.WriteAllText(targetPath, editor.Text, new UTF8Encoding(false));
            currentFile = targetPath;
            savedTextSnapshot = editor.Text;
            UpdateWindowTitle();
            SetStatus("Imported post to " + targetPath);
            return true;
        }

        private string BuildDefaultPostPath(string text)
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

            string fileName = date + "-" + SanitizePostSlug(title) + ".md";
            return Path.Combine(postsDir, fileName);
        }

        private static bool IsPathInsideDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void OpenRoamGuidePanel()
        {
            string aboutPath = GetRoamGuidePath();
            if (!File.Exists(aboutPath))
            {
                MessageBox.Show(this, "Could not find roam guide page:" + Environment.NewLine + aboutPath, "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RoamGuideData data;
            try
            {
                data = LoadRoamGuideData(aboutPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var form = new Form())
            {
                form.Text = "Neutriverse Roam Guide";
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimumSize = new Size(880, 640);
                form.ClientSize = new Size(980, 720);
                form.BackColor = Surface;
                form.ForeColor = TextPrimary;
                form.FormBorderStyle = FormBorderStyle.None;
                TrySetFormIcon(form);

                var chrome = new TableLayoutPanel();
                chrome.Dock = DockStyle.Fill;
                chrome.BackColor = Surface;
                chrome.Padding = new Padding(1);
                chrome.ColumnCount = 1;
                chrome.RowCount = 4;
                chrome.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
                chrome.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
                chrome.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                chrome.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
                form.Controls.Add(chrome);

                var header = CreateGuideHeader(form);
                chrome.Controls.Add(header, 0, 0);

                var nav = new FlowLayoutPanel();
                nav.Dock = DockStyle.Fill;
                nav.BackColor = SurfaceRaised;
                nav.Padding = new Padding(18, 9, 18, 8);
                nav.FlowDirection = FlowDirection.LeftToRight;
                nav.WrapContents = false;
                chrome.Controls.Add(nav, 0, 1);

                var contentHost = new Panel();
                contentHost.Dock = DockStyle.Fill;
                contentHost.BackColor = Surface;
                contentHost.Padding = new Padding(18, 16, 18, 16);
                chrome.Controls.Add(contentHost, 0, 2);

                var infoKicker = CreateGuideTextBox(data.InfoKicker, false);
                var infoText = CreateGuideTextBox(data.InfoText, true);
                var infoPage = CreateGuideInfoPage(infoKicker, infoText);

                var nowKicker = CreateGuideTextBox(data.NowKicker, false);
                var nowTitle = CreateGuideTextBox(data.NowTitle, false);
                var nowSummary = CreateGuideTextBox(data.NowSummary, true);
                var nowUpdated = CreateGuideTextBox(data.NowUpdated, false);
                var signalGrid = CreateGuideGrid("Label", "Value", data.SignalItems);
                var statusGrid = CreateGuideGrid("Label", "Value", data.StatusItems);
                var roadmapGrid = CreateGuideGrid("Step", "Text", data.RoadmapItems);
                var orbitPage = CreateGuideOrbitPage(nowKicker, nowTitle, nowSummary, nowUpdated, signalGrid, statusGrid, roadmapGrid);

                var readingGrid = CreateGuideGrid("Title", "Date", data.ReadingStackItems);
                var visualGrid = CreateGuideGrid("Title", "Date", data.VisualStackItems);
                var stackPage = CreateGuideStackPage(readingGrid, visualGrid);

                contentHost.Controls.Add(infoPage);
                contentHost.Controls.Add(orbitPage);
                contentHost.Controls.Add(stackPage);

                var infoButton = CreateGuideNavButton("Info");
                var orbitButton = CreateGuideNavButton("Orbit");
                var stackButton = CreateGuideNavButton("Stacks");
                nav.Controls.Add(infoButton);
                nav.Controls.Add(orbitButton);
                nav.Controls.Add(stackButton);

                Action<Panel, Button> showPage = delegate(Panel page, Button selected)
                {
                    infoPage.Visible = false;
                    orbitPage.Visible = false;
                    stackPage.Visible = false;
                    page.Visible = true;
                    StyleGuideNavButton(infoButton, selected == infoButton);
                    StyleGuideNavButton(orbitButton, selected == orbitButton);
                    StyleGuideNavButton(stackButton, selected == stackButton);
                    page.BringToFront();
                };

                infoButton.Click += delegate { showPage(infoPage, infoButton); };
                orbitButton.Click += delegate { showPage(orbitPage, orbitButton); };
                stackButton.Click += delegate { showPage(stackPage, stackButton); };
                showPage(infoPage, infoButton);

                Action refreshDataFromControls = delegate
                {
                    data.InfoKicker = infoKicker.Text.Trim();
                    data.InfoText = NormalizeMultilineText(infoText.Text);
                    data.NowKicker = nowKicker.Text.Trim();
                    data.NowTitle = nowTitle.Text.Trim();
                    data.NowSummary = NormalizeMultilineText(nowSummary.Text);
                    data.NowUpdated = nowUpdated.Text.Trim();
                    data.SignalItems = ReadGuideGrid(signalGrid);
                    data.StatusItems = ReadGuideGrid(statusGrid);
                    data.RoadmapItems = ReadGuideGrid(roadmapGrid);
                    data.ReadingStackItems = ReadGuideGrid(readingGrid);
                    data.VisualStackItems = ReadGuideGrid(visualGrid);
                };

                Action saveGuide = delegate
                {
                    refreshDataFromControls();
                    SaveRoamGuideData(aboutPath, data);
                    SetStatus("Saved roam guide page.");
                    MessageBox.Show(form, "Saved roam guide page:" + Environment.NewLine + aboutPath, "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };

                var saveButton = CreateGuideActionButton("Save", false);
                saveButton.Click += delegate { saveGuide(); };

                var savePublishButton = CreateGuideActionButton("Save && Publish", true);
                savePublishButton.Click += delegate
                {
                    refreshDataFromControls();
                    SaveRoamGuideData(aboutPath, data);
                    PublishRoamGuidePage(aboutPath);
                };

                var reloadButton = CreateGuideActionButton("Reload", false);
                reloadButton.Click += delegate
                {
                    form.DialogResult = DialogResult.Retry;
                    form.Close();
                };

                var closeButton = CreateGuideActionButton("Close", false);
                closeButton.Click += delegate { form.Close(); };

                var buttonPanel = new FlowLayoutPanel();
                buttonPanel.Dock = DockStyle.Fill;
                buttonPanel.FlowDirection = FlowDirection.RightToLeft;
                buttonPanel.Padding = new Padding(18, 10, 18, 10);
                buttonPanel.BackColor = SurfaceRaised;
                buttonPanel.Controls.Add(closeButton);
                buttonPanel.Controls.Add(savePublishButton);
                buttonPanel.Controls.Add(saveButton);
                buttonPanel.Controls.Add(reloadButton);
                chrome.Controls.Add(buttonPanel, 0, 3);

                DialogResult result = form.ShowDialog(this);
                if (result == DialogResult.Retry)
                {
                    OpenRoamGuidePanel();
                }
            }
        }

        private string GetRoamGuidePath()
        {
            return Path.Combine(repoRoot, "_tabs", "about.md");
        }

        private void TrySetFormIcon(Form form)
        {
            try
            {
                if (Icon != null)
                {
                    form.Icon = Icon;
                }
            }
            catch
            {
            }
        }

        private Panel CreateGuideHeader(Form form)
        {
            var header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Color.FromArgb(13, 15, 20);
            header.Padding = new Padding(18, 8, 12, 8);
            header.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
            };

            var title = new Label();
            title.AutoSize = false;
            title.Dock = DockStyle.Left;
            title.Width = 420;
            title.Text = "Neutriverse Roam Guide";
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.ForeColor = TextPrimary;
            title.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);

            var subtitle = new Label();
            subtitle.AutoSize = false;
            subtitle.Dock = DockStyle.Fill;
            subtitle.Text = "Info / Orbit / Stack control panel";
            subtitle.TextAlign = ContentAlignment.MiddleLeft;
            subtitle.ForeColor = TextMuted;
            subtitle.Font = new Font("Segoe UI", 9f);

            var close = CreateGuideActionButton("X", false);
            close.Dock = DockStyle.Right;
            close.Width = 38;
            close.Margin = new Padding(0);
            close.Click += delegate { form.Close(); };

            header.Controls.Add(subtitle);
            header.Controls.Add(title);
            header.Controls.Add(close);
            return header;
        }

        private Button CreateGuideNavButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.Width = 108;
            button.Height = 32;
            button.Margin = new Padding(0, 0, 8, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            StyleGuideNavButton(button, false);
            return button;
        }

        private void StyleGuideNavButton(Button button, bool active)
        {
            button.BackColor = active ? Color.FromArgb(36, 50, 88) : SurfaceSoft;
            button.ForeColor = active ? TextPrimary : TextMuted;
            button.FlatAppearance.BorderColor = active ? LogoGold : Border;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 48, 62);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(31, 43, 75);
        }

        private Button CreateGuideActionButton(string text, bool primary)
        {
            var button = new Button();
            button.Text = text;
            button.Width = Math.Max(92, TextRenderer.MeasureText(text, new Font("Segoe UI", 9f, FontStyle.Bold)).Width + 30);
            button.Height = 32;
            button.Margin = new Padding(8, 0, 0, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            button.BackColor = primary ? Color.FromArgb(28, 54, 105) : SurfaceSoft;
            button.ForeColor = primary ? TextPrimary : TextMuted;
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(110, 162, 255) : Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(36, 68, 126) : Color.FromArgb(42, 48, 62);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(22, 44, 88) : Color.FromArgb(31, 36, 47);
            return button;
        }

        private Panel CreateGuideInfoPage(TextBox kicker, TextBox infoText)
        {
            var page = CreateGuidePagePanel();
            var layout = CreateGuideTable(4);
            layout.RowStyles.Clear();
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            AddGuideLabel(layout, "Kicker", 0);
            layout.Controls.Add(kicker, 1, 0);
            AddGuideLabel(layout, "Info text", 1);
            layout.Controls.Add(infoText, 1, 1);
            layout.SetRowSpan(infoText, 3);
            page.Controls.Add(layout);
            return page;
        }

        private Panel CreateGuideOrbitPage(TextBox kicker, TextBox title, TextBox summary, TextBox updated, DataGridView signal, DataGridView statusItems, DataGridView roadmap)
        {
            var page = CreateGuidePagePanel();
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Surface;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var top = new TableLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.BackColor = Surface;
            top.ColumnCount = 4;
            top.RowCount = 3;
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddGuideLabel(top, "Kicker", 0, 0);
            top.Controls.Add(kicker, 1, 0);
            AddGuideLabel(top, "Updated", 0, 2);
            top.Controls.Add(updated, 3, 0);
            AddGuideLabel(top, "Title", 1, 0);
            top.Controls.Add(title, 1, 1);
            top.SetColumnSpan(title, 3);
            AddGuideLabel(top, "Summary", 2, 0);
            top.Controls.Add(summary, 1, 2);
            top.SetColumnSpan(summary, 3);
            root.Controls.Add(top, 0, 0);

            ConfigureCompactGuideGrid(signal);
            ConfigureCompactGuideGrid(statusItems);
            ConfigureCompactGuideGrid(roadmap);

            var lower = new TableLayoutPanel();
            lower.Dock = DockStyle.Fill;
            lower.BackColor = Surface;
            lower.ColumnCount = 3;
            lower.RowCount = 1;
            lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            lower.Controls.Add(CreateGuideGridPanel("Signal", signal), 0, 0);
            lower.Controls.Add(CreateGuideGridPanel("Status", statusItems), 1, 0);
            lower.Controls.Add(CreateGuideGridPanel("Roadmap", roadmap), 2, 0);
            root.Controls.Add(lower, 0, 1);

            page.Controls.Add(root);
            return page;
        }

        private Panel CreateGuideStackPage(DataGridView reading, DataGridView visual)
        {
            var page = CreateGuidePagePanel();
            var splitStacks = new SplitContainer();
            splitStacks.Dock = DockStyle.Fill;
            splitStacks.Orientation = Orientation.Vertical;
            splitStacks.SplitterWidth = 4;
            splitStacks.BackColor = Border;

            splitStacks.Panel1.Controls.Add(CreateGuideGridPanel("Future Reading Stack", reading));
            splitStacks.Panel2.Controls.Add(CreateGuideGridPanel("Future Media Stack", visual));
            page.Controls.Add(splitStacks);

            page.Resize += delegate
            {
                if (splitStacks.Width > 0)
                {
                    splitStacks.SplitterDistance = splitStacks.Width / 2;
                }
            };

            return page;
        }

        private Panel CreateGuidePagePanel()
        {
            var page = new Panel();
            page.Dock = DockStyle.Fill;
            page.BackColor = Surface;
            page.ForeColor = TextPrimary;
            page.Padding = new Padding(0);
            return page;
        }

        private TableLayoutPanel CreateGuideTable(int rows)
        {
            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.BackColor = Surface;
            layout.ColumnCount = 2;
            layout.RowCount = rows;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            }
            return layout;
        }

        private Panel CreateGuideGridPanel(string title, DataGridView grid)
        {
            var panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Surface;
            panel.Padding = new Padding(0);

            var label = new Label();
            label.Dock = DockStyle.Top;
            label.Height = 34;
            label.Text = title;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = LogoGold;
            label.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            label.Padding = new Padding(8, 0, 8, 0);

            panel.Controls.Add(grid);
            panel.Controls.Add(label);
            return panel;
        }

        private void AddGuideLabel(TableLayoutPanel layout, string text, int row)
        {
            AddGuideLabel(layout, text, row, 0);
        }

        private void AddGuideLabel(TableLayoutPanel layout, string text, int row, int column)
        {
            var label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.TopLeft;
            label.ForeColor = TextMuted;
            label.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            label.Padding = new Padding(0, 7, 8, 0);
            layout.Controls.Add(label, column, row);
        }

        private TextBox CreateGuideTextBox(string text, bool multiline)
        {
            var box = new TextBox();
            box.Text = text ?? "";
            box.Dock = DockStyle.Fill;
            box.Multiline = multiline;
            box.AcceptsReturn = multiline;
            box.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
            box.BackColor = GetEditorBackColor();
            box.ForeColor = GetEditorTextColor();
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = new Font("Segoe UI", 10f);
            return box;
        }

        private DataGridView CreateGuideGrid(string firstHeader, string secondHeader, List<GuidePair> items)
        {
            var grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = true;
            grid.AllowUserToDeleteRows = true;
            grid.AutoGenerateColumns = false;
            grid.BackgroundColor = Surface;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.GridColor = Border;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.DefaultCellStyle.BackColor = GetEditorBackColor();
            grid.DefaultCellStyle.ForeColor = GetEditorTextColor();
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(35, 58, 103);
            grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.BackColor = SurfaceRaised;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            grid.EnableHeadersVisualStyles = false;

            var first = new DataGridViewTextBoxColumn();
            first.HeaderText = firstHeader;
            first.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            first.FillWeight = 68;
            var second = new DataGridViewTextBoxColumn();
            second.HeaderText = secondHeader;
            second.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            second.FillWeight = 32;
            grid.Columns.Add(first);
            grid.Columns.Add(second);

            foreach (GuidePair item in items)
            {
                grid.Rows.Add(item.Label, item.Value);
            }

            return grid;
        }

        private void ConfigureCompactGuideGrid(DataGridView grid)
        {
            grid.ScrollBars = ScrollBars.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 24;
            grid.RowTemplate.Height = 24;
        }

        private static List<GuidePair> ReadGuideGrid(DataGridView grid)
        {
            var items = new List<GuidePair>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow)
                {
                    continue;
                }

                string first = Convert.ToString(row.Cells[0].Value).Trim();
                string second = Convert.ToString(row.Cells[1].Value).Trim();
                if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second))
                {
                    continue;
                }

                items.Add(new GuidePair(CleanGuideCell(first), CleanGuideCell(second)));
            }
            return items;
        }

        private RoamGuideData LoadRoamGuideData(string aboutPath)
        {
            string text = File.ReadAllText(aboutPath, Encoding.UTF8);
            var data = new RoamGuideData();
            data.InfoKicker = GetLiquidAssign(text, "about_info_kicker");
            data.InfoText = GetLiquidCapture(text, "about_info_text");
            data.NowKicker = GetLiquidAssign(text, "about_now_kicker");
            data.NowTitle = GetLiquidAssign(text, "about_now_title");
            data.NowSummary = GetLiquidCapture(text, "about_now_summary");
            data.NowUpdated = GetLiquidAssign(text, "about_now_updated");
            data.SignalItems = ParseGuidePairs(GetLiquidAssign(text, "signal_items"));
            data.StatusItems = ParseGuidePairs(GetLiquidAssign(text, "status_items"));
            data.RoadmapItems = ParseGuidePairs(GetLiquidAssign(text, "roadmap_items"));
            data.ReadingStackItems = ParseGuidePairs(GetLiquidAssign(text, "reading_stack_items"));
            data.VisualStackItems = ParseGuidePairs(GetLiquidAssign(text, "visual_stack_items"));
            return data;
        }

        private void SaveRoamGuideData(string aboutPath, RoamGuideData data)
        {
            string text = File.ReadAllText(aboutPath, Encoding.UTF8);
            text = ReplaceLiquidAssign(text, "about_info_kicker", data.InfoKicker, false);
            text = ReplaceLiquidCapture(text, "about_info_text", data.InfoText);
            text = ReplaceLiquidAssign(text, "about_now_kicker", data.NowKicker, false);
            text = ReplaceLiquidAssign(text, "about_now_title", data.NowTitle, false);
            text = ReplaceLiquidCapture(text, "about_now_summary", data.NowSummary);
            text = ReplaceLiquidAssign(text, "about_now_updated", data.NowUpdated, false);
            text = ReplaceLiquidAssign(text, "signal_items", FormatGuidePairs(data.SignalItems), true);
            text = ReplaceLiquidAssign(text, "status_items", FormatGuidePairs(data.StatusItems), true);
            text = ReplaceLiquidAssign(text, "roadmap_items", FormatGuidePairs(data.RoadmapItems), true);
            text = ReplaceLiquidAssign(text, "reading_stack_items", FormatGuidePairs(data.ReadingStackItems), true);
            text = ReplaceLiquidAssign(text, "visual_stack_items", FormatGuidePairs(data.VisualStackItems), true);
            File.WriteAllText(aboutPath, text, new UTF8Encoding(false));
        }

        private void PublishRoamGuidePage(string aboutPath)
        {
            if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                MessageBox.Show(this, "The target blog folder is not a git repository:" + Environment.NewLine + repoRoot, "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            GitResult staged = RunGit("diff --cached --name-only", repoRoot, 30000);
            if (!staged.Success)
            {
                ShowGitOutput("Could not inspect staged changes.", staged);
                return;
            }
            if (!string.IsNullOrWhiteSpace(staged.Output))
            {
                MessageBox.Show(this, "There are already staged changes in the blog repository. Commit or unstage them before using Save && Publish from the guide panel.", "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string commitMessage = PromptForText("Commit message", "Commit message for roam guide update:", "Update roam guide");
            if (string.IsNullOrWhiteSpace(commitMessage))
            {
                return;
            }

            using (var progress = new PublishProgressForm("Publishing Roam Guide"))
            {
                progress.Show(this);
                Cursor previousCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                UseWaitCursor = true;
                try
                {
                    GitResult addResult = RunGitStep(progress, "Stage _tabs/about.md", "add -- " + QuoteArg("_tabs/about.md"), repoRoot, 60000);
                    if (!addResult.Success)
                    {
                        ShowGitOutput("Failed to stage roam guide page.", addResult);
                        return;
                    }

                    GitResult cachedCheck = RunGitStep(progress, "Check staged diff", "diff --cached --check", repoRoot, 60000);
                    if (!cachedCheck.Success)
                    {
                        ShowGitOutput("git diff --cached --check failed. Fix whitespace/errors before publishing.", cachedCheck);
                        return;
                    }

                    GitResult cachedDiff = RunGitStep(progress, "Confirm staged changes", "diff --cached --quiet", repoRoot, 60000);
                    if (cachedDiff.ExitCode == 0)
                    {
                        if (HasLocalCommitsToPush(progress))
                        {
                            progress.AppendLog("No new staged guide diff, but local commits are waiting to be pushed.");
                            if (!RunFetchRebasePush(progress))
                            {
                                return;
                            }
                            progress.MarkComplete("Pushed existing local commits.");
                            MessageBox.Show(this, "Pushed existing local commits to GitHub.", "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            SetStatus("Pushed existing local commits.");
                            return;
                        }

                        MessageBox.Show(this, "No roam guide changes were found for publishing.", "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    GitResult commitResult = RunGitStep(progress, "Commit guide update", "commit -m " + QuoteArg(commitMessage), repoRoot, 120000);
                    if (!commitResult.Success)
                    {
                        ShowGitOutput("Commit failed.", commitResult);
                        return;
                    }

                    if (!RunFetchRebasePush(progress))
                    {
                        return;
                    }

                    progress.MarkComplete("Published roam guide.");
                    MessageBox.Show(this, "Roam guide published to GitHub." + Environment.NewLine + Environment.NewLine + commitResult.Output.Trim(), "Roam Guide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus("Published roam guide.");
                }
                finally
                {
                    UseWaitCursor = false;
                    Cursor.Current = previousCursor;
                }
            }
        }

        private bool HasLocalCommitsToPush(PublishProgressForm progress)
        {
            GitResult ahead = RunGitStep(progress, "Check local commits", "rev-list --count origin/main..HEAD", repoRoot, 30000);
            if (!ahead.Success)
            {
                return false;
            }

            int count;
            return int.TryParse((ahead.Output ?? "").Trim(), out count) && count > 0;
        }

        private static string GetLiquidAssign(string text, string name)
        {
            Match match = Regex.Match(text, "{%\\s*assign\\s+" + Regex.Escape(name) + "\\s*=\\s*'(?<value>.*?)'\\s*(?:\\|\\s*split:\\s*'\\|\\|'\\s*)?%}", RegexOptions.Singleline);
            return match.Success ? UnescapeLiquidValue(match.Groups["value"].Value) : "";
        }

        private static string GetLiquidCapture(string text, string name)
        {
            Match match = Regex.Match(text, "{%\\s*capture\\s+" + Regex.Escape(name) + "\\s*%}(?<value>.*?){%\\s*endcapture\\s*%}", RegexOptions.Singleline);
            return match.Success ? match.Groups["value"].Value.Trim() : "";
        }

        private static string ReplaceLiquidAssign(string text, string name, string value, bool splitList)
        {
            string suffix = splitList ? " | split: '||'" : "";
            string replacement = "{% assign " + name + " = '" + EscapeLiquidValue(value) + "'" + suffix + " %}";
            string pattern = "{%\\s*assign\\s+" + Regex.Escape(name) + "\\s*=\\s*'.*?'\\s*(?:\\|\\s*split:\\s*'\\|\\|'\\s*)?%}";
            return Regex.Replace(text, pattern, replacement, RegexOptions.Singleline);
        }

        private static string ReplaceLiquidCapture(string text, string name, string value)
        {
            string replacement = "{% capture " + name + " %}" + NormalizeMultilineText(value) + "{% endcapture %}";
            string pattern = "{%\\s*capture\\s+" + Regex.Escape(name) + "\\s*%}.*?{%\\s*endcapture\\s*%}";
            return Regex.Replace(text, pattern, replacement, RegexOptions.Singleline);
        }

        private static List<GuidePair> ParseGuidePairs(string value)
        {
            var items = new List<GuidePair>();
            foreach (string rawItem in (value ?? "").Split(new string[] { "||" }, StringSplitOptions.None))
            {
                string item = rawItem.Trim();
                if (item.Length == 0)
                {
                    continue;
                }

                string[] parts = item.Split(new char[] { '|' }, 2);
                items.Add(new GuidePair(parts.Length > 0 ? parts[0].Trim() : "", parts.Length > 1 ? parts[1].Trim() : ""));
            }
            return items;
        }

        private static string FormatGuidePairs(List<GuidePair> items)
        {
            var parts = new List<string>();
            foreach (GuidePair item in items)
            {
                string label = CleanGuideCell(item.Label);
                string value = CleanGuideCell(item.Value);
                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                parts.Add(label + "|" + value);
            }
            return string.Join("||", parts.ToArray());
        }

        private static string CleanGuideCell(string value)
        {
            return NormalizeInlineImageText(value).Replace("|", "｜");
        }

        private static string NormalizeMultilineText(string value)
        {
            if (value == null)
            {
                return "";
            }
            return value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        private static string EscapeLiquidValue(string value)
        {
            return (value ?? "").Replace("'", "&#39;");
        }

        private static string UnescapeLiquidValue(string value)
        {
            return (value ?? "").Replace("&#39;", "'");
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

        private void InsertInlineImageFromDialog()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Multiselect = true;
                dialog.Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                InsertInlineImageFiles(dialog.FileNames);
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
            ImageCopyResult copyResult = CopyImagesToPostMedia(files);
            var inserted = new StringBuilder();
            foreach (CopiedImage image in copyResult.Images)
            {
                inserted.AppendLine("![" + image.Alt + "](" + image.FileName.Replace("\\", "/") + ")");
                inserted.AppendLine();
            }

            if (inserted.Length > 0)
            {
                InsertAtCursor(inserted.ToString());
                SetStatus("Inserted image(s) into " + copyResult.TargetDirectory);
            }
        }

        private void InsertInlineImageFiles(IEnumerable<string> files)
        {
            string selectedAlt = NormalizeInlineImageText(editor.SelectedText);
            ImageCopyResult copyResult = CopyImagesToPostMedia(files);
            Dictionary<string, string> captions = PromptForInlineImageCaptions(copyResult.Images);
            var inserted = new StringBuilder();
            foreach (CopiedImage image in copyResult.Images)
            {
                string alt = string.IsNullOrWhiteSpace(selectedAlt) || copyResult.Images.Count > 1 ? image.Alt : selectedAlt;
                inserted.AppendLine("{% include inline-image.html");
                inserted.AppendLine("  src=\"" + EscapeLiquidParameter(image.FileName.Replace("\\", "/")) + "\"");
                inserted.AppendLine("  alt=\"" + EscapeLiquidParameter(alt) + "\"");
                string caption;
                if (captions.TryGetValue(image.FileName, out caption) && !string.IsNullOrWhiteSpace(caption))
                {
                    inserted.AppendLine("  caption=\"" + EscapeLiquidParameter(caption) + "\"");
                }
                inserted.AppendLine("  align=\"right\"");
                inserted.AppendLine("%}");
                inserted.AppendLine();
            }

            if (inserted.Length > 0)
            {
                InsertAtCursor(inserted.ToString());
                SetStatus("Inserted inline illustration(s) into " + copyResult.TargetDirectory);
            }
        }

        private Dictionary<string, string> PromptForInlineImageCaptions(IList<CopiedImage> images)
        {
            var captions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (images.Count == 0)
            {
                return captions;
            }

            DialogResult addCaptions = MessageBox.Show(
                this,
                "Add small captions below the inserted inline image(s)?",
                "Inline Image Captions",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (addCaptions != DialogResult.Yes)
            {
                return captions;
            }

            if (images.Count == 1)
            {
                string caption = PromptForText("Inline image caption", "Small caption below this image:", "");
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    captions[images[0].FileName] = caption;
                }
                return captions;
            }

            using (var form = new Form())
            using (var label = new Label())
            using (var panel = new Panel())
            using (var okButton = new Button())
            using (var skipButton = new Button())
            {
                form.Text = "Inline image captions";
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ClientSize = new Size(640, Math.Min(460, 118 + images.Count * 42));
                form.BackColor = Surface;
                form.ForeColor = TextPrimary;

                label.Text = "Optional captions. Leave a field blank to insert that image without a caption.";
                label.SetBounds(14, 14, 612, 24);
                label.ForeColor = TextMuted;

                panel.SetBounds(14, 46, 612, form.ClientSize.Height - 94);
                panel.AutoScroll = true;
                panel.BackColor = Surface;

                var textBoxes = new List<TextBox>();
                for (int i = 0; i < images.Count; i++)
                {
                    CopiedImage image = images[i];
                    var nameLabel = new Label();
                    nameLabel.Text = image.FileName;
                    nameLabel.SetBounds(0, i * 42 + 4, 220, 24);
                    nameLabel.ForeColor = TextMuted;

                    var textBox = new TextBox();
                    textBox.SetBounds(230, i * 42, 350, 26);
                    textBox.BackColor = GetEditorBackColor();
                    textBox.ForeColor = GetEditorTextColor();
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    textBox.Tag = image.FileName;

                    panel.Controls.Add(nameLabel);
                    panel.Controls.Add(textBox);
                    textBoxes.Add(textBox);
                }

                okButton.Text = "Insert";
                okButton.DialogResult = DialogResult.OK;
                okButton.SetBounds(448, form.ClientSize.Height - 38, 84, 28);

                skipButton.Text = "No captions";
                skipButton.DialogResult = DialogResult.Cancel;
                skipButton.SetBounds(538, form.ClientSize.Height - 38, 88, 28);

                form.Controls.Add(label);
                form.Controls.Add(panel);
                form.Controls.Add(okButton);
                form.Controls.Add(skipButton);
                form.AcceptButton = okButton;
                form.CancelButton = skipButton;

                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return captions;
                }

                foreach (TextBox textBox in textBoxes)
                {
                    string caption = textBox.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(caption))
                    {
                        captions[(string)textBox.Tag] = caption;
                    }
                }
            }

            return captions;
        }

        private ImageCopyResult CopyImagesToPostMedia(IEnumerable<string> files)
        {
            string originalText = editor.Text;
            int originalSelectionStart = editor.SelectionStart;
            int originalSelectionLength = editor.SelectionLength;
            int frontMatterClose = FindFrontMatterClose(originalText);
            string text = EnsureMediaSubpath(originalText);
            if (!string.Equals(originalText, text, StringComparison.Ordinal))
            {
                int delta = text.Length - originalText.Length;
                editor.Text = text;
                if (delta > 0 && frontMatterClose >= 0 && originalSelectionStart >= frontMatterClose)
                {
                    originalSelectionStart += delta;
                }
                originalSelectionStart = Math.Min(originalSelectionStart, editor.TextLength);
                originalSelectionLength = Math.Min(originalSelectionLength, editor.TextLength - originalSelectionStart);
                editor.Select(originalSelectionStart, originalSelectionLength);
            }

            string mediaSubpath = GetFrontMatterValue(editor.Text, "media_subpath");
            string relativeFolder = mediaSubpath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
            string targetDir = Path.Combine(repoRoot, relativeFolder);
            Directory.CreateDirectory(targetDir);

            var result = new ImageCopyResult();
            result.MediaSubpath = mediaSubpath;
            result.TargetDirectory = targetDir;

            foreach (string source in files)
            {
                if (!File.Exists(source) || !IsImage(source))
                {
                    continue;
                }

                string name = UniqueFileName(targetDir, SanitizeFileName(Path.GetFileName(source)));
                File.Copy(source, Path.Combine(targetDir, name), false);
                result.Images.Add(new CopiedImage
                {
                    FileName = name,
                    Alt = Path.GetFileNameWithoutExtension(name)
                });
            }

            return result;
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

        private static string NormalizeInlineImageText(string text)
        {
            return Regex.Replace((text ?? "").Trim(), "\\s+", " ");
        }

        private static string EscapeLiquidParameter(string value)
        {
            return NormalizeInlineImageText(value).Replace("\"", "'");
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

        private void WrapTextBlock(string classes, string backgroundColor)
        {
            WrapSelection("<div class=\"" + classes + "\" style=\"--nv-text-block-bg: " + backgroundColor + ";\">", "</div>");
        }

        private void InsertConfiguredTextBlock(TextBlockOptions options)
        {
            string original = string.IsNullOrEmpty(options.OriginalText) ? "Original text" : options.OriginalText;
            string encodedOriginal = WebUtility.HtmlEncode(original);
            string encodedHover = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(options.HoverText) ? "中文译文" : options.HoverText);
            string replacement;
            if (options.EnableHover)
            {
                replacement =
                    "<div class=\"" + options.BlockClasses + "\" tabindex=\"0\" style=\"--nv-text-block-bg: " + options.BackgroundColor + ";\"><span class=\"nv-text-original\">" +
                    encodedOriginal +
                    "</span><span class=\"nv-text-translation " + options.TranslationFontClass + "\">" +
                    encodedHover +
                    "</span></div>";
            }
            else
            {
                replacement =
                    "<div class=\"" + options.BlockClasses + "\" style=\"--nv-text-block-bg: " + options.BackgroundColor + ";\">" +
                    encodedOriginal +
                    "</div>";
            }

            int start = editor.SelectionStart;
            editor.SelectedText = replacement;
            int originalStart = replacement.IndexOf(encodedOriginal, StringComparison.Ordinal);
            editor.SelectionStart = originalStart >= 0 ? start + originalStart : start;
            editor.SelectionLength = encodedOriginal.Length;
            editor.Focus();
        }

        private void OpenTextBlockPanel()
        {
            TextBlockOptions options = TextBlockOptions.Default(editor.SelectedText);
            using (var form = new Form())
            using (var originalLabel = new Label())
            using (var originalBox = new TextBox())
            using (var hoverCheck = new CheckBox())
            using (var hoverLabel = new Label())
            using (var hoverBox = new TextBox())
            using (var fontLabel = new Label())
            using (var fontCombo = new ComboBox())
            using (var hoverFontLabel = new Label())
            using (var hoverFontCombo = new ComboBox())
            using (var alignLabel = new Label())
            using (var alignCombo = new ComboBox())
            using (var bgLabel = new Label())
            using (var bgBox = new TextBox())
            using (var okButton = new Button())
            using (var cancelButton = new Button())
            {
                form.Text = "Text Block";
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ClientSize = new Size(660, 520);
                form.BackColor = Surface;
                form.ForeColor = TextPrimary;

                originalLabel.Text = "Default text";
                originalLabel.SetBounds(14, 14, 632, 22);
                originalLabel.ForeColor = TextMuted;

                originalBox.Multiline = true;
                originalBox.ScrollBars = ScrollBars.Vertical;
                originalBox.Text = options.OriginalText;
                originalBox.SetBounds(14, 40, 632, 118);
                originalBox.BackColor = GetEditorBackColor();
                originalBox.ForeColor = GetEditorTextColor();
                originalBox.BorderStyle = BorderStyle.FixedSingle;

                hoverCheck.Text = "Switch to hover text on mouse hover/focus";
                hoverCheck.Checked = options.EnableHover;
                hoverCheck.SetBounds(14, 172, 420, 24);
                hoverCheck.ForeColor = TextPrimary;

                hoverLabel.Text = "Hover text";
                hoverLabel.SetBounds(14, 204, 632, 22);
                hoverLabel.ForeColor = TextMuted;

                hoverBox.Multiline = true;
                hoverBox.ScrollBars = ScrollBars.Vertical;
                hoverBox.Text = options.HoverText;
                hoverBox.SetBounds(14, 230, 632, 118);
                hoverBox.BackColor = GetEditorBackColor();
                hoverBox.ForeColor = GetEditorTextColor();
                hoverBox.BorderStyle = BorderStyle.FixedSingle;

                fontLabel.Text = "Default font";
                fontLabel.SetBounds(14, 366, 120, 22);
                fontLabel.ForeColor = TextMuted;

                fontCombo.DropDownStyle = ComboBoxStyle.DropDownList;
                fontCombo.Items.AddRange(new object[] { "Script English", "Normal", "Serif", "Mono" });
                fontCombo.SelectedIndex = 0;
                fontCombo.SetBounds(130, 362, 150, 28);

                hoverFontLabel.Text = "Hover font";
                hoverFontLabel.SetBounds(304, 366, 120, 22);
                hoverFontLabel.ForeColor = TextMuted;

                hoverFontCombo.DropDownStyle = ComboBoxStyle.DropDownList;
                hoverFontCombo.Items.AddRange(new object[] { "Ma Shan Zheng", "Zhi Mang Xing", "Long Cang", "Liu Jian Mao Cao", "ZCOOL XiaoWei" });
                hoverFontCombo.SelectedIndex = 0;
                hoverFontCombo.SetBounds(416, 362, 230, 28);

                alignLabel.Text = "Alignment";
                alignLabel.SetBounds(14, 408, 120, 22);
                alignLabel.ForeColor = TextMuted;

                alignCombo.DropDownStyle = ComboBoxStyle.DropDownList;
                alignCombo.Items.AddRange(new object[] { "Left", "Center", "Right" });
                alignCombo.SelectedIndex = 0;
                alignCombo.SetBounds(130, 404, 150, 28);

                bgLabel.Text = "Background";
                bgLabel.SetBounds(304, 408, 120, 22);
                bgLabel.ForeColor = TextMuted;

                bgBox.Text = options.BackgroundColor;
                bgBox.SetBounds(416, 404, 230, 26);
                bgBox.BackColor = GetEditorBackColor();
                bgBox.ForeColor = GetEditorTextColor();
                bgBox.BorderStyle = BorderStyle.FixedSingle;

                okButton.Text = "Insert";
                okButton.DialogResult = DialogResult.OK;
                okButton.SetBounds(466, 474, 84, 28);

                cancelButton.Text = "Cancel";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.SetBounds(562, 474, 84, 28);

                form.Controls.AddRange(new Control[] {
                    originalLabel, originalBox, hoverCheck, hoverLabel, hoverBox,
                    fontLabel, fontCombo, hoverFontLabel, hoverFontCombo,
                    alignLabel, alignCombo, bgLabel, bgBox, okButton, cancelButton
                });
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                options.OriginalText = originalBox.Text;
                options.EnableHover = hoverCheck.Checked;
                options.HoverText = hoverBox.Text;
                options.DefaultFontClass = TextBlockOptions.FontClassFromLabel(fontCombo.Text);
                options.TranslationFontClass = TextBlockOptions.TranslationFontClassFromLabel(hoverFontCombo.Text);
                options.AlignmentClass = TextBlockOptions.AlignmentClassFromLabel(alignCombo.Text);
                options.BackgroundColor = NormalizeTextBlockColor(bgBox.Text);
                InsertConfiguredTextBlock(options);
            }
        }

        private static string NormalizeTextBlockColor(string value)
        {
            string color = (value ?? "").Trim();
            return Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ? color : "#18191b";
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
            if (!livePreview)
            {
                previewDirty = true;
                UpdateUiState();
                return;
            }

            previewTimer.Stop();
            previewTimer.Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.N))
            {
                NewDraft();
                return true;
            }

            if (keyData == (Keys.Control | Keys.O))
            {
                OpenPost();
                return true;
            }

            if (keyData == (Keys.Control | Keys.S))
            {
                SavePost(false);
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.S))
            {
                SavePost(true);
                return true;
            }

            if (keyData == Keys.F5)
            {
                RenderPreview();
                return true;
            }

            if (keyData == (Keys.Control | Keys.L))
            {
                TogglePreviewMode();
                return true;
            }

            if (keyData == (Keys.Control | Keys.E))
            {
                ToggleComfortMode();
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.I))
            {
                InsertInlineImageFromDialog();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void TogglePreviewMode()
        {
            livePreview = !livePreview;
            if (!livePreview)
            {
                previewTimer.Stop();
                scrollSyncTimer.Stop();
            }
            else
            {
                lastPreviewScrollTop = GetPreviewScrollTop();
                scrollSyncTimer.Start();
            }
            if (previewModeButton != null)
            {
                previewModeButton.Checked = livePreview;
                previewModeButton.Text = livePreview ? "Live" : "Manual";
                previewModeButton.Width = Math.Max(62, TextRenderer.MeasureText(previewModeButton.Text, previewModeButton.Font).Width + 20);
            }

            if (livePreview && previewDirty)
            {
                RenderPreview();
            }
            else
            {
                SetStatus(livePreview ? "Live preview mode enabled." : "Manual preview mode enabled.");
            }
            UpdateUiState();
        }

        private Color GetEditorBackColor()
        {
            return comfortMode ? Color.FromArgb(31, 32, 27) : Color.FromArgb(17, 20, 24);
        }

        private Color GetEditorTextColor()
        {
            return comfortMode ? Color.FromArgb(211, 204, 184) : Color.FromArgb(210, 216, 222);
        }

        private Color GetPreviewBackColor()
        {
            return comfortMode ? Color.FromArgb(31, 32, 27) : Color.FromArgb(27, 27, 30);
        }

        private Color GetPreviewTextColor()
        {
            return comfortMode ? Color.FromArgb(207, 200, 181) : Color.FromArgb(196, 202, 208);
        }

        private void ToggleComfortMode()
        {
            comfortMode = !comfortMode;
            if (eyeButton != null)
            {
                eyeButton.Checked = comfortMode;
                eyeButton.Text = comfortMode ? "Eye On" : "Eye";
                eyeButton.Width = Math.Max(52, TextRenderer.MeasureText(eyeButton.Text, eyeButton.Font).Width + 20);
            }

            ApplyEditorTheme();
            RenderPreview();
            QueueHighlight();
            SetStatus(comfortMode ? "Eye comfort mode enabled." : "Eye comfort mode disabled.");
            UpdateUiState();
        }

        private void ApplyEditorTheme()
        {
            editor.BackColor = GetEditorBackColor();
            editor.ForeColor = GetEditorTextColor();
            editor.SelectionColor = editor.ForeColor;
            split.Panel2.BackColor = editor.BackColor;
            if (sourceHeader != null)
            {
                sourceHeader.BackColor = comfortMode ? Color.FromArgb(38, 37, 31) : SurfaceRaised;
            }
            if (previewHeader != null)
            {
                previewHeader.BackColor = comfortMode ? Color.FromArgb(38, 37, 31) : SurfaceRaised;
            }
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
            var scroll = new NativePoint();
            applyingHighlight = true;

            try
            {
                SendMessage(editor.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
                SendMessage(editor.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                editor.SuspendLayout();
                editor.SelectAll();
                editor.SelectionColor = GetEditorTextColor();
                editor.SelectionFont = new Font(editor.Font, FontStyle.Regular);

                ColorPattern("(?m)^---\\s*$", Color.FromArgb(139, 148, 158), false);
                ColorPattern("(?m)^(title|date|categories|tags|description|media_subpath|image|hidden|display_date|display_updated_at):", Color.FromArgb(121, 192, 255), true);
                ColorPattern("(?m)^#{1,6}\\s+.*$", Color.FromArgb(88, 166, 255), true);
                ColorPattern("(?m)^\\s*[-*+]\\s+", Color.FromArgb(255, 171, 112), true);
                ColorPattern("(?m)^\\s*\\d+\\.\\s+", Color.FromArgb(255, 171, 112), true);
                ColorPattern("<[^>]+>", Color.FromArgb(139, 148, 158), false);
                ColorPattern("class=\\\"[^\\\"]*nv-[^\\\"]*\\\"", Color.FromArgb(210, 168, 255), false);
                ColorPattern("`[^`]+`", Color.FromArgb(126, 231, 135), false);
                ColorPattern("\\*\\*[^*]+\\*\\*", comfortMode ? Color.FromArgb(229, 219, 190) : Color.FromArgb(230, 235, 240), true);
                ColorPattern("~~[^~]+~~", Color.FromArgb(255, 123, 114), false);

                editor.SelectionStart = Math.Min(start, editor.TextLength);
                editor.SelectionLength = Math.Min(length, editor.TextLength - editor.SelectionStart);
                editor.SelectionColor = GetEditorTextColor();
                SendMessage(editor.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
            }
            finally
            {
                editor.ResumeLayout();
                SendMessage(editor.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                editor.Invalidate();
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
            CapturePreviewScroll();
            string html = BuildHtml(editor.Text);
            previewDirty = false;
            preview.DocumentText = html;
            SetStatus(livePreview ? "Preview refreshed." : "Manual preview refreshed.");
            UpdateUiState();
        }

        private void CapturePreviewScroll()
        {
            restorePreviewScroll = false;
            pendingPreviewScroll = Point.Empty;
            if (preview.Document == null || preview.Document.Window == null)
            {
                return;
            }

            try
            {
                int x = Convert.ToInt32(preview.Document.InvokeScript("eval", new object[] { "document.documentElement.scrollLeft || document.body.scrollLeft || 0" }));
                int y = Convert.ToInt32(preview.Document.InvokeScript("eval", new object[] { "document.documentElement.scrollTop || document.body.scrollTop || 0" }));
                pendingPreviewScroll = new Point(x, y);
                restorePreviewScroll = true;
            }
            catch
            {
                restorePreviewScroll = false;
            }
        }

        private void SyncPreviewToEditorTopLine()
        {
            if (!livePreview || syncingScroll || DateTime.Now < ignoreScrollSyncUntil || preview.Document == null)
            {
                return;
            }

            int charIndex = editor.GetCharIndexFromPosition(new Point(2, 2));
            int line = editor.GetLineFromCharIndex(charIndex);
            try
            {
                syncingScroll = true;
                preview.Document.InvokeScript("nwScrollToSourceLine", new object[] { line });
                lastPreviewScrollTop = GetPreviewScrollTop();
                ignoreScrollSyncUntil = DateTime.Now.AddMilliseconds(350);
            }
            catch
            {
            }
            finally
            {
                syncingScroll = false;
            }
        }

        private void PollPreviewScrollSync()
        {
            if (!livePreview || syncingScroll || DateTime.Now < ignoreScrollSyncUntil || preview.Document == null)
            {
                return;
            }

            int scrollTop = GetPreviewScrollTop();
            if (scrollTop < 0)
            {
                return;
            }

            if (lastPreviewScrollTop < 0)
            {
                lastPreviewScrollTop = scrollTop;
                return;
            }

            if (Math.Abs(scrollTop - lastPreviewScrollTop) < 8)
            {
                return;
            }

            lastPreviewScrollTop = scrollTop;
            int line = GetPreviewTopSourceLine();
            if (line >= 0)
            {
                ScrollEditorToLine(line);
            }
        }

        private int GetPreviewScrollTop()
        {
            try
            {
                object value = preview.Document.InvokeScript("eval", new object[] { "document.documentElement.scrollTop || document.body.scrollTop || 0" });
                return Convert.ToInt32(value);
            }
            catch
            {
                return -1;
            }
        }

        private int GetPreviewTopSourceLine()
        {
            try
            {
                object value = preview.Document.InvokeScript("nwTopSourceLine");
                return Convert.ToInt32(value);
            }
            catch
            {
                return -1;
            }
        }

        private void ScrollEditorToLine(int line)
        {
            if (line < 0 || line >= editor.Lines.Length)
            {
                return;
            }

            try
            {
                syncingScroll = true;
                int firstChar = editor.GetFirstCharIndexFromLine(line);
                if (firstChar < 0)
                {
                    return;
                }

                int currentFirst = editor.GetLineFromCharIndex(editor.GetCharIndexFromPosition(new Point(2, 2)));
                int delta = line - currentFirst;
                if (delta != 0)
                {
                    SendMessage(editor.Handle, 0x00B6, IntPtr.Zero, new IntPtr(delta));
                    ignoreScrollSyncUntil = DateTime.Now.AddMilliseconds(350);
                }
            }
            finally
            {
                syncingScroll = false;
            }
        }

        private string BuildHtml(string source)
        {
            string body = StripFrontMatter(source);
            int bodyStartLine = GetBodyStartLine(source);
            string mediaSubpath = GetFrontMatterValue(source, "media_subpath");
            string cssPath = Path.Combine(repoRoot, "assets", "css", "ChirpyDefault.css");
            string css = File.Exists(cssPath) ? File.ReadAllText(cssPath, Encoding.UTF8) : "";
            string title = WebUtility.HtmlEncode(GetFrontMatterValue(source, "title"));
            string previewBack = ColorTranslator.ToHtml(GetPreviewBackColor());
            string previewText = ColorTranslator.ToHtml(GetPreviewTextColor());
            string previewHeading = comfortMode ? "#e0d5b8" : "#dce8f7";
            string previewCodeBack = comfortMode ? "#292a24" : "#242426";
            string previewInlineCode = comfortMode ? "#34342d" : "#2a2a2d";
            string previewFontFaces = BuildPreviewFontFaces();
            string baseCss =
                previewFontFaces +
                "html,body{background:" + previewBack + "!important;color:" + previewText + "!important;}" +
                "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Noto Sans SC','Microsoft YaHei',sans-serif;line-height:1.75;padding:2rem 2.35rem;max-width:860px;margin:0 auto;}" +
                ".content{font-size:1.03rem;letter-spacing:0;}" +
                ".content p{margin:0 0 1rem;}" +
                ".content h1,.content h2,.content h3,.content h4{color:" + previewHeading + "!important;font-weight:500;line-height:1.35;margin:2.1rem 0 1rem;}" +
                ".content>h1:first-child{margin-top:0;font-size:2.05rem;font-weight:500;}" +
                ".content h1{font-size:2.05rem}.content h2{font-size:1.65rem}.content h3{font-size:1.35rem}.content h4{font-size:1.18rem}" +
                "strong{font-weight:700;color:" + previewHeading + ";} em{font-style:italic;} del{text-decoration:line-through;} u{text-decoration:underline;}" +
                "a{color:#8fb7ff;} img{max-width:100%;height:auto;border-radius:.35rem;}" +
                ".content .inline-illustration{box-sizing:border-box;float:right;width:42%;max-width:320px;margin:.15rem 0 1rem 1.5rem}.content .inline-illustration.inline-left{float:left;margin:.15rem 1.5rem 1rem 0}.content .inline-illustration img{display:block;width:100%;max-width:100%;height:auto}.content .inline-illustration figcaption{margin-top:.5rem;color:rgba(226,231,241,.68);font-size:.78rem;line-height:1.35;text-align:center}.content h2,.content h3{clear:both}@media(max-width:720px){.content .inline-illustration,.content .inline-illustration.inline-left{float:none;width:100%;max-width:100%;margin:1rem 0}}" +
                "hr{height:1px;margin:2rem 0;border:0;background:#30363d;}" +
                ".content ul,.content ol{margin:0 0 1rem 1.35rem;padding-left:1.25rem}.content li{margin:.35rem 0}.content li::marker{color:#9aa4b2;}" +
                ".content blockquote{margin:1.2rem 0 1.2rem 1rem;padding:.1rem 0 .1rem 1rem;border-left:.22rem solid #4f6fff!important;background:transparent!important;color:" + previewText + "!important;}" +
                ".content blockquote p{margin:.45rem 0;color:" + previewText + "!important;}" +
                ".content code{font-family:Consolas,'Cascadia Mono',monospace;font-size:.92em;background:" + previewInlineCode + "!important;color:" + previewHeading + "!important;border-radius:0;padding:.13rem .34rem;}" +
                ".content pre{margin:1rem 0 1.25rem;padding:1rem;background:" + previewCodeBack + "!important;color:" + previewHeading + "!important;border-radius:0;overflow:auto;white-space:pre-wrap;word-wrap:break-word;}" +
                ".content pre code{display:block;padding:0;background:transparent!important;color:inherit!important;border:0;}" +
                ".content .nv-text-block{margin:1.25rem 0 1.5rem;padding:1.05rem 1.2rem;border:1px solid rgba(0,77,255,.46);border-radius:0;background:#18191b;box-shadow:0 0 0 1px rgba(0,77,255,.1),0 0 1.15rem rgba(0,77,255,.16);color:rgba(226,231,241,.88);font-family:inherit;font-size:.96rem;line-height:1.75;white-space:pre-wrap;}" +
                ".content .nv-text-block.nv-font-script{font-family:'Segoe Script','Brush Script MT','Lucida Handwriting',cursive;font-size:1.08rem;line-height:1.9}.content .nv-text-block.nv-font-serif{font-family:Georgia,'Times New Roman',serif}.content .nv-text-block.nv-font-mono{font-family:'Cascadia Mono',Consolas,monospace;font-size:.92rem}" +
                ".content .nv-text-block.nv-font-cn-script,.content .nv-text-block .nv-font-cn-script{font-family:'NV Ma Shan Zheng',cursive;font-size:1.05rem;letter-spacing:.03em;line-height:1.95}.content .nv-text-block.nv-font-cn-longcang,.content .nv-text-block .nv-font-cn-longcang{font-family:'NV Long Cang',cursive;font-size:1.1rem;line-height:1.95}.content .nv-text-block.nv-font-cn-zhimang,.content .nv-text-block .nv-font-cn-zhimang{font-family:'NV Zhi Mang Xing',cursive;font-size:1.05rem;letter-spacing:.03em;line-height:1.95}.content .nv-text-block.nv-font-cn-maocao,.content .nv-text-block .nv-font-cn-maocao{font-family:'NV Liu Jian Mao Cao',cursive;font-size:1.08rem;line-height:2}.content .nv-text-block.nv-font-cn-mashan,.content .nv-text-block .nv-font-cn-mashan{font-family:'NV Ma Shan Zheng',cursive;font-size:1.06rem;line-height:1.9}.content .nv-text-block.nv-font-cn-xiaowei,.content .nv-text-block .nv-font-cn-xiaowei{font-family:'NV ZCOOL XiaoWei',serif;font-size:1.02rem;line-height:1.9}" +
                ".content .nv-text-block.nv-bilingual{cursor:help;white-space:normal}.content .nv-text-block .nv-text-original,.content .nv-text-block .nv-text-translation{white-space:pre-wrap}.content .nv-text-block .nv-text-translation{display:none}.content .nv-text-block.nv-bilingual:hover>.nv-text-original,.content .nv-text-block.nv-bilingual:focus>.nv-text-original,.content .nv-text-block.nv-bilingual:focus-within>.nv-text-original{display:none}.content .nv-text-block.nv-bilingual:hover>.nv-text-translation,.content .nv-text-block.nv-bilingual:focus>.nv-text-translation,.content .nv-text-block.nv-bilingual:focus-within>.nv-text-translation{display:block}" +
                ".content .nv-text-block.nv-align-center{text-align:center}.content .nv-text-block.nv-align-right{text-align:right}" +
                ".content table{border-collapse:collapse;width:100%;margin:1rem 0 1.25rem;display:table}.content th,.content td{border:1px solid #34383f;padding:.45rem .65rem}.content th{background:" + previewCodeBack + "!important;color:" + previewHeading + "!important;font-weight:700}.content td{background:" + previewBack + "!important}" +
                ".nv-red{color:#ff7d7d!important}.nv-orange{color:#ffab70!important}.nv-gold{color:#d8b76a!important}.nv-green{color:#7ee787!important}.nv-cyan{color:#76e3ea!important}.nv-blue{color:#79c0ff!important}.nv-purple{color:#d2a8ff!important}.nv-pink{color:#ff9bd2!important}.nv-muted{color:#8b949e!important}" +
                ".nv-underline{display:inline;border-bottom:.08em solid currentColor!important;text-decoration:none!important;padding-bottom:.04em}.nv-dotted{display:inline;border-bottom:.1em dotted currentColor!important;text-decoration:none!important;padding-bottom:.04em}.nv-wavy{display:inline;border-bottom:.1em solid currentColor!important;text-decoration:none!important;padding-bottom:.04em}" +
                ".nv-mark,.nv-mark-gold{background:rgba(216,183,106,.20)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}.nv-mark-red{background:rgba(255,125,125,.18)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}.nv-mark-blue{background:rgba(143,183,255,.18)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}.nv-mark-green{background:rgba(130,217,130,.16)!important;color:#dce8f7!important;padding:.05em .22em;border-radius:.18em}" +
                ".nv-key{border:0;border-bottom:2px solid currentColor;background:transparent!important;color:#d8b76a!important;padding:.02em .18em;font-weight:600}.nv-spoiler{background:currentColor!important;color:#c9d1d9!important;border-radius:.18em;padding:.05em .22em}.nv-spoiler:hover{background:rgba(255,255,255,.08)!important;color:#c9d1d9!important}";
            string syncScript =
                "<script>" +
                "function nwScrollToSourceLine(line){var nodes=document.querySelectorAll('[data-src-line]');if(!nodes.length)return;var target=nodes[0];for(var i=0;i<nodes.length;i++){var n=parseInt(nodes[i].getAttribute('data-src-line'),10);if(n>=line){target=nodes[i];break;}target=nodes[i];}target.scrollIntoView(true);}" +
                "function nwTopSourceLine(){var nodes=document.querySelectorAll('[data-src-line]');if(!nodes.length)return -1;var best=nodes[0],bestDist=999999;for(var i=0;i<nodes.length;i++){var r=nodes[i].getBoundingClientRect();var d=Math.abs(r.top);if(r.bottom>=0&&d<bestDist){best=nodes[i];bestDist=d;}}return parseInt(best.getAttribute('data-src-line'),10);}" +
                "</script>";

            return "<!doctype html><html data-mode=\"dark\"><head><meta charset=\"utf-8\"><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><style>" +
                   css +
                   baseCss +
                   "</style></head><body><article class=\"content\"><h1>" + title + "</h1>" +
                   RenderMarkdown(body, mediaSubpath, bodyStartLine) +
                   "</article>" + syncScript + "</body></html>";
        }

        private string RenderMarkdown(string body, string mediaSubpath, int bodyStartLine)
        {
            var output = new StringBuilder();
            var paragraph = new StringBuilder();
            bool inCode = false;
            bool inList = false;
            bool inOrderedList = false;
            var tableRows = new List<string>();
            var quoteLines = new List<string>();
            int paragraphSourceLine = -1;
            int tableSourceLine = -1;
            int quoteSourceLine = -1;
            int codeSourceLine = -1;

            Action flushParagraph = delegate
            {
                if (paragraph.Length > 0)
                {
                    output.Append("<p").Append(SourceLineAttr(paragraphSourceLine)).Append(">")
                        .Append(RenderInline(paragraph.ToString().Trim(), mediaSubpath)).AppendLine("</p>");
                    paragraph.Length = 0;
                    paragraphSourceLine = -1;
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

                output.Append("<blockquote").Append(SourceLineAttr(quoteSourceLine)).AppendLine(">");
                foreach (string quoteLine in quoteLines)
                {
                    output.Append("<p>").Append(RenderInline(quoteLine.Trim(), mediaSubpath)).AppendLine("</p>");
                }
                output.AppendLine("</blockquote>");
                quoteLines.Clear();
                quoteSourceLine = -1;
            };

            Action flushTable = delegate
            {
                if (tableRows.Count == 0)
                {
                    return;
                }

                output.Append("<table").Append(SourceLineAttr(tableSourceLine)).AppendLine(">");
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
                tableSourceLine = -1;
            };

            string[] rawLines = body.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < rawLines.Length; i++)
            {
                string raw = rawLines[i];
                int sourceLine = bodyStartLine + i;
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
                        codeSourceLine = sourceLine;
                        output.Append("<pre").Append(SourceLineAttr(codeSourceLine)).AppendLine("><code>");
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

                if (IsTextBlockStart(trimmed))
                {
                    flushParagraph();
                    closeList();
                    flushTable();
                    flushQuote();
                    var textBlock = new StringBuilder();
                    string textBlockClass = ExtractTextBlockClasses(line);
                    string textBlockStyle = ExtractTextBlockStyle(line);
                    string firstContent = ExtractTextBlockFirstLine(line);
                    if (firstContent.Length > 0)
                    {
                        textBlock.AppendLine(firstContent);
                    }
                    while (!trimmed.Contains("</div>") && i + 1 < rawLines.Length)
                    {
                        i++;
                        line = rawLines[i].TrimEnd();
                        trimmed = line.Trim();
                        if (trimmed.Equals("</div>", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        textBlock.AppendLine(line);
                    }
                    output.Append("<div").Append(SourceLineAttr(sourceLine)).Append(" class=\"").Append(WebUtility.HtmlEncode(textBlockClass)).Append("\"");
                    if (textBlockStyle.Length > 0)
                    {
                        output.Append(" style=\"").Append(WebUtility.HtmlEncode(textBlockStyle)).Append("\"");
                    }
                    output.Append(">")
                        .Append(RenderTextBlockInnerHtml(textBlock.ToString().TrimEnd()))
                        .AppendLine("</div>");
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

                if (trimmed.StartsWith("{% include inline-image.html", StringComparison.Ordinal))
                {
                    flushParagraph();
                    closeList();
                    flushTable();
                    flushQuote();
                    var include = new StringBuilder();
                    include.AppendLine(trimmed);
                    while (!trimmed.Contains("%}") && i + 1 < rawLines.Length)
                    {
                        i++;
                        trimmed = rawLines[i].Trim();
                        include.AppendLine(trimmed);
                    }
                    output.Append(RenderInlineImageInclude(include.ToString(), mediaSubpath, sourceLine));
                    continue;
                }

                if (IsTableLine(trimmed))
                {
                    flushParagraph();
                    closeList();
                    flushQuote();
                    if (tableSourceLine < 0)
                    {
                        tableSourceLine = sourceLine;
                    }
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
                    output.Append("<h").Append(level).Append(SourceLineAttr(sourceLine)).Append(">")
                        .Append(RenderInline(heading.Groups[2].Value, mediaSubpath))
                        .Append("</h").Append(level).AppendLine(">");
                    continue;
                }

                if (trimmed.StartsWith(">"))
                {
                    flushParagraph();
                    closeList();
                    if (quoteSourceLine < 0)
                    {
                        quoteSourceLine = sourceLine;
                    }
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
                        output.Append("<ul").Append(SourceLineAttr(sourceLine)).AppendLine(">");
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
                        output.Append("<ol").Append(SourceLineAttr(sourceLine)).AppendLine(">");
                        inOrderedList = true;
                    }
                    output.Append("<li>").Append(RenderInline(ordered.Groups[1].Value, mediaSubpath)).AppendLine("</li>");
                    continue;
                }

                if (paragraph.Length > 0)
                {
                    paragraph.Append(" ");
                }
                else
                {
                    paragraphSourceLine = sourceLine;
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

        private static string SourceLineAttr(int line)
        {
            return line >= 0 ? " data-src-line=\"" + line + "\"" : "";
        }

        private string BuildPreviewFontFaces()
        {
            var fonts = new[]
            {
                new[] { "NV Zhi Mang Xing", "zhimangxing", "ZhiMangXing-Regular.ttf" },
                new[] { "NV Long Cang", "longcang", "LongCang-Regular.ttf" },
                new[] { "NV Liu Jian Mao Cao", "liujianmaocao", "LiuJianMaoCao-Regular.ttf" },
                new[] { "NV Ma Shan Zheng", "mashanzheng", "MaShanZheng-Regular.ttf" },
                new[] { "NV ZCOOL XiaoWei", "zcoolxiaowei", "ZCOOLXiaoWei-Regular.ttf" }
            };
            var css = new StringBuilder();
            foreach (string[] font in fonts)
            {
                string path = Path.Combine(repoRoot, "assets", "fonts", "chinese-display", font[1], font[2]);
                if (!File.Exists(path))
                {
                    continue;
                }
                css.Append("@font-face{font-family:'").Append(font[0]).Append("';src:url('")
                    .Append(new Uri(path).AbsoluteUri)
                    .Append("') format('truetype');font-display:swap;}");
            }
            return css.ToString();
        }

        private static bool IsTextBlockStart(string trimmed)
        {
            return Regex.IsMatch(trimmed, "^<div\\s+[^>]*class\\s*=\\s*(['\\\"])[^'\\\"]*\\bnv-text-block\\b[^'\\\"]*\\1[^>]*>", RegexOptions.IgnoreCase);
        }

        private static string ExtractTextBlockClasses(string line)
        {
            Match match = Regex.Match(line, "\\bclass\\s*=\\s*(['\\\"])([^'\\\"]*)\\1", RegexOptions.IgnoreCase);
            var classes = new List<string>();
            classes.Add("nv-text-block");
            if (!match.Success)
            {
                return string.Join(" ", classes.ToArray());
            }

            foreach (string rawClass in match.Groups[2].Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string className = rawClass.Trim();
                if (className == "nv-text-block")
                {
                    continue;
                }
                if (className == "nv-font-script" || className == "nv-font-serif" || className == "nv-font-mono" ||
                    className == "nv-font-cn-script" || className == "nv-font-cn-longcang" ||
                    className == "nv-font-cn-zhimang" || className == "nv-font-cn-maocao" || className == "nv-font-cn-mashan" ||
                    className == "nv-font-cn-xiaowei" || className == "nv-align-center" ||
                    className == "nv-align-right" || className == "nv-bilingual")
                {
                    classes.Add(className);
                }
            }

            return string.Join(" ", classes.ToArray());
        }

        private static string ExtractTextBlockStyle(string line)
        {
            Match match = Regex.Match(line, "\\bstyle\\s*=\\s*(['\\\"])([^'\\\"]*)\\1", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return "";
            }

            Match color = Regex.Match(match.Groups[2].Value, "--nv-text-block-bg\\s*:\\s*(#[0-9a-fA-F]{6})\\s*;?", RegexOptions.IgnoreCase);
            return color.Success ? "--nv-text-block-bg: " + color.Groups[1].Value + ";" : "";
        }

        private static string RenderTextBlockInnerHtml(string content)
        {
            var replacements = new Dictionary<string, string>();
            int index = 0;
            string tokenized = Regex.Replace(content, "</span>|<span\\s+[^>]*class\\s*=\\s*(['\\\"])([^'\\\"]*)\\1[^>]*>", delegate(Match match)
            {
                string html;
                if (match.Value.StartsWith("</", StringComparison.Ordinal))
                {
                    html = "</span>";
                }
                else
                {
                    string classes = ExtractAllowedTextSpanClasses(match.Groups[2].Value);
                    if (classes.Length == 0)
                    {
                        return match.Value;
                    }
                    html = "<span class=\"" + WebUtility.HtmlEncode(classes) + "\">";
                }

                string token = "%%NVSPAN" + index.ToString(CultureInfo.InvariantCulture) + "%%";
                index++;
                replacements[token] = html;
                return token;
            }, RegexOptions.IgnoreCase);

            string encoded = WebUtility.HtmlEncode(tokenized);
            foreach (KeyValuePair<string, string> replacement in replacements)
            {
                encoded = encoded.Replace(replacement.Key, replacement.Value);
            }
            return encoded;
        }

        private static string ExtractAllowedTextSpanClasses(string classValue)
        {
            var classes = new List<string>();
            foreach (string rawClass in classValue.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string className = rawClass.Trim();
                if (className == "nv-text-original" || className == "nv-text-translation" ||
                    className == "nv-font-cn-script" || className == "nv-font-cn-longcang" ||
                    className == "nv-font-cn-zhimang" || className == "nv-font-cn-maocao" || className == "nv-font-cn-mashan" ||
                    className == "nv-font-cn-xiaowei" || className == "nv-align-center" ||
                    className == "nv-align-right")
                {
                    classes.Add(className);
                }
            }
            return string.Join(" ", classes.ToArray());
        }

        private static string ExtractTextBlockFirstLine(string line)
        {
            Match match = Regex.Match(line, "^\\s*<div\\s+[^>]*class\\s*=\\s*(['\\\"])[^'\\\"]*\\bnv-text-block\\b[^'\\\"]*\\1[^>]*>(.*)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return "";
            }

            return Regex.Replace(match.Groups[2].Value, "</div>\\s*$", "", RegexOptions.IgnoreCase);
        }

        private string RenderInlineImageInclude(string includeText, string mediaSubpath, int sourceLine)
        {
            Dictionary<string, string> attributes = ParseIncludeAttributes(includeText);
            string src;
            if (!attributes.TryGetValue("src", out src) || string.IsNullOrWhiteSpace(src))
            {
                return "";
            }

            string alt;
            attributes.TryGetValue("alt", out alt);
            string caption;
            attributes.TryGetValue("caption", out caption);
            string href;
            attributes.TryGetValue("href", out href);
            string align;
            attributes.TryGetValue("align", out align);

            string figureClass = "inline-illustration" + (string.Equals(align, "left", StringComparison.OrdinalIgnoreCase) ? " inline-left" : "");
            string resolvedSrc = ResolveImageSource(src, mediaSubpath);
            string resolvedHref = string.IsNullOrWhiteSpace(href) ? "" : ResolveImageSource(href, mediaSubpath);

            var output = new StringBuilder();
            output.Append("<figure").Append(SourceLineAttr(sourceLine)).Append(" class=\"").Append(figureClass).Append("\">");
            if (!string.IsNullOrWhiteSpace(resolvedHref))
            {
                output.Append("<a href=\"").Append(WebUtility.HtmlEncode(resolvedHref)).Append("\">");
            }
            output.Append("<img src=\"").Append(WebUtility.HtmlEncode(resolvedSrc)).Append("\" alt=\"").Append(WebUtility.HtmlEncode(alt ?? "")).Append("\">");
            if (!string.IsNullOrWhiteSpace(resolvedHref))
            {
                output.Append("</a>");
            }
            if (!string.IsNullOrWhiteSpace(caption))
            {
                output.Append("<figcaption>").Append(WebUtility.HtmlEncode(caption)).Append("</figcaption>");
            }
            output.AppendLine("</figure>");
            return output.ToString();
        }

        private static Dictionary<string, string> ParseIncludeAttributes(string includeText)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(includeText, "(\\w+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')"))
            {
                attributes[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            }
            return attributes;
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

        private static int GetBodyStartLine(string text)
        {
            int close = FindFrontMatterClose(text);
            if (close < 0)
            {
                return 0;
            }

            int after = text.IndexOf('\n', close);
            if (after < 0)
            {
                return 0;
            }

            int line = 0;
            for (int i = 0; i <= after && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
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

        private static string SanitizePostSlug(string title)
        {
            string cleaned = SanitizeFileName(title).ToLowerInvariant();
            cleaned = Regex.Replace(cleaned, @"[^\w\u3400-\u9fff\-]+", "-");
            cleaned = Regex.Replace(cleaned, "-+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(cleaned) ? "new-post" : cleaned;
        }

        private static string SanitizeFolderSegment(string name)
        {
            string cleaned = SanitizePostSlug(name);
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

        private sealed class GitResult
        {
            public int ExitCode;
            public string Output = "";
            public string Error = "";
            public bool Success { get { return ExitCode == 0; } }
        }

        private sealed class CopiedImage
        {
            public string FileName = "";
            public string Alt = "";
        }

        private sealed class ImageCopyResult
        {
            public string MediaSubpath = "";
            public string TargetDirectory = "";
            public List<CopiedImage> Images = new List<CopiedImage>();
        }

        private sealed class GuidePair
        {
            public string Label;
            public string Value;

            public GuidePair(string label, string value)
            {
                Label = label ?? "";
                Value = value ?? "";
            }
        }

        private sealed class RoamGuideData
        {
            public string InfoKicker = "";
            public string InfoText = "";
            public string NowKicker = "";
            public string NowTitle = "";
            public string NowSummary = "";
            public string NowUpdated = "";
            public List<GuidePair> SignalItems = new List<GuidePair>();
            public List<GuidePair> StatusItems = new List<GuidePair>();
            public List<GuidePair> RoadmapItems = new List<GuidePair>();
            public List<GuidePair> ReadingStackItems = new List<GuidePair>();
            public List<GuidePair> VisualStackItems = new List<GuidePair>();
        }

        private sealed class PublishProgressForm : Form
        {
            private readonly Label stepLabel;
            private readonly Label commandLabel;
            private readonly TextBox logBox;
            private readonly ProgressBar progressBar;
            private int stepCount;

            public PublishProgressForm(string title)
            {
                Text = title;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = false;
                MaximizeBox = false;
                ClientSize = new Size(680, 420);
                BackColor = Surface;
                ForeColor = TextPrimary;

                var root = new TableLayoutPanel();
                root.Dock = DockStyle.Fill;
                root.BackColor = Surface;
                root.Padding = new Padding(16);
                root.ColumnCount = 1;
                root.RowCount = 4;
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                Controls.Add(root);

                stepLabel = new Label();
                stepLabel.Dock = DockStyle.Fill;
                stepLabel.Text = "Preparing...";
                stepLabel.ForeColor = TextPrimary;
                stepLabel.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
                root.Controls.Add(stepLabel, 0, 0);

                commandLabel = new Label();
                commandLabel.Dock = DockStyle.Fill;
                commandLabel.Text = "";
                commandLabel.ForeColor = TextMuted;
                commandLabel.Font = new Font("Consolas", 8.5f);
                root.Controls.Add(commandLabel, 0, 1);

                progressBar = new ProgressBar();
                progressBar.Dock = DockStyle.Fill;
                progressBar.Style = ProgressBarStyle.Marquee;
                progressBar.MarqueeAnimationSpeed = 32;
                root.Controls.Add(progressBar, 0, 2);

                logBox = new TextBox();
                logBox.Dock = DockStyle.Fill;
                logBox.Multiline = true;
                logBox.ReadOnly = true;
                logBox.ScrollBars = ScrollBars.Vertical;
                logBox.BackColor = Color.FromArgb(11, 13, 17);
                logBox.ForeColor = TextMuted;
                logBox.BorderStyle = BorderStyle.FixedSingle;
                logBox.Font = new Font("Consolas", 9f);
                root.Controls.Add(logBox, 0, 3);
            }

            public void BeginStep(string step, string command)
            {
                stepCount++;
                stepLabel.Text = "Step " + stepCount + ": " + step;
                commandLabel.Text = command;
                AppendLog("");
                AppendLog(">> " + step);
                AppendLog(command);
                Application.DoEvents();
            }

            public void AppendGitResult(GitResult result)
            {
                AppendLog("Exit code: " + result.ExitCode);
                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    AppendLog(result.Output.Trim());
                }
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    AppendLog(result.Error.Trim());
                }
                Application.DoEvents();
            }

            public void AppendLog(string text)
            {
                if (logBox.IsDisposed)
                {
                    return;
                }
                logBox.AppendText(text + Environment.NewLine);
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            }

            public void MarkComplete(string message)
            {
                stepLabel.Text = message;
                commandLabel.Text = "Done";
                progressBar.Style = ProgressBarStyle.Blocks;
                progressBar.Value = 100;
                AppendLog("Done.");
                Application.DoEvents();
            }
        }

        private static GitResult RunGit(string arguments, string workingDirectory, int timeoutMilliseconds)
        {
            var result = new GitResult();
            try
            {
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "git";
                startInfo.Arguments = arguments;
                startInfo.WorkingDirectory = workingDirectory;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        result.ExitCode = -1;
                        result.Error = "Could not start git.";
                        return result;
                    }

                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        result.ExitCode = -1;
                        result.Error = "git " + arguments + " timed out.";
                        return result;
                    }

                    result.Output = process.StandardOutput.ReadToEnd();
                    result.Error = process.StandardError.ReadToEnd();
                    result.ExitCode = process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.Error = ex.ToString();
            }

            return result;
        }

        private GitResult RunGitStep(PublishProgressForm progress, string step, string arguments, string workingDirectory, int timeoutMilliseconds)
        {
            if (progress != null)
            {
                progress.BeginStep(step, "git " + arguments);
            }
            SetStatus(step + "...");
            GitResult result = RunGit(arguments, workingDirectory, timeoutMilliseconds);
            if (progress != null)
            {
                progress.AppendGitResult(result);
            }
            return result;
        }

        private static string QuoteArg(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private void ShowGitOutput(string title, GitResult result)
        {
            string output = (result.Output ?? "").Trim();
            string error = (result.Error ?? "").Trim();
            string message = title + Environment.NewLine + Environment.NewLine +
                "Exit code: " + result.ExitCode + Environment.NewLine + Environment.NewLine +
                (output.Length > 0 ? "Output:" + Environment.NewLine + output + Environment.NewLine + Environment.NewLine : "") +
                (error.Length > 0 ? "Error:" + Environment.NewLine + error : "");

            MessageBox.Show(this, message, "Publish to GitHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus(title);
        }

        private string PromptForText(string title, string prompt, string defaultValue)
        {
            using (var form = new Form())
            using (var label = new Label())
            using (var textBox = new TextBox())
            using (var okButton = new Button())
            using (var cancelButton = new Button())
            {
                form.Text = title;
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ClientSize = new Size(520, 136);
                form.BackColor = Surface;
                form.ForeColor = TextPrimary;

                label.Text = prompt;
                label.SetBounds(14, 14, 492, 22);
                label.ForeColor = TextMuted;

                textBox.Text = defaultValue;
                textBox.SetBounds(14, 44, 492, 26);
                textBox.BackColor = GetEditorBackColor();
                textBox.ForeColor = GetEditorTextColor();
                textBox.BorderStyle = BorderStyle.FixedSingle;

                okButton.Text = "OK";
                okButton.DialogResult = DialogResult.OK;
                okButton.SetBounds(326, 92, 84, 28);

                cancelButton.Text = "Cancel";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.SetBounds(422, 92, 84, 28);

                form.Controls.Add(label);
                form.Controls.Add(textBox);
                form.Controls.Add(okButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                return form.ShowDialog(this) == DialogResult.OK ? textBox.Text.Trim() : "";
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!ConfirmSaveChanges())
            {
                e.Cancel = true;
            }
        }

        private bool ConfirmSaveChanges()
        {
            if (!HasUnsavedChanges())
            {
                return true;
            }

            DialogResult result = MessageBox.Show(
                this,
                "Save changes before continuing?",
                "Neutriverse Writer",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (result == DialogResult.Cancel)
            {
                return false;
            }

            if (result == DialogResult.No)
            {
                return true;
            }

            return SavePost(false);
        }

        private bool HasUnsavedChanges()
        {
            return !string.Equals(editor.Text, savedTextSnapshot, StringComparison.Ordinal);
        }

        private void UpdateWindowTitle()
        {
            string name = string.IsNullOrWhiteSpace(currentFile) ? "Untitled" : Path.GetFileName(currentFile);
            Text = (HasUnsavedChanges() ? "* " : "") + name + " - Neutriverse Writer";
            UpdateUiState();
        }

        private sealed class TextBlockOptions
        {
            public string OriginalText = "";
            public bool EnableHover = true;
            public string HoverText = "";
            public string DefaultFontClass = "nv-font-script";
            public string TranslationFontClass = "nv-font-cn-script";
            public string AlignmentClass = "";
            public string BackgroundColor = "#18191b";

            public string BlockClasses
            {
                get
                {
                    var classes = new List<string>();
                    classes.Add("nv-text-block");
                    if (EnableHover)
                    {
                        classes.Add("nv-bilingual");
                    }
                    if (!string.IsNullOrWhiteSpace(DefaultFontClass))
                    {
                        classes.Add(DefaultFontClass);
                    }
                    if (!string.IsNullOrWhiteSpace(AlignmentClass))
                    {
                        classes.Add(AlignmentClass);
                    }
                    return string.Join(" ", classes.ToArray());
                }
            }

            public static TextBlockOptions Default(string selectedText)
            {
                return new TextBlockOptions
                {
                    OriginalText = string.IsNullOrWhiteSpace(selectedText) ? "Original text" : selectedText,
                    EnableHover = true,
                    HoverText = "中文译文",
                    DefaultFontClass = "nv-font-script",
                    TranslationFontClass = "nv-font-cn-script",
                    AlignmentClass = "",
                    BackgroundColor = "#18191b"
                };
            }

            public static string FontClassFromLabel(string label)
            {
                switch (label)
                {
                    case "Script English":
                        return "nv-font-script";
                    case "Serif":
                        return "nv-font-serif";
                    case "Mono":
                        return "nv-font-mono";
                    default:
                        return "";
                }
            }

            public static string TranslationFontClassFromLabel(string label)
            {
                switch (label)
                {
                    case "Zhi Mang Xing":
                        return "nv-font-cn-zhimang";
                    case "Long Cang":
                        return "nv-font-cn-longcang";
                    case "Liu Jian Mao Cao":
                        return "nv-font-cn-maocao";
                    case "ZCOOL XiaoWei":
                        return "nv-font-cn-xiaowei";
                    case "Ma Shan Zheng":
                    default:
                        return "nv-font-cn-script";
                }
            }

            public static string AlignmentClassFromLabel(string label)
            {
                switch (label)
                {
                    case "Center":
                        return "nv-align-center";
                    case "Right":
                        return "nv-align-right";
                    default:
                        return "";
                }
            }
        }

        private sealed class NeutriverseToolStripRenderer : ToolStripProfessionalRenderer
        {
            public NeutriverseToolStripRenderer()
                : base(new NeutriverseColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(Border))
                {
                    Rectangle rect = new Rectangle(0, e.ToolStrip.Height - 1, e.ToolStrip.Width, 1);
                    e.Graphics.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Top);
                }
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                PaintItemBackground(e);
            }

            protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
            {
                PaintItemBackground(e);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                PaintItemBackground(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y1 = 8;
                int y2 = e.Item.Height - 8;
                int x = e.Item.Width / 2;
                using (var pen = new Pen(Color.FromArgb(74, Border)))
                {
                    e.Graphics.DrawLine(pen, x, y1, x, y2);
                }
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Selected ? LogoGold : TextMuted;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? (e.Item.Selected ? LogoGold : TextPrimary) : TextMuted;
                base.OnRenderItemText(e);
            }

            private static void PaintItemBackground(ToolStripItemRenderEventArgs e)
            {
                var button = e.Item as ToolStripButton;
                bool active = button != null && button.Checked;
                Color fill = active ? Color.FromArgb(42, 39, 28) : e.Item.Pressed ? Color.FromArgb(24, 38, 91) : e.Item.Selected ? SurfaceSoft : SurfaceRaised;
                if (e.ToolStrip is ToolStripDropDown)
                {
                    fill = e.Item.Selected ? Color.FromArgb(31, 43, 75) : SurfaceRaised;
                }

                using (var brush = new SolidBrush(fill))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                }

                if (active || e.Item.Selected || e.Item.Pressed)
                {
                    using (var pen = new Pen(active ? LogoGold : e.Item.Pressed ? LogoBlue : Color.FromArgb(92, LogoBlue)))
                    {
                        Rectangle borderRect = new Rectangle(1, 1, e.Item.Width - 3, e.Item.Height - 3);
                        e.Graphics.DrawRectangle(pen, borderRect);
                    }
                }
            }
        }

        private sealed class NeutriverseColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin { get { return SurfaceRaised; } }
            public override Color ToolStripGradientMiddle { get { return SurfaceRaised; } }
            public override Color ToolStripGradientEnd { get { return SurfaceRaised; } }
            public override Color MenuStripGradientBegin { get { return SurfaceRaised; } }
            public override Color MenuStripGradientEnd { get { return SurfaceRaised; } }
            public override Color ImageMarginGradientBegin { get { return SurfaceRaised; } }
            public override Color ImageMarginGradientMiddle { get { return SurfaceRaised; } }
            public override Color ImageMarginGradientEnd { get { return SurfaceRaised; } }
            public override Color ToolStripDropDownBackground { get { return SurfaceRaised; } }
            public override Color MenuItemSelected { get { return SurfaceSoft; } }
            public override Color MenuItemSelectedGradientBegin { get { return SurfaceSoft; } }
            public override Color MenuItemSelectedGradientEnd { get { return SurfaceSoft; } }
            public override Color MenuItemBorder { get { return LogoBlue; } }
            public override Color ButtonSelectedGradientBegin { get { return SurfaceSoft; } }
            public override Color ButtonSelectedGradientMiddle { get { return SurfaceSoft; } }
            public override Color ButtonSelectedGradientEnd { get { return SurfaceSoft; } }
            public override Color ButtonPressedGradientBegin { get { return Color.FromArgb(24, 38, 91); } }
            public override Color ButtonPressedGradientMiddle { get { return Color.FromArgb(24, 38, 91); } }
            public override Color ButtonPressedGradientEnd { get { return Color.FromArgb(24, 38, 91); } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Border; } }
        }
    }
}
