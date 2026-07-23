// 全局 Console 中继:
// vendored DepotDownloader 的所有输出走 Console.Out/Error,
// Steam Guard/密码等交互走 Console.ReadLine(ConsoleAuthenticator)。
// 这里把三者重定向:输出 → 行日志事件;ReadLine → 阻塞等待 Web UI 提交。
// 效果等价于 Python 版的 pexpect 方案,但提示捕获是确定性的(无需正则猜测)。
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace SteamDl.Core
{
    public sealed class ConsoleRelay
    {
        public static ConsoleRelay Instance { get; } = new();

        readonly object _sync = new();
        readonly StringBuilder _partial = new();
        readonly BlockingCollection<string> _answers = new();
        TextWriter _passthrough;

        /// <summary>每产生一个完整输出行触发。</summary>
        public event Action<string> LineWritten;
        /// <summary>引擎阻塞等待输入时触发,参数为提示文本。</summary>
        public event Action<string> InputRequested;
        /// <summary>输入被满足、继续执行时触发。</summary>
        public event Action InputSatisfied;

        ConsoleRelay()
        {
        }

        /// <summary>接管进程级 Console,进程内只需调用一次。</summary>
        public void Install(bool passthroughToStdout = false)
        {
            if (passthroughToStdout)
            {
                _passthrough = Console.Out;
            }

            var writer = new RelayWriter(this);
            Console.SetOut(writer);
            Console.SetError(writer);
            Console.SetIn(new RelayReader(this));
        }

        /// <summary>由 Web API(/api/input)调用,喂给阻塞中的 ReadLine。</summary>
        public void SupplyInput(string answer)
        {
            _answers.Add(answer ?? string.Empty);
        }

        /// <summary>清空积压的输入(任务开始前调用,避免上一次残留)。</summary>
        public void DrainPendingInput()
        {
            while (_answers.TryTake(out _))
            {
            }
        }

        /// <summary>主动向 UI 发起一次询问并阻塞等待(用于引擎调用前的密码收集)。</summary>
        public string PromptAndRead(string prompt)
        {
            InputRequested?.Invoke(prompt);
            var answer = _answers.Take();
            InputSatisfied?.Invoke();
            return answer;
        }

        internal void OnChar(char c)
        {
            _passthrough?.Write(c);

            string line = null;
            lock (_sync)
            {
                if (c == '\n' || c == '\r')
                {
                    if (_partial.Length > 0)
                    {
                        line = _partial.ToString().Trim();
                        _partial.Clear();
                    }
                }
                else
                {
                    _partial.Append(c);
                }
            }

            if (!string.IsNullOrEmpty(line))
            {
                LineWritten?.Invoke(line);
            }
        }

        internal string OnReadLine()
        {
            // 被 ConsoleAuthenticator 等阻塞调用:未换行的残余输出就是提示语
            string prompt;
            lock (_sync)
            {
                prompt = _partial.ToString().Trim();
                _partial.Clear();
            }

            InputRequested?.Invoke(prompt);
            var answer = _answers.Take();
            InputSatisfied?.Invoke();
            return answer;
        }

        sealed class RelayWriter(ConsoleRelay relay) : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value) => relay.OnChar(value);

            public override void Write(string value)
            {
                if (value == null)
                {
                    return;
                }

                foreach (var c in value)
                {
                    relay.OnChar(c);
                }
            }
        }

        sealed class RelayReader(ConsoleRelay relay) : TextReader
        {
            public override string ReadLine() => relay.OnReadLine();
        }
    }
}
