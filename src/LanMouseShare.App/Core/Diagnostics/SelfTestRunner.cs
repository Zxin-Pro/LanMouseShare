using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LanMouseShare.Core.Diagnostics
{
    /// <summary>
    /// 自检收集器（对应「测试代理」的第一阶段产出）。
    ///
    /// 用法：由 App.RunSelfTest() 逐项 Check，最后读取 Summary / Details。
    /// 输出格式被工具脚本 tools\smoke-test.ps1 解析，因此
    /// 「SELFTEST RESULT:」这一行由调用方负责写入日志，格式不要改动。
    /// </summary>
    public sealed class SelfTestRunner
    {
        private readonly List<string> _failedNames = new List<string>();
        private readonly StringBuilder _details = new StringBuilder();
        private int _passed;
        private int _total;

        public int Passed
        {
            get { return _passed; }
        }

        public int Total
        {
            get { return _total; }
        }

        public bool AllPassed
        {
            get { return _total > 0 && _passed == _total; }
        }

        /// <summary>多行明细，每行形如「  [PASS] 项目名 —— 说明」。</summary>
        public string Details
        {
            get { return _details.ToString(); }
        }

        /// <summary>未通过项的名称，逗号分隔。</summary>
        public string FailedNames
        {
            get
            {
                if (_failedNames.Count == 0)
                {
                    return string.Empty;
                }
                return string.Join(", ", _failedNames.ToArray());
            }
        }

        /// <summary>一行汇总。</summary>
        public string Summary
        {
            get
            {
                if (_total == 0)
                {
                    return "未执行任何检查";
                }
                string head = _passed.ToString(CultureInfo.InvariantCulture) + "/" +
                              _total.ToString(CultureInfo.InvariantCulture) + " 项通过";
                if (AllPassed)
                {
                    return "全部通过（" + head + "）";
                }
                return "存在失败（" + head + "）：" + FailedNames;
            }
        }

        public void Check(string name, bool passed, string detail)
        {
            _total++;
            if (passed)
            {
                _passed++;
            }
            else
            {
                _failedNames.Add(name);
            }

            _details.Append(passed ? "  [PASS] " : "  [FAIL] ")
                    .Append(name);
            if (!string.IsNullOrEmpty(detail))
            {
                _details.Append(" —— ").Append(detail);
            }
            _details.Append(Environment.NewLine);
        }
    }
}
