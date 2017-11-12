using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace WebSocketServer
{
    class Program
    {
        readonly static MyEventLog log = new MyEventLog();

        static void Main(string[] args)
        {
            try
            {
                var port = int.Parse(ConfigurationManager.AppSettings["port"]);

                using (var hl = new HttpListener())
                {
                    hl.Prefixes.Add($"http://localhost:{port}/");
                    hl.Start();

                    log.WriteEntry($"WebSocketServer active, listening on {hl.Prefixes.Last()}", EventLogEntryType.Information);

                    while (true)
                    {
                        var ctx = hl.GetContext();
                        if (ctx.Request.IsWebSocketRequest)
                        {
                            ThreadPool.QueueUserWorkItem(ProcessRequest, ctx);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.WriteEntry($"Initialization: {ex}", EventLogEntryType.Error);
            }
        }

        private static void ProcessRequest(object state)
        {
            var hlc = state as HttpListenerContext;
            if (hlc != null)
                ProcessRequest(hlc);
        }

        private async static void ProcessRequest(HttpListenerContext ctx)
        {
            try
            {
                var wsctx = await ctx.AcceptWebSocketAsync(null);
                var ip = ctx.Request.RemoteEndPoint.ToString();
                log.WriteEntry($"Connection from {ip}", EventLogEntryType.Information);
                
                using (var ws = wsctx.WebSocket)
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        var recvbuf = WebSocket.CreateServerBuffer(1024);
                        var result = await ws.ReceiveAsync(recvbuf, System.Threading.CancellationToken.None);
                        var message = System.Text.Encoding.UTF8.GetString(recvbuf.Array).Trim('\0');

                        if (String.IsNullOrEmpty(message))
                            continue;

                        var processor = new CommandProcessor(ws, message);
                        processor.run();
                    }
                }
            }
            catch (Exception ex)
            {
                log.WriteEntry(ex.ToString(), EventLogEntryType.Error);
            }
        }

        class CommandException : Exception
        {
            private readonly string command;

            public CommandException(string command, string textError) : base(textError)
            {
                this.command = command;
            }

            public override String ToString()
            {
                return $"Command '{command}' caused error {base.ToString()}";
            }
        }

        class CommandWarningException : CommandException
        {
            public CommandWarningException(string command, string textError) : base(command, textError) { }
        }

        class CommandErrorException : CommandException
        {
            public CommandErrorException(string command, string textError) : base(command, textError) { }
        }


        class CommandProcessor
        {
            private readonly WebSocket ws;

            private readonly string input;

            public CommandProcessor(WebSocket ws, string input)
            {
                this.ws = ws;
                this.input = input;
            }

            public void run()
            {
                try
                {
                    if (input == null || input.Length == 0 || input.Length >= 1024)
                        throw new CommandWarningException(input, "incorrect input parameters");

                    Match mtch = Regex.Match(input, @"^([a-z]+?) (.+)$", RegexOptions.IgnoreCase);
                    if (!mtch.Success || mtch.Groups.Count != 3)
                        throw new CommandWarningException(input, "incorrect input format");

                    var commandName = mtch.Groups[1].Value.ToLower();
                    var arguments = mtch.Groups[2].Value;

                    Route(commandName, arguments);
                }
                catch (CommandWarningException cwe)
                {
                    log.WriteEntry(cwe.ToString(), EventLogEntryType.Warning);
                }
                catch (CommandErrorException cee)
                {
                    log.WriteEntry(cee.ToString(), EventLogEntryType.Error);
                }
                catch (Exception ex)
                {
                    log.WriteEntry(ex.ToString(), EventLogEntryType.Error);
                }
            }

            private void Route(string commandName, string arguments)
            {
                switch (commandName)
                {
                    case "exec": RunProgram(arguments); break;
                    case "echo": Echo(arguments); break;
                    default:
                        throw new CommandErrorException(commandName, "unrecognized command");
                }
            }

            private async void Echo(string arguments)
            {
                var sendbuf = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(arguments));
                await ws.SendAsync(sendbuf, WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
            }

            private void RunProgram(string arguments)
            {
                var psi = new ProcessStartInfo()
                {
                    FileName = arguments
                };
                Process.Start(psi);
            }
        }

        class MyEventLog
        {
            readonly object locking = new object();

            internal void WriteEntry(string text, EventLogEntryType type)
            {
                lock (locking)
                {
                    text = $"{DateTime.Now.ToLongTimeString()} {type} {text}\r\n";

                    Console.Write(text);
                    File.AppendAllText(GetLogFileName(), text);
                }
            }

            private string GetLogFileName()
            {
                return DateTime.Today.ToShortDateString().Replace("-", "") + ".log";
            }
        }


    }
}
