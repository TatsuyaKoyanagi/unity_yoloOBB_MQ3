// FrameSenderToPC.cs  (v2: + frameId pose-history + reply channel on the same socket)
//
// Quest 3 -> PC frame + camera-pose streamer, AND PC -> Quest reply reader, all on
// one TCP connection (port 5006, bidirectional).
//
// Attach to a GameObject in the CameraViewer scene; assign the SAME
// PassthroughCameraAccess that CameraViewerManager uses. With USB + adb reverse,
// keep Pc Host = 127.0.0.1.
//
// Outbound per frame:  [4 BE hdrLen][hdr JSON][4 BE imgLen][JPEG]
//   header adds intrinsics (intr) and pose, as before.
// Inbound replies:     [4 BE len][JSON]   (e.g. {"type":"board_pose",...})
//   raw JSON strings are queued for the main thread (BoardAnchorReceiver parses them).
//
// This component also keeps a frameId -> camera-world-pose history so the anchor
// math can use the pose from the EXACT frame the PC processed.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Meta.XR;
using UnityEngine;

namespace CoThink
{
    public class FrameSenderToPC : MonoBehaviour
    {
        [Header("Camera (assign the same one CameraViewer uses)")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Network")]
        [Tooltip("USB+adb reverse: keep 127.0.0.1. Wi-Fi: your PC's LAN IP.")]
        [SerializeField] private string m_pcHost = "127.0.0.1";
        [SerializeField] private int m_pcPort = 5006;

        [Header("Streaming")]
        [SerializeField] private float m_sendInterval = 0.066f;   // ~15 fps
        [Range(1, 100)] [SerializeField] private int m_jpegQuality = 70;
        [Tooltip("Downscale longer side to this many px (0 = native).")]
        [SerializeField] private int m_maxSize = 640;

        private Texture2D m_readTex;
        private RenderTexture m_rt;

        private readonly object m_lock = new object();
        private byte[] m_latestPayload;
        private bool m_hasNew;

        // frameId -> camera world pose (written + read on the MAIN thread only)
        private struct PoseEntry { public int frameId; public Pose pose; public bool valid; }
        private const int HISTORY = 256;
        private readonly PoseEntry[] m_history = new PoseEntry[HISTORY];

        // PC -> Quest replies as raw JSON (reader thread -> main thread)
        private readonly ConcurrentQueue<string> m_replyJson = new ConcurrentQueue<string>();

        private Thread m_sendThread;
        private volatile bool m_running;
        private int m_frameId;

        // ---- public API used by BoardAnchorReceiver (main thread) ----
        public bool TryGetCameraPose(int frameId, out Pose pose)
        {
            int idx = ((frameId % HISTORY) + HISTORY) % HISTORY;
            var e = m_history[idx];
            if (e.valid && e.frameId == frameId) { pose = e.pose; return true; }
            pose = default;
            return false;
        }

        public bool TryDequeueReplyJson(out string json) => m_replyJson.TryDequeue(out json);

        private IEnumerator Start()
        {
            if (m_cameraAccess == null)
            {
                Debug.LogError("FrameSenderToPC: m_cameraAccess is not assigned.");
                enabled = false;
                yield break;
            }
            while (!m_cameraAccess.IsPlaying)
                yield return null;

            m_running = true;
            m_sendThread = new Thread(SenderLoop) { IsBackground = true };
            m_sendThread.Start();
            StartCoroutine(CaptureLoop());
        }

        private IEnumerator CaptureLoop()
        {
            var wait = new WaitForSeconds(m_sendInterval);
            while (m_running)
            {
                yield return new WaitForEndOfFrame();
                if (m_cameraAccess.IsPlaying)
                    CaptureAndQueue();
                yield return wait;
            }
        }

        private void CaptureAndQueue()
        {
            var src = m_cameraAccess.GetTexture();
            if (src == null || src.width <= 0 || src.height <= 0)
                return;

            int sw = src.width, sh = src.height;
            int dw = sw, dh = sh;
            if (m_maxSize > 0)
            {
                float s = Mathf.Min(1f, (float)m_maxSize / Mathf.Max(sw, sh));
                dw = Mathf.Max(1, Mathf.RoundToInt(sw * s));
                dh = Mathf.Max(1, Mathf.RoundToInt(sh * s));
            }

            if (m_rt == null || m_rt.width != dw || m_rt.height != dh)
            {
                if (m_rt != null) m_rt.Release();
                m_rt = new RenderTexture(dw, dh, 0, RenderTextureFormat.ARGB32);
                m_rt.Create();
            }
            if (m_readTex == null || m_readTex.width != dw || m_readTex.height != dh)
                m_readTex = new Texture2D(dw, dh, TextureFormat.RGB24, false);

            var prev = RenderTexture.active;
            Graphics.Blit(src, m_rt);
            RenderTexture.active = m_rt;
            m_readTex.ReadPixels(new Rect(0, 0, dw, dh), 0, 0, false);
            m_readTex.Apply(false);
            RenderTexture.active = prev;

            byte[] jpg = m_readTex.EncodeToJPG(m_jpegQuality);

            var pose = m_cameraAccess.GetCameraPose();
            Vector3 p = pose.position;
            Quaternion q = pose.rotation;

            var intr = m_cameraAccess.Intrinsics;
            float fx = intr.FocalLength.x, fy = intr.FocalLength.y;
            float cx = intr.PrincipalPoint.x, cy = intr.PrincipalPoint.y;
            var curRes = m_cameraAccess.CurrentResolution;
            int rw = curRes.x, rh = curRes.y;

            int id = m_frameId++;
            long tMs = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);

            // store camera world pose for this frameId (main thread)
            m_history[id % HISTORY] = new PoseEntry { frameId = id, pose = new Pose(p, q), valid = true };

            string header =
                "{\"frameId\":" + id +
                ",\"tMs\":" + tMs +
                ",\"w\":" + dw + ",\"h\":" + dh +
                ",\"native\":{\"w\":" + sw + ",\"h\":" + sh + "}" +
                ",\"intr\":{\"fx\":" + F(fx) + ",\"fy\":" + F(fy) +
                ",\"cx\":" + F(cx) + ",\"cy\":" + F(cy) +
                ",\"rw\":" + rw + ",\"rh\":" + rh + "}" +
                ",\"pose\":{\"px\":" + F(p.x) + ",\"py\":" + F(p.y) + ",\"pz\":" + F(p.z) +
                ",\"qx\":" + F(q.x) + ",\"qy\":" + F(q.y) + ",\"qz\":" + F(q.z) + ",\"qw\":" + F(q.w) + "}}";

            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            byte[] msg = new byte[4 + headerBytes.Length + 4 + jpg.Length];
            int o = 0;
            WriteBE(msg, ref o, headerBytes.Length);
            Buffer.BlockCopy(headerBytes, 0, msg, o, headerBytes.Length); o += headerBytes.Length;
            WriteBE(msg, ref o, jpg.Length);
            Buffer.BlockCopy(jpg, 0, msg, o, jpg.Length);

            lock (m_lock)
            {
                m_latestPayload = msg;
                m_hasNew = true;
            }
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static void WriteBE(byte[] buf, ref int o, int value)
        {
            buf[o++] = (byte)((value >> 24) & 0xFF);
            buf[o++] = (byte)((value >> 16) & 0xFF);
            buf[o++] = (byte)((value >> 8) & 0xFF);
            buf[o++] = (byte)(value & 0xFF);
        }

        private void SenderLoop()
        {
            while (m_running)
            {
                TcpClient client = null;
                NetworkStream stream = null;
                Thread reader = null;
                try
                {
                    client = new TcpClient();
                    client.Connect(m_pcHost, m_pcPort);
                    client.NoDelay = true;
                    stream = client.GetStream();
                    Debug.Log("FrameSenderToPC: connected to " + m_pcHost + ":" + m_pcPort);

                    var localStream = stream;
                    reader = new Thread(() => ReaderLoop(localStream)) { IsBackground = true };
                    reader.Start();

                    while (m_running)
                    {
                        byte[] payload = null;
                        lock (m_lock)
                        {
                            if (m_hasNew) { payload = m_latestPayload; m_hasNew = false; }
                        }
                        if (payload != null) stream.Write(payload, 0, payload.Length);
                        else Thread.Sleep(5);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("FrameSenderToPC: " + e.Message + " (retry in 1s)");
                    Thread.Sleep(1000);
                }
                finally
                {
                    try { if (stream != null) stream.Close(); } catch { }
                    try { if (client != null) client.Close(); } catch { }
                    try { if (reader != null) reader.Join(200); } catch { }
                }
            }
        }

        private void ReaderLoop(NetworkStream stream)
        {
            byte[] lenBuf = new byte[4];
            try
            {
                while (m_running)
                {
                    if (!ReadExact(stream, lenBuf, 4)) break;
                    int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                    if (len <= 0 || len > 1000000) break;
                    byte[] buf = new byte[len];
                    if (!ReadExact(stream, buf, len)) break;
                    m_replyJson.Enqueue(Encoding.UTF8.GetString(buf));
                }
            }
            catch { /* connection closed; SenderLoop will reconnect */ }
        }

        private static bool ReadExact(NetworkStream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) return false;
                off += r;
            }
            return true;
        }

        private void OnDestroy()
        {
            m_running = false;
            try { if (m_sendThread != null) m_sendThread.Join(200); } catch { }
            if (m_rt != null) { m_rt.Release(); m_rt = null; }
        }
    }
}