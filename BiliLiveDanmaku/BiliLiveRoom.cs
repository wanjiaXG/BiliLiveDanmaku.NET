﻿using BiliLive.Commands;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable 0067

namespace BiliLive
{
    public class BiliLiveRoom
    {
        private static readonly Dictionary<CommandType, FieldInfo> ListennerMapping;

        static BiliLiveRoom()
        {
            ListennerMapping = new Dictionary<CommandType, FieldInfo>();
            foreach (EventInfo info in typeof(BiliLiveRoom).GetEvents())
            {
                if (info.GetCustomAttribute<CommandTypeAttribute>() is CommandTypeAttribute attr)
                {
                    FieldInfo fieldInfo = typeof(BiliLiveRoom).GetField(info.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    ListennerMapping.Add(attr.Type, fieldInfo);
                }
            }
        }

        public Protocols Protocol { get; set; }

        public delegate void ConnectionEventHandler();
        public event ConnectionEventHandler Connected;
        public event ConnectionEventHandler Disconnected;

        public delegate void CommandHandler<T>(T cmd) where T : Command;

        public delegate void ConnectionFailedHandler(string message);
        public event ConnectionFailedHandler ConnectionFailed;

        public delegate void ServerHeartbeatRecievedHandler();
        public event ServerHeartbeatRecievedHandler ServerHeartbeatRecieved;

        public delegate void PopularityRecievedHandler(uint popularity);
        public event PopularityRecievedHandler PopularityRecieved;

        [CommandType(CommandType.COMBO_SEND)]
        public event CommandHandler<ComboSend> OnComboSend;

        [CommandType(CommandType.DANMU_MSG)]
        public event CommandHandler<Danmaku> OnDanmaku;

        [CommandType(CommandType.SEND_GIFT)]
        public event CommandHandler<Gift> OnGift;

        [CommandType(CommandType.GUARD_BUY)]
        public event CommandHandler<GuardBuy> OnGuardBuy;

        [CommandType(CommandType.INTERACT_WORD)]
        public event CommandHandler<InteractWord> OnInteractWord;

        [CommandType(CommandType.LIVE)]
        public event CommandHandler<Live> OnLive;

        [CommandType(CommandType.PREPARING)]
        public event CommandHandler<Preparing> OnPreparing;

        [CommandType(CommandType.ROOM_BLOCK_MSG)]
        public event CommandHandler<RoomBlock> OnRoomBlock;

        [CommandType(CommandType.SUPER_CHAT_MESSAGE)]
        public event CommandHandler<SuperChat> OnSuperChat;

        [CommandType(CommandType.WATCHED_CHANGE)]
        public event CommandHandler<WatchedChanged> OnWatchedChanged;

        [CommandType(CommandType.WELCOME)]
        public event CommandHandler<Welcome> OnWelcome;

        [CommandType(CommandType.WELCOME_GUARD)]
        public event CommandHandler<WelcomeGuard> OnWelcomeGuard;

        [CommandType(CommandType.WIDGET_BANNER)]
        public event CommandHandler<WidgetBanner> OnWidgetBanner;

        [CommandType(CommandType.HTTP_API_ONLINE_USER)]
        public event CommandHandler<OnlineUser> OnOnlineUser;

        public event CommandHandler<Command> OnUnknow;

        public event CommandHandler<Command> OnRaw;

        private TcpClient DanmakuTcpClient { get; set; }
        private ClientWebSocket DanmakuWebSocket { get; set; }
        private uint RoomId { get; set; }

        private BiliPackReader PackReader { get; set; }
        private BiliPackWriter PackWriter { get; set; }

        private Thread HeartbeatSenderThread { get; set; }
        private bool IsHeartbeatSenderRunning { get; set; }
        private Thread EventListenerThread { get; set; }
        public bool IsRunning { get; private set; }


        public string Cookie { set; get; }

        public BiliLiveRoom(uint roomId, string cookie, Protocols protocol = Protocols.Tcp) : this(roomId, protocol)
        {
            Cookie = cookie;
        }

        public BiliLiveRoom(uint roomId, Protocols protocol = Protocols.Tcp)
        {
            IsHeartbeatSenderRunning = false;
            IsRunning = false;
            RoomId = roomId;
            Protocol = protocol;
        }


        #region Public methods
        public Task<bool> ConnectAsync() => new Task<bool>(Connect);

        public bool Connect()
        {
            try
            {
                DanmakuServer danmakuServer = GetDanmakuServer(RoomId);
                if (danmakuServer == null)
                    return false;

                switch (Protocol)
                {
                    case Protocols.Tcp:
                        DanmakuTcpClient = GetTcpConnection(danmakuServer);
                        Stream stream = DanmakuTcpClient.GetStream();

                        stream.ReadTimeout = 30 * 1000 + 1000;
                        stream.WriteTimeout = 30 * 1000 + 1000;

                        PackReader = new BiliPackReader(stream);
                        PackWriter = new BiliPackWriter(stream);
                        break;
                    case Protocols.Ws:
                        DanmakuWebSocket = GetWsConnection(danmakuServer);
                        PackReader = new BiliPackReader(DanmakuWebSocket);
                        PackWriter = new BiliPackWriter(DanmakuWebSocket);
                        break;
                    case Protocols.Wss:
                        DanmakuWebSocket = GetWssConnection(danmakuServer);
                        PackReader = new BiliPackReader(DanmakuWebSocket);
                        PackWriter = new BiliPackWriter(DanmakuWebSocket);
                        break;
                }

                if (!InitConnection(danmakuServer))
                {
                    Disconnect();
                    return false;
                }

                StartEventListener();
                StartHeartbeatSender();

                Connected?.Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public Task DisconnectAsync() => new Task(Disconnect);

        public void Disconnect()
        {
            StopEventListener();
            StopHeartbeatSender();
            if (DanmakuTcpClient != null)
                try { DanmakuTcpClient.Close(); } catch { }
            if (DanmakuWebSocket != null)
            {
                try { DanmakuWebSocket.Abort(); } catch { }

                try { DanmakuWebSocket.Dispose(); } catch { }
                  
            }
            Disconnected?.Invoke();
        }

        #endregion

        #region Connect to a DanmakuServer

        private TcpClient GetTcpConnection(DanmakuServer danmakuServer)
        {
            TcpClient tcpClient = new TcpClient();
            tcpClient.Connect(danmakuServer.Server, danmakuServer.Port);
            return tcpClient;
        }

        private ClientWebSocket GetWsConnection(DanmakuServer danmakuServer)
        {
            ClientWebSocket clientWebSocket = new ClientWebSocket();
            clientWebSocket.ConnectAsync(new Uri($"ws://{danmakuServer.Server}:{danmakuServer.WsPort}/sub"), CancellationToken.None).GetAwaiter().GetResult();
            return clientWebSocket;
        }

        private ClientWebSocket GetWssConnection(DanmakuServer danmakuServer)
        {
            ClientWebSocket clientWebSocket = new ClientWebSocket();
            clientWebSocket.ConnectAsync(new Uri($"wss://{danmakuServer.Server}:{danmakuServer.WssPort}/sub"), CancellationToken.None).GetAwaiter().GetResult();
            return clientWebSocket;
        }

        private bool InitConnection(DanmakuServer danmakuServer)
        {
            JToken initMsg = new JObject
            {
                ["uid"] = 0,
                ["roomid"] = danmakuServer.RoomId,
                ["protover"] = 2,
                ["platform"] = "web",
                ["clientver"] = "1.12.0",
                ["type"] = 2,
                ["key"] = danmakuServer.Token
            };

            try
            {
                PackWriter.SendMessage((int)BiliPackWriter.MessageType.CONNECT, initMsg.ToString());
                return true;
            }
            catch (SocketException)
            {
                ConnectionFailed?.Invoke("连接请求发送失败");
                return false;
            }
            catch (InvalidOperationException)
            {
                ConnectionFailed?.Invoke("连接请求发送失败");
                return false;
            }
            catch (IOException)
            {
                ConnectionFailed?.Invoke("连接请求发送失败");
                return false;
            }
        }

        #endregion

        #region Room info
        private long GetRealRoomId(long roomId)
        {
            try
            {
                string url = "https://api.live.bilibili.com/room/v1/Room/room_init?id=" + roomId;
                using (WebClient client = new WebClient())
                {
                    string result = client.DownloadString(url);
                    if(result != null)
                    {
                        Match match = Regex.Match(result, "\"room_id\":(?<RoomId>[0-9]+)");
                        if (match.Success)
                            return uint.Parse(match.Groups["RoomId"].Value);
                    }
                    throw new Exception();
                }
            }
            catch
            {
                ConnectionFailed?.Invoke("未能找到直播间");
                return -1;
            }

        }

        private DanmakuServer GetDanmakuServer(long roomId)
        {
            roomId = GetRealRoomId(roomId);
            if (roomId < 0)
            {
                return null;
            }
            try
            {
                using(WebClient client = new WebClient())
                {
                    string url = "https://api.live.bilibili.com/room/v1/Danmu/getConf?room_id=" + roomId;
                    string result = client.DownloadString(url);
                    JObject json = JObject.Parse(result);
                    if (json!=null && json.ContainsKey("code") && int.Parse(json["code"].ToString()) != 0)
                    {
                        return null;
                    }

                    DanmakuServer danmakuServer = new DanmakuServer
                    {
                        RoomId = roomId,
                        Server = json["data"]["host_server_list"][0]["host"].ToString(),
                        Port = int.Parse(json["data"]["host_server_list"][0]["port"].ToString()),
                        WsPort = int.Parse(json["data"]["host_server_list"][0]["ws_port"].ToString()),
                        WssPort = int.Parse(json["data"]["host_server_list"][0]["wss_port"].ToString()),
                        Token = json["data"]["token"].ToString()
                    };

                    return danmakuServer;
                }
            }
            catch (WebException)
            {
                ConnectionFailed?.Invoke("直播间信息获取失败");
                return null;
            }
        }

        #endregion

        #region Heartbeat Sender
        private void StopHeartbeatSender()
        {
            IsHeartbeatSenderRunning = false;
            if (HeartbeatSenderThread != null && HeartbeatSenderThread.IsAlive)
                try { HeartbeatSenderThread.Abort(); } catch { }
        }

        private void StartHeartbeatSender()
        {
            StopHeartbeatSender();
            HeartbeatSenderThread = new Thread(delegate ()
            {
                IsHeartbeatSenderRunning = true;
                while (IsHeartbeatSenderRunning)
                {
                    try
                    {
                        PackWriter.SendMessage((int)BiliPackWriter.MessageType.HEARTBEAT, "[object Object]");
                    }
                    catch (SocketException)
                    {
                        ConnectionFailed?.Invoke("心跳包发送失败");
                        Disconnect();
                    }
                    catch (InvalidOperationException)
                    {
                        ConnectionFailed?.Invoke("心跳包发送失败");
                        Disconnect();
                    }
                    catch (IOException)
                    {
                        ConnectionFailed?.Invoke("心跳包发送失败");
                        Disconnect();
                    }
                    Thread.Sleep(30 * 1000);
                }
            });
            HeartbeatSenderThread.Start();
        }
        #endregion

        #region Event listener
        private void StopEventListener()
        {
            IsRunning = false;
            if (EventListenerThread != null && EventListenerThread.IsAlive)
            {
                try { EventListenerThread.Abort(); } catch { }
            }
        }

        private void StartEventListener()
        {
            EventListenerThread = new Thread(delegate ()
            {
                IsRunning = true;
                while (IsRunning)
                {
                    try
                    {
                        BiliPackReader.IPack[] packs = PackReader.ReadPacksAsync();

                        List<Command> items = new List<Command>();

                        foreach (BiliPackReader.IPack pack in packs)
                        {
                            switch (pack.PackType)
                            {
                                case BiliPackReader.PackTypes.Popularity:
                                    PopularityRecieved?.Invoke(((BiliPackReader.PopularityPack)pack).Popularity);
                                    break;
                                case BiliPackReader.PackTypes.Command:
                                    JToken value = ((BiliPackReader.CommandPack)pack).Value;
                                    Command item = BiliLiveJsonParser.Parse(value);
                                    if (item != null)
                                        items.Add(item);
                                    break;
                                case BiliPackReader.PackTypes.Heartbeat:
                                    ServerHeartbeatRecieved?.Invoke();
                                    OnOnlineUser?.Invoke(OnlineUser.NewInstance(Cookie));
                                    break;
                            }
                        }

                        foreach (var item in items)
                        {
                            OnRaw?.Invoke(item);
                            if (item is Command cmd)
                            {
                                if (ListennerMapping.ContainsKey(item.CommandType))
                                {
                                    var info = ListennerMapping[item.CommandType];
                                    if(info.GetValue(this) is Delegate handler)
                                    {
                                        handler.DynamicInvoke(cmd);
                                    }
                                }
                                else
                                {
                                    OnUnknow?.Invoke(item);
                                }
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        ConnectionFailed?.Invoke("数据读取失败, 正在断开连接...");
                        Disconnect();
                    }
                    catch (IOException)
                    {
                        ConnectionFailed?.Invoke("数据读取失败, 正在断开连接...");
                        Disconnect();
                    }
                }
            });
            EventListenerThread.Start();
        }

        #endregion

        #region Message Sender
        
        public class Result
        {
            public bool Success { get; }

            public string Message { get; }

            public Result(bool success, string message)
            {
                Success = success;
                Message = message;
            }
        }

        public Result Send(string message)
        {
            //HTTP API + COOKIE
            return new Result(false, "error");
        }

        public Result ChangeRoomName(string name)
        {
            //HTTP API + COOKIE
            return new Result(true, "OK");
        }

        

        #endregion
    }
}