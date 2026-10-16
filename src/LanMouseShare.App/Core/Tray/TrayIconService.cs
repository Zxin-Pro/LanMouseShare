using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Resources;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Core.Tray
{
    /// <summary>
    /// 托盘菜单要回调的动作集合。
    /// 用普通类而不是接口，是为了让 App 用对象初始化器一次性把所有动作装进来，
    /// 少写一堆构造参数；每个字段都允许为 null（菜单项会自动禁用）。
    /// </summary>
    public sealed class TrayMenuCallbacks
    {
        /// <summary>显示主窗口。</summary>
        public Action ShowWindow;
        /// <summary>退出程序。</summary>
        public Action ExitApplication;
        /// <summary>以主机模式开始共享。</summary>
        public Action StartAsHost;
        /// <summary>以副机模式开始共享。</summary>
        public Action StartAsClient;
        /// <summary>停止共享。</summary>
        public Action StopSharing;
        /// <summary>强制把光标收回本机（热键 Ctrl+Alt+Home 的等价菜单项）。</summary>
        public Action ForceReturn;
        /// <summary>检查更新。</summary>
        public Action CheckUpdate;
        /// <summary>打开配置目录。</summary>
        public Action OpenConfigFolder;
        /// <summary>当前是否正在共享。</summary>
        public Func<bool> IsRunning;
        /// <summary>当前是否以主机角色运行。</summary>
        public Func<bool> IsHost;
    }

    /// <summary>
    /// 系统托盘图标（「托盘代理」的完整产出）。
    ///
    /// 设计要点：
    ///   * 用 WinForms 的 NotifyIcon，WPF 本身没有托盘 API；
    ///   * 关闭主窗口只是隐藏，真正退出必须显式 Dispose，否则图标会残留在任务栏
    ///     （这是 WinForms 托盘最常见的“幽灵图标”问题）；
    ///   * 图标资源从 pack:// 里读取，读不到就退回系统默认图标，绝不抛异常；
    ///   * 菜单项在 Opening 事件里动态启用/禁用，菜单打开前状态还没确定，
    ///     静态设置一定会显示错。
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private const string IconResourceUri = "pack://application:,,,/Assets/app.ico";
        private const string BaseTooltip = "LanMouseShare 局域网键鼠共享";

        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _menu;
        private Icon _icon;
        private MemoryStream _iconStream;
        private TrayMenuCallbacks _callbacks;
        private ToolStripMenuItem _showItem;
        private ToolStripMenuItem _hostItem;
        private ToolStripMenuItem _clientItem;
        private ToolStripMenuItem _stopItem;
        private ToolStripMenuItem _returnItem;
        private ToolStripMenuItem _updateItem;
        private ToolStripMenuItem _folderItem;
        private ToolStripMenuItem _exitItem;
        private bool _disposed;

        /// <summary>托盘是否已经成功创建。</summary>
        public bool IsAvailable
        {
            get { return _notifyIcon != null && !_disposed; }
        }

        /// <summary>
        /// 创建托盘图标。
        /// 必须在 UI 线程上调用（NotifyIcon 需要消息泵）。
        /// </summary>
        public void Initialize(Window owner, TrayMenuCallbacks callbacks)
        {
            if (_notifyIcon != null)
            {
                return;
            }

            _callbacks = callbacks;
            _icon = LoadIcon();

            _menu = new ContextMenuStrip();
            _showItem = new ToolStripMenuItem("显示主窗口(&S)");
            _showItem.Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold);
            _showItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.ShowWindow); };

            _hostItem = new ToolStripMenuItem("以主机模式开始共享(&H)");
            _hostItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.StartAsHost); };

            _clientItem = new ToolStripMenuItem("以副机模式开始共享(&C)");
            _clientItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.StartAsClient); };

            _stopItem = new ToolStripMenuItem("停止共享(&T)");
            _stopItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.StopSharing); };

            _returnItem = new ToolStripMenuItem("把光标收回本机(&R)");
            _returnItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.ForceReturn); };

            _updateItem = new ToolStripMenuItem("检查更新(&U)");
            _updateItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.CheckUpdate); };

            _folderItem = new ToolStripMenuItem("打开配置目录(&F)");
            _folderItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.OpenConfigFolder); };

            _exitItem = new ToolStripMenuItem("退出(&X)");
            _exitItem.Click += delegate { Run(_callbacks == null ? null : _callbacks.ExitApplication); };

            _menu.Items.Add(_showItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_hostItem);
            _menu.Items.Add(_clientItem);
            _menu.Items.Add(_stopItem);
            _menu.Items.Add(_returnItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_updateItem);
            _menu.Items.Add(_folderItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_exitItem);

            // 菜单每次弹出前刷新可用状态与文案。
            _menu.Opening += OnMenuOpening;

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = _icon;
            _notifyIcon.Text = BaseTooltip;
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += delegate { Run(_callbacks == null ? null : _callbacks.ShowWindow); };
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool running = false;
            bool isHost = false;
            try
            {
                if (_callbacks != null && _callbacks.IsRunning != null)
                {
                    running = _callbacks.IsRunning();
                }
                if (_callbacks != null && _callbacks.IsHost != null)
                {
                    isHost = _callbacks.IsHost();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取托盘菜单状态失败：" + ex.Message);
            }

            _hostItem.Enabled = !running || !isHost;
            _clientItem.Enabled = !running || isHost;
            _stopItem.Enabled = running;
            _returnItem.Enabled = running && isHost;
            _hostItem.Checked = running && isHost;
            _clientItem.Checked = running && !isHost;
        }

        /// <summary>更新托盘提示文字（鼠标悬停时显示）。</summary>
        public void UpdateTooltip(string text)
        {
            if (!IsAvailable)
            {
                return;
            }
            try
            {
                string tooltip = string.IsNullOrEmpty(text) ? BaseTooltip : (BaseTooltip + " · " + text);
                // NotifyIcon.Text 上限 63 个字符，超了会抛异常。
                if (tooltip.Length > 63)
                {
                    tooltip = tooltip.Substring(0, 63);
                }
                _notifyIcon.Text = tooltip;
            }
            catch (Exception ex)
            {
                Log.Warn("更新托盘提示失败：" + ex.Message);
            }
        }

        /// <summary>弹出气泡提示（Win7 及以上都支持）。</summary>
        public void ShowBalloon(string title, string text)
        {
            if (!IsAvailable)
            {
                return;
            }
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.ShowBalloonTip(3000);
            }
            catch (Exception ex)
            {
                Log.Warn("托盘气泡提示失败：" + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_menu != null)
            {
                _menu.Opening -= OnMenuOpening;
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
            if (_icon != null)
            {
                _icon.Dispose();
                _icon = null;
            }
            if (_iconStream != null)
            {
                _iconStream.Dispose();
                _iconStream = null;
            }
        }

        private static void Run(Action action)
        {
            if (action == null)
            {
                return;
            }
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("托盘菜单动作执行失败", ex);
            }
        }

        /// <summary>
        /// 从 WPF 资源里读取 app.ico。
        /// 先把资源流完整读进内存流，既避免“流被释放后图标失效”，
        /// 也便于用 SmallIconSize 构造托盘所需的 16x16 尺寸。
        /// </summary>
        private Icon LoadIcon()
        {
            try
            {
                StreamResourceInfo info = System.Windows.Application.GetResourceStream(new Uri(IconResourceUri));
                if (info != null && info.Stream != null)
                {
                    byte[] raw;
                    using (Stream source = info.Stream)
                    {
                        MemoryStream buffer = new MemoryStream();
                        source.CopyTo(buffer);
                        raw = buffer.ToArray();
                    }

                    // 保存内存流引用：GDI+ 的 Icon 在某些情况下仍会引用底层流。
                    _iconStream = new MemoryStream(raw);
                    return new Icon(_iconStream, SystemInformation.SmallIconSize);
                }
                Log.Warn("未能从资源中读取 " + IconResourceUri + "，托盘将使用系统默认图标。");
            }
            catch (Exception ex)
            {
                Log.Warn("托盘图标加载失败：" + ex.GetType().Name + " " + ex.Message);
            }
            return SystemIcons.Application;
        }
    }
}
