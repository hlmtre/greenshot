#region Greenshot GNU General Public License

// Greenshot - a free and open source screenshot tool
// Copyright (C) 2007-2018 Thomas Braun, Jens Klingen, Robin Krom
// 
// For more information see: http://getgreenshot.org/
// The Greenshot project is hosted on GitHub https://github.com/greenshot/greenshot
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 1 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

#endregion

#region Usings

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Dapplo.Ini;
using Dapplo.Windows.Desktop;
using Greenshot.Configuration;
using Greenshot.Destinations;
using Greenshot.Drawing;
using Greenshot.Help;
using Greenshot.Helpers;
using GreenshotPlugin.Controls;
using GreenshotPlugin.Core;
using GreenshotPlugin.Core.Enums;
using GreenshotPlugin.Gfx;
using GreenshotPlugin.Interfaces;
using Dapplo.Log;
using Timer = System.Timers.Timer;
using Dapplo.Windows.Dpi;
using Dapplo.Windows.App;
using Dapplo.Windows.Common.Structs;
using Dapplo.Windows.DesktopWindowsManager;
using Dapplo.Windows.Dpi.Enums;
using Dapplo.Windows.Dpi.Forms;
using Dapplo.Windows.Kernel32;
using Greenshot.Gfx;
using GreenshotPlugin.Addons;
using GreenshotPlugin.Extensions;
using GreenshotPlugin.Interfaces.Plugin;

#endregion

namespace Greenshot.Forms
{
    /// <summary>
    ///     Description of MainForm.
    /// </summary>
    [Export(typeof(IGreenshotHost))]
    [Export]
    public partial class MainForm : BaseForm, IGreenshotHost
    {
        private static readonly LogSource Log = new LogSource();
        private readonly ICoreConfiguration _coreConfiguration;
        private readonly IEnumerable<IDestination> _destinations;

        // Timer for the double click test
        private readonly Timer _doubleClickTimer = new Timer();
        // Make sure we have only one settings form
        private readonly SettingsForm _settingsForm;
        // Make sure we have only one about form
        private AboutForm _aboutForm;
        // Thumbnail preview
        private ThumbnailForm _thumbnailForm;

        public DpiHandler ContextMenuDpiHandler { get; private set; }

        [ImportingConstructor]
        public MainForm(ICoreConfiguration coreConfiguration, SettingsForm settingsForm, [ImportMany] IEnumerable<IDestination> destinations)
        {
            _coreConfiguration = coreConfiguration;
            _settingsForm = settingsForm;
            _destinations = destinations;
            Instance = this;
        }

        public void Initialize()
        {
            Log.Debug().WriteLine("Initializing MainForm.");
            // Factory for surface objects
            ImageOutput.SurfaceFactory = () => new Surface();

            //
            // The InitializeComponent() call is required for Windows Forms designer support.
            //
            try
            {
                _settingsForm.Initialize();
                InitializeComponent();
                SetupBitmapScaleHandler();
            }
            catch (ArgumentException ex)
            {
                // Added for Bug #1420, this doesn't solve the issue but maybe the user can do something with it.
                ex.Data.Add("more information here", "http://support.microsoft.com/kb/943140");
                throw;
            }
            notifyIcon.Icon = GreenshotResources.GetGreenshotIcon();

            // Disable access to the settings, for feature #3521446
            contextmenu_settings.Visible = !_coreConfiguration.DisableSettings;

            UpdateUi();

            if (_coreConfiguration.DisableQuickSettings)
            {
                contextmenu_quicksettings.Visible = false;
            }
            else
            {
                // Do after all plugins & finding the destination, otherwise they are missing!
                InitializeQuickSettingsMenu();
            }

            // Set the Greenshot icon visibility depending on the configuration. (Added for feature #3521446)
            // Setting it to true this late prevents Problems with the context menu
            notifyIcon.Visible = !_coreConfiguration.HideTrayicon;

            // Check if it's the first time launch?
            if (_coreConfiguration.IsFirstLaunch)
            {
                _coreConfiguration.IsFirstLaunch = false;
                Log.Info().WriteLine("FirstLaunch: Created new configuration, showing balloon.");
                try
                {
                    notifyIcon.BalloonTipClicked += BalloonTipClicked;
                    notifyIcon.BalloonTipClosed += BalloonTipClosed;
                    notifyIcon.ShowBalloonTip(2000, "Greenshot",
                        Language.GetFormattedString(LangKey.tooltip_firststart, HotkeyControl.GetLocalizedHotkeyStringFromString(_coreConfiguration.RegionHotkey)), ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Log.Warn().WriteLine(ex, "Exception while showing first launch: ");
                }
            }
            
            // Make Greenshot use less memory after startup
            if (_coreConfiguration.MinimizeWorkingSetSize)
            {
                PsApi.EmptyWorkingSet();
            }
        }

        public static MainForm Instance { get; set; }

        public Form GreenshotForm => Instance;

        public NotifyIcon NotifyIcon => notifyIcon;

        /// <summary>
        ///     Main context menu
        /// </summary>
        public ContextMenuStrip MainMenu => contextMenu;

        private void BalloonTipClicked(object sender, EventArgs e)
        {
            try
            {
                ShowSetting();
            }
            finally
            {
                BalloonTipClosed(sender, e);
            }
        }

        private void BalloonTipClosed(object sender, EventArgs e)
        {
            notifyIcon.BalloonTipClicked -= BalloonTipClicked;
            notifyIcon.BalloonTipClosed -= BalloonTipClosed;
        }

        protected override void WndProc(ref Message m)
        {
            if (HotkeyControl.HandleMessages(ref m))
            {
                return;
            }
            // BUG-1809 prevention, filter the InputLangChange messages
            if (WmInputLangChangeRequestFilter.PreFilterMessageExternal(ref m))
            {
                return;
            }
            base.WndProc(ref m);
        }

        public void UpdateUi()
        {
            // As the form is never loaded, call ApplyLanguage ourselves
            ApplyLanguage();
            
            // Show hotkeys in Contextmenu
            contextmenu_capturearea.ShortcutKeyDisplayString = HotkeyControl.GetLocalizedHotkeyStringFromString(_coreConfiguration.RegionHotkey);
            contextmenu_capturelastregion.ShortcutKeyDisplayString = HotkeyControl.GetLocalizedHotkeyStringFromString(_coreConfiguration.LastregionHotkey);
            contextmenu_capturewindow.ShortcutKeyDisplayString = HotkeyControl.GetLocalizedHotkeyStringFromString(_coreConfiguration.WindowHotkey);
            contextmenu_capturefullscreen.ShortcutKeyDisplayString = HotkeyControl.GetLocalizedHotkeyStringFromString(_coreConfiguration.FullscreenHotkey);
            contextmenu_captureie.ShortcutKeyDisplayString = HotkeyControl.GetLocalizedHotkeyStringFromString(_coreConfiguration.IEHotkey);
        }

        /// <summary>
        ///     Handle the notify icon click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NotifyIconClickTest(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            // The right button will automatically be handled with the context menu, here we only check the left.
            if (_coreConfiguration.DoubleClickAction == ClickActions.DO_NOTHING)
            {
                // As there isn't a double-click we can start the Left click
                NotifyIconClick(_coreConfiguration.LeftClickAction);
                // ready with the test
                return;
            }
            // If the timer is enabled we are waiting for a double click...
            if (_doubleClickTimer.Enabled)
            {
                // User clicked a second time before the timer tick: Double-click!
                _doubleClickTimer.Elapsed -= NotifyIconSingleClickTest;
                _doubleClickTimer.Stop();
                NotifyIconClick(_coreConfiguration.DoubleClickAction);
            }
            else
            {
                // User clicked without a timer, set the timer and if it ticks it was a single click
                // Create timer, if it ticks before the NotifyIconClickTest is called again we have a single click
                _doubleClickTimer.Elapsed += NotifyIconSingleClickTest;
                _doubleClickTimer.Interval = SystemInformation.DoubleClickTime;
                _doubleClickTimer.Start();
            }
        }

        /// <summary>
        ///     Called by the doubleClickTimer, this means a single click was used on the tray icon
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NotifyIconSingleClickTest(object sender, EventArgs e)
        {
            _doubleClickTimer.Elapsed -= NotifyIconSingleClickTest;
            _doubleClickTimer.Stop();
            BeginInvoke((MethodInvoker) delegate { NotifyIconClick(_coreConfiguration.LeftClickAction); });
        }

        /// <summary>
        ///     Handle the notify icon click
        /// </summary>
        private void NotifyIconClick(ClickActions clickAction)
        {
            switch (clickAction)
            {
                case ClickActions.OPEN_LAST_IN_EXPLORER:
                    Contextmenu_OpenRecent(this, null);
                    break;
                case ClickActions.OPEN_LAST_IN_EDITOR:
                    _coreConfiguration.ValidateAndCorrectOutputFileAsFullpath();

                    if (File.Exists(_coreConfiguration.OutputFileAsFullpath))
                    {
                        CaptureHelper.CaptureFile(_coreConfiguration.OutputFileAsFullpath, _destinations.Find(typeof(EditorDestination)));
                    }
                    break;
                case ClickActions.OPEN_SETTINGS:
                    ShowSetting();
                    break;
                case ClickActions.SHOW_CONTEXT_MENU:
                    var oMethodInfo = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                    oMethodInfo?.Invoke(notifyIcon, null);
                    break;
            }
        }

        /// <summary>
        ///     The Contextmenu_OpenRecent currently opens the last know save location
        /// </summary>
        private void Contextmenu_OpenRecent(object sender, EventArgs eventArgs)
        {
            _coreConfiguration.ValidateAndCorrectOutputFilePath();
            _coreConfiguration.ValidateAndCorrectOutputFileAsFullpath();
            var path = _coreConfiguration.OutputFileAsFullpath;
            if (!File.Exists(path))
            {
                path = FilenameHelper.FillVariables(_coreConfiguration.OutputFilePath, false);
                // Fix for #1470, problems with a drive which is no longer available
                try
                {
                    var lastFilePath = Path.GetDirectoryName(_coreConfiguration.OutputFileAsFullpath);

                    if (lastFilePath != null && Directory.Exists(lastFilePath))
                    {
                        path = lastFilePath;
                    }
                    else if (!Directory.Exists(path))
                    {
                        // What do I open when nothing can be found? Right, nothing...
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn().WriteLine(ex, "Couldn't open the path to the last exported file, taking default.");
                }
            }
            try
            {
                ExplorerHelper.OpenInExplorer(path);
            }
            catch (Exception ex)
            {
                // Make sure we show what we tried to open in the exception
                ex.Data.Add("path", path);
                Log.Warn().WriteLine(ex, "Couldn't open the path to the last exported file");
                // No reason to create a bug-form, we just display the error.
                MessageBox.Show(this, ex.Message, $"Opening {path}", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        ///     Shutdown / cleanup
        /// </summary>
        public void Exit()
        {
            Log.Info().WriteLine("Exit: " + EnvironmentInfo.EnvironmentToString(false));

            ImageOutput.RemoveTmpFiles();

            // make the icon invisible otherwise it stays even after exit!!
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                notifyIcon = null;
            }
        }


        /// <summary>
        ///     Do work in the background
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BackgroundWorkerTimerTick(object sender, EventArgs e)
        {
            if (_coreConfiguration.MinimizeWorkingSetSize)
            {
                PsApi.EmptyWorkingSet();
            }
            if (UpdateHelper.IsUpdateCheckNeeded())
            {
                Log.Debug().WriteLine("BackgroundWorkerTimerTick checking for update");
                // Start update check in the background
                var backgroundTask = new Thread(UpdateHelper.CheckAndAskForUpdate)
                {
                    Name = "Update check",
                    IsBackground = true
                };
                backgroundTask.Start();
            }
        }


        /// <summary>
        /// Setup the Bitmap scaling (for icons)
        /// </summary>
        private void SetupBitmapScaleHandler()
        {
            ContextMenuDpiHandler = contextMenu.AttachDpiHandler();

            var dpiChangeSubscription = DpiHandler.OnDpiChangeInfo.Subscribe(info =>
            {
                switch (info.DpiChangeEventType)
                {
                    case DpiChangeEventTypes.Before:
                        // Change the ImageScalingSize before setting the bitmaps
                        var width = DpiHandler.ScaleWithDpi(coreConfiguration.IconSize.Width, info.NewDpi);
                        var size = new Size(width, width);
                        contextMenu.SuspendLayout();
                        contextMenu.ImageScalingSize = size;
                        contextmenu_quicksettings.Size = new Size(170, width + 8);
                        break;
                    case DpiChangeEventTypes.After:
                        // Redraw the form
                        contextMenu.ResumeLayout(true);
                        contextMenu.Refresh();
                        notifyIcon.Icon = GreenshotResources.GetGreenshotIcon();
                        break;
                }
            });

            var contextMenuResourceScaleHandler = BitmapScaleHandler.WithComponentResourceManager(ContextMenuDpiHandler, GetType(), (bitmap, dpi) => bitmap.ScaleIconForDisplaying(dpi));

            contextMenuResourceScaleHandler.AddTarget(contextmenu_capturewindow, "contextmenu_capturewindow.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_capturearea, "contextmenu_capturearea.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_capturelastregion, "contextmenu_capturelastregion.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_capturefullscreen, "contextmenu_capturefullscreen.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_captureclipboard, "contextmenu_captureclipboard.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_openfile, "contextmenu_openfile.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_settings, "contextmenu_settings.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_help, "contextmenu_help.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_donate, "contextmenu_donate.Image");
            contextMenuResourceScaleHandler.AddTarget(contextmenu_exit, "contextmenu_exit.Image");

            // this is special handling, for the icons which come from the executables
            var exeBitmapScaleHandler = BitmapScaleHandler.Create<string>(ContextMenuDpiHandler,
                (path, dpi) => PluginUtils.GetCachedExeIcon(path, 0, dpi >= 120),
                (bitmap, dpi) => bitmap.ScaleIconForDisplaying(dpi));
            exeBitmapScaleHandler.AddTarget(contextmenu_captureie, PluginUtils.GetExePath("iexplore.exe"));

            // Add cleanup
            Application.ApplicationExit += (sender, args) =>
            {
                dpiChangeSubscription.Dispose();
                ContextMenuDpiHandler.Dispose();
                contextMenuResourceScaleHandler.Dispose();
                exeBitmapScaleHandler.Dispose();
            };
        }

#region mainform events

        private void MainFormFormClosing(object sender, FormClosingEventArgs e)
        {
            Log.Debug().WriteLine("Mainform closing, reason: {0}", e.CloseReason);
            Instance = null;
            Exit();
        }

        private void MainFormActivated(object sender, EventArgs e)
        {
            Hide();
            ShowInTaskbar = false;
        }

#endregion

#region key handlers

        private void CaptureFile()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Image files (*.greenshot, *.png, *.jpg, *.gif, *.bmp, *.ico, *.tiff, *.wmf)|*.greenshot; *.png; *.jpg; *.jpeg; *.gif; *.bmp; *.ico; *.tiff; *.tif; *.wmf"
            };
            if (openFileDialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            if (File.Exists(openFileDialog.FileName))
            {
                CaptureHelper.CaptureFile(openFileDialog.FileName);
            }
        }
        
        private void CaptureIE()
        {
            if (_coreConfiguration.IECapture)
            {
                CaptureHelper.CaptureIe(true, null);
            }
        }

#endregion

#region contextmenu

        private void ContextMenuOpening(object sender, CancelEventArgs e)
        {
            contextmenu_captureclipboard.Enabled = ClipboardHelper.ContainsImage();
            contextmenu_capturelastregion.Enabled = coreConfiguration.LastCapturedRegion != NativeRect.Empty;

            // IE context menu code
            try
            {
                if (_coreConfiguration.IECapture && IeCaptureHelper.IsIeRunning())
                {
                    contextmenu_captureie.Enabled = true;
                    contextmenu_captureiefromlist.Enabled = true;
                }
                else
                {
                    contextmenu_captureie.Enabled = false;
                    contextmenu_captureiefromlist.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn().WriteLine("Problem accessing IE information: {0}", ex.Message);
            }

            // Multi-Screen captures
            contextmenu_capturefullscreen.Click -= CaptureFullScreenToolStripMenuItemClick;
            contextmenu_capturefullscreen.DropDownOpening -= MultiScreenDropDownOpening;
            contextmenu_capturefullscreen.DropDownClosed -= MultiScreenDropDownClosing;
            if (Screen.AllScreens.Length > 1)
            {
                contextmenu_capturefullscreen.DropDownOpening += MultiScreenDropDownOpening;
                contextmenu_capturefullscreen.DropDownClosed += MultiScreenDropDownClosing;
            }
            else
            {
                contextmenu_capturefullscreen.Click += CaptureFullScreenToolStripMenuItemClick;
            }

            var now = DateTime.Now;
            if (now.Month == 12 && now.Day > 19 && now.Day < 27 || // christmas
                now.Month == 3 && now.Day > 13 && now.Day < 21)
            {
                // birthday
                var resources = new ComponentResourceManager(typeof(MainForm));
                contextmenu_donate.Image = (Image) resources.GetObject("contextmenu_present.Image");
            }
        }

        private void ContextMenuClosing(object sender, EventArgs e)
        {
            contextmenu_captureiefromlist.DropDownItems.Clear();
            contextmenu_capturewindowfromlist.DropDownItems.Clear();
            CleanupThumbnail();
        }

        /// <summary>
        ///     Build a selectable list of IE tabs when we enter the menu item
        /// </summary>
        private void CaptureIeMenuDropDownOpening(object sender, EventArgs e)
        {
            if (!_coreConfiguration.IECapture)
            {
                return;
            }
            try
            {
                var tabs = IeCaptureHelper.GetBrowserTabs();
                contextmenu_captureiefromlist.DropDownItems.Clear();
                if (tabs.Count > 0)
                {
                    contextmenu_captureie.Enabled = true;
                    contextmenu_captureiefromlist.Enabled = true;
                    var counter = new Dictionary<IInteropWindow, int>();

                    foreach (var tabData in tabs)
                    {
                        var title = tabData.Value;
                        if (title == null)
                        {
                            continue;
                        }
                        if (title.Length > _coreConfiguration.MaxMenuItemLength)
                        {
                            title = title.Substring(0, Math.Min(title.Length, _coreConfiguration.MaxMenuItemLength));
                        }
                        var captureIeTabItem = contextmenu_captureiefromlist.DropDownItems.Add(title);
                        var index = counter.ContainsKey(tabData.Key) ? counter[tabData.Key] : 0;
                        captureIeTabItem.Image = tabData.Key.GetDisplayIcon();
                        captureIeTabItem.Tag = new KeyValuePair<IInteropWindow, int>(tabData.Key, index++);
                        captureIeTabItem.Click += Contextmenu_captureiefromlist_Click;
                        contextmenu_captureiefromlist.DropDownItems.Add(captureIeTabItem);
                        if (counter.ContainsKey(tabData.Key))
                        {
                            counter[tabData.Key] = index;
                        }
                        else
                        {
                            counter.Add(tabData.Key, index);
                        }
                    }
                }
                else
                {
                    contextmenu_captureie.Enabled = false;
                    contextmenu_captureiefromlist.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn().WriteLine("Problem accessing IE information: {0}", ex.Message);
            }
        }

        /// <summary>
        ///     MultiScreenDropDownOpening is called when mouse hovers over the Capture-Screen context menu
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MultiScreenDropDownOpening(object sender, EventArgs e)
        {
            var captureScreenMenuItem = (ToolStripMenuItem) sender;
            captureScreenMenuItem.DropDownItems.Clear();
            if (Screen.AllScreens.Length <= 1)
            {
                return;
            }
            var allScreensBounds = WindowCapture.GetScreenBounds();

            var captureScreenItem = new ToolStripMenuItem(Language.GetString(LangKey.contextmenu_capturefullscreen_all));
            captureScreenItem.Click += delegate { BeginInvoke((MethodInvoker) delegate { CaptureHelper.CaptureFullscreen(false, ScreenCaptureMode.FullScreen); }); };
            captureScreenMenuItem.DropDownItems.Add(captureScreenItem);
            foreach (var screen in Screen.AllScreens)
            {
                var screenToCapture = screen;
                var deviceAlignment = "";
                if (screen.Bounds.Top == allScreensBounds.Top && screen.Bounds.Bottom != allScreensBounds.Bottom)
                {
                    deviceAlignment += " " + Language.GetString(LangKey.contextmenu_capturefullscreen_top);
                }
                else if (screen.Bounds.Top != allScreensBounds.Top && screen.Bounds.Bottom == allScreensBounds.Bottom)
                {
                    deviceAlignment += " " + Language.GetString(LangKey.contextmenu_capturefullscreen_bottom);
                }
                if (screen.Bounds.Left == allScreensBounds.Left && screen.Bounds.Right != allScreensBounds.Right)
                {
                    deviceAlignment += " " + Language.GetString(LangKey.contextmenu_capturefullscreen_left);
                }
                else if (screen.Bounds.Left != allScreensBounds.Left && screen.Bounds.Right == allScreensBounds.Right)
                {
                    deviceAlignment += " " + Language.GetString(LangKey.contextmenu_capturefullscreen_right);
                }
                captureScreenItem = new ToolStripMenuItem(deviceAlignment);
                captureScreenItem.Click += delegate { BeginInvoke((MethodInvoker) delegate { CaptureHelper.CaptureRegion(false, screenToCapture.Bounds); }); };
                captureScreenMenuItem.DropDownItems.Add(captureScreenItem);
            }
        }

        /// <summary>
        ///     MultiScreenDropDownOpening is called when mouse leaves the context menu
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MultiScreenDropDownClosing(object sender, EventArgs e)
        {
            var captureScreenMenuItem = (ToolStripMenuItem) sender;
            captureScreenMenuItem.DropDownItems.Clear();
        }

        /// <summary>
        ///     Build a selectable list of windows when we enter the menu item
        /// </summary>
        private void CaptureWindowFromListMenuDropDownOpening(object sender, EventArgs e)
        {
            // The Capture window context menu item used to go to the following code:
            // captureForm.MakeCapture(CaptureMode.Window, false);
            // Now we check which windows are there to capture
            var captureWindowFromListMenuItem = (ToolStripMenuItem) sender;
            AddCaptureWindowMenuItems(captureWindowFromListMenuItem, Contextmenu_capturewindowfromlist_Click);
        }

        private void CaptureWindowFromListMenuDropDownClosed(object sender, EventArgs e)
        {
            CleanupThumbnail();
        }

        private void ShowThumbnailOnEnter(object sender, EventArgs e)
        {
            if (!(sender is ToolStripMenuItem captureWindowItem))
            {
                return;
            }

            var window = captureWindowItem.Tag as IInteropWindow;
            if (_thumbnailForm == null)
            {
                _thumbnailForm = new ThumbnailForm();
            }
            _thumbnailForm.ShowThumbnail(window, captureWindowItem.GetCurrentParent().TopLevelControl);
        }

        private void HideThumbnailOnLeave(object sender, EventArgs e)
        {
            _thumbnailForm?.Hide();
        }

        private void CleanupThumbnail()
        {
            if (_thumbnailForm == null)
            {
                return;
            }
            _thumbnailForm.Close();
            _thumbnailForm = null;
        }

        public void AddCaptureWindowMenuItems(ToolStripMenuItem menuItem, EventHandler eventHandler)
        {
            menuItem.DropDownItems.Clear();
            // check if thumbnailPreview is enabled and DWM is enabled
            var thumbnailPreview = _coreConfiguration.ThumnailPreview && Dwm.IsDwmEnabled;

            foreach (var window in InteropWindowQuery.GetTopLevelWindows().Concat(AppQuery.WindowsStoreApps))
            {
                var title = window.GetCaption();
                if (title == null)
                {
                    continue;
                }
                if (title.Length > _coreConfiguration.MaxMenuItemLength)
                {
                    title = title.Substring(0, Math.Min(title.Length, _coreConfiguration.MaxMenuItemLength));
                }
                var captureWindowItem = menuItem.DropDownItems.Add(title);
                captureWindowItem.Tag = window;
                captureWindowItem.Image = window.GetDisplayIcon(ContextMenuDpiHandler.Dpi > DpiHandler.DefaultScreenDpi);
                captureWindowItem.Click += eventHandler;
                // Only show preview when enabled
                if (thumbnailPreview)
                {
                    captureWindowItem.MouseEnter += ShowThumbnailOnEnter;
                    captureWindowItem.MouseLeave += HideThumbnailOnLeave;
                }
            }
        }

        private void CaptureAreaToolStripMenuItemClick(object sender, EventArgs e)
        {
            BeginInvoke((MethodInvoker) delegate { CaptureHelper.CaptureRegion(false); });
        }

        private void CaptureClipboardToolStripMenuItemClick(object sender, EventArgs e)
        {
            BeginInvoke(new Action(() => CaptureHelper.CaptureClipboard()));
        }

        private void OpenFileToolStripMenuItemClick(object sender, EventArgs e)
        {
            BeginInvoke((MethodInvoker) CaptureFile);
        }

        private void CaptureFullScreenToolStripMenuItemClick(object sender, EventArgs e)
        {
            BeginInvoke((MethodInvoker) delegate { CaptureHelper.CaptureFullscreen(false, _coreConfiguration.ScreenCaptureMode); });
        }

        private void Contextmenu_capturelastregionClick(object sender, EventArgs e)
        {
            BeginInvoke((MethodInvoker) delegate { CaptureHelper.CaptureLastRegion(false); });
        }

        private void Contextmenu_capturewindow_Click(object sender, EventArgs e)
        {
            BeginInvoke((MethodInvoker) delegate { CaptureHelper.CaptureWindowInteractive(false); });
        }

        private void Contextmenu_capturewindowfromlist_Click(object sender, EventArgs e)
        {
            var clickedItem = (ToolStripMenuItem) sender;
            BeginInvoke((MethodInvoker) delegate
            {
                try
                {
                    var windowToCapture = (InteropWindow) clickedItem.Tag;
                    CaptureHelper.CaptureWindow(windowToCapture);
                }
                catch (Exception exception)
                {
                    Log.Error().WriteLine(exception);
                }
            });
        }

        private void Contextmenu_captureie_Click(object sender, EventArgs e)
        {
            CaptureIE();
        }

        private void Contextmenu_captureiefromlist_Click(object sender, EventArgs e)
        {
            if (!_coreConfiguration.IECapture)
            {
                Log.Info().WriteLine("IE Capture is disabled.");
                return;
            }
            var clickedItem = (ToolStripMenuItem) sender;
            var tabData = (KeyValuePair<IInteropWindow, int>) clickedItem.Tag;
            BeginInvoke((MethodInvoker) delegate
            {
                var ieWindowToCapture = tabData.Key;
                if (ieWindowToCapture != null && ieWindowToCapture.IsMinimized())
                {
                    ieWindowToCapture.Restore();
                }
                try
                {
                    IeCaptureHelper.ActivateIeTab(ieWindowToCapture, tabData.Value);
                }
                catch (Exception exception)
                {
                    Log.Error().WriteLine(exception);
                }
                try
                {
                    CaptureHelper.CaptureIe(false, ieWindowToCapture);
                }
                catch (Exception exception)
                {
                    Log.Error().WriteLine(exception);
                }
            });
        }

        /// <summary>
        ///     Context menu entry "Support Greenshot"
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Contextmenu_donateClick(object sender, EventArgs e)
        {
            BeginInvoke((MethodInvoker) delegate { Process.Start("http://getgreenshot.org/support/?version=" + Assembly.GetEntryAssembly().GetName().Version); });
        }

        /// <summary>
        ///     Context menu entry "Preferences"
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Contextmenu_settingsClick(object sender, EventArgs e)
        {
            BeginInvoke((MethodInvoker) ShowSetting);
        }

        /// <summary>
        ///     This is called indirectly from the context menu "Preferences"
        /// </summary>
        public void ShowSetting()
        {
            
            if (Application.OpenForms.OfType<SettingsForm>().Any())
            {
                // TODO: Await?
                InteropWindowFactory.CreateFor(_settingsForm.Handle).ToForegroundAsync();
            }
            else
            {
                if (_settingsForm.ShowDialog() == DialogResult.OK)
                {
                    InitializeQuickSettingsMenu();
                }
            }
        }

        /// <summary>
        ///     The "About Greenshot" entry is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Contextmenu_aboutClick(object sender, EventArgs e)
        {
            ShowAbout();
        }

        public void ShowAbout()
        {
            if (_aboutForm != null)
            {
                // TODO: Await?
                InteropWindowFactory.CreateFor(_aboutForm.Handle).ToForegroundAsync();
            }
            else
            {
                try
                {
                    using (_aboutForm = new AboutForm())
                    {
                        _aboutForm.ShowDialog(this);
                    }
                }
                finally
                {
                    _aboutForm = null;
                }
            }
        }

        /// <summary>
        ///     The "Help" entry is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Contextmenu_helpClick(object sender, EventArgs e)
        {
            HelpFileLoader.LoadHelp();
        }

        /// <summary>
        ///     The "Exit" entry is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Contextmenu_exitClick(object sender, EventArgs e)
        {
            // Gracefull shutdown
            System.Windows.Application.Current.Shutdown(0);
        }

        private void CheckStateChangedHandler(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuSelectListItem captureMouseItem)
            {
                _coreConfiguration.CaptureMousepointer = captureMouseItem.Checked;
            }
        }

        /// <summary>
        ///     This needs to be called to initialize the quick settings menu entries
        /// </summary>
        private void InitializeQuickSettingsMenu()
        {
            contextmenu_quicksettings.DropDownItems.Clear();

            if (_coreConfiguration.DisableQuickSettings)
            {
                return;
            }

            // Only add if the value is not fixed
            if (!_coreConfiguration.IsWriteProtected("CaptureMousepointer"))
            {
                // For the capture mousecursor option
                var captureMouseItem = new ToolStripMenuSelectListItem
                {
                    Text = Language.GetString("settings_capture_mousepointer"),
                    Checked = _coreConfiguration.CaptureMousepointer,
                    CheckOnClick = true
                };
                captureMouseItem.CheckStateChanged += CheckStateChangedHandler;

                contextmenu_quicksettings.DropDownItems.Add(captureMouseItem);
            }
            ToolStripMenuSelectList selectList;
            if (!_coreConfiguration.IsWriteProtected("Destinations"))
            {
                // screenshot destination
                selectList = new ToolStripMenuSelectList("destinations", true)
                {
                    Text = Language.GetString(LangKey.settings_destination)
                };
                // Working with IDestination:
                foreach (var destination in _destinations)
                {
                    selectList.AddItem(destination.Description, destination, _coreConfiguration.OutputDestinations.Contains(destination.Designation));
                }
                selectList.CheckedChanged += QuickSettingDestinationChanged;
                contextmenu_quicksettings.DropDownItems.Add(selectList);
            }

            if (!_coreConfiguration.IsWriteProtected("WindowCaptureMode"))
            {
                // Capture Modes
                selectList = new ToolStripMenuSelectList("capturemodes", false)
                {
                    Text = Language.GetString(LangKey.settings_window_capture_mode)
                };
                var enumTypeName = typeof(WindowCaptureModes).Name;
                foreach (WindowCaptureModes captureMode in Enum.GetValues(typeof(WindowCaptureModes)))
                {
                    selectList.AddItem(Language.GetString(enumTypeName + "." + captureMode), captureMode, _coreConfiguration.WindowCaptureMode == captureMode);
                }
                selectList.CheckedChanged += QuickSettingCaptureModeChanged;
                contextmenu_quicksettings.DropDownItems.Add(selectList);
            }

            // print options
            selectList = new ToolStripMenuSelectList("printoptions", true)
            {
                Text = Language.GetString(LangKey.settings_printoptions)
            };

            foreach (var outputPrintIniValue in _coreConfiguration.GetIniValues().Values.Where(value => value.PropertyName.StartsWith("OutputPrint") && value.ValueType == typeof(bool) && !_coreConfiguration.IsWriteProtected(value.PropertyName)))
            {
                selectList.AddItem(Language.GetString(outputPrintIniValue.PropertyName), outputPrintIniValue, (bool) outputPrintIniValue.Value);
            }
            if (selectList.DropDownItems.Count > 0)
            {
                selectList.CheckedChanged += QuickSettingBoolItemChanged;
                contextmenu_quicksettings.DropDownItems.Add(selectList);
            }
            else
            {
                selectList.Dispose();
            }

            // effects
            selectList = new ToolStripMenuSelectList("effects", true)
            {
                Text = Language.GetString(LangKey.settings_visualization)
            };

            var iniValue = _coreConfiguration["PlayCameraSound"];
            var languageKey = _coreConfiguration.GetTagValue(iniValue.PropertyName, ConfigTags.LanguageKey) as string;

            if (!_coreConfiguration.IsWriteProtected(iniValue.PropertyName))
            {
                selectList.AddItem(Language.GetString(languageKey), iniValue, (bool) iniValue.Value);
            }
            iniValue = _coreConfiguration["ShowTrayNotification"];
            languageKey = _coreConfiguration.GetTagValue(iniValue.PropertyName, ConfigTags.LanguageKey) as string;
            if (!_coreConfiguration.IsWriteProtected(iniValue.PropertyName))
            {
                selectList.AddItem(Language.GetString(languageKey), iniValue, (bool) iniValue.Value);
            }
            if (selectList.DropDownItems.Count > 0)
            {
                selectList.CheckedChanged += QuickSettingBoolItemChanged;
                contextmenu_quicksettings.DropDownItems.Add(selectList);
            }
            else
            {
                selectList.Dispose();
            }
        }

        private void QuickSettingCaptureModeChanged(object sender, EventArgs e)
        {
            var item = ((ItemCheckedChangedEventArgs) e).Item;
            var windowsCaptureMode = (WindowCaptureModes) item.Data;
            if (item.Checked)
            {
                _coreConfiguration.WindowCaptureMode = windowsCaptureMode;
            }
        }

        private void QuickSettingBoolItemChanged(object sender, EventArgs e)
        {
            var item = ((ItemCheckedChangedEventArgs) e).Item;
            if (item.Data is IniValue iniValue)
            {
                iniValue.Value = item.Checked;
            }
        }

        private void QuickSettingDestinationChanged(object sender, EventArgs e)
        {
            var item = ((ItemCheckedChangedEventArgs) e).Item;
            var selectedDestination = (IDestination) item.Data;
            if (item.Checked)
            {
                if (selectedDestination.Designation.Equals(typeof(PickerDestination).GetDesignation()))
                {
                    // If the item is the destination picker, remove all others
                    _coreConfiguration.OutputDestinations.Clear();
                }
                else
                {
                    // If the item is not the destination picker, remove the picker
                    _coreConfiguration.OutputDestinations.Remove(typeof(PickerDestination).GetDesignation());
                }
                // Checked an item, add if the destination is not yet selected
                if (!_coreConfiguration.OutputDestinations.Contains(selectedDestination.Designation))
                {
                    _coreConfiguration.OutputDestinations.Add(selectedDestination.Designation);
                }
            }
            else
            {
                // deselected a destination, only remove if it was selected
                if (_coreConfiguration.OutputDestinations.Contains(selectedDestination.Designation))
                {
                    _coreConfiguration.OutputDestinations.Remove(selectedDestination.Designation);
                }
            }
            // Check if something was selected, if not make the picker the default
            if (_coreConfiguration.OutputDestinations == null || _coreConfiguration.OutputDestinations.Count == 0)
            {
                _coreConfiguration.OutputDestinations.Add(typeof(PickerDestination).GetDesignation());
            }

            // Rebuild the quick settings menu with the new settings.
            InitializeQuickSettingsMenu();
        }

#endregion
    }
}