using System;
using System.Collections.Generic;
using System.Threading;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// 出站帧队列（第七阶段：性能优化代理的核心成果）。
    ///
    /// 为什么不让钩子回调直接 Send：
    ///   WH_*_LL 回调的预算是 300ms，超时会被系统静默摘钩。
    ///   直接在回调里做 TCP 写，一旦网络抖动就会超时摘钩 —— 表现为
    ///   「用着用着鼠标就不受控了」，而且没有任何错误提示，极难排查。
    ///   所以回调只往队列里塞一个对象（微秒级），发送交给后台线程。
    ///
    /// 为什么队列里要合并鼠标移动：
    ///   游戏鼠标 1000Hz 上报，每秒 1000 个 move 帧足以把 100Mbps 网络
    ///   喂满并让副机出现明显延迟。但移动是**状态**不是**事件** ——
    ///   中间位置丢了不影响结果，只要最后位置对。所以同类型的相邻 move
    ///   就地覆盖。按键和滚轮是事件，**绝不合并、绝不丢弃**。
    ///
    /// 顺序性：合并只在「队尾也是 move」时进行，所以
    ///   move, click, move 的先后关系永远不会被打乱（拖拽才不会被玩坏）。
    /// </summary>
    public sealed class FramePump : IDisposable
    {
        private const int MaxPendingFrames = 512;

        private readonly PeerConnection _peer;
        private readonly object _sync = new object();
        private readonly LinkedList<Frame> _queue = new LinkedList<Frame>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private Thread _thread;
        private volatile bool _running;

        private long _sent;
        private long _coalesced;
        private long _dropped;
        private long _failed;
        private string _lastError;

        public FramePump(PeerConnection peer)
        {
            if (peer == null)
            {
                throw new ArgumentNullException("peer");
            }

            _peer = peer;
        }

        public long SentCount
        {
            get { return Interlocked.Read(ref _sent); }
        }

        public long CoalescedCount
        {
            get { return Interlocked.Read(ref _coalesced); }
        }

        public long DroppedCount
        {
            get { return Interlocked.Read(ref _dropped); }
        }

        public long FailedCount
        {
            get { return Interlocked.Read(ref _failed); }
        }

        public string LastError
        {
            get { return _lastError; }
        }

        public int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    return _queue.Count;
                }
            }
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _thread = new Thread(SendLoop);
            _thread.IsBackground = true;
            _thread.Name = "lms-frame-pump";
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
        }

        /// <summary>入队。从钩子回调里调用，必须保持微秒级。</summary>
        public void Post(Frame frame)
        {
            if (frame == null)
            {
                return;
            }

            lock (_sync)
            {
                LinkedListNode<Frame> tail = _queue.Last;
                bool isMove = frame.type == Msg.MouseMove;

                if (isMove && tail != null && tail.Value.type == Msg.MouseMove)
                {
                    // 就地覆盖：顺序不变，位置取最新
                    tail.Value = frame;
                    Interlocked.Increment(ref _coalesced);
                }
                else
                {
                    _queue.AddLast(frame);
                }

                if (_queue.Count > MaxPendingFrames)
                {
                    TrimLocked();
                }
            }

            _signal.Set();
        }

        public void Clear()
        {
            lock (_sync)
            {
                _queue.Clear();
            }
        }

        private void TrimLocked()
        {
            // 只丢最老的 mouse_move，按键事件一律保留到最后
            LinkedListNode<Frame> node = _queue.First;
            while (node != null && _queue.Count > MaxPendingFrames)
            {
                LinkedListNode<Frame> next = node.Next;
                if (node.Value.type == Msg.MouseMove)
                {
                    _queue.Remove(node);
                    Interlocked.Increment(ref _dropped);
                }

                node = next;
            }
        }

        private void SendLoop()
        {
            while (_running)
            {
                Frame frame = null;
                lock (_sync)
                {
                    LinkedListNode<Frame> first = _queue.First;
                    if (first != null)
                    {
                        frame = first.Value;
                        _queue.RemoveFirst();
                    }
                }

                if (frame == null)
                {
                    _signal.WaitOne(2);
                    continue;
                }

                try
                {
                    if (_peer.SendAuthenticated(frame))
                    {
                        Interlocked.Increment(ref _sent);
                    }
                    else
                    {
                        Interlocked.Increment(ref _failed);
                        _lastError = "发送 " + frame.type + " 时连接不可用";
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    _lastError = "发送 " + frame.type + " 失败：" + ex.Message;
                }
            }

            _signal.Reset();
        }

        public void Dispose()
        {
            _running = false;
            _signal.Set();

            try
            {
                if (_thread != null && _thread.IsAlive)
                {
                    _thread.Join(500);
                }
            }
            catch
            {
            }

            _signal.Close();
        }
    }
}
