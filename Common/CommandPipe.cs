#if NET10_0_OR_GREATER
// This shared source also compiles with the Windows .NET Framework C# compiler.
#pragma warning disable CS8618, CS8625
#endif
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace LittleTools.Common
{
    // Shared by the .NET Framework tray host and the cross-platform assistant.
    internal sealed class CommandPipe : IDisposable
    {
        public const string ManagerName = "LittleTools.Manager.Command.v1";
        public const string AssistantName = "LittleTools.Assistant.Command.v1";
        private readonly object gate = new object();
        private NamedPipeServerStream active;
        private bool disposed;

        public CommandPipe(string name, Action<string> onCommand)
        {
            var thread = new Thread(delegate() { Listen(name, onCommand); });
            thread.IsBackground = true;
            thread.Start();
        }

        public static int Send(string name, string command, int timeout)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(timeout);
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true))
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine(command);
                        var response = reader.ReadLineAsync();
                        int processId;
                        return response.Wait(timeout) && int.TryParse(response.Result, out processId) ? processId : 0;
                    }
                }
            }
            catch { return 0; }
        }

        private void Listen(string name, Action<string> onCommand)
        {
            int processId;
            using (var process = Process.GetCurrentProcess()) processId = process.Id;
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        lock (gate)
                        {
                            if (disposed) return;
                            active = server;
                        }
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true))
                        using (var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true))
                        {
                            var request = reader.ReadLineAsync();
                            if (!request.Wait(3000)) continue;
                            if (request.Result == null) continue;
                            writer.AutoFlush = true;
                            writer.WriteLine(processId);
                            // Acknowledge before dispatching Exit, which may dispose
                            // this server immediately on the application's UI thread.
                            if (request.Result != "ping") onCommand(request.Result);
                        }
                    }
                }
                catch
                {
                    lock (gate) { if (disposed) return; }
                    Thread.Sleep(100);
                }
                finally { lock (gate) { active = null; } }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                if (active != null) active.Dispose();
            }
        }
    }
}
