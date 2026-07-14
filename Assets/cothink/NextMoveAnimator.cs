// NextMoveAnimator.cs  (v1: 次手ピースの移動+回転アニメーション)
// standalone_insertion_puzzle.py の draw_move_animation を Quest 側に移植。
// - solution: cells から次手ピースの目標セル群（目標姿勢のスラブ形状）と重心、rots[bid] から目標回転を取得
// - detections: 次手と同名の未配置ピースの現在位置 (x,y) と OBB 角 a を取得
// - state: next の更新でアニメ対象を切替（next=-1 / 未受信は非表示）
// アニメ: 開始pose→目標poseをループ再生（smoothstepイージング、末尾フェードアウト）。
// 回転は対称性を考慮した最短回転（O=90°周期 / I,S,Z=180° / T,J,L=360°）。
//
// Setup:
//   BoardAnchorReceiver をアサイン。Flip Y は BlockMarker/GoalLayer と同値に。
//   Flip Angle / Angle In Degrees は実機ギズモで要確認（下記「要確認」参照）。
//
// 要確認（実機/サーバ側）:
//   1. detections の a の単位: ultralytics OBB の r は**ラジアン**。サーバが度に変換して
//      送っている場合は m_angleInDegrees=true にする。pc_board_pose_server.py 側の
//      build 部分を確認すること。
//   2. 回転方向の符号: flipY で Y を反転しているため、盤面座標系の正回転が Unity ローカル Z
//      回りでは逆符号になる可能性が高い（m_flipAngle=true が初期仮定）。実機で
//      「ピースを45°傾けて置いた時、ゴーストの初期姿勢が実物と一致するか」で確定する。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoThink
{
    [Serializable] internal class AnimSolCell { public int r, c, bid; public string name; public float x, y, w, h; }
    [Serializable] internal class AnimSolMsg  { public string type; public int gw, gh; public AnimSolCell[] cells; public int[] order; public int[] rots; }
    [Serializable] internal class AnimStateMsg { public string type; public int[] placed; public int next; }
    [Serializable] internal class AnimDetItem { public string n; public float x, y, a; }
    [Serializable] internal class AnimDetMsg  { public string type; public int frameId; public AnimDetItem[] items; }

    public class NextMoveAnimator : MonoBehaviour
    {
        [SerializeField] private BoardAnchorReceiver m_anchor;

        [Header("座標系（BlockMarker/GoalLayer と揃える）")]
        [Tooltip("BlockMarkerReceiver / GoalLayerReceiver の Flip Y と同じ値に。")]
        [SerializeField] private bool m_flipY = true;
        [Tooltip("flipY に伴う回転符号の反転。実機で要確認。")]
        [SerializeField] private bool m_flipAngle = true;
        [Tooltip("detections の a が度なら true / ラジアンなら false（ultralytics 生値はラジアン）。")]
        [SerializeField] private bool m_angleInDegrees = false;

        [Header("アニメーション")]
        [SerializeField] private float m_duration = 1.4f;   // ANIM_DURATION
        [SerializeField, Range(0f, 1f)] private float m_fade = 0.35f; // ANIM_FADE（末尾フェード割合）
        [SerializeField, Range(0f, 1f)] private float m_alpha = 0.7f; // ANIM_ALPHA
        [SerializeField] private float m_thickness = 0.012f; // ゴーストの厚み（ブロック実寸に近づけると視認性↑）
        [SerializeField] private float m_zOffset = 0.006f;   // GoalLayer より手前に浮かせる

        [Header("検出マッチング")]
        [Tooltip("配置済み同名ピース重心からこの距離以内の検出は開始位置候補から除外(m)")]
        [SerializeField] private float m_excludeRadius = 0.02f;

        [Header("位置安定化（静止中固定・動いたら追従）")]
        [Tooltip("検出遅延による頭部運動時のズレを抑える。ブロック静止中はゴースト開始位置を固定。")]
        [SerializeField] private bool m_stabilize = true;
        [Tooltip("この距離[m]以内の移動は静止とみなし更新しない")]
        [SerializeField] private float m_moveThreshold = 0.008f;
        [Tooltip("追従時の平滑化の強さ")]
        [SerializeField] private float m_followSmooth = 12f;
        [Tooltip("角度の静止しきい値[deg]")]
        [SerializeField] private float m_angleThreshold = 8f;

        // ピース本来色（GoalLayerReceiver.PIECE_COLORS と同値）
        private static readonly Dictionary<string, Color> PIECE_COLORS = new Dictionary<string, Color>
        {
            { "I", new Color(1f, 0f, 0f) }, { "O", new Color(0f, 1f, 0f) },
            { "T", new Color(0f, 0f, 1f) }, { "S", new Color(1f, 1f, 0f) },
            { "Z", new Color(1f, 0f, 1f) }, { "J", new Color(1f, 0.65f, 0f) },
            { "L", new Color(0f, 1f, 1f) },
        };

        // 回転対称性: 360°/sym が同値回転の周期（standalone の sym_map と同値）
        private static readonly Dictionary<string, int> SYM = new Dictionary<string, int>
        {
            { "O", 4 }, { "I", 2 }, { "S", 2 }, { "Z", 2 }, { "T", 1 }, { "J", 1 }, { "L", 1 },
        };

        private class PieceInfo
        {
            public string name;
            public Vector2 centroid;                      // 盤面メートル（未flip）
            public readonly List<Vector4> cellRects = new List<Vector4>(); // (dx, dy, w, h) 重心からのオフセット（未flip）
            public int rotIdx;                            // 目標回転 0..3
        }

        private readonly Dictionary<int, PieceInfo> m_pieces = new Dictionary<int, PieceInfo>();
        private readonly HashSet<int> m_placed = new HashSet<int>();
        private int m_next = -999;

        // 最新検出から選んだ開始pose（盤面座標・未flip・度）
        private bool m_hasSrc;
        private Vector2 m_srcPos;
        private float m_srcDeg;

        // ゴースト
        private Transform m_pivot;
        private readonly List<GameObject> m_slabs = new List<GameObject>();
        private int m_builtBid = -999;
        private float m_loopT0;

        // ---- 実行時セッター（MR内設定パネル用）----
        public float Alpha
        {
            get => m_alpha;
            set => m_alpha = Mathf.Clamp01(value);
        }

        private void OnEnable()
        {
            if (m_anchor == null) return;
            m_anchor.OnSolutionJson += OnSolution;
            m_anchor.OnStateJson += OnState;
            m_anchor.OnDetectionsJson += OnDetections;
        }

        private void OnDisable()
        {
            if (m_anchor == null) return;
            m_anchor.OnSolutionJson -= OnSolution;
            m_anchor.OnStateJson -= OnState;
            m_anchor.OnDetectionsJson -= OnDetections;
        }

        // ---------- 受信 ----------

        private void OnSolution(string json)
        {
            AnimSolMsg msg;
            try { msg = JsonUtility.FromJson<AnimSolMsg>(json); } catch { return; }
            if (msg == null || msg.cells == null) return;

            m_pieces.Clear();
            m_placed.Clear();
            m_next = -999;
            m_builtBid = -999;
            m_hasSrc = false;

            // bidごとにセルを集約
            var byBid = new Dictionary<int, List<AnimSolCell>>();
            foreach (var cell in msg.cells)
            {
                if (!byBid.TryGetValue(cell.bid, out var list))
                {
                    list = new List<AnimSolCell>();
                    byBid[cell.bid] = list;
                }
                list.Add(cell);
            }

            foreach (var kv in byBid)
            {
                var cells = kv.Value;
                var info = new PieceInfo { name = cells[0].name };
                float sx = 0f, sy = 0f;
                foreach (var c in cells) { sx += c.x; sy += c.y; }
                info.centroid = new Vector2(sx / cells.Count, sy / cells.Count);
                foreach (var c in cells)
                    info.cellRects.Add(new Vector4(c.x - info.centroid.x, c.y - info.centroid.y, c.w, c.h));
                info.rotIdx = (msg.rots != null && kv.Key >= 0 && kv.Key < msg.rots.Length) ? msg.rots[kv.Key] : 0;
                m_pieces[kv.Key] = info;
            }
            Debug.Log($"NextMoveAnimator: solution loaded, {m_pieces.Count} pieces");
        }

        private void OnState(string json)
        {
            AnimStateMsg msg;
            try { msg = JsonUtility.FromJson<AnimStateMsg>(json); } catch { return; }
            if (msg == null || msg.type != "state") return;
            m_placed.Clear();
            if (msg.placed != null)
                foreach (var b in msg.placed) m_placed.Add(b);
            if (msg.next != m_next)
            {
                m_next = msg.next;
                m_hasSrc = false;          // 前ピースの検出を持ち越さない
                m_loopT0 = Time.time;      // ループを頭出し
            }
        }

        private void OnDetections(string json)
        {
            if (m_next < 0 || !m_pieces.TryGetValue(m_next, out var target)) return;

            AnimDetMsg msg;
            try { msg = JsonUtility.FromJson<AnimDetMsg>(json); } catch { return; }
            if (msg == null || msg.items == null) return;

            // 次手と同名 かつ 配置済み同名ピースの近傍でない検出のうち、目標に最も近いもの
            float bestDist = float.MaxValue;
            bool found = false;
            Vector2 bestPos = Vector2.zero;
            float bestDeg = 0f;

            foreach (var it in msg.items)
            {
                if (it.n != target.name) continue;
                var p = new Vector2(it.x, it.y);

                bool nearPlaced = false;
                foreach (var bid in m_placed)
                {
                    if (!m_pieces.TryGetValue(bid, out var pl) || pl.name != target.name) continue;
                    if (Vector2.Distance(p, pl.centroid) < m_excludeRadius) { nearPlaced = true; break; }
                }
                if (nearPlaced) continue;

                float d = Vector2.Distance(p, target.centroid);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestPos = p;
                    bestDeg = m_angleInDegrees ? it.a : it.a * Mathf.Rad2Deg;
                    found = true;
                }
            }

            if (found)
            {
                if (!m_stabilize || !m_hasSrc)
                {
                    // 初回、または安定化オフ: そのまま採用
                    m_srcPos = bestPos;
                    m_srcDeg = bestDeg;
                }
                else
                {
                    // 静止中は固定、動いたら平滑追従（頭部運動時のズレ抑制）
                    if (Vector2.Distance(bestPos, m_srcPos) > m_moveThreshold)
                    {
                        float a = 1f - Mathf.Exp(-m_followSmooth * Time.deltaTime);
                        m_srcPos = Vector2.Lerp(m_srcPos, bestPos, a);
                    }
                    if (Mathf.Abs(Mathf.DeltaAngle(m_srcDeg, bestDeg)) > m_angleThreshold)
                    {
                        float a = 1f - Mathf.Exp(-m_followSmooth * Time.deltaTime);
                        m_srcDeg = Mathf.LerpAngle(m_srcDeg, bestDeg, a);
                    }
                }
                m_hasSrc = true;
            }
        }

        // ---------- 描画 ----------

        private void Update()
        {
            var root = (m_anchor != null) ? m_anchor.BoardRoot : null;
            bool active = root != null && m_next >= 0 && m_hasSrc && m_pieces.ContainsKey(m_next);
            if (!active)
            {
                if (m_pivot != null && m_pivot.gameObject.activeSelf) m_pivot.gameObject.SetActive(false);
                return;
            }

            var info = m_pieces[m_next];
            EnsureGhost(root, info);

            // ---- 進行度（standalone: elapsed % DURATION → smoothstep）----
            float elapsed = Mathf.Repeat(Time.time - m_loopT0, m_duration);
            float t = elapsed / m_duration;
            float prog = t * t * (3f - 2f * t);

            // ---- 位置補間（盤面座標で補間し、最後に flip 適用）----
            Vector2 pos = Vector2.LerpUnclamped(m_srcPos, info.centroid, prog);
            float y = m_flipY ? -pos.y : pos.y;
            m_pivot.localPosition = new Vector3(pos.x, y, m_zOffset);

            // ---- 回転補間（対称性を考慮した最短回転）----
            // ゴーストは目標姿勢で構築済みなので、ピボット角は delta*(1-prog) → 0 に収束。
            float targetDeg = 90f * info.rotIdx;
            int sym = SYM.TryGetValue(info.name ?? "", out var s) ? s : 1;
            float step = 360f / sym;
            float delta = Mathf.Repeat(m_srcDeg - targetDeg, step);
            if (delta > step * 0.5f) delta -= step;
            float zDeg = delta * (1f - prog);
            if (m_flipAngle) zDeg = -zDeg;
            m_pivot.localRotation = Quaternion.Euler(0f, 0f, zDeg);

            // ---- フェード（末尾 m_fade 区間で alpha→0）----
            float fadeStart = 1f - m_fade;
            float a = (prog <= fadeStart || m_fade <= 0f)
                ? m_alpha
                : m_alpha * (1f - (prog - fadeStart) / m_fade);

            Color baseCol = PIECE_COLORS.TryGetValue(info.name ?? "", out var pc) ? pc : Color.white;
            var col = new Color(baseCol.r, baseCol.g, baseCol.b, a);
            foreach (var slab in m_slabs) SetColor(slab, col);
        }

        private void EnsureGhost(Transform root, PieceInfo info)
        {
            if (m_pivot == null)
            {
                m_pivot = new GameObject("NextMovePivot").transform;
            }
            m_pivot.SetParent(root, false);

            if (m_builtBid != m_next)
            {
                // セル数に合わせてスラブを用意
                while (m_slabs.Count < info.cellRects.Count)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    var c = go.GetComponent<Collider>(); if (c != null) Destroy(c);
                    go.name = "NextMoveSlab_" + m_slabs.Count;
                    go.transform.SetParent(m_pivot, false);
                    MakeTransparent(go);
                    m_slabs.Add(go);
                }
                for (int i = 0; i < m_slabs.Count; i++)
                {
                    bool use = i < info.cellRects.Count;
                    m_slabs[i].SetActive(use);
                    if (!use) continue;
                    var r = info.cellRects[i];
                    float dy = m_flipY ? -r.y : r.y;
                    m_slabs[i].transform.localPosition = new Vector3(r.x, dy, 0f);
                    m_slabs[i].transform.localScale = new Vector3(r.z, r.w, m_thickness);
                }
                m_builtBid = m_next;
            }
            if (!m_pivot.gameObject.activeSelf) m_pivot.gameObject.SetActive(true);
        }

        // ---------- マテリアル（GoalLayerReceiver と同処理） ----------

        private static void MakeTransparent(GameObject go)
        {
            var rend = go.GetComponent<Renderer>(); if (rend == null) return;
            var mat = rend.material;
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
        }

        private static void SetColor(GameObject go, Color color)
        {
            var rend = go.GetComponent<Renderer>(); if (rend == null) return;
            var mat = rend.material;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
        }
    }
}