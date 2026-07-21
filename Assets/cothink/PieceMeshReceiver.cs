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
    [Serializable] internal class MeshSolCell { public int r, c, bid; public string name; public float x, y, w, h; }
    [Serializable] internal class MeshSolMsg  { public string type; public MeshSolCell[] cells; }
    [Serializable] internal class MeshStateMsg { public string type; public int[] placed; public int next;
        public string warnKind; public string warnName; }

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
        [Tooltip("配置確定したブロックの重畳STLを消す（視界をクリーンに）")]
        [SerializeField] private bool m_hidePlaced = true;
        [Tooltip("配置済み判定の距離しきい値[m]（目標セル重心からこの距離以内の検出を配置済みとみなし消す）")]
        [SerializeField] private float m_placedRadius = 0.025f;
        [Tooltip("解に含まれない種類のブロック（トラップ）にはSTLを重畳しない")]
        [SerializeField] private bool m_hideTraps = true;

        [Header("誤接触の×印")]
        [Tooltip("touch警告中のブロック上に赤い×印を表示する")]
        [SerializeField] private bool m_showWarnMark = true;
        [Tooltip("×印の大きさ[m]")]
        [SerializeField] private float m_markSize = 0.045f;
        [Tooltip("×印のバーの太さ比率（サイズに対する割合）")]
        [SerializeField, Range(0.05f, 0.4f)] private float m_markThickness = 0.16f;
        [Tooltip("×印をブロックから浮かせる高さ[m]")]
        [SerializeField] private float m_markHeight = 0.03f;

        [Header("位置安定化（静止中固定・動いたら追従）")]
        [Tooltip("検出の遅延による頭部運動時のズレを抑える。ブロック静止中はホログラムを固定。")]
        [SerializeField] private bool m_stabilize = true;
        [Tooltip("この距離[m]以内の移動は「静止」とみなし位置を更新しない")]
        [SerializeField] private float m_moveThreshold = 0.008f;
        [Tooltip("追従時の平滑化の強さ（大きいほどキビキビ）")]
        [SerializeField] private float m_followSmooth = 12f;
        [Tooltip("実移動と確定するのに必要な連続フレーム数（頭部連動の一過性ズレを弾く）")]
        [SerializeField] private int m_moveConfirmFrames = 3;
        [Tooltip("角度の静止しきい値[deg]")]
        [SerializeField] private float m_angleThreshold = 8f;

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

        // solution: bid -> ピース目標重心（盤面メートル、未flip）
        private readonly Dictionary<int, Vector2> m_pieceTarget = new Dictionary<int, Vector2>();
        // solution: 解に含まれる種類（トラップ判定用）
        private readonly HashSet<string> m_solutionNames = new HashSet<string>();
        // state: 配置確定済み bid
        private readonly HashSet<int> m_placed = new HashSet<int>();
        // state: 誤接触警告（touch のとき該当種類のSTLを赤く）
        private string m_warnKind = "";
        private string m_warnName = "";

        // 位置安定化: 種類別に「安定化された検出」を保持し、フレーム間で最近傍マッチング。
        // 【頭部連動ズレ対策】頭部運動でホモグラフィが揺らぐと全検出が一斉に同方向へシフトする。
        // 全トラックの変位の中央値=グローバルドリフトとみなして差し引き、残差変位だけを
        // 「本当のブロック移動」として扱う（1個だけ動く=実移動、全部動く=頭部連動ノイズ）。
        // さらに移動確認カウンタ（m_moveConfirmFrames連続）を通過して初めて追従を開始する。
        private class Track
        {
            public string name; public Vector2 pos; public float deg;
            public bool alive; public int moveCount;
        }
        private readonly List<Track> m_tracks = new List<Track>();
        private readonly List<Track> m_frameTracks = new List<Track>();

        // 作業バッファ（毎フレームの割当を避ける）
        private struct Match { public Track track; public Vector2 newPos; public float newDeg; public Vector2 disp; }
        private readonly List<Match> m_matches = new List<Match>();
        private readonly List<float> m_dxBuf = new List<float>();
        private readonly List<float> m_dyBuf = new List<float>();

        // ×印マーカーのプール（BoardRoot直下、ブロック回転の影響を受けない）
        private readonly List<GameObject> m_markPool = new List<GameObject>();

        private static float Median(List<float> v)
        {
            if (v.Count == 0) return 0f;
            v.Sort();
            int m = v.Count / 2;
            return (v.Count % 2 == 1) ? v[m] : 0.5f * (v[m - 1] + v[m]);
        }

        // 生検出を安定化して返す。頭部連動ドリフトは無視し、実移動のみ追従。
        private void StabilizeDetections(MeshDetItem[] items, List<Track> outTracks)
        {
            outTracks.Clear();
            if (!m_stabilize)
            {
                foreach (var it in items)
                {
                    float deg0 = m_angleInDegrees ? it.a : it.a * Mathf.Rad2Deg;
                    outTracks.Add(new Track { name=(it.n??"").ToUpperInvariant(), pos=new Vector2(it.x,it.y), deg=deg0, alive=true });
                }
                return;
            }

            foreach (var t in m_tracks) t.alive = false;
            int n0 = m_tracks.Count;
            var usedTrack = new bool[n0];
            m_matches.Clear();

            // ---- pass1: 最近傍マッチング（既存トラックのみ対象）----
            foreach (var it in items)
            {
                string key = (it.n ?? "").ToUpperInvariant();
                var p = new Vector2(it.x, it.y);
                float deg = m_angleInDegrees ? it.a : it.a * Mathf.Rad2Deg;

                int best = -1; float bd = float.MaxValue;
                for (int i = 0; i < n0; i++)
                {
                    if (usedTrack[i] || m_tracks[i].name != key) continue;
                    float d = Vector2.Distance(p, m_tracks[i].pos);
                    if (d < bd) { bd = d; best = i; }
                }

                if (best >= 0 && bd < 0.05f)
                {
                    var tr = m_tracks[best];
                    usedTrack[best] = true;
                    tr.alive = true;
                    m_matches.Add(new Match { track = tr, newPos = p, newDeg = deg, disp = p - tr.pos });
                    outTracks.Add(tr);
                }
                else
                {
                    var tr = new Track { name = key, pos = p, deg = deg, alive = true };
                    m_tracks.Add(tr);
                    outTracks.Add(tr);
                }
            }

            // ---- グローバルドリフト（頭部連動の一斉シフト）を中央値で推定 ----
            m_dxBuf.Clear(); m_dyBuf.Clear();
            foreach (var mt in m_matches) { m_dxBuf.Add(mt.disp.x); m_dyBuf.Add(mt.disp.y); }
            Vector2 drift = (m_matches.Count >= 2)
                ? new Vector2(Median(m_dxBuf), Median(m_dyBuf))
                : Vector2.zero;   // 1個以下ではドリフトと実移動を区別できないので補正しない

            // ---- pass2: 残差変位で移動判定 → 確認カウンタ → 追従 ----
            foreach (var mt in m_matches)
            {
                var tr = mt.track;
                Vector2 residual = mt.disp - drift;

                if (residual.magnitude > m_moveThreshold)
                {
                    tr.moveCount++;
                    if (tr.moveCount >= m_moveConfirmFrames)
                    {
                        // 実移動と確定 → 生座標へ平滑追従
                        float a = 1f - Mathf.Exp(-m_followSmooth * Time.deltaTime);
                        tr.pos = Vector2.Lerp(tr.pos, mt.newPos, a);
                    }
                    // 確認中は位置を保持（頭部連動の一過性シフトを反映しない）
                }
                else
                {
                    tr.moveCount = 0;   // 静止 → 位置保持（world固定と同義）
                }

                // 角度は残差の影響が小さいので従来通り（しきい値超で平滑追従）
                float dd = Mathf.DeltaAngle(tr.deg, mt.newDeg);
                if (Mathf.Abs(dd) > m_angleThreshold)
                {
                    float a = 1f - Mathf.Exp(-m_followSmooth * Time.deltaTime);
                    tr.deg = Mathf.LerpAngle(tr.deg, mt.newDeg, a);
                }
            }

            m_tracks.RemoveAll(t => !t.alive);
        }

        private void Awake()
        {
            BuildMeshMap();
        }

        // ---- 実行時セッター（MR内設定パネル用）----
        public float Alpha
        {
            get => m_alpha;
            set => m_alpha = Mathf.Clamp01(value);
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
            if (m_anchor != null)
            {
                m_anchor.OnDetectionsJson += OnDetections;
                m_anchor.OnSolutionJson += OnSolution;
                m_anchor.OnStateJson += OnState;
            }
        }

        private void OnDisable()
        {
            if (m_anchor != null)
            {
                m_anchor.OnDetectionsJson -= OnDetections;
                m_anchor.OnSolutionJson -= OnSolution;
                m_anchor.OnStateJson -= OnState;
            }
            HideAll();
            HideMarks(0);
        }

        // solution: 各bidの目標重心を保持（配置済み判定の基準）
        private void OnSolution(string json)
        {
            MeshSolMsg msg;
            try { msg = JsonUtility.FromJson<MeshSolMsg>(json); } catch { return; }
            if (msg == null || msg.cells == null) return;
            var sum = new Dictionary<int, Vector2>();
            var cnt = new Dictionary<int, int>();
            foreach (var c in msg.cells)
            {
                sum[c.bid] = (sum.TryGetValue(c.bid, out var s) ? s : Vector2.zero) + new Vector2(c.x, c.y);
                cnt[c.bid] = (cnt.TryGetValue(c.bid, out var k) ? k : 0) + 1;
            }
            m_pieceTarget.Clear();
            foreach (var kv in sum)
                m_pieceTarget[kv.Key] = kv.Value / cnt[kv.Key];

            // 解に含まれる種類を記録（トラップ判定用）
            m_solutionNames.Clear();
            foreach (var c in msg.cells)
                if (!string.IsNullOrEmpty(c.name))
                    m_solutionNames.Add(c.name.ToUpperInvariant());

            m_placed.Clear();
        }

        // state: 配置確定済み bid を更新
        private void OnState(string json)
        {
            MeshStateMsg msg;
            try { msg = JsonUtility.FromJson<MeshStateMsg>(json); } catch { return; }
            if (msg == null || msg.type != "state") return;
            m_placed.Clear();
            if (msg.placed != null)
                foreach (var b in msg.placed) m_placed.Add(b);
            m_warnKind = msg.warnKind ?? "";
            m_warnName = (msg.warnName ?? "").ToUpperInvariant();
        }

        // ある検出位置が、配置済みピースの目標近傍か
        private bool IsAtPlaced(Vector2 pos)
        {
            if (!m_hidePlaced) return false;
            foreach (var bid in m_placed)
            {
                if (!m_pieceTarget.TryGetValue(bid, out var t)) continue;
                if (Vector2.Distance(pos, t) <= m_placedRadius) return true;
            }
            return false;
        }

        private void OnDetections(string json)
        {
            var root = (m_anchor != null) ? m_anchor.BoardRoot : null;
            if (root == null) return;

            MeshDetMsg msg;
            try { msg = JsonUtility.FromJson<MeshDetMsg>(json); } catch { return; }
            if (msg == null || msg.items == null) { Hide(0); return; }

            // 生検出を安定化（静止中は位置固定、動いたら平滑追従）
            StabilizeDetections(msg.items, m_frameTracks);
            int n = m_frameTracks.Count;
            EnsurePool(n);

            int marksUsed = 0;
            for (int i = 0; i < n; i++)
            {
                var tr = m_frameTracks[i];
                var go = m_pool[i];
                string key = tr.name;

                if (!m_meshMap.TryGetValue(key, out var mesh))
                {
                    go.SetActive(false);
                    continue;
                }

                // touch警告中の該当種類か（誤ブロックを掴んでいる）
                bool touchWarn = (m_warnKind == "touch" && key == m_warnName);

                // トラップ（解に含まれない種類）にはSTLを出さない。
                // ただし touch 警告中は例外: 掴んだトラップを赤で出現させ「使わない」を伝える。
                if (m_hideTraps && m_solutionNames.Count > 0 && !m_solutionNames.Contains(key) && !touchWarn)
                {
                    go.SetActive(false);
                    continue;
                }

                // 配置確定済みピースの位置にある検出は重畳STLを消す（視界クリーン化）
                if (IsAtPlaced(tr.pos))
                {
                    go.SetActive(false);
                    continue;
                }

                // メッシュ差し替え（実体は子GOの MeshFilter）
                SetMesh(go, mesh);

                go.transform.SetParent(root, false);

                // 位置（安定化後の盤面メートル → 平面内オフセット加算 → flipY 適用）
                float rawX = tr.pos.x + m_planeOffset.x;
                float rawY = tr.pos.y + m_planeOffset.y;
                float y = m_flipY ? -rawY : rawY;
                go.transform.localPosition = new Vector3(rawX, y, m_zOffset);

                // 回転（安定化後の盤面系角度[deg] を Z 回り）＋ 盤面に寝かせる初期回転
                float deg = tr.deg;
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

                Color col;
                if (touchWarn)
                {
                    // 誤接触: ブロックを黒く（上に赤い×印が乗る）
                    col = new Color(0.05f, 0.05f, 0.05f, Mathf.Max(m_alpha, 0.85f));
                }
                else
                {
                    col = PIECE_COLORS.TryGetValue(key, out var pc) ? pc : Color.white;
                    col.a = m_alpha;
                }
                SetColor(go, col);

                // 誤接触ブロックの上に赤い×印を重畳
                if (touchWarn && m_showWarnMark)
                {
                    PlaceMark(marksUsed++, root,
                              new Vector3(rawX, y, m_zOffset + m_markHeight));
                }

                go.SetActive(true);
            }
            Hide(n);
            HideMarks(marksUsed);
        }

        // ---- ×印マーカー ----
        private GameObject BuildMark()
        {
            var root = new GameObject("WarnMark");
            for (int k = 0; k < 2; k++)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var c = bar.GetComponent<Collider>(); if (c != null) Destroy(c);
                bar.transform.SetParent(root.transform, false);
                bar.transform.localRotation = Quaternion.Euler(0f, 0f, k == 0 ? 45f : -45f);
                var mr = bar.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                var mat = new Material(sh);
                var red = new Color(1f, 0.05f, 0.05f, 1f);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", red);
                mat.color = red;
                // 半透明ブロック(Transparentキュー=3000)より後に描画し、×が確実に上に乗るようにする
                mat.renderQueue = 3100;
                mr.material = mat;
            }
            root.SetActive(false);
            return root;
        }

        private void EnsureMarkPool(int n)
        {
            while (m_markPool.Count < n)
                m_markPool.Add(BuildMark());
        }

        private void PlaceMark(int idx, Transform root, Vector3 localPos)
        {
            EnsureMarkPool(idx + 1);
            var mark = m_markPool[idx];
            mark.transform.SetParent(root, false);
            mark.transform.localPosition = localPos;
            mark.transform.localRotation = Quaternion.identity;
            float t = m_markSize * m_markThickness;
            for (int k = 0; k < 2; k++)
                mark.transform.GetChild(k).localScale = new Vector3(m_markSize, t, t);
            if (!mark.activeSelf) mark.SetActive(true);
        }

        private void HideMarks(int from)
        {
            for (int i = from; i < m_markPool.Count; i++)
                if (m_markPool[i].activeSelf) m_markPool[i].SetActive(false);
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