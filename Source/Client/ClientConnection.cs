﻿using Harmony;
using Ionic.Zlib;
using Multiplayer.Common;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace Multiplayer.Client
{
    public class ClientJoiningState : MpConnectionState
    {
        public JoiningState state;

        public ClientJoiningState(IConnection connection) : base(connection)
        {
            connection.Send(Packets.Client_Defs, MpVersion.Protocol);

            state = JoiningState.Connected;
            ConnectionStatusListeners.TryNotifyAll_Connected();
        }

        [PacketHandler(Packets.Server_DefsOK)]
        public void HandleDefsOK(ByteReader data)
        {
            Multiplayer.session.gameName = data.ReadString();

            connection.Send(Packets.Client_Username, Multiplayer.username);
            connection.Send(Packets.Client_RequestWorld);

            state = JoiningState.Downloading;
        }

        [PacketHandler(Packets.Server_WorldData)]
        [IsFragmented]
        public void HandleWorldData(ByteReader data)
        {
            connection.State = ConnectionStateEnum.ClientPlaying;
            Log.Message("Game data size: " + data.Length);

            int factionId = data.ReadInt32();
            Multiplayer.session.myFactionId = factionId;

            int tickUntil = data.ReadInt32();

            byte[] worldData = GZipStream.UncompressBuffer(data.ReadPrefixedBytes());
            OnMainThread.cachedGameData = worldData;

            List<int> mapsToLoad = new List<int>();

            int mapCmdsCount = data.ReadInt32();
            for (int i = 0; i < mapCmdsCount; i++)
            {
                int mapId = data.ReadInt32();

                int mapCmdsLen = data.ReadInt32();
                List<ScheduledCommand> mapCmds = new List<ScheduledCommand>(mapCmdsLen);
                for (int j = 0; j < mapCmdsLen; j++)
                    mapCmds.Add(ScheduledCommand.Deserialize(new ByteReader(data.ReadPrefixedBytes())));

                OnMainThread.cachedMapCmds[mapId] = mapCmds;
            }

            int mapDataCount = data.ReadInt32();
            for (int i = 0; i < mapDataCount; i++)
            {
                int mapId = data.ReadInt32();
                byte[] rawMapData = data.ReadPrefixedBytes();

                byte[] mapData = GZipStream.UncompressBuffer(rawMapData);
                OnMainThread.cachedMapData[mapId] = mapData;
                mapsToLoad.Add(mapId);
            }

            TickPatch.tickUntil = tickUntil;

            TickPatch.skipToTickUntil = true;
            TickPatch.afterSkip = () => Multiplayer.Client.Send(Packets.Client_WorldReady);

            ReloadGame(mapsToLoad);
        }

        private static XmlDocument GetGameDocument(List<int> mapsToLoad)
        {
            XmlDocument gameDoc = ScribeUtil.LoadDocument(OnMainThread.cachedGameData);
            XmlNode gameNode = gameDoc.DocumentElement["game"];

            foreach (int map in mapsToLoad)
            {
                using (XmlReader reader = XmlReader.Create(new MemoryStream(OnMainThread.cachedMapData[map])))
                {
                    XmlNode mapNode = gameDoc.ReadNode(reader);
                    gameNode["maps"].AppendChild(mapNode);

                    if (gameNode["currentMapIndex"] == null)
                        gameNode.AddNode("currentMapIndex", map.ToString());
                }
            }

            return gameDoc;
        }

        public static void ReloadGame(List<int> mapsToLoad, bool async = true)
        {
            LoadPatch.gameToLoad = GetGameDocument(mapsToLoad);
            TickPatch.replayTimeSpeed = TimeSpeed.Paused;

            if (async)
            {
                LongEventHandler.QueueLongEvent(() =>
                {
                    MemoryUtility.ClearAllMapsAndWorld();
                    Current.Game = new Game();
                    Current.Game.InitData = new GameInitData();
                    Current.Game.InitData.gameToLoad = "server";

                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        LongEventHandler.QueueLongEvent(() => PostLoad(), "MpSimulating", false, null);
                    });
                }, "Play", "MpLoading", true, null);
            }
            else
            {
                LongEventHandler.QueueLongEvent(() =>
                {
                    SaveLoad.LoadInMainThread(LoadPatch.gameToLoad);
                    PostLoad();
                }, "MpLoading", false, null);
            }
        }

        private static void PostLoad()
        {
            // If the client gets disconnected during loading
            if (Multiplayer.Client == null) return;

            OnMainThread.cachedAtTime = TickPatch.Timer;
            Multiplayer.session.replayTimerStart = TickPatch.Timer;

            var factionData = Multiplayer.WorldComp.factionData.GetValueSafe(Multiplayer.session.myFactionId);
            if (factionData != null && factionData.online)
                Multiplayer.RealPlayerFaction = Find.FactionManager.GetById(factionData.factionId);
            else
                Multiplayer.RealPlayerFaction = Multiplayer.DummyFaction;

            // todo find a better way
            Multiplayer.game.myFactionLoading = null;

            Multiplayer.WorldComp.cmds = new Queue<ScheduledCommand>(OnMainThread.cachedMapCmds.GetValueSafe(ScheduledCommand.Global) ?? new List<ScheduledCommand>());
            // Map cmds are added in MapAsyncTimeComp.FinalizeInit
        }

        [PacketHandler(Packets.Server_DisconnectReason)]
        public void HandleDisconnectReason(ByteReader data)
        {
            string reasonKey = data.ReadString();
            Multiplayer.session.disconnectServerReason = reasonKey.Translate();
        }
    }

    public enum JoiningState
    {
        Connected, Downloading
    }

    public class ClientPlayingState : MpConnectionState, IConnectionStatusListener
    {
        public ClientPlayingState(IConnection connection) : base(connection)
        {
        }

        [PacketHandler(Packets.Server_TimeControl)]
        public void HandleTimeControl(ByteReader data)
        {
            int tickUntil = data.ReadInt32();
            TickPatch.tickUntil = tickUntil;
        }

        [PacketHandler(Packets.Server_KeepAlive)]
        public void HandleKeepAlive(ByteReader data)
        {
            int id = data.ReadInt32();
            connection.Send(Packets.Client_KeepAlive, id);
        }

        [PacketHandler(Packets.Server_Command)]
        public void HandleCommand(ByteReader data)
        {
            ScheduledCommand cmd = ScheduledCommand.Deserialize(data);
            cmd.issuedBySelf = data.ReadBool();
            OnMainThread.ScheduleCommand(cmd);
        }

        [PacketHandler(Packets.Server_PlayerList)]
        public void HandlePlayerList(ByteReader data)
        {
            var action = (PlayerListAction)data.ReadByte();

            if (action == PlayerListAction.Add)
            {
                var info = PlayerInfo.Read(data);
                if (!Multiplayer.session.players.Contains(info))
                    Multiplayer.session.players.Add(info);
            }
            else if (action == PlayerListAction.Remove)
            {
                int id = data.ReadInt32();
                Multiplayer.session.players.RemoveAll(p => p.id == id);
            }
            else if (action == PlayerListAction.List)
            {
                int count = data.ReadInt32();

                Multiplayer.session.players.Clear();
                for (int i = 0; i < count; i++)
                    Multiplayer.session.players.Add(PlayerInfo.Read(data));
            }
            else if (action == PlayerListAction.Latencies)
            {
                int[] latencies = data.ReadPrefixedInts();

                for (int i = 0; i < Multiplayer.session.players.Count; i++)
                    Multiplayer.session.players[i].latency = latencies[i];
            }
            else if (action == PlayerListAction.Status)
            {
                var id = data.ReadInt32();
                var status = (PlayerStatus)data.ReadByte();
                var player = Multiplayer.session.GetPlayerInfo(id);

                if (player != null)
                    player.status = status;
            }
        }

        [PacketHandler(Packets.Server_Chat)]
        public void HandleChat(ByteReader data)
        {
            string msg = data.ReadString();
            Multiplayer.session.AddMsg(msg);
        }

        [PacketHandler(Packets.Server_Cursor)]
        public void HandleCursor(ByteReader data)
        {
            int playerId = data.ReadInt32();
            var player = Multiplayer.session.players.Find(p => p.id == playerId);
            if (player == null) return;

            byte seq = data.ReadByte();
            if (seq < player.cursorSeq && player.cursorSeq - seq < 128) return;

            byte map = data.ReadByte();
            player.map = map;

            if (map == byte.MaxValue) return;

            byte icon = data.ReadByte();
            float x = data.ReadShort() / 10f;
            float z = data.ReadShort() / 10f;

            player.cursorSeq = seq;
            player.lastCursor = player.cursor;
            player.lastDelta = Multiplayer.Clock.ElapsedMillisDouble() - player.updatedAt;
            player.cursor = new Vector3(x, 0, z);
            player.updatedAt = Multiplayer.Clock.ElapsedMillisDouble();
            player.cursorIcon = icon;
        }

        [PacketHandler(Packets.Server_MapResponse)]
        public void HandleMapResponse(ByteReader data)
        {
            int mapId = data.ReadInt32();

            int mapCmdsLen = data.ReadInt32();
            List<ScheduledCommand> mapCmds = new List<ScheduledCommand>(mapCmdsLen);
            for (int j = 0; j < mapCmdsLen; j++)
                mapCmds.Add(ScheduledCommand.Deserialize(new ByteReader(data.ReadPrefixedBytes())));

            OnMainThread.cachedMapCmds[mapId] = mapCmds;

            byte[] mapData = GZipStream.UncompressBuffer(data.ReadPrefixedBytes());
            OnMainThread.cachedMapData[mapId] = mapData;

            //ClientJoiningState.ReloadGame(TickPatch.tickUntil, Find.Maps.Select(m => m.uniqueID).Concat(mapId).ToList());
            // todo Multiplayer.client.Send(Packets.CLIENT_MAP_LOADED);
        }

        [PacketHandler(Packets.Server_Notification)]
        public void HandleNotification(ByteReader data)
        {
            string key = data.ReadString();
            string[] args = data.ReadPrefixedStrings();

            Messages.Message(key.Translate(Array.ConvertAll(args, s => (NamedArgument)s)), MessageTypeDefOf.SilentInput, false);
        }

        [PacketHandler(Packets.Server_DisconnectReason)]
        public void HandleDisconnectReason(ByteReader data)
        {
            string reasonKey = data.ReadString();
            Multiplayer.session.disconnectServerReason = reasonKey.Translate();
        }

        [PacketHandler(Packets.Server_SyncInfo)]
        [IsFragmented]
        public void HandleDesyncCheck(ByteReader data)
        {
            Multiplayer.game?.sync.Add(SyncInfo.Deserialize(data));
        }

        [PacketHandler(Packets.Server_Pause)]
        public void HandlePause(ByteReader data)
        {
            bool pause = data.ReadBool();
            // This packet doesn't get processed in time during a synchronous long event 
        }

        [PacketHandler(Packets.Server_Debug)]
        public void HandleDebug(ByteReader data)
        {
            int tick = data.ReadInt32();
            var info = Multiplayer.game.sync.buffer.FirstOrDefault(b => b.startTick == tick);

            File.WriteAllText("arbiter_traces.txt", info?.TracesToString() ?? "null");
        }

        public void Connected()
        {
        }

        public void Disconnected()
        {
            Find.WindowStack.windows.Clear();
            Find.WindowStack.Add(new DisconnectedWindow(Multiplayer.session.disconnectServerReason ?? Multiplayer.session.disconnectNetReason));
        }
    }

    public class ClientSteamState : MpConnectionState
    {
        public ClientSteamState(IConnection connection) : base(connection)
        {
            //connection.Send(Packets.Client_SteamRequest);
        }

        [PacketHandler(Packets.Server_SteamAccept)]
        public void HandleSteamAccept(ByteReader data)
        {
            connection.State = ConnectionStateEnum.ClientJoining;
        }

        [PacketHandler(Packets.Server_DisconnectReason)]
        public void HandleDisconnectReason(ByteReader data)
        {
            string reasonKey = data.ReadString();
            Multiplayer.session.disconnectServerReason = reasonKey.Translate();
        }
    }

    public interface IConnectionStatusListener
    {
        void Connected();
        void Disconnected();
    }

    public static class ConnectionStatusListeners
    {
        private static IEnumerable<IConnectionStatusListener> All
        {
            get
            {
                if (Find.WindowStack != null)
                    foreach (Window window in Find.WindowStack.Windows.ToList())
                        if (window is IConnectionStatusListener listener)
                            yield return listener;

                if (Multiplayer.Client?.StateObj is IConnectionStatusListener state)
                    yield return state;
            }
        }

        public static void TryNotifyAll_Connected()
        {
            foreach (var listener in All)
            {
                try
                {
                    listener.Connected();
                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }
            }
        }

        public static void TryNotifyAll_Disconnected()
        {
            foreach (var listener in All)
            {
                try
                {
                    listener.Disconnected();
                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }
            }
        }
    }

}
