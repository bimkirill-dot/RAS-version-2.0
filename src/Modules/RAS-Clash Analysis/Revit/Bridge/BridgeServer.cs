using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RASv2.Core;

namespace RASv2.ClashAnalysis.Bridge
{
    /// <summary>
    /// Мост слушает только 127.0.0.1 — без прав администратора и без вопроса брандмауэра; порт — первый свободный из
    /// Ports (у Revit 2023 и 2026, открытых вместе, — разные). Просьбы — только от своего сайта (заголовок Origin): посторонние
    /// страницы Revit не командуют (DECISIONS.md, «Тонкости моста»). Простой HTTP/1.1 без сторонних библиотек: GET /status,
    /// POST /show; Revit API здесь не вызывается — только обработчики, которые отдают работу в Revit.
    /// </summary>
    internal sealed class BridgeServer : IDisposable
    {
        public static readonly int[] Ports = { 5181, 5182, 5183, 5184, 5185 };
        private const int MaxHead = 16 * 1024, MaxBody = 1024 * 1024;

        private readonly TcpListener _listener;
        private readonly string _site;
        private readonly Func<byte[]> _status;
        private readonly Func<ShowRequest, ShowReply> _show;
        private readonly ModuleLog _log;
        private volatile bool _stopped;

        public int Port { get; }

        private BridgeServer(TcpListener listener, int port, string site, Func<byte[]> status, Func<ShowRequest, ShowReply> show, ModuleLog log)
        {
            _listener = listener;
            Port = port;
            _site = site;
            _status = status;
            _show = show;
            _log = log;
            new Thread(Accept) { IsBackground = true, Name = "RAS-Clash Analysis: мост" }.Start();
        }

        /// <summary>Занять первый свободный порт; все заняты — null (в журнал).</summary>
        /// <param name="site">Адрес сайта коллизий: просьбы — только с него (Logic.SiteOrigins).</param>
        public static BridgeServer Start(string site, Func<byte[]> status, Func<ShowRequest, ShowReply> show, ModuleLog log)
        {
            foreach (var port in Ports)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                    return new BridgeServer(listener, port, site, status, show, log);
                }
                catch (SocketException)
                {
                    // порт занят (другой Revit или чужая программа) — следующий
                }
            }
            log.Info("Мост: все порты " + string.Join(", ", Ports) + " заняты — «Показать в Revit» в этом Revit не работает");
            return null;
        }

        private void Accept()
        {
            while (!_stopped)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) when (_stopped) { return; }
                catch (Exception ex)
                {
                    _log.Error("Мост: не принято подключение", ex);
                    Thread.Sleep(500);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var stream = client.GetStream();
                    var request = ReadRequest(stream);
                    if (request == null) return;
                    Reply(stream, request);
                }
                catch (IOException)
                {
                    // браузер закрыл соединение (ушёл со страницы) — отвечать некому
                }
                catch (Exception ex)
                {
                    _log.Error("Мост: ошибка обработки просьбы", ex);
                }
            }
        }

        private sealed class Request
        {
            public string Method, Path, Origin;
            public byte[] Body = Array.Empty<byte>();
        }

        private static Request ReadRequest(NetworkStream stream)
        {
            var head = new List<byte>(1024);
            while (head.Count < MaxHead)
            {
                var b = stream.ReadByte();
                if (b < 0) return null;
                head.Add((byte)b);
                var n = head.Count;
                if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') break;
            }
            var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var request = new Request { Method = first[0].ToUpperInvariant(), Path = first[1].Split('?')[0] };
            var length = 0;
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) request.Origin = value;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out length);
            }
            if (length > 0 && length <= MaxBody)
            {
                request.Body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var r = stream.Read(request.Body, read, length - read);
                    if (r <= 0) return null;
                    read += r;
                }
            }
            return request;
        }

        private void Reply(NetworkStream stream, Request request)
        {
            // только свой сайт: у браузера Origin есть всегда, когда страница обращается к другому адресу
            if (!Logic.SiteOrigins.Allowed(request.Origin, _site))
            {
                Send(stream, 403, "Forbidden", null, null);
                return;
            }
            if (request.Method == "OPTIONS")
            {
                Send(stream, 204, "No Content", null, request.Origin);
                return;
            }
            if (request.Method == "GET" && request.Path == "/status")
            {
                Send(stream, 200, "OK", _status(), request.Origin);
                return;
            }
            if (request.Method == "POST" && request.Path == "/show")
            {
                ShowReply reply;
                try { reply = _show(BridgeProtocol.Read<ShowRequest>(request.Body)); }
                catch (System.Runtime.Serialization.SerializationException) { reply = ShowReply.Fail("Мост не понял просьбу сайта — обновите страницу."); }
                Send(stream, 200, "OK", BridgeProtocol.Write(reply), request.Origin);
                return;
            }
            Send(stream, 404, "Not Found", null, request.Origin);
        }

        private static void Send(NetworkStream stream, int code, string text, byte[] body, string origin)
        {
            body = body ?? Array.Empty<byte>();
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(code).Append(' ').Append(text).Append("\r\n");
            if (body.Length > 0) head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            if (origin != null)
            {
                head.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
                head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                head.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                // сайт на сервере (позже) — из сети к 127.0.0.1: браузер спрашивает разрешение на доступ к локальному адресу
                head.Append("Access-Control-Allow-Private-Network: true\r\n");
                head.Append("Vary: Origin\r\n");
            }
            head.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(bytes, 0, bytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            _stopped = true;
            try { _listener.Stop(); }
            catch (SocketException) { /* уже остановлен — Revit закрывается, ответить некому */ }
        }
    }
}
