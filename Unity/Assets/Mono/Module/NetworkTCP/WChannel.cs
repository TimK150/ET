using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ET
{
    public class WChannel : AChannel
    {
        public HttpListenerWebSocketContext WebSocketContext { get; }

        private readonly WService Service;

        private readonly WebSocket webSocket;

        private readonly Queue<byte[]> queue = new Queue<byte[]>();

        private bool isSending;

        private bool isConnected;

        private readonly MemoryStream recvStream;

        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        public WChannel(long id, HttpListenerWebSocketContext webSocketContext, WService service)
        {
            this.Id = id;
            this.Service = service;
            this.ChannelType = ChannelType.Accept;
            this.WebSocketContext = webSocketContext;
            this.webSocket = webSocketContext.WebSocket;
            this.recvStream = new MemoryStream(ushort.MaxValue);
            isConnected = true;

            this.Service.ThreadSynchronizationContext.PostNext(() =>
            {
                this.cancellationTokenSource = new CancellationTokenSource();
                this.StartRecv().Coroutine();
                this.StartSend().Coroutine();
            });
        }

        public WChannel(long id, WebSocket webSocket, string connectUrl, WService service)
        {
            this.Id = id;
            this.Service = service;
            this.ChannelType = ChannelType.Connect;
            this.webSocket = webSocket;
            this.recvStream = new MemoryStream(ushort.MaxValue);

            isConnected = false;

            this.Service.ThreadSynchronizationContext.PostNext(() => this.ConnectAsync(connectUrl).Coroutine());
        }

        public override void Dispose()
        {
            if (this.IsDisposed)
            {
                return;
            }

            this.cancellationTokenSource.Cancel();
            this.cancellationTokenSource.Dispose();
            this.cancellationTokenSource = null;

            this.webSocket.Dispose();
        }

        public async ETTask ConnectAsync(string url)
        {
            try
            {
                await ((ClientWebSocket)this.webSocket).ConnectAsync(new Uri(url), cancellationTokenSource.Token);
                isConnected = true;

                this.StartRecv().Coroutine();
                this.StartSend().Coroutine();
            }
            catch (Exception e)
            {
                Log.Error(e);
                this.OnError(ErrorCore.ERR_WebsocketConnectError);
            }
        }

        public void Send(MemoryStream stream)
        {
            byte[] bytes = new byte[stream.Length - stream.Position];
            Array.Copy(stream.GetBuffer(), stream.Position, bytes, 0, bytes.Length);
            this.queue.Enqueue(bytes);

            if (this.isConnected)
            {
                this.StartSend().Coroutine();
            }
        }

        public async ETTask StartSend()
        {
            if (this.IsDisposed)
            {
                return;
            }

            try
            {
                if (this.isSending)
                {
                    return;
                }

                this.isSending = true;

                while (true)
                {
                    if (this.queue.Count == 0)
                    {
                        this.isSending = false;
                        return;
                    }

                    byte[] bytes = this.queue.Dequeue();
                    try
                    {
                        await this.webSocket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length), WebSocketMessageType.Binary, true, cancellationTokenSource.Token);
                        if (this.IsDisposed)
                        {
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                        this.OnError(ErrorCore.ERR_WebsocketSendError);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private byte[] cache = new byte[ushort.MaxValue];

        public async ETTask StartRecv()
        {
            if (this.IsDisposed)
            {
                return;
            }

            try
            {
                while (webSocket.State==WebSocketState.Open)
                {
                    //#if NOT_UNITY
                    //                    ValueWebSocketReceiveResult receiveResult;
                    //#else
                    byte[] returnBytes = new byte[10240];
                    WebSocketReceiveResult webSocketReceiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(returnBytes), CancellationToken.None);
                    string ReceivesData = Encoding.UTF8.GetString(returnBytes, 0, webSocketReceiveResult.Count);
                    ReceivesData = $"我已经收到数据：{ReceivesData}";
                    Console.WriteLine(ReceivesData);
                    returnBytes = Encoding.UTF8.GetBytes(ReceivesData);
                    await webSocket.SendAsync(new ArraySegment<byte>(returnBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
                this.OnError(ErrorCore.ERR_WebsocketRecvError);
            }
            finally
            {
                if (webSocket.State == WebSocketState.Closed)
                    this.Dispose();
            }
        }

        private void OnRead(MemoryStream memoryStream)
        {
            try
            {
                long channelId = this.Id;
                this.Service.OnRead(channelId, memoryStream);
            }
            catch (Exception e)
            {
                Log.Error($"{this.RemoteAddress} {memoryStream.Length} {e}");
                // 出现任何消息解析异常都要断开Session，防止客户端伪造消息
                this.OnError(ErrorCore.ERR_PacketParserError);
            }
        }

        private void OnError(int error)
        {
            Log.Debug($"WChannel error: {error} {this.RemoteAddress}");

            long channelId = this.Id;

            this.Service.Remove(channelId);

            this.Service.OnError(channelId, error);
        }
    }
}