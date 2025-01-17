﻿using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

using HarmonyLib;

using Steamworks;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Compress {
  [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
  public class Compress : BaseUnityPlugin {
    public const string PluginGUID = "redseiko.valheim.compress";
    public const string PluginName = "Compress";
    public const string PluginVersion = "1.4.0";

    static ManualLogSource _logger;
    static ConfigEntry<bool> _isModEnabled;

    Harmony _harmony;

    public void Awake() {
      _logger = Logger;

      _isModEnabled =
          Config.Bind("_Global", "isModEnabled", true, "Globally enable or disable this mod (restart required).");

      if (_isModEnabled.Value) {
        _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGUID);
      }
    }

    public void OnDestroy() {
      _harmony?.UnpatchSelf();
    }

    class CompressConfig {
      public bool CompressZdoData { get; set; }
    }

    static readonly ConcurrentDictionary<ZRpc, CompressConfig> _rpcCompressConfigCache = new();
    static readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    static void RPC_CompressHandshake(ZRpc rpc, bool isEnabled) {
      LogInfo($"Received CompressHandshake from: {rpc.m_socket.GetHostName()}, isEnabled: {isEnabled}");

      CompressConfig config = _rpcCompressConfigCache.GetOrAdd(rpc, key => new());
      config.CompressZdoData = isEnabled;

      if (ZNet.m_isServer) {
        rpc.Invoke("CompressHandshake", true);
      }
    }

    [HarmonyPatch(typeof(ZDOMan))]
    class ZDOManPatch {
      [HarmonyPostfix]
      [HarmonyPatch(nameof(ZDOMan.AddPeer))]
      static void AddPeerPostfix(ref ZDOMan __instance, ref ZNetPeer netPeer) {
        netPeer.m_rpc.Register("CompressedZDOData", new Action<ZRpc, ZPackage>(RPC_CompressedZDOData));
        netPeer.m_rpc.Register("CompressHandshake", new Action<ZRpc, bool>(RPC_CompressHandshake));

        if (ZNet.m_isServer) {
          return;
        }

        LogInfo($"Sending CompressHandshake to server...");
        netPeer.m_rpc.Invoke("CompressHandshake", true);
      }

      [HarmonyTranspiler]
      [HarmonyPatch(nameof(ZDOMan.SendZDOs))]
      static IEnumerable<CodeInstruction> SendZDOsTranspiler(IEnumerable<CodeInstruction> instructions) {
        return new CodeMatcher(instructions)
          .MatchForward(useEnd: false, new CodeMatch(OpCodes.Callvirt, typeof(ZRpc).GetMethod(nameof(ZRpc.Invoke))))
          .SetAndAdvance(
              OpCodes.Call,
              Transpilers.EmitDelegate<Action<ZRpc, string, object[]>>(SendZDOsInvokeDelegate).operand)
          .InstructionEnumeration();
      }

      [HarmonyPostfix]
      [HarmonyPatch(nameof(ZDOMan.Update))]
      static void UpdatePostfix() {
        if (_stopwatch.ElapsedMilliseconds < 60000) {
          return;
        }

        LogCompressStats();
        _stopwatch.Restart();
      }
    }

    static long _compressedBytesSent;
    static long _uncompressedBytesSent;
    static long _compressedBytesRecv;
    static long _uncompressedBytesRecv;

    static readonly MemoryStream _compressStream = new();
    static readonly MemoryStream _decompressStream = new();

    static readonly int _zdoDataHashCode = "ZDOData".GetStableHashCode();
    static readonly int _compressedZdoDataHashCode = "CompressedZDOData".GetStableHashCode();

    static void SendZDOsInvokeDelegate(ZRpc rpc, string method, params object[] parameters) {
      ZPackage package = (ZPackage) parameters[0];
      package.m_writer.Flush();

      if (_rpcCompressConfigCache.TryGetValue(rpc, out CompressConfig config) && config.CompressZdoData) {
        int uncompressedLength = (int) package.m_stream.Length;
        _uncompressedBytesSent += uncompressedLength;

        _compressStream.SetLength(0);

        using (GZipStream gzipStream = new(_compressStream, CompressionLevel.Fastest, leaveOpen: true)) {
          gzipStream.Write(package.m_stream.GetBuffer(), 0, uncompressedLength);
        }

        int compressedLength = (int) _compressStream.Length;
        _compressedBytesSent += compressedLength;

        if (!rpc.IsConnected() || compressedLength == 0) {
          return;
        }

        rpc.m_pkg.Clear();
        rpc.m_pkg.Write(_compressedZdoDataHashCode);
        rpc.m_pkg.m_writer.Write(compressedLength);
        rpc.m_pkg.m_writer.Write(_compressStream.GetBuffer(), 0, compressedLength);

        _compressStream.SetLength(0);

        rpc.SendPackage(rpc.m_pkg);
      } else {
        rpc.m_pkg.Clear();
        rpc.m_pkg.Write(_zdoDataHashCode);

        int packageLength = (int) package.m_stream.Length;
        rpc.m_pkg.m_writer.Write(packageLength);
        rpc.m_pkg.m_writer.Write(package.m_stream.GetBuffer(), 0, packageLength);

        rpc.SendPackage(rpc.m_pkg);
      }
    }

    class AsyncSocket : IDisposable {
      readonly ZSteamSocket _socket;
      readonly BlockingCollection<byte[]> _sendQueue;
      readonly CancellationTokenSource _cancellationTokenSource;

      public AsyncSocket(ZSteamSocket socket) {
        _socket = socket;
        _sendQueue = new(new ConcurrentQueue<byte[]>());
        _cancellationTokenSource = new();

        new Thread(() => SendLoop(_cancellationTokenSource.Token)).Start();
      }

      public void QueuePackage(byte[] data) {
        _sendQueue.Add(data);
      }

      void SendLoop(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
          try {
            byte[] data = _sendQueue.Take();

            while (!cancellationToken.IsCancellationRequested && !SendPackage(data)) {
              Thread.Sleep(millisecondsTimeout: 50);
            }
          } catch (OperationCanceledException) {
            break;
          } catch (Exception exception) {
            LogError($"{exception}");
          }
        }
      }

      bool SendPackage(byte[] data) {
        if (!_socket.IsConnected()) {
          return false;
        }

        int dataLength = data.Length;

        IntPtr intPtr = Marshal.AllocHGlobal(dataLength);
        Marshal.Copy(data, 0, intPtr, dataLength);

        EResult result =
            ZNet.m_isServer
                ? SteamGameServerNetworkingSockets.SendMessageToConnection(
                      _socket.m_con, intPtr, (uint) dataLength, 8, out _)
                : SteamNetworkingSockets.SendMessageToConnection(
                      _socket.m_con, intPtr, (uint) dataLength, 8, out _);

        Marshal.FreeHGlobal(intPtr);

        if (result != EResult.k_EResultOK) {
          return false;
        }

        _socket.m_totalSent += dataLength;
        return true;
      }

      bool _isDisposed;

      protected virtual void Dispose(bool disposing) {
        if (_isDisposed) {
          return;
        }

        if (disposing) {
          _cancellationTokenSource.Cancel();
          _cancellationTokenSource.Dispose();
          _sendQueue.Dispose();
        }

        _isDisposed = true;
      }

      public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
      }
    }

    static readonly ConcurrentDictionary<HSteamNetConnection, AsyncSocket> _asyncSocketByConnection = new();

    [HarmonyPatch(typeof(ZNet))]
    class ZNetPatch {
      [HarmonyPrefix]
      [HarmonyPatch(nameof(ZNet.OnNewConnection))]
      static void OnNewConnectionPrefix(ref ZNet __instance, ref ZNetPeer peer) {
        ZSteamSocket socket = (ZSteamSocket) peer.m_socket;

        if (_asyncSocketByConnection.TryAdd(socket.m_con, new AsyncSocket(socket))) {
          LogInfo($"Wrapping socket for peer {peer.m_socket.GetHostName()} into AsyncSocket...");
        }
      }
    }

    [HarmonyPatch(typeof(ZSteamSocket))]
    class ZSteamSocketPatch {
      [HarmonyPrefix]
      [HarmonyPatch(nameof(ZSteamSocket.Send))]
      static bool SendPrefix(ref ZSteamSocket __instance, ref ZPackage pkg) {
        if (pkg.Size() <= 0 || !__instance.IsConnected()) {
          return false;
        }

        if (_asyncSocketByConnection.TryGetValue(__instance.m_con, out AsyncSocket socket)) {
          socket.QueuePackage(pkg.GetArray());
          return false;
        }

        return true;
      }

      [HarmonyPrefix]
      [HarmonyPatch(nameof(ZSteamSocket.SendQueuedPackages))]
      static bool SendQueuedPackagesPrefix(ref ZSteamSocket __instance) {
        if (_asyncSocketByConnection.ContainsKey(__instance.m_con)) {
          return false;
        }

        return true;
      }

      [HarmonyPrefix]
      [HarmonyPatch(nameof(ZSteamSocket.Close))]
      static void ClosePrefix(ref ZSteamSocket __instance) {
        if (_asyncSocketByConnection.TryRemove(__instance.m_con, out AsyncSocket socket)) {
          socket.Dispose();
        }
      }
    }

    [HarmonyPatch(typeof(ZPackage))]
    class ZPackagePatch {
      [HarmonyPrefix]
      [HarmonyPatch(nameof(ZPackage.Write), typeof(ZPackage))]
      static bool WritePrefix_ZPackage(ref ZPackage __instance, ref ZPackage pkg) {
        pkg.m_writer.Flush();
        pkg.m_stream.Flush();

        int packageLength = (int) pkg.m_stream.Length;
        __instance.m_writer.Write(packageLength);
        __instance.m_writer.Write(pkg.m_stream.GetBuffer(), 0, packageLength);

        return false;
      }
    }

    static void RPC_CompressedZDOData(ZRpc rpc, ZPackage package) {
      int compressedLength = (int) package.m_stream.Length;
      _compressedBytesRecv += compressedLength;

      _decompressStream.SetLength(0);

      using (GZipStream gzipStream = new(package.m_stream, CompressionMode.Decompress, leaveOpen: true)) {
        gzipStream.CopyTo(_decompressStream);
      }

      int uncompressedLength = (int) _decompressStream.Length;
      _uncompressedBytesRecv += uncompressedLength;

      package.Clear();
      package.m_stream.Write(_decompressStream.GetBuffer(), 0, uncompressedLength);
      package.m_stream.Position = 0;

      _decompressStream.SetLength(0);
      ZDOMan.m_instance.RPC_ZDOData(rpc, package);
    }

    static void LogCompressStats() {
      LogInfo(
          string.Format(
              "Totals:\n  Sent C/U: {0:N} KB / {1:N} KB ({2:P})\n  Recv C/U: {3:N} KB / {4:N} KB ({5:P})",
              _compressedBytesSent / 1024d,
              _uncompressedBytesSent / 1024d,
              (double) _compressedBytesSent / _uncompressedBytesSent,
              _compressedBytesRecv / 1024d,
              _uncompressedBytesRecv / 1024d,
              (double) _compressedBytesRecv / _uncompressedBytesRecv));
    }

    static void LogInfo(string message) {
      _logger.LogInfo($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
    }

    static void LogError(string message) {
      _logger.LogError($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
    }
  }
}