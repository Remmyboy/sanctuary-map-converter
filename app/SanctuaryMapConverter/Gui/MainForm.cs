using System.Linq;
using System.Windows.Forms;
using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter.Gui
{
    // One window, one job: convert Supreme Commander: Forged Alliance maps
    // into Sanctuary maps, one at a time or a folder at a time, and deploy
    // them. The FA-textures mode is gated on the user's own Forged Alliance
    // install: the tool ships no GPG art, and if env.scd is not on this
    // machine the option simply is not available.
    public sealed class MainForm : Form
    {
        // -- convert --
        readonly ComboBox _mapPicker = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 430 };
        readonly RadioButton _modeFa = new() { Text = "Original FA textures (local play only)", AutoSize = true };
        readonly RadioButton _modeCc0 = new() { Text = "CC0 textures (shareable)", AutoSize = true, Checked = true };
        readonly ComboBox _convBiome = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        readonly Button _convert = new() { Text = "Convert", Width = 120, Height = 32 };
        readonly Button _convertAll = new() { Text = "Convert all", Width = 120, Height = 26 };
        readonly TextBox _mapsFolder = new() { Width = 340 };


        // -- shared --
        readonly TextBox _faPath = new() { Width = 340 };
        readonly TextBox _sanctuaryPath = new() { Width = 340 };
        readonly CheckBox _deploy = new() { Text = "Deploy to Sanctuary (both game and editor)", AutoSize = true, Checked = true };
        readonly CheckBox _border = new() { Text = "Author a border around the map (the engine mirrors the map without one)", AutoSize = true, Checked = true };
        readonly TextBox _log = new()
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Font = new System.Drawing.Font("Consolas", 9f),
        };

        readonly List<string> _mapFolders = new();
        string _packDir, _tableCsv;
        UserSettings _settings = new();

        public MainForm()
        {
            Text = "SCFA > Sanctuary Map Converter";
            Width = 940; Height = 760;
            StartPosition = FormStartPosition.CenterScreen;

            var paths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 10, 10, 0) };
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddRow(paths, "Forged Alliance install", _faPath, MakeButton("Browse...", () => PickFolder(_faPath, RefreshFaGate)));
            AddRow(paths, "Sanctuary install", _sanctuaryPath, MakeButton("Browse...", () => PickFolder(_sanctuaryPath, null)));
            paths.Controls.Add(new Label());
            paths.Controls.Add(_deploy);
            paths.Controls.Add(new Label());
            paths.Controls.Add(new Label());
            paths.Controls.Add(_border);
            paths.Controls.Add(new Label());

            var convertBox = new GroupBox { Text = "Convert a Supreme Commander: Forged Alliance map", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
            var ct = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // Two ways in: point at a folder full of maps and pick from the
            // list, or browse straight to one map. Auto-detection fills the
            // first in when it can, and whatever is chosen is remembered.
            AddRow(ct, "Maps folder", _mapsFolder, MakeButton("Browse...", PickMapsFolder));
            AddRow(ct, "Source map", _mapPicker, MakeButton("One map...", PickSourceFolder));
            var modes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            modes.Controls.Add(_modeCc0);
            modes.Controls.Add(_modeFa);
            ct.Controls.Add(new Label { Text = "Textures", AutoSize = true, Anchor = AnchorStyles.Left });
            ct.Controls.Add(modes);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Anchor = AnchorStyles.Right };
            buttons.Controls.Add(_convert);
            buttons.Controls.Add(_convertAll);
            ct.Controls.Add(buttons);
            // The biome is the lighting/fog base; the source map's own sun and
            // fog override what translates.
            _convBiome.Items.AddRange(new object[] { "Tropical", "Highlands", "Winter", "Evergreen", "Arid" });
            _convBiome.SelectedIndex = 0;
            AddRow(ct, "Lighting biome", _convBiome, new Label());
            convertBox.Controls.Add(ct);

            Controls.Add(_log);
            // Dock order: last added Top control sits highest.
            Controls.Add(convertBox);
            Controls.Add(paths);

            _convert.Click += (_, _) => RunConvert();
            _convertAll.Click += (_, _) => RunConvertAll();
            Load += (_, _) => Detect();
        }

        static Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => onClick();
            return b;
        }

        static void AddRow(TableLayoutPanel t, string label, Control mid, Control right)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            mid.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(mid);
            t.Controls.Add(right);
        }

        void Detect()
        {
            // A remembered path wins over detection: the user pointed at it
            // for a reason, and re-detecting would overrule them every launch.
            _settings = UserSettings.Load();
            _faPath.Text = Pick(_settings.FaInstall, () => GamePaths.FindFaInstall());
            _sanctuaryPath.Text = Pick(_settings.SanctuaryInstall, () => GamePaths.FindSanctuaryInstall());
            _mapsFolder.Text = _settings.MapsFolder is string m && Directory.Exists(m) ? m : "";
            (_packDir, _tableCsv) = GamePaths.DataFiles(GamePaths.FindDataDir());

            if (!GamePaths.HaveCc0Data(_packDir, _tableCsv))
            {
                _modeCc0.Enabled = false;
                _modeCc0.Text = "CC0 textures (needs the data folder - see README)";
                _modeFa.Checked = true;
            }
            RefreshFaGate();
            RefreshMapList();

            Log("SCFA > Sanctuary Map Converter");
            Log($"  Forged Alliance: {(_faPath.Text.Length > 0 ? _faPath.Text : "not found - FA-textures mode disabled")}");
            Log($"  Sanctuary:       {(_sanctuaryPath.Text.Length > 0 ? _sanctuaryPath.Text : "not found - set it to deploy")}");
            Log($"  CC0 library:     {(_modeCc0.Enabled ? _packDir : "missing")}");
            if (_mapFolders.Count > 0)
                Log($"  {_mapFolders.Count} source maps found. Convert one, or convert them all.");
            else
                Log("  No source maps found - set 'Maps folder' to your Forged Alliance maps folder " +
                    "(its own \\maps, or Documents\\My Games\\Gas Powered Games\\Supreme Commander Forged Alliance\\Maps).");
            Log("");
        }

        static string Pick(string saved, Func<string> detect) =>
            !string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved) ? saved : (detect() ?? "");

        void SaveSettings()
        {
            _settings.FaInstall = _faPath.Text;
            _settings.SanctuaryInstall = _sanctuaryPath.Text;
            _settings.MapsFolder = _mapsFolder.Text;
            _settings.Save();
        }

        void RefreshFaGate()
        {
            bool haveFa = _faPath.Text.Length > 0 && File.Exists(GamePaths.ScdPath(_faPath.Text));
            _modeFa.Enabled = haveFa;
            if (!haveFa)
            {
                _modeFa.Text = "Original FA textures (needs your Forged Alliance install)";
                if (_modeFa.Checked && _modeCc0.Enabled) _modeCc0.Checked = true;
            }
            else _modeFa.Text = "Original FA textures (local play only)";
        }

        void RefreshMapList()
        {
            _mapFolders.Clear();
            _mapPicker.Items.Clear();

            // The chosen maps folder first, then whatever detection finds, and
            // never the same folder twice - the two overlap whenever the user
            // picks a folder we would have found anyway.
            var roots = new List<string>();
            if (_mapsFolder.Text.Length > 0 && Directory.Exists(_mapsFolder.Text)) roots.Add(_mapsFolder.Text);
            roots.AddRange(GamePaths.SourceMapRoots(_faPath.Text.Length > 0 ? _faPath.Text : null));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (!seen.Add(Path.GetFullPath(root))) continue;
                // A folder holding one map, rather than a folder of maps, is
                // what someone naturally browses to - so accept both.
                if (HasMap(root)) { AddMap(root, seen); continue; }
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(root); } catch { continue; }
                foreach (var dir in subs) if (HasMap(dir)) AddMap(dir, seen);
            }
            if (_mapPicker.Items.Count > 0) _mapPicker.SelectedIndex = 0;
            _convertAll.Enabled = _mapFolders.Count > 0;
            _convertAll.Text = _mapFolders.Count > 0 ? $"Convert all {_mapFolders.Count}" : "Convert all";
        }

        static bool HasMap(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*.scmap").Any(); }
            catch { return false; }
        }

        void AddMap(string dir, HashSet<string> seen)
        {
            if (!seen.Add(Path.GetFullPath(dir))) return;
            _mapFolders.Add(dir);
            _mapPicker.Items.Add(Path.GetFileName(dir));
        }

        void PickMapsFolder()
        {
            using var d = new FolderBrowserDialog
            {
                Description = "Your Forged Alliance maps folder - the one holding a folder per map",
                SelectedPath = _mapsFolder.Text.Length > 0 ? _mapsFolder.Text : "",
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            _mapsFolder.Text = d.SelectedPath;
            RefreshMapList();
            SaveSettings();
            Log(_mapFolders.Count > 0
                ? $"  {_mapFolders.Count} source maps found in {d.SelectedPath}"
                : $"  no .scmap found under {d.SelectedPath} - pick the folder that holds a folder per map");
        }

        void PickSourceFolder()
        {
            using var d = new FolderBrowserDialog { Description = "Folder containing the .scmap" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (!HasMap(d.SelectedPath)) { Log($"  no .scmap in {d.SelectedPath}"); return; }
            _mapFolders.Add(d.SelectedPath);
            _mapPicker.Items.Add(Path.GetFileName(d.SelectedPath));
            _mapPicker.SelectedIndex = _mapPicker.Items.Count - 1;
            _convertAll.Enabled = true;
        }

        void PickFolder(TextBox target, Action after)
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog(this) != DialogResult.OK) return;
            target.Text = d.SelectedPath;
            after?.Invoke();
            RefreshMapList();
            SaveSettings();
        }

        string OutputRoot(string sanctuary) => sanctuary.Length > 0
            ? GamePaths.EngineMaps(sanctuary)
            : Path.Combine(AppContext.BaseDirectory, "converted");

        void RunConvert()
        {
            if (_mapPicker.SelectedIndex < 0) { Log("pick a map first"); return; }
            string source = _mapFolders[_mapPicker.SelectedIndex];
            bool cc0 = _modeCc0.Checked;
            string sanctuary = _sanctuaryPath.Text;
            bool deploy = _deploy.Checked && sanctuary.Length > 0;

            var o = new ConvertOptions
            {
                Source = source,
                Cc0Textures = cc0,
                Border = _border.Checked,
                Biome = (string)_convBiome.SelectedItem,
                ScdPath = _faPath.Text.Length > 0 ? GamePaths.ScdPath(_faPath.Text) : null,
                PackDir = _packDir,
                TableCsv = _tableCsv,
                OutputMapsRoot = OutputRoot(sanctuary),
            };

            RunJob(_convert, $"- converting {Path.GetFileName(source)} ({(cc0 ? "CC0 textures" : "FA textures")}) -", log =>
            {
                var result = new Converter(o, log).Run();
                if (deploy) Deployer.Deploy(result.MapDir, sanctuary, log);
                log($"DONE  {result.DisplayName}: {result.Spawns} spawns, {result.Alloys} alloys, {result.Props:n0} props");
            });
        }

        /// Convert every map in the list. One failure - a campaign map with no
        /// spawns, a pre-Forged-Alliance format - must not stop the rest, so
        /// each is caught and counted and the run carries on.
        void RunConvertAll()
        {
            if (_mapFolders.Count == 0) { Log("no maps listed - set the maps folder first"); return; }
            var sources = _mapFolders.ToList();
            bool cc0 = _modeCc0.Checked;
            bool border = _border.Checked;
            string biome = (string)_convBiome.SelectedItem;
            string scd = _faPath.Text.Length > 0 ? GamePaths.ScdPath(_faPath.Text) : null;
            string sanctuary = _sanctuaryPath.Text;
            bool deploy = _deploy.Checked && sanctuary.Length > 0;
            string outRoot = OutputRoot(sanctuary);
            string packDir = _packDir, tableCsv = _tableCsv;

            RunJob(_convertAll, $"- converting {sources.Count} maps ({(cc0 ? "CC0 textures" : "FA textures")}) -", log =>
            {
                int ok = 0;
                var failed = new List<string>();
                foreach (var src in sources)
                {
                    string name = Path.GetFileName(src);
                    try
                    {
                        var result = new Converter(new ConvertOptions
                        {
                            Source = src, Cc0Textures = cc0, Biome = biome, ScdPath = scd, Border = border,
                            PackDir = packDir, TableCsv = tableCsv, OutputMapsRoot = outRoot,
                        }, _ => { }).Run();
                        if (deploy) Deployer.Deploy(result.MapDir, sanctuary, _ => { });
                        ok++;
                        log($"  OK    {result.DisplayName}");
                    }
                    catch (Exception e)
                    {
                        failed.Add(name);
                        log($"  SKIP  {name}: {e.Message}");
                    }
                }
                log($"DONE  {ok} converted, {failed.Count} skipped -> {outRoot}");
            });
        }

        void RunJob(Button button, string banner, Action<Action<string>> work)
        {
            button.Enabled = false;
            Log(banner);
            void UiLog(string s) => Invoke(() => Log(s));
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    work(UiLog);
                    UiLog("Restart Sanctuary before loading new maps - it caches map files at launch.");
                    UiLog("");
                }
                catch (Exception e)
                {
                    UiLog("FAILED  " + e.Message);
                    UiLog("");
                }
                finally
                {
                    Invoke(() => button.Enabled = true);
                }
            });
        }

        void Log(string s) => _log.AppendText(s + Environment.NewLine);
    }
}
