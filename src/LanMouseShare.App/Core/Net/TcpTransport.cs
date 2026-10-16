using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// TCP 传输层（第二阶段：网络基础代理）。
    ///
    /// 设计要点：
    ///   * 专用后台线程做阻塞读，不用 async/await —— 消息泵逻辑更好推理，
    ///     而且 csc 在 net48 下对 async 状态机的支持没必要引入。
    ///   * NoDelay = true：关掉 Nagle 算法。键鼠共享最怕的是延迟抖动，
    ///     小包合并省下的那点带宽远不值 40ms 的额外延迟。
    ///   * 一个实例同一时刻只服务一个对端；断开后由上层决定是否重新监听。
    ///   * Send 加锁：网络流不是线程安全的。
    /// </summary>
    public sealed class TcpTransport : IDisposable
    {
        private readonly ProtocolCodec _codec = new ProtocolCodec();
        private readonly object _sendLock = new object();

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private volatile bool _running;
        private bool _disposed;

        public event Action<Frame> FrameReceived;
        public event Action<string> StateChanged;
        public event Action<Exception> Failed;

        /// <summary>本次连接累计收到的坏帧字节数（用于诊断）。</summary>
        public int DroppedBytes { get; private set; }

        public bool IsConnected
        {
            get { return _stream != null; }
        }

        // ---------------------------------------------------------------- 监听

        public void StartListen(int port)
        {
            Close(false);
            _codec.Reset();
            DroppedBytes = 0;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            RaiseState("正在监听 0.0.0.0:" + port);

            StartThread("lms-accept", AcceptLoop);
        }

        private void AcceptLoop()
        {
            try
            {
                TcpClient client = _listener.AcceptTcpClient();
                Configure(client);
                _client = client;
                _stream = client.GetStream();
                RaiseState("对端已接入：" + Describe(client));
                StartThread("lms-read", ReadLoop);
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    RaiseFailed(ex);
                }
            }
        }

        // ---------------------------------------------------------------- 连接

        public bool Connect(string host, int port, int timeoutMs)
        {
            Close(false);
            _codec.Reset();
            DroppedBytes = 0;

            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                bool completed = ar.AsyncWaitHandle.WaitOne(timeoutMs, false);
                if (!completed)
                {
                    try { client.Close(); } catch (Exception) { }
                    RaiseFailed(new TimeoutException("连接 " + host + ":" + port + " 超时（" + timeoutMs + " ms）"));
                    return false;
                }

                client.EndConnect(ar);
                Configure(client);
                _client = client;
                _stream = client.GetStream();
                _running = true;
                RaiseState("已连接到 " + host + ":" + port);
                StartThread("lms-read", ReadLoop);
                return true;
            }
            catch (Exception ex)
            {
                try { client.Close(); } catch (Exception) { }
                RaiseFailed(ex);
                return false;
            }
        }

        private static void Configure(TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveBufferSize = 64 * 1024;
            client.SendBufferSize = 64 * 1024;
            client.LingerState = new LingerOption(false, 0);
        }

        private static string Describe(TcpClient client)
        {
            try
            {
                if (client.Client != null && client.Client.RemoteEndPoint != null)
                {
                    return client.Client.RemoteEndPoint.ToString();
                }
            }
            catch (Exception)
            {
            }

            return "未知地址";
        }

        // ---------------------------------------------------------------- 读循环

        private void ReadLoop()
        {
            byte[] buffer = new byte[8192];
            try
            {
                while (_running)
                {
                    NetworkStream s = _stream;
                    if (s == null)
                    {
                        break;
                    }

                    int n = s.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                    {
                        break;
                    }

                    int dropped;
                    List<Frame> frames = _codec.Append(buffer, 0, n, out dropped);
                    if (dropped > 0)
                    {
                        DroppedBytes += dropped;
                    }

                    for (int i = 0; i < frames.Count; i++)
                    {
                        Action<Frame> handler = FrameReceived;
                        if (handler != null)
                        {
                            handler(frames[i]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    RaiseFailed(ex);
                }
            }
            finally
            {
                RaiseState("连接已断开");
            }
        }

        // ---------------------------------------------------------------- 发送

        public bool Send(Frame frame)
        {
            NetworkStream s = _stream;
            if (s == null)
            {
                return false;
            }

            byte[] line;
            try
            {
                line = _codec.Encode(frame);
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                return false;
            }

            lock (_sendLock)
            {
                try
                {
                    s.Write(line, 0, line.Length);
                    s.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    RaiseFailed(ex);
                    return false;
                }
            }
        }

        // ---------------------------------------------------------------- 关闭

        public void Close()
        {
            Close(true);
        }

        private void Close(bool notify)
        {
            _running = false;

            try { if (_stream != null) { _stream.Close(); } }
            catch (Exception) { }

            try { if (_client != null) { _client.Close(); } }
            catch (Exception) { }

            try { if (_listener != null) { _listener.Stop(); } }
            catch (Exception) { }

            _stream = null;
            _client = null;
            _listener = null;

            if (notify)
            {
                RaiseState("已关闭");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close(false);
        }

        // ---------------------------------------------------------------- 事件

        private void StartThread(string name, ThreadStart body)
        {
            Thread t = new Thread(body);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        private void RaiseState(string text)
        {
            Action<string> handler = StateChanged;
            if (handler != null)
            {
                handler(text);
            }
        }

        private void RaiseFailed(Exception ex)
        {
            Action<Exception> handler = Failed;
            if (handler != null)
            {
                handler(ex);
            }
        }
    }
}
