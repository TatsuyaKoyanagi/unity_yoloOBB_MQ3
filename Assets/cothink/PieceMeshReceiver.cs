// PieceMeshReceiver.cs  (v1: 検出ブロックを STL メッシュで重畳)
// BlockMarkerReceiver（色分け球）の置き換え/上位版。
// detections を受けて、ピース種別に対応する事前インポート済み STL メッシュを
// 検出位置 (x,y) に配置し、盤面系角度 a で Z 回りに回転させて実ブロックに重ねる。
//
// 前提（standalone_insertion_puzzle.py の draw_stl_on_detection と整合）:
//   - STL は「1単位 = 1セル」でモデリングされている（Iピースなら長さ4×幅1の座標範囲）。
//     → Unity では 1単位 = CELL_SIZE_M (0.02m) に対応。m_cellSize で拡縮。
//   - STL 原点 = ピース中心 の前提（standalone も重心補正なし）。
//     ズレる場合は m_centerOnBounds=true で各メッシュの bounds 中心を原点に補正。
//   - a は盤面(canonical)系の角度[rad]（サーバ v4 で変換済み）。
//     flipY で Y を反転しているため回転符号も反転しうる → m_flipAngle で調整。
//
// Setup:
//   1. STL 7種(I,O,T,S,Z,J,L)を Unity の Assets にインポート（Unity 6 は STL 標準対応）。
//   2. 本コンポーネントを受信系オブジェクトに追加、m_anchor をアサイン。
//   3. m_pieceMeshes に 7 要素、name="I".."L" と対応 Mesh を割り当て。
//   4. Flip Y は BlockMarker/GoalLayer と同値、Flip Angle は NextMoveAnimator と同値に。
//
// 注意: BlockMarkerReceiver と同時に有効にすると球とメッシュが二重表示される。
//        本番はどちらか一方（通常は本コンポーネント）だけを有効にする。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoThink
{
    [Serializable] internal class MeshDetItem { public string n; public float x, y, a; }
    [Serializable] internal class MeshDetMsg  { public string type; public int frameId; public MeshDetItem[] items; }

    public class PieceMeshReceiver : MonoBehaviour
    {
        [Serializable]
        public struct NamedMesh { public string name; public Mesh mesh; }

        [SerializeField] private BoardAnchorReceiver m_anchor;

        [Header("メッシュ（name=I..L に対応する STL Mesh をアサイン）")]
        [SerializeField] private NamedMesh[] m_pieceMeshes;

        [Header("座標系（BlockMarker/GoalLayer/NextMoveAnimator と揃える）")]
        [SerializeField] private bool m_flipY = true;
        [Tooltip("盤面系角度をローカルZ回りに適用する際の符号反転。NextMoveAnimator と同値に。")]
        [SerializeField] private bool m_flipAngle = true;
        [Tooltip("detections の a が度なら true / ラジアンなら false（サーバ v4 はラジアン）。")]
        [SerializeField] private bool m_angleInDegrees = false;

        [Header("寸法・姿勢")]
        [Tooltip("STL 1単位 = 1セル。セル実寸[m]。")]
        [SerializeField] private float m_cellSize = 0.02f;
        [Tooltip("高さ(Z)方向のみ別スケールしたい場合の倍率。1で等方。")]
        [SerializeField] private float m_heightScale = 1.0f;
        [Tooltip("盤面平面にメッシュを寝かせるための初期回転(度)。STLがZ-upなら x=-90 等。")]
        [SerializeField] private Vector3 m_baseEuler = Vector3.zero;
        [Tooltip("各メッシュの bounds 中心を原点に補正（STL原点がピース中心でない場合）。")]
        [SerializeField] private bool m_centerOnBounds = false;
        [Tooltip("盤面から浮かせる量[m]（Zオフセット）。")]
        [SerializeField] private float m_zOffset = 0.0f;
        [Tooltip("盤面平面内の位置微調整[m]。baseEulerで寝かせた際のY下駄などを打ち消す。" +
                 "x=盤面X方向, y=盤面Y方向（flipY適用前の生値に加算）。")]
        [SerializeField] private Vector2 m_planeOffset = Vector2.zero;

        [Header("見た目")]
        [SerializeField, Range(0f, 1f)] private float m_alpha = 0.6f;

        // ピース本来色（GoalLayer/NextMoveAnimator と同値）
        private static readonly Dictionary<string, Color> PIECE_COLORS = new Dictionary<string, Color>
        {
            { "I", new Color(1f, 0f, 0f) }, { "O", new Color(0f, 1f, 0f) },
            { "T", new Color(0f, 0f, 1f) }, { "S", new Color(1f, 1f, 0f) },
            { "Z", new Color(1f, 0f, 1f) }, { "J", new Color(1f, 0.65f, 0f) },
            { "L", new Color(0f, 1f, 1f) },
        };

        private readonly Dictionary<string, Mesh> m_meshMap = new Dictionary<string, Mesh>();
        private readonly Dictionary<string, Vector3> m_centerOffset = new Dictionary<string, Vector3>();
        private readonly List<GameObject> m_pool = new List<GameObject>();

        private void Awake()
        {
            BuildMeshMap();
        }

        private void BuildMeshMap()
        {
            m_meshMap.Clear();
            m_centerOffset.Clear();
            if (m_pieceMeshes == null) return;
            foreach (var nm in m_pieceMeshes)
            {
                if (nm.mesh == null || string.IsNullOrEmpty(nm.name)) continue;
                string key = nm.name.ToUpperInvariant();
                m_meshMap[key] = nm.mesh;
                // bounds 中心（ローカル座標）。center-on-bounds 時に -offset で原点補正。
                m_centerOffset[key] = nm.mesh.bounds.center;
            }
            Debug.Log($"PieceMeshReceiver: meshes = {string.Join(",", m_meshMap.Keys)}");
        }

        private void OnEnable()
        {
            if (m_anchor != null) m_anchor.OnDetectionsJson += OnDetections;
        }

        private void OnDisable()
        {
            if (m_anchor != null) m_anchor.OnDetectionsJson -= OnDetections;
            HideAll();
        }

        private void OnDetections(string json)
        {
            var root = (m_anchor != null) ? m_anchor.BoardRoot : null;
            if (root == null) return;

            MeshDetMsg msg;
            try { msg = JsonUtility.FromJson<MeshDetMsg>(json); } catch { return; }
            if (msg == null || msg.items == null) { Hide(0); return; }

            int n = msg.items.Length;
            EnsurePool(n);

            for (int i = 0; i < n; i++)
            {
                var it = msg.items[i];
                var go = m_pool[i];
                string key = (it.n ?? "").ToUpperInvariant();

                if (!m_meshMap.TryGetValue(key, out var mesh))
                {
                    go.SetActive(false);
                    continue;
                }

                // メッシュ差し替え（実体は子GOの MeshFilter）
                SetMesh(go, mesh);

                go.transform.SetParent(root, false);

                // 位置（盤面メートル → 平面内オフセット加算 → flipY 適用）
                float rawX = it.x + m_planeOffset.x;
                float rawY = it.y + m_planeOffset.y;
                float y = m_flipY ? -rawY : rawY;
                go.transform.localPosition = new Vector3(rawX, y, m_zOffset);

                // 回転（盤面系角度 a を Z 回り）＋ 盤面に寝かせる初期回転
                float deg = m_angleInDegrees ? it.a : it.a * Mathf.Rad2Deg;
                if (m_flipAngle) deg = -deg;
                go.transform.localRotation = Quaternion.Euler(0f, 0f, deg) * Quaternion.Euler(m_baseEuler);

                // スケール（1単位=1セル、高さのみ別倍率可）
                go.transform.localScale = new Vector3(m_cellSize, m_cellSize, m_cellSize * m_heightScale);

                // 原点補正（bounds中心を原点へ）
                var child = go.transform.GetChild(0);
                if (m_centerOnBounds && m_centerOffset.TryGetValue(key, out var c))
                    child.localPosition = -c;
                else
                    child.localPosition = Vector3.zero;

                Color col = PIECE_COLORS.TryGetValue(key, out var pc) ? pc : Color.white;
                col.a = m_alpha;
                SetColor(go, col);

                go.SetActive(true);

                // --- デバッグ: 最初の1個だけ配置情報を出力（原因切り分け用。確認後に削除可） ---
                if (i == 0)
                {
                    var mfDbg = go.transform.GetChild(0).GetComponent<MeshFilter>();
                    var b = (mfDbg != null && mfDbg.sharedMesh != null) ? mfDbg.sharedMesh.bounds.size : Vector3.zero;
                    Debug.Log($"[PieceMesh] {key} localPos={go.transform.localPosition:F3} " +
                              $"world={go.transform.position:F3} scale={go.transform.localScale:F4} " +
                              $"meshBounds={b:F3} active={go.activeInHierarchy}");
                }
            }
            Hide(n);
        }

        private void EnsurePool(int n)
        {
            while (m_pool.Count < n)
            {
                // 親GO（位置・回転・スケール担当）+ 子GO（メッシュ実体、原点補正担当）
                var parent = new GameObject("PieceMesh_" + m_pool.Count);
                var child = new GameObject("Mesh").transform;
                child.SetParent(parent.transform, false);
                child.gameObject.AddComponent<MeshFilter>();
                var mr = child.gameObject.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                MakeTransparentMaterial(mr);
                // MeshFilter/MeshRenderer は子GOに置き、親GOは pos/rot/scale のみ担当。
                // メッシュ差し替え・色設定・原点補正は child 経由で行う（SetMesh/SetColor）。
                parent.SetActive(false);
                m_pool.Add(parent);
            }
        }

        // 親GOの MeshFilter は子にあるため、差し替え用に子の MeshFilter を返すヘルパ
        private void SetMesh(GameObject parent, Mesh mesh)
        {
            var mf = parent.transform.GetChild(0).GetComponent<MeshFilter>();
            if (mf.sharedMesh != mesh) mf.sharedMesh = mesh;
        }

        private void Hide(int from)
        {
            for (int i = from; i < m_pool.Count; i++)
                if (m_pool[i].activeSelf) m_pool[i].SetActive(false);
        }

        private void HideAll() { Hide(0); }

        private static void MakeTransparentMaterial(MeshRenderer mr)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var mat = new Material(sh);
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            mr.material = mat;
        }

        private static void SetColor(GameObject parent, Color color)
        {
            var mr = parent.transform.GetChild(0).GetComponent<MeshRenderer>();
            if (mr == null) return;
            var mat = mr.material;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
        }
    }
}