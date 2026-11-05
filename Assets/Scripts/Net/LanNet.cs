using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public struct NetState
{
    public byte id;
    public Vector3 pos;
    public Quaternion rot;
    public Vector3 vel;
    public float steer, rpm, throttle, slip;
    public bool boost;
    public byte lap;
    public float dist;
    public float finish;
}

public class HostInfo
{
    public string name;
    public IPEndPoint ep;
    public int players;
    public byte phase;
    public float lastSeen;
}

public struct Packet
{
    public byte[] data;
    public IPEndPoint ep;
}

// Minimal UDP transport for same-network play: broadcast discovery + unreliable state packets.
public class LanNet : IDisposable
{
    public const int GamePort = 47790;
    public const int DiscoveryPort = 47791;
    public const uint Magic = 0x44524956; // "DRIV"
    public const byte MDiscover = 1, MJoin = 2, MWelcome = 3, MLobby = 4, MState = 5, MLeave = 6, MReject = 7;

    UdpClient sock, disc;
    Thread rxThread, discThread;
    volatile bool running;
    public readonly ConcurrentQueue<Packet> Inbox = new ConcurrentQueue<Packet>();
    public readonly ConcurrentQueue<HostInfo> Discovered = new ConcurrentQueue<HostInfo>();
    public bool IsHost { get; private set; }
    public string Error;

    public bool StartHost()
    {
        try
        {
            IsHost = true;
            sock = new UdpClient(AddressFamily.InterNetwork);
            sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Client.Bind(new IPEndPoint(IPAddress.Any, GamePort));
            sock.EnableBroadcast = true;
            StartRx();
            return true;
        }
        catch (Exception e) { Error = e.Message; Debug.LogException(e); return false; }
    }

    public bool StartClient()
    {
        try
        {
            IsHost = false;
            sock = new UdpClient(AddressFamily.InterNetwork);
            sock.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            StartRx();
            return true;
        }
        catch (Exception e) { Error = e.Message; Debug.LogException(e); return false; }
    }

    void StartRx()
    {
        running = true;
        rxThread = new Thread(() =>
        {
            while (running)
            {
                try
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] d = sock.Receive(ref ep);
                    if (d.Length >= 5 && BitConverter.ToUInt32(d, 0) == Magic) Inbox.Enqueue(new Packet { data = d, ep = ep });
                }
                catch (SocketException) { if (!running) break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { if (!running) break; }
            }
        }) { IsBackground = true, Name = "net-rx" };
        rxThread.Start();
    }

    public void StartDiscoveryListen()
    {
        if (disc != null) return;
        try
        {
            disc = new UdpClient();
            disc.ExclusiveAddressUse = false;
            disc.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            disc.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            running = true;
            discThread = new Thread(() =>
            {
                while (running && disc != null)
                {
                    try
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        byte[] d = disc.Receive(ref ep);
                        if (d.Length < 6 || BitConverter.ToUInt32(d, 0) != Magic || d[4] != MDiscover) continue;
                        using (var br = new BinaryReader(new MemoryStream(d, 5, d.Length - 5)))
                        {
                            string name = br.ReadString();
                            int port = br.ReadInt32();
                            int players = br.ReadByte();
                            byte phase = br.ReadByte();
                            Discovered.Enqueue(new HostInfo { name = name, ep = new IPEndPoint(ep.Address, port), players = players, phase = phase });
                        }
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception) { if (!running) break; }
                }
            }) { IsBackground = true, Name = "net-disc" };
            discThread.Start();
        }
        catch (Exception e) { Error = "Discovery: " + e.Message; Debug.LogWarning(e); }
    }

    public void StopDiscoveryListen()
    {
        try { disc?.Close(); } catch { }
        disc = null;
    }

    static List<IPAddress> cachedBroadcasts;
    static float cacheTime = -100f;

    public static List<IPAddress> BroadcastAddresses()
    {
        if (cachedBroadcasts != null && Time.realtimeSinceStartup - cacheTime < 10f) return cachedBroadcasts;
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] mask = null;
                    try { mask = ua.IPv4Mask?.GetAddressBytes(); } catch { }
                    if (mask == null || mask.Length != 4 || mask[0] == 0) mask = new byte[] { 255, 255, 255, 0 };
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    var addr = new IPAddress(b);
                    if (!list.Contains(addr)) list.Add(addr);
                }
            }
        }
        catch (Exception) { }
        list.Add(IPAddress.Loopback);
        cachedBroadcasts = list;
        cacheTime = Time.realtimeSinceStartup;
        return list;
    }

    public static List<string> LocalIPs()
    {
        var res = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        res.Add(ua.Address.ToString());
            }
        }
        catch (Exception) { }
        return res;
    }

    public void SendDiscovery(string hostName, int players, byte phase)
    {
        var w = Begin(MDiscover);
        w.Write(hostName); w.Write(GamePort); w.Write((byte)players); w.Write(phase);
        byte[] d = End(w);
        foreach (var a in BroadcastAddresses())
        {
            try { sock.Send(d, d.Length, new IPEndPoint(a, DiscoveryPort)); } catch { }
        }
    }

    public void Send(byte[] d, IPEndPoint ep)
    {
        if (sock == null || ep == null) return;
        try { sock.Send(d, d.Length, ep); } catch { }
    }

    // ----- serialization helpers -----
    public static BinaryWriter Begin(byte type)
    {
        var w = new BinaryWriter(new MemoryStream(256), Encoding.UTF8);
        w.Write(Magic); w.Write(type);
        return w;
    }

    public static byte[] End(BinaryWriter w)
    {
        w.Flush();
        var ms = (MemoryStream)w.BaseStream;
        var d = ms.ToArray();
        w.Dispose();
        return d;
    }

    public static void WriteState(BinaryWriter w, NetState s)
    {
        w.Write(s.id);
        w.Write(s.pos.x); w.Write(s.pos.y); w.Write(s.pos.z);
        w.Write(s.rot.x); w.Write(s.rot.y); w.Write(s.rot.z); w.Write(s.rot.w);
        w.Write(s.vel.x); w.Write(s.vel.y); w.Write(s.vel.z);
        w.Write((sbyte)Mathf.Clamp(Mathf.RoundToInt(s.steer * 2f), -127, 127));
        w.Write((ushort)Mathf.Clamp(s.rpm, 0, 65535));
        w.Write((byte)(Mathf.Clamp01(s.throttle) * 255));
        w.Write((byte)(Mathf.Clamp01(s.slip) * 255));
        w.Write(s.boost);
        w.Write(s.lap);
        w.Write(s.dist);
        w.Write(s.finish);
    }

    public static NetState ReadState(BinaryReader r)
    {
        var s = new NetState();
        s.id = r.ReadByte();
        s.pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        s.rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        s.vel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        s.steer = r.ReadSByte() / 2f;
        s.rpm = r.ReadUInt16();
        s.throttle = r.ReadByte() / 255f;
        s.slip = r.ReadByte() / 255f;
        s.boost = r.ReadBoolean();
        s.lap = r.ReadByte();
        s.dist = r.ReadSingle();
        s.finish = r.ReadSingle();
        return s;
    }

    public void Dispose()
    {
        running = false;
        try { sock?.Close(); } catch { }
        try { disc?.Close(); } catch { }
        sock = null; disc = null;
    }
}
