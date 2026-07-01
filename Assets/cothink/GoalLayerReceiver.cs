// GoalLayerReceiver.cs  (Week3 段取り②: solution + state coloring)
// Draws the goal silhouette (one semi-transparent slab per solution cell, as a
// child of the board anchor) and recolors it from the PC `state` message:
//   placed = green, next piece = its own color (pulsing), future = grey.
// Set m_flipY to the SAME value as BlockMarkerReceiver.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoThink
{
    [Serializable] internal class SolCell { public int r, c, bid; public string name; public float x, y, w, h; }
    [Serializable] internal class SolMsg  { public string type; public int gw, gh; public SolCell[] cells; public int[] order; }
    [Serializable] internal class StateMsg { public string type; public int frameId; public int[] placed; public int next; }

    public class GoalLayerReceiver : MonoBehaviour
    {
        [SerializeField] private BoardAnchorReceiver m_anchor;
        [Tooltip("Match BlockMarkerReceiver's Flip Y.")]
        [SerializeField] private bool m_flipY = true;

        [Header("Slab")]
        [SerializeField] private float m_thickness = 0.002f;
        [SerializeField] private float m_zOffset = 0.0f;
        [Range(0f, 1f)] [SerializeField] private float m_alpha = 0.4f;

        [Header("State colors")]
        [SerializeField] private Color m_futureColor = new Color(0.6f, 0.6f, 0.6f);
        [SerializeField] private Color m_placedColor = new Color(0.15f, 0.9f, 0.2f);
        [Tooltip("次手ピースの点滅速度。")]
        [SerializeField] private float m_pulseSpeed = 2.5f;

        private static readonly Dictionary<string, Color> PIECE_COLORS = new Dictionary<string, Color>
        {
            { "I", Color.red }, { "O", Color.green }, { "T", new Color(0.2f,0.4f,1f) },
            { "S", Color.yellow }, { "Z", Color.magenta }, { "J", new Color(1f,0.6f,0f) },
            { "L", Color.cyan },
        };

        private readonly List<GameObject> m_pool = new List<GameObject>();
        private readonly List<int> m_cellBid = new List<int>();
        private readonly List<Color> m_cellPiece = new List<Color>();  // そのセルのピース本来色
        private int m_activeCount;

        private readonly HashSet<int> m_placed = new HashSet<int>();
        private int m_next = -1;
        private bool m_hasState;

        private void OnEnable()
        {
            if (m_anchor != null)
            {
                m_anchor.OnSolutionJson += OnSolution;
                m_anchor.OnStateJson += OnState;
            }
        }

        private void OnDisable()
        {
            if (m_anchor != null)
            {
                m_anchor.OnSolutionJson -= OnSolution;
                m_anchor.OnStateJson -= OnState;
            }
        }

        private void OnSolution(string json)
        {
            var root = (m_anchor != null) ? m_anchor.BoardRoot : null;
            if (root == null) return;
            SolMsg msg;
            try { msg = JsonUtility.FromJson<SolMsg>(json); } catch { return; }
            if (msg == null || msg.cells == null) return;

            int n = msg.cells.Length;
            EnsurePool(n);
            m_cellBid.Clear(); m_cellPiece.Clear();
            for (int i = 0; i < n; i++)
            {
                var cell = msg.cells[i];
                var go = m_pool[i];
                go.transform.SetParent(root, false);
                float y = m_flipY ? -cell.y : cell.y;
                go.transform.localPosition = new Vector3(cell.x, y, m_zOffset);
                go.transform.localScale = new Vector3(cell.w, cell.h, m_thickness);
                InitTransparent(go);
                go.SetActive(true);

                m_cellBid.Add(cell.bid);
                Color pc = PIECE_COLORS.TryGetValue(cell.name ?? "", out var col) ? col : Color.white;
                m_cellPiece.Add(pc);
            }
            m_activeCount = n;
            Hide(n);

            // 新しい解答が来たら state はリセット（全セル未配置=grey）
            m_placed.Clear(); m_next = -1; m_hasState = false;
            ApplyStaticColors();
            Debug.Log($"GoalLayer: drew {n} cells (gw={msg.gw}, gh={msg.gh})");
        }

        private void OnState(string json)
        {
            StateMsg msg;
            try { msg = JsonUtility.FromJson<StateMsg>(json); } catch { return; }
            if (msg == null) return;
            m_placed.Clear();
            if (msg.placed != null) foreach (var b in msg.placed) m_placed.Add(b);
            m_next = msg.next;
            m_hasState = true;
            ApplyStaticColors();   // placed=緑 / future=grey を反映（next は Update でパルス）
        }

        // placed / future の色を確定。next 手のセルは Update() で点滅させるので触らない。
        private void ApplyStaticColors()
        {
            for (int i = 0; i < m_activeCount; i++)
            {
                int bid = m_cellBid[i];
                if (m_hasState && bid == m_next) continue;
                Color c = (m_hasState && m_placed.Contains(bid)) ? m_placedColor : m_futureColor;
                c.a = m_alpha;
                SetColor(m_pool[i], c);
            }
        }

        private void Update()
        {
            if (!m_hasState || m_next < 0) return;
            float t = Mathf.PingPong(Time.time * m_pulseSpeed, 1f);
            for (int i = 0; i < m_activeCount; i++)
            {
                if (m_cellBid[i] != m_next) continue;
                Color c = Color.Lerp(m_futureColor, m_cellPiece[i], t);
                c.a = m_alpha;
                SetColor(m_pool[i], c);
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

        // マテリアルを一度だけ透過設定にする（毎フレーム作り直さない）
        private static void InitTransparent(GameObject go)
        {
            var rend = go.GetComponent<Renderer>(); if (rend == null) return;
            var mat = rend.material; // default URP Lit instance
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
