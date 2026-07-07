// BoardAnchorReceiver.cs  (v4: 平滑化つき連続トラッキング + solution/detections event)
// 盤面姿勢を毎フレーム追従し、Lerp/Slerpで平滑化（追従するが揺れない）。
// 子のシルエット/マーカーは自動で一緒に動く。4隅が隠れたら最後の姿勢を保持。
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
        [Tooltip("毎フレーム盤面に追従(true) / 初回ロック後は固定(false)")]
        [SerializeField] private bool m_trackContinuously = true;
        [Tooltip("大きいほどキビキビ / 小さいほど滑らか。8〜15目安")]
        [SerializeField] private float m_smooth = 10f;
        [Tooltip("実行中にチェックすると次の良好姿勢で再ロック")]
        [SerializeField] private bool m_recalibrate = false;

        [Header("カメラ→目オフセット補正")]
        [Tooltip("パススルーカメラ光学中心から目(描画基準)へのオフセット[m]。" +
                 "カメラ座標系(X右,Y上,Z前)で指定。Quest3の額カメラは目より上・前にあるため、" +
                 "アンカーが上にずれる場合は y をマイナス方向へ(例 -0.02)。" +
                 "この量はカメラ姿勢の回転で回してからワールド位置に加算される。")]
        [SerializeField] private Vector3 m_camToEyeOffset = Vector3.zero;

        private Transform m_boardRoot;
        private bool m_hasTarget, m_locked;
        private Vector3 m_targetPos;
        private Quaternion m_targetRot;

        public event Action<string> OnDetectionsJson;
        public event Action<string> OnSolutionJson;
        public event Action<string> OnStateJson;
        public Transform BoardRoot => m_boardRoot;
        public bool IsAnchored => m_locked;

        // ---- 実行時セッター（MR内設定パネル用）----
        public Vector3 CamToEyeOffset
        {
            get => m_camToEyeOffset;
            set => m_camToEyeOffset = value;
        }

        private void Update()
        {
            if (m_sender != null)
            {
                while (m_sender.TryDequeueReplyJson(out var json))
                {
                    if (json.Contains("\"state\""))      { OnStateJson?.Invoke(json);      continue; }
                if (json.Contains("\"solution\""))   { OnSolutionJson?.Invoke(json);   continue; }
                    if (json.Contains("\"detections\"")) { OnDetectionsJson?.Invoke(json); continue; }
                    BoardPoseReply r;
                    try { r = JsonUtility.FromJson<BoardPoseReply>(json); } catch { continue; }
                    if (r == null || r.type != "board_pose" || !r.ok) continue;
                    UpdateTarget(r);
                }
            }
            ApplySmoothing();
        }

        private void UpdateTarget(BoardPoseReply r)
        {
            if (m_recalibrate) { m_locked = false; m_recalibrate = false; }
            if (m_locked && !m_trackContinuously) return; // 固定モードでロック済みなら更新しない
            if (!m_sender.TryGetCameraPose(r.frameId, out var camPose)) return;

            Vector3 posCv = new Vector3(r.px, r.py, r.pz);
            Quaternion rotCv = new Quaternion(r.qx, r.qy, r.qz, r.qw);
            // OpenCVカメラ座標 -> Unity: Y反転
            Vector3 posCam = new Vector3(posCv.x, -posCv.y, posCv.z);
            Quaternion rotCam = new Quaternion(-rotCv.x, rotCv.y, -rotCv.z, rotCv.w);

            // カメラ光学中心 → 目(描画基準)へのオフセットを、そのフレームのカメラ姿勢で
            // 回してからワールド位置に加える（角度が変わっても正しく追従する）。
            Vector3 eyePos = camPose.position + camPose.rotation * m_camToEyeOffset;

            m_targetPos = eyePos + camPose.rotation * posCam;
            m_targetRot = camPose.rotation * rotCam;
            m_hasTarget = true;
        }

        private void ApplySmoothing()
        {
            if (!m_hasTarget) return;
            if (m_boardRoot == null) m_boardRoot = BuildAxisGizmo();

            if (!m_locked)
            {
                m_boardRoot.SetPositionAndRotation(m_targetPos, m_targetRot); // 初回はスナップ
                m_boardRoot.gameObject.SetActive(true);
                m_locked = true;
                Debug.Log($"BoardAnchor: locked at {m_targetPos:F3}");
                return;
            }
            if (!m_trackContinuously) return;

            float t = 1f - Mathf.Exp(-m_smooth * Time.deltaTime); // フレームレート非依存の平滑化
            m_boardRoot.position = Vector3.Lerp(m_boardRoot.position, m_targetPos, t);
            m_boardRoot.rotation = Quaternion.Slerp(m_boardRoot.rotation, m_targetRot, t);
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