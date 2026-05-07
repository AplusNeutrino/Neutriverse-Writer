using System;
using System.Collections.Generic;
using System.Drawing;
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
            split.Panel1.Controls.Add(preview);

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
                }
            };
            editor.DragEnter += EditorDragEnter;
            editor.DragDrop += EditorDragDrop;
            split.Panel2.Controls.Add(editor);

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
            statusStrip.Items.Add(status);
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

            AddDropdown(toolbar, "File", new Dictionary<string, EventHandler>
            {
                { "New", delegate { NewDraft(); } },
                { "Open", delegate { OpenPost(); } },
                { "Save", delegate { SavePost(false); } },
                { "Save As", delegate { SavePost(true); } }
            });
            toolbar.Items.Add(new ToolStripSeparator());
            AddButton(toolbar, "Insert Image", delegate { InsertImagesFromDialog(); });
            AddButton(toolbar, "Refresh", delegate { RenderPreview(); });
            previewModeButton = AddToggleButton(toolbar, "Live", delegate { TogglePreviewMode(); });
            previewModeButton.Checked = true;
            eyeButton = AddToggleButton(toolbar, "Eye", delegate { ToggleComfortMode(); });
            toolbar.Items.Add(new ToolStripSeparator());

            AddDropdown(toolbar, "H", new Dictionary<string, EventHandler>
            {
                { "H1", delegate { PrefixLines("# "); } },
                { "H2", delegate { PrefixLines("## "); } },
                { "H3", delegate { PrefixLines("### "); } }
            });
            AddButton(toolbar, "Quote", delegate { PrefixLines("> "); });
            AddDropdown(toolbar, "List", new Dictionary<string, EventHandler>
            {
                { "Unordered", delegate { PrefixLines("- "); } },
                { "Ordered", delegate { NumberLines(); } }
            });
            AddDropdown(toolbar, "Table", new Dictionary<string, EventHandler>
            {
                { "2 columns", delegate { InsertTable(2, 2); } },
                { "3 columns", delegate { InsertTable(3, 3); } },
                { "4 columns", delegate { InsertTable(4, 3); } }
            });
            AddDropdown(toolbar, "Code", new Dictionary<string, EventHandler>
            {
                { "Inline Code", delegate { WrapSelection("`", "`"); } },
                { "Code Block", delegate { WrapSelection("```" + Environment.NewLine, Environment.NewLine + "```"); } }
            });
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

        private ToolStripButton AddButton(ToolStrip toolbar, string text, EventHandler click)
        {
            var button = new ToolStripButton(text);
            StyleToolButton(button, toolbar, text);
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
            var dropdown = new ToolStripDropDownButton(text);
            dropdown.AutoSize = false;
            dropdown.Width = Math.Max(44, TextRenderer.MeasureText(text, toolbar.Font).Width + 26);
            dropdown.Height = 30;
            dropdown.Padding = new Padding(8, 0, 8, 0);
            dropdown.Margin = new Padding(1, 0, 1, 0);
            dropdown.DisplayStyle = ToolStripItemDisplayStyle.Text;
            dropdown.ForeColor = TextPrimary;
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
                    dialog.FileName = date + "-" + SanitizeFileName(title) + ".md";
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
            if (!livePreview)
            {
                previewDirty = true;
                return;
            }

            previewTimer.Stop();
            previewTimer.Start();
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
        }

        private void ApplyEditorTheme()
        {
            editor.BackColor = GetEditorBackColor();
            editor.ForeColor = GetEditorTextColor();
            editor.SelectionColor = editor.ForeColor;
            split.Panel2.BackColor = editor.BackColor;
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
            string baseCss =
                "html,body{background:" + previewBack + "!important;color:" + previewText + "!important;}" +
                "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Noto Sans SC','Microsoft YaHei',sans-serif;line-height:1.75;padding:2rem 2.35rem;max-width:860px;margin:0 auto;}" +
                ".content{font-size:1.03rem;letter-spacing:0;}" +
                ".content p{margin:0 0 1rem;}" +
                ".content h1,.content h2,.content h3,.content h4{color:" + previewHeading + "!important;font-weight:500;line-height:1.35;margin:2.1rem 0 1rem;}" +
                ".content>h1:first-child{margin-top:0;font-size:2.05rem;font-weight:500;}" +
                ".content h1{font-size:2.05rem}.content h2{font-size:1.65rem}.content h3{font-size:1.35rem}.content h4{font-size:1.18rem}" +
                "strong{font-weight:700;color:" + previewHeading + ";} em{font-style:italic;} del{text-decoration:line-through;} u{text-decoration:underline;}" +
                "a{color:#8fb7ff;} img{max-width:100%;height:auto;border-radius:.35rem;}" +
                "hr{height:1px;margin:2rem 0;border:0;background:#30363d;}" +
                ".content ul,.content ol{margin:0 0 1rem 1.35rem;padding-left:1.25rem}.content li{margin:.35rem 0}.content li::marker{color:#9aa4b2;}" +
                ".content blockquote{margin:1.2rem 0 1.2rem 1rem;padding:.1rem 0 .1rem 1rem;border-left:.22rem solid #4f6fff!important;background:transparent!important;color:" + previewText + "!important;}" +
                ".content blockquote p{margin:.45rem 0;color:" + previewText + "!important;}" +
                ".content code{font-family:Consolas,'Cascadia Mono',monospace;font-size:.92em;background:" + previewInlineCode + "!important;color:" + previewHeading + "!important;border-radius:0;padding:.13rem .34rem;}" +
                ".content pre{margin:1rem 0 1.25rem;padding:1rem;background:" + previewCodeBack + "!important;color:" + previewHeading + "!important;border-radius:0;overflow:auto;white-space:pre-wrap;word-wrap:break-word;}" +
                ".content pre code{display:block;padding:0;background:transparent!important;color:inherit!important;border:0;}" +
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
