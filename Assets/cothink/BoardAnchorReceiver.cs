// BoardAnchorReceiver.cs  (v4: + state event)
using System;
using UnityEngine;

namespace CoThink
{
    [Serializable]
    internal class BoardPoseReply
    {
        public string type; public int frameId; public bool ok;
        public float px, py, pz, qx, qy, qz, qw;
    }

    public class BoardAnchorReceiver : MonoBehaviour
    {
        [SerializeField] private FrameSenderToPC m_sender;
        [SerializeField] private bool m_continuousUpdate = false;
        [SerializeField] private bool m_recalibrate = false;

        private Transform m_boardRoot;
        private bool m_anchored;

        public event Action<string> OnDetectionsJson;
        public event Action<string> OnSolutionJson;
        public event Action<string> OnStateJson;
        public Transform BoardRoot => m_boardRoot;
        public bool IsAnchored => m_anchored;

        private void Update()
        {
            if (m_sender == null) return;
            while (m_sender.TryDequeueReplyJson(out var json))
            {
                if (json.Contains("\"solution\""))   { OnSolutionJson?.Invoke(json);   continue; }
                if (json.Contains("\"state\""))      { OnStateJson?.Invoke(json);      continue; }
                if (json.Contains("\"detections\"")) { OnDetectionsJson?.Invoke(json); continue; }
                BoardPoseReply r;
                try { r = JsonUtility.FromJson<BoardPoseReply>(json); }
                catch { continue; }
                if (r == null || r.type != "board_pose" || !r.ok) continue;
                HandleReply(r);
            }
        }

        private void HandleReply(BoardPoseReply r)
        {
            if (m_recalibrate) { m_anchored = false; m_recalibrate = false; }
            if (m_anchored && !m_continuousUpdate) return;
            if (!m_sender.TryGetCameraPose(r.frameId, out var camPose)) return;

            Vector3 posCv = new Vector3(r.px, r.py, r.pz);
            Quaternion rotCv = new Quaternion(r.qx, r.qy, r.qz, r.qw);
            Vector3 posCam = new Vector3(posCv.x, -posCv.y, posCv.z);
            Quaternion rotCam = new Quaternion(-rotCv.x, rotCv.y, -rotCv.z, rotCv.w);
            Vector3 worldPos = camPose.position + camPose.rotation * posCam;
            Quaternion worldRot = camPose.rotation * rotCam;

            PlaceFrame(worldPos, worldRot);
            m_anchored = true;
            Debug.Log($"BoardAnchor: pinned at {worldPos:F3} (frame {r.frameId})");
        }

        private void PlaceFrame(Vector3 pos, Quaternion rot)
        {
            if (m_boardRoot == null) m_boardRoot = BuildAxisGizmo();
            m_boardRoot.SetPositionAndRotation(pos, rot);
            m_boardRoot.gameObject.SetActive(true);
        }

        private Transform BuildAxisGizmo()
        {
            var root = new GameObject("BoardOrigin").transform;
            const float len = 0.05f, thick = 0.005f;
            AddRod(root, new Vector3(len*0.5f,0,0), new Vector3(len,thick,thick), Color.red);
            AddRod(root, new Vector3(0,len*0.5f,0), new Vector3(thick,len,thick), Color.green);
            AddRod(root, new Vector3(0,0,len*0.5f), new Vector3(thick,thick,len), Color.blue);
            var o = GameObject.CreatePrimitive(PrimitiveType.Cube);
            o.transform.SetParent(root, false); o.transform.localScale = Vector3.one*0.012f;
            StripCollider(o); Paint(o, Color.white);
            return root;
        }

        private void AddRod(Transform p, Vector3 lp, Vector3 ls, Color c)
        {
            var rod = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rod.transform.SetParent(p, false); rod.transform.localPosition = lp; rod.transform.localScale = ls;
            StripCollider(rod); Paint(rod, c);
        }

        private static void StripCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);
        }

        public static void Paint(GameObject go, Color color)
        {
            var rend = go.GetComponent<Renderer>(); if (rend == null) return;
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var mat = new Material(sh); mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            rend.material = mat;
        }
    }
}