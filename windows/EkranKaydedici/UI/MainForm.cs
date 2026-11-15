using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EkranKaydedici.Models;
using EkranKaydedici.Services;

namespace EkranKaydedici.UI;

public sealed class MainForm : Form
{
    private const int HotkeyF8Id = 0x5801;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF8 = 0x77;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static readonly Color BgWindow = Color.FromArgb(24, 24, 30);
    private static readonly Color BgCard = Color.FromArgb(34, 34, 44);
    private static readonly Color BorderCard = Color.FromArgb(54, 54, 68);
    private static readonly Color AccentBlue = Color.FromArgb(53, 132, 228);
    private static readonly Color AccentGreen = Color.FromArgb(46, 194, 126);
    private static readonly Color AccentRed = Color.FromArgb(224, 27, 36);
    private static readonly Color TextPrimary = Color.FromArgb(242, 242, 247);
    private static readonly Color TextMuted = Color.FromArgb(165, 165, 180);

    private readonly AppConfig _config;
    private readonly ReplayService _replay;
    private readonly EventWaitHandle _showEvent;

    private bool _allowShow;
    private bool _reallyClosing;
    private bool _syncingUi;
    private bool _isSavingClip;
    private string? _lastSavedFile;
    private DateTime _manualStartUtc;

    private readonly Label _dotLabel;
    private readonly Label _headlineLabel;
    private readonly Label _statusLabel;
    private readonly Button _clipButton;
    private readonly CheckBox _chkReplay;
    private readonly ComboBox _cmbDuration;
    private readonly CheckBox _chkSysAudio;
    private readonly CheckBox _chkMic;
    private readonly CheckBox _chkStartup;
    private readonly RadioButton _radFullscreen;
    private readonly RadioButton _radRegion;
    private readonly Button _manualButton;
    private readonly Panel _lastCard;
    private readonly Label _lastFileLabel;
    private readonly Label _lastSubLabel;
    private readonly System.Windows.Forms.Timer _manualTimer;
    private readonly NotifyIcon _trayIcon;

    public MainForm(bool startInBackground, EventWaitHandle showEvent)
    {
        _allowShow = !startInBackground;
        _showEvent = showEvent;
        _config = AppConfig.Load();
        _replay = new ReplayService();
        _lastSavedFile = ReplayService.GetLatestRecording();

        Text = "Ekran Kaydedici";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgWindow;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(390, 520);

        var appIcon = LoadEmbeddedIcon();
        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        // Tray icon
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Pencereyi Aç", null, (_, _) => ShowMainWindow());
        trayMenu.Items.Add("Klip Kaydet (F8)", null, (_, _) => _ = TriggerSaveClipAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Çıkış", null, (_, _) => QuitCompletely());

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "Ekran Kaydedici (F8)",
            ContextMenuStrip = trayMenu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };

        // Status Card
        var statusCard = CreateCard(16, 14, 358, 72);
        _dotLabel = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(16, 14),
        };
        _headlineLabel = new Label
        {
            Text = "Hazır",
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(40, 12),
        };
        _statusLabel = new Label
        {
            Text = "Anlık Tekrar hazırlanıyor...",
            Font = new Font("Segoe UI", 8.8f),
            ForeColor = TextMuted,
            AutoSize = false,
            Size = new Size(326, 20),
            Location = new Point(18, 42),
        };
        statusCard.Controls.AddRange([_dotLabel, _headlineLabel, _statusLabel]);
        Controls.Add(statusCard);

        // Primary Medal F8 Clip Button
        _clipButton = new Button
        {
            Text = "Son 30 Saniyeyi Kaydet (F8)",
            Location = new Point(16, 98),
            Size = new Size(358, 42),
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentBlue,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _clipButton.FlatAppearance.BorderSize = 0;
        _clipButton.Click += (_, _) => _ = TriggerSaveClipAsync();
        Controls.Add(_clipButton);

        // Settings Card
        var settingsCard = CreateCard(16, 152, 358, 174);
        _chkReplay = CreateCheck("Anlık Tekrar (Medal Modu · F8)", 14, 12);
        _chkReplay.CheckedChanged += (_, _) => OnSettingsChanged();

        var lblDur = new Label
        {
            Text = "Klip Süresi:",
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(14, 46),
        };
        _cmbDuration = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(216, 42),
            Size = new Size(126, 26),
            BackColor = Color.FromArgb(46, 46, 58),
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
        };
        foreach (var sec in AppConfig.ReplayDurationOptions)
        {
            _cmbDuration.Items.Add($"{sec} saniye");
        }
        _cmbDuration.SelectedIndexChanged += (_, _) => OnSettingsChanged();

        _chkSysAudio = CreateCheck("Sistem Sesi", 14, 78);
        _chkSysAudio.CheckedChanged += (_, _) => OnSettingsChanged();

        _chkMic = CreateCheck("Mikrofon", 14, 108);
        _chkMic.CheckedChanged += (_, _) => OnSettingsChanged();

        _chkStartup = CreateCheck("Windows ile arka planda başlat", 14, 138);
        _chkStartup.CheckedChanged += (_, _) => OnSettingsChanged();

        settingsCard.Controls.AddRange([_chkReplay, lblDur, _cmbDuration, _chkSysAudio, _chkMic, _chkStartup]);
        Controls.Add(settingsCard);

        // Manual Recording Card
        var manualCard = CreateCard(16, 338, 358, 92);
        var lblManual = new Label
        {
            Text = "Manuel Kayıt:",
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(14, 10),
        };
        _radFullscreen = new RadioButton
        {
            Text = "Tam Ekran",
            ForeColor = TextPrimary,
            AutoSize = true,
            Checked = true,
            Location = new Point(120, 9),
        };
        _radRegion = new RadioButton
        {
            Text = "Bölge Seç",
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(235, 9),
        };
        _manualButton = new Button
        {
            Text = "Manuel Kaydı Başlat",
            Location = new Point(14, 40),
            Size = new Size(330, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 52, 66),
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _manualButton.FlatAppearance.BorderSize = 0;
        _manualButton.Click += (_, _) => OnManualButtonClick();

        manualCard.Controls.AddRange([lblManual, _radFullscreen, _radRegion, _manualButton]);
        Controls.Add(manualCard);

        // Last Recording Card
        _lastCard = CreateCard(16, 442, 358, 64);
        _lastFileLabel = new Label
        {
            Text = "",
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = false,
            Size = new Size(220, 20),
            Location = new Point(14, 11),
        };
        _lastSubLabel = new Label
        {
            Text = "",
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8f),
            AutoSize = false,
            Size = new Size(220, 18),
            Location = new Point(14, 34),
        };
        var playBtn = new Button
        {
            Text = "Oynat",
            Location = new Point(236, 16),
            Size = new Size(52, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 52, 66),
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand,
        };
        playBtn.FlatAppearance.BorderSize = 0;
        playBtn.Click += (_, _) => OpenLastFile();

        var folderBtn = new Button
        {
            Text = "Klasör",
            Location = new Point(294, 16),
            Size = new Size(52, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 52, 66),
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand,
        };
        folderBtn.FlatAppearance.BorderSize = 0;
        folderBtn.Click += (_, _) => OpenOutputFolder();

        _lastCard.Controls.AddRange([_lastFileLabel, _lastSubLabel, playBtn, folderBtn]);
        Controls.Add(_lastCard);

        _manualTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _manualTimer.Tick += (_, _) => OnManualTimerTick();

        _replay.ReplayStateChanged += (active, detail) =>
        {
            if (IsDisposed)
            {
                return;
            }
            BeginInvoke(() => RefreshStatus(detail));
        };

        SyncUiFromConfig();
        RefreshLastFileCard();

        // Ensure Handle exists even when starting hidden (--background) so F8 hotkey works!
        _ = Handle;
        StartupManager.Apply(_config.RunAtStartup);
        StartShowListener();
        _ = InitializeBackendAsync();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowShow && value)
        {
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (OperatingSystem.IsWindows())
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            _ = RegisterHotKey(Handle, HotkeyF8Id, ModNoRepeat, VkF8);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing
            && (_config.ReplayEnabled || _replay.IsManualRecording))
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyF8Id)
        {
            _ = TriggerSaveClipAsync();
            return;
        }
        base.WndProc(ref m);
    }

    private async Task InitializeBackendAsync()
    {
        var err = await Task.Run(() => _replay.EnsureReady());
        if (err is not null)
        {
            RefreshStatus(err);
            return;
        }

        if (_config.ReplayEnabled)
        {
            await Task.Run(() => _replay.StartReplay(
                _config.ReplaySeconds,
                _config.SystemAudio,
                _config.MicAudio,
                _config.Fps));
        }
        else
        {
            RefreshStatus();
        }
    }

    private void StartShowListener()
    {
        var thread = new Thread(() =>
        {
            while (!_reallyClosing)
            {
                if (_showEvent.WaitOne(500))
                {
                    if (_reallyClosing)
                    {
                        break;
                    }
                    try
                    {
                        BeginInvoke(ShowMainWindow);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
    }

    private void ShowMainWindow()
    {
        _allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
    }

    private void SyncUiFromConfig()
    {
        _syncingUi = true;
        _chkReplay.Checked = _config.ReplayEnabled;
        var idx = Array.IndexOf(AppConfig.ReplayDurationOptions, _config.ReplaySeconds);
        _cmbDuration.SelectedIndex = idx >= 0 ? idx : 1;
        _chkSysAudio.Checked = _config.SystemAudio;
        _chkMic.Checked = _config.MicAudio;
        _chkStartup.Checked = _config.RunAtStartup;
        _syncingUi = false;
        RefreshStatus();
    }

    private void OnSettingsChanged()
    {
        if (_syncingUi)
        {
            return;
        }

        _config.ReplayEnabled = _chkReplay.Checked;
        var idx = _cmbDuration.SelectedIndex;
        if (idx >= 0 && idx < AppConfig.ReplayDurationOptions.Length)
        {
            _config.ReplaySeconds = AppConfig.ReplayDurationOptions[idx];
        }
        _config.SystemAudio = _chkSysAudio.Checked;
        _config.MicAudio = _chkMic.Checked;
        _config.RunAtStartup = _chkStartup.Checked;
        _config.Save();

        StartupManager.Apply(_config.RunAtStartup);
        RefreshStatus("Ayar uygulanıyor...");

        _ = Task.Run(() =>
        {
            if (_replay.IsManualRecording)
            {
                return;
            }
            if (_config.ReplayEnabled)
            {
                _replay.StartReplay(_config.ReplaySeconds, _config.SystemAudio, _config.MicAudio, _config.Fps);
            }
            else
            {
                _replay.StopReplay();
            }
        });
    }

    private void RefreshStatus(string? message = null)
    {
        var isManual = _replay.IsManualRecording;
        var replayActive = _config.ReplayEnabled && _replay.IsReplayRunning;

        if (isManual)
        {
            var elapsed = DateTime.UtcNow - _manualStartUtc;
            _dotLabel.ForeColor = AccentRed;
            _headlineLabel.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
            _statusLabel.Text = message ?? "Manuel kayıt devam ediyor...";
            _manualButton.Text = "Kaydı Durdur";
            _manualButton.BackColor = AccentRed;
            _clipButton.Enabled = false;
            SetSettingsEnabled(false);
        }
        else
        {
            _manualButton.Text = "Manuel Kaydı Başlat";
            _manualButton.BackColor = Color.FromArgb(52, 52, 66);
            SetSettingsEnabled(true);

            _clipButton.Text = $"Son {_config.ReplaySeconds} Saniyeyi Kaydet (F8)";
            if (replayActive)
            {
                _dotLabel.ForeColor = AccentGreen;
                _headlineLabel.Text = "Anlık Tekrar · F8";
                _statusLabel.Text = message ?? $"Son {_config.ReplaySeconds} sn hazır ({_replay.GetEncoderSummary()})";
                _clipButton.Enabled = !_isSavingClip;
                _trayIcon.Text = $"Ekran Kaydedici — Anlık Tekrar Aktif (F8)";
            }
            else if (_config.ReplayEnabled)
            {
                _dotLabel.ForeColor = TextMuted;
                _headlineLabel.Text = "Hazırlanıyor...";
                _statusLabel.Text = message ?? "Arka plan tamponu başlatılıyor";
                _clipButton.Enabled = false;
                _trayIcon.Text = "Ekran Kaydedici — Hazırlanıyor";
            }
            else
            {
                _dotLabel.ForeColor = TextMuted;
                _headlineLabel.Text = "Anlık Tekrar Kapalı";
                _statusLabel.Text = message ?? "F8 ile klip almak için Anlık Tekrar'ı açın";
                _clipButton.Enabled = false;
                _trayIcon.Text = "Ekran Kaydedici — Kapalı";
            }
        }
    }

    private void SetSettingsEnabled(bool enabled)
    {
        _chkReplay.Enabled = enabled;
        _cmbDuration.Enabled = enabled;
        _chkSysAudio.Enabled = enabled;
        _chkMic.Enabled = enabled;
        _radFullscreen.Enabled = enabled;
        _radRegion.Enabled = enabled;
    }

    private async Task TriggerSaveClipAsync()
    {
        if (_isSavingClip)
        {
            return;
        }
        if (_replay.IsManualRecording)
        {
            ShowBalloon("Ekran Kaydedici", "Manuel kayıt sürerken F8 klip alınamaz.", ToolTipIcon.Info);
            return;
        }
        if (!_config.ReplayEnabled || !_replay.IsReplayRunning)
        {
            ShowBalloon("Anlık Tekrar Kapalı", "F8 ile klip almak için Anlık Tekrar'ı açın.", ToolTipIcon.Warning);
            return;
        }

        _isSavingClip = true;
        RefreshStatus("Klip kaydediliyor...");
        var saved = await _replay.SaveClipAsync();
        _isSavingClip = false;

        if (saved is not null)
        {
            _lastSavedFile = saved;
            RefreshLastFileCard();
            var name = Path.GetFileName(saved);
            RefreshStatus($"Klip kaydedildi: {name}");
            ShowBalloon("Klip Kaydedildi!", name, ToolTipIcon.Info);
        }
        else
        {
            RefreshStatus("Tampon henüz hazırlanıyor, birkaç saniye sonra tekrar deneyin");
            ShowBalloon("Klip Alınamadı", "Tampon henüz yeterli görüntü biriktirmedi.", ToolTipIcon.Warning);
        }
    }

    private void OnManualButtonClick()
    {
        if (_replay.IsManualRecording)
        {
            _manualTimer.Stop();
            _manualButton.Enabled = false;
            RefreshStatus("Kayıt kaydediliyor...");

            _ = Task.Run(() =>
            {
                var saved = _replay.StopManualRecording();
                if (_config.ReplayEnabled)
                {
                    _replay.StartReplay(_config.ReplaySeconds, _config.SystemAudio, _config.MicAudio, _config.Fps);
                }
                BeginInvoke(() =>
                {
                    _manualButton.Enabled = true;
                    if (saved is not null)
                    {
                        _lastSavedFile = saved;
                        RefreshLastFileCard();
                        ShowBalloon("Kayıt Tamamlandı", Path.GetFileName(saved), ToolTipIcon.Info);
                    }
                    RefreshStatus(saved is not null ? "Kayıt tamamlandı" : null);
                });
            });
            return;
        }

        Rectangle? region = null;
        if (_radRegion.Checked)
        {
            var wasVisible = Visible;
            Hide();
            Thread.Sleep(180);
            using var picker = new RegionSelectForm();
            var res = picker.ShowDialog();
            if (wasVisible)
            {
                ShowMainWindow();
            }
            if (res != DialogResult.OK || picker.SelectedRegion is null)
            {
                RefreshStatus("Bölge seçimi iptal edildi");
                return;
            }
            region = picker.SelectedRegion;
        }

        _manualButton.Enabled = false;
        RefreshStatus("Manuel kayıt başlatılıyor...");

        _ = Task.Run(() =>
        {
            var err = _replay.StartManualRecording(region, _config.SystemAudio, _config.MicAudio, _config.Fps);
            BeginInvoke(() =>
            {
                _manualButton.Enabled = true;
                if (err is not null)
                {
                    RefreshStatus($"Başlatılamadı: {err}");
                    if (_config.ReplayEnabled)
                    {
                        _ = Task.Run(() => _replay.StartReplay(
                            _config.ReplaySeconds, _config.SystemAudio, _config.MicAudio, _config.Fps));
                    }
                    return;
                }
                _manualStartUtc = DateTime.UtcNow;
                _manualTimer.Start();
                RefreshStatus(region is null ? "Tam ekran kaydediliyor..." : "Seçili bölge kaydediliyor...");
            });
        });
    }

    private void OnManualTimerTick()
    {
        if (!_replay.IsManualRecording)
        {
            _manualTimer.Stop();
            var saved = _replay.StopManualRecording();
            if (saved is not null)
            {
                _lastSavedFile = saved;
                RefreshLastFileCard();
            }
            RefreshStatus("Kayıt durdu");
            return;
        }
        RefreshStatus();
    }

    private void RefreshLastFileCard()
    {
        if (!string.IsNullOrEmpty(_lastSavedFile) && File.Exists(_lastSavedFile))
        {
            var fi = new FileInfo(_lastSavedFile);
            var sizeStr = fi.Length >= 1024 * 1024
                ? $"{fi.Length / (1024.0 * 1024.0):F1} MB"
                : $"{Math.Max(1, fi.Length / 1024)} KB";
            _lastFileLabel.Text = fi.Name;
            _lastSubLabel.Text = $"{sizeStr} · Videolar\\EkranKayitlari";
            _lastCard.Visible = true;
            ClientSize = new Size(390, 520);
        }
        else
        {
            _lastCard.Visible = false;
            ClientSize = new Size(390, 446);
        }
    }

    private void OpenLastFile()
    {
        if (!string.IsNullOrEmpty(_lastSavedFile) && File.Exists(_lastSavedFile))
        {
            Process.Start(new ProcessStartInfo(_lastSavedFile) { UseShellExecute = true });
        }
    }

    private static void OpenOutputFolder()
    {
        Directory.CreateDirectory(AppConfig.OutputDir);
        Process.Start(new ProcessStartInfo(AppConfig.OutputDir) { UseShellExecute = true });
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _trayIcon.ShowBalloonTip(2500, title, text, icon);
    }

    private void QuitCompletely()
    {
        _reallyClosing = true;
        _manualTimer.Stop();
        _replay.Dispose();
        if (OperatingSystem.IsWindows() && IsHandleCreated)
        {
            UnregisterHotKey(Handle, HotkeyF8Id);
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Close();
        Application.Exit();
    }

    private static Panel CreateCard(int x, int y, int w, int h)
    {
        var panel = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = BgCard,
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderCard);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    private static CheckBox CreateCheck(string text, int x, int y)
    {
        return new CheckBox
        {
            Text = text,
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(x, y),
            Cursor = Cursors.Hand,
        };
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("EkranKaydedici.app.ico");
            return stream is not null ? new Icon(stream) : null;
        }
        catch
        {
            return null;
        }
    }
}
