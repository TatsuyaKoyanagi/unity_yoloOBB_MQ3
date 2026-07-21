// GoalLayerReceiver.cs  (v2: solution + state による色分け/次手パルス)
// - solution: セル配置を受けてスラブ群を構築（bidごとにグループ化）
// - state   : placed/next を受けて色を更新
//     配置済み(placed) = 緑 / 次の1手(next) = 本来色でパルス / 先の手 = 薄グレー
// 矢印は仕様から削除済み。
// Setup: BoardAnchorReceiver をアサイン。Flip Y は BlockMarkerReceiver と同値。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoThink
{
    [Serializable] internal class SolCell { public int r, c, bid; public string name; public float x, y, w, h; }
    [Serializable] internal class SolMsg  { public string type; public int gw, gh; public SolCell[] cells; public int[] order; }
    [Serializable] internal class StateMsg { public string type; public int[] placed; public int next;
        public string warnKind; public int warnBid; public string warnName; public float warnDeg; }

    public class GoalLayerReceiver : MonoBehaviour
    {
        [SerializeField] private BoardAnchorReceiver m_anchor;
        [Tooltip("BlockMarkerReceiver の Flip Y と同じ値に。")]
        [SerializeField] private bool m_flipY = true;
        [SerializeField] private float m_thickness = 0.002f;
        [SerializeField] private float m_zOffset = 0.0f;

        [Header("状態色")]
        [SerializeField] private Color m_ghostColor  = new Color(0.65f, 0.65f, 0.65f, 0.30f); // 先の手
        [SerializeField] private Color m_placedColor = new Color(0.10f, 0.85f, 0.20f, 0.45f); // 配置済み
        [SerializeField, Range(0.5f, 10f)] private float m_pulseSpeed = 5f;                    // 次手パルス速度
        [Tooltip("次手パルスのアルファ最小値（暗い時）")]
        [SerializeField, Range(0f, 1f)] private float m_nextAlphaMin = 0.25f;
        [Tooltip("次手パルスのアルファ振幅（最大 = min + amp）")]
        [SerializeField, Range(0f, 1f)] private float m_nextAlphaAmp = 0.5f;

        [Header("警告色（Co-thinkフィードバック）")]
        [Tooltip("角度違い: 位置は合っているが向きが違う")]
        [SerializeField] private Color m_warnAngleColor = new Color(1f, 0.55f, 0f); // オレンジ
        [Tooltip("誤ブロック: 次手の場所に別種のブロックが置かれている")]
        [SerializeField] private Color m_warnPieceColor = new Color(1f, 0.1f, 0.1f); // 赤

        // ピース本来色（PC側 BLOCK_COLORS と対応, BGR->RGB換算済み）
        private static readonly Dictionary<string, Color> PIECE_COLORS = new Dictionary<string, Color>
        {
            { "I", new Color(1f, 0f, 0f) },       // 赤
            { "O", new Color(0f, 1f, 0f) },       // 緑
            { "T", new Color(0f, 0f, 1f) },       // 青
            { "S", new Color(1f, 1f, 0f) },       // 黄
            { "Z", new Color(1f, 0f, 1f) },       // マゼンタ
            { "J", new Color(1f, 0.65f, 0f) },    // オレンジ
            { "L", new Color(0f, 1f, 1f) },       // シアン
        };

        private class Piece
        {
            public string name;
            public readonly List<GameObject> slabs = new List<GameObject>();
        }

        private readonly List<GameObject> m_pool = new List<GameObject>();
        private readonly Dictionary<int, Piece> m_pieces = new Dictionary<int, Piece>(); // bid -> piece
        private readonly HashSet<int> m_placed = new HashSet<int>();
        private int m_next = -999;   // state未受信＝次手なし
        private string m_warnKind = "";
        private int m_warnBid = -1;

        private void OnEnable()
        {
            if (m_anchor == null) return;
            m_anchor.OnSolutionJson += OnSolution;
            m_anchor.OnDetectionsJson += OnAnyJson; // stateはdetectionsと同経路で来ないが将来用
            m_anchor.OnStateJson += OnState;
        }

        private void OnDisable()
        {
            if (m_anchor == null) return;
            m_anchor.OnSolutionJson -= OnSolution;
            m_anchor.OnDetectionsJson -= OnAnyJson;
            m_anchor.OnStateJson -= OnState;
        }

        private void OnAnyJson(string _) { }

        // ---- 実行時セッター（MR内設定パネル用）----
        public float GhostAlpha
        {
            get => m_ghostColor.a;
            set
            {
                var c = m_ghostColor; c.a = Mathf.Clamp01(value); m_ghostColor = c;
                // 診断用（パネル反映問題の切り分け）。解決したら削除可。
                Debug.Log($"GoalLayer[{GetInstanceID()}]: GhostAlpha={c.a:F2} pieces={m_pieces.Count}");
            }
        }
        public float PlacedAlpha
        {
            get => m_placedColor.a;
            set { var c = m_placedColor; c.a = Mathf.Clamp01(value); m_placedColor = c; }
        }
        public float NextAlphaMin
        {
            get => m_nextAlphaMin;
            set => m_nextAlphaMin = Mathf.Clamp01(value);
        }

        private void OnSolution(string json)
        {
            var root = (m_anchor != null) ? m_anchor.BoardRoot : null;
            if (root == null) return;
            SolMsg msg;
            try { msg = JsonUtility.FromJson<SolMsg>(json); } catch { return; }
            if (msg == null || msg.cells == null) return;

            // 再構築
            m_pieces.Clear();
            m_placed.Clear();
            m_next = -999;

            int n = msg.cells.Length;
            EnsurePool(n);
            for (int i = 0; i < n; i++)
            {
                var cell = msg.cells[i];
                var go = m_pool[i];
                go.transform.SetParent(root, false);
                float y = m_flipY ? -cell.y : cell.y;
                go.transform.localPosition = new Vector3(cell.x, y, m_zOffset);
                go.transform.localScale = new Vector3(cell.w, cell.h, m_thickness);
                MakeTransparent(go);
                go.SetActive(true);

                if (!m_pieces.TryGetValue(cell.bid, out var piece))
                {
                    piece = new Piece { name = cell.name };
                    m_pieces[cell.bid] = piece;
                }
                piece.slabs.Add(go);
            }
            Hide(n);
            ApplyColors(1f);
            Debug.Log($"GoalLayer: built {m_pieces.Count} pieces / {n} cells");
        }

        private void OnState(string json)
        {
            StateMsg msg;
            try { msg = JsonUtility.FromJson<StateMsg>(json); } catch { return; }
            if (msg == null || msg.type != "state") return;
            m_placed.Clear();
            if (msg.placed != null)
                foreach (var b in msg.placed) m_placed.Add(b);
            m_next = msg.next;
            m_warnKind = msg.warnKind ?? "";
            m_warnBid = msg.warnBid;
        }

        private void Update()
        {
            if (m_pieces.Count == 0) return;
            // パルス係数 0.35..1.0
            float pulse = 0.35f + 0.65f * (0.5f + 0.5f * Mathf.Sin(Time.time * m_pulseSpeed));
            ApplyColors(pulse);
        }

        private void ApplyColors(float pulse)
        {
            foreach (var kv in m_pieces)
            {
                int bid = kv.Key;
                var piece = kv.Value;
                Color col;
                if (m_placed.Contains(bid))
                {
                    col = m_placedColor;
                }
                else if (bid == m_next && m_next >= 0)
                {
                    // 警告があれば次手を警告色でパルス（angle=オレンジ / piece=赤）
                    Color baseCol;
                    if (m_warnBid == bid && m_warnKind == "piece")      baseCol = m_warnPieceColor;
                    else if (m_warnBid == bid && m_warnKind == "angle") baseCol = m_warnAngleColor;
                    else baseCol = PIECE_COLORS.TryGetValue(piece.name ?? "", out var pc) ? pc : Color.white;
                    col = new Color(baseCol.r, baseCol.g, baseCol.b, m_nextAlphaMin + m_nextAlphaAmp * pulse);
                }
                else
                {
                    col = m_ghostColor;
                }
                foreach (var slab in piece.slabs)
                    SetColor(slab, col);
            }
        }

        private void EnsurePool(int n)
        {
            while (m_pool.Count < n)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);
                go.name = "GoalCell_" + m_pool.Count;
                go.SetActive(false);
                m_pool.Add(go);
            }
        }

        private void Hide(int from)
        {
            for (int i = from; i < m_pool.Count; i++)
                if (m_pool[i].activeSelf) m_pool[i].SetActive(false);
        }

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