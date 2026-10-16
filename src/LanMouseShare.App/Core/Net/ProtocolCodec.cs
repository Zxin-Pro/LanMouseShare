using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// JSON 行协议编解码器（第二阶段：协议设计代理）。
    ///
    /// 设计要点：
    ///   * 增量解析：收到的字节先攒进缓冲区，遇到 '\n' 才切出一帧。
    ///     TCP 是字节流，一次 Read 可能只拿到半行，也可能拿到三行半。
    ///   * 坏帧只丢该帧并计数，绝不让一条脏数据打断整条连接。
    ///   * 单帧上限 64KB：防止对端发超长行把内存吃光。
    ///   * 两个独立的 JavaScriptSerializer 实例：Encode 在发送线程、
    ///     Decode 在读取线程，实例不是线程安全的，分开用最省事。
    /// </summary>
    public sealed class ProtocolCodec
    {
        public const int MaxFrameBytes = 64 * 1024;

        private readonly JavaScriptSerializer _encoder = new JavaScriptSerializer();
        private readonly JavaScriptSerializer _decoder = new JavaScriptSerializer();
        private readonly List<byte> _buffer = new List<byte>();

        /// <summary>把一帧编码成「JSON + '\n'」的 UTF-8 字节。</summary>
        public byte[] Encode(Frame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            if (frame.stamp == 0)
            {
                frame.stamp = Frame.NowMs();
            }

            string json = _encoder.Serialize(frame);
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] line = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, line, 0, body.Length);
            line[body.Length] = (byte)'\n';
            return line;
        }

        /// <summary>
        /// 追加收到的字节，返回本次能切出的完整帧（0 个或多个）。
        /// droppedBytes 报告被丢弃的坏字节数（脏帧 / 超长行）。
        /// </summary>
        public List<Frame> Append(byte[] data, int offset, int count, out int droppedBytes)
        {
            droppedBytes = 0;
            List<Frame> frames = new List<Frame>();
            if (data == null || count <= 0)
            {
                return frames;
            }

            for (int i = 0; i < count; i++)
            {
                _buffer.Add(data[offset + i]);
            }

            int start = 0;
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (_buffer[i] != (byte)'\n')
                {
                    continue;
                }

                int lineLength = i - start;
                if (lineLength > 0)
                {
                    byte[] line = new byte[lineLength];
                    for (int k = 0; k < lineLength; k++)
                    {
                        line[k] = _buffer[start + k];
                    }

                    Frame frame = Decode(line);
                    if (frame != null)
                    {
                        frames.Add(frame);
                    }
                    else
                    {
                        droppedBytes += lineLength + 1;
                    }
                }

                start = i + 1;
            }

            if (start > 0)
            {
                _buffer.RemoveRange(0, start);
            }

            if (_buffer.Count > MaxFrameBytes)
            {
                droppedBytes += _buffer.Count;
                _buffer.Clear();
            }

            return frames;
        }

        /// <summary>解码单帧；任何异常或缺少 type 都返回 null。</summary>
        public Frame Decode(byte[] utf8)
        {
            try
            {
                string text = Encoding.UTF8.GetString(utf8);
                if (text.Length == 0)
                {
                    return null;
                }

                Frame frame = _decoder.Deserialize<Frame>(text);
                if (frame == null || string.IsNullOrEmpty(frame.type))
                {
                    return null;
                }

                return frame;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Reset()
        {
            _buffer.Clear();
        }
    }
}
