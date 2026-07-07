// SettingsPanel.cs  (v1: MR内デバッグ設定パネル / OVRInput 数値編集式)
//
// 実験中にコントローラのスティック+ボタンで、カメラ→目オフセットと各種透過率を
// その場で微調整するためのパネル。レイキャスト不要（OVRInput直読み）で堅牢。
//
// 操作:
//   Bボタン(右手)      : パネル開閉
//   右スティック 上下  : 項目選択（カーソル移動）
//   右スティック 左右  : 選択中の値を増減（倒し続けで連続変化）
//
// Setup:
//   1. 空のGameObjectを作り、本コンポーネントを追加。
//   2. m_anchor / m_goal / m_pieceMesh / m_nextMove に各Receiverをアサイン
//      （未アサインの項目は自動的にスキップされる）。
//   3. Canvas/Text はコード内で自動生成するので、シーン側の準備は不要。
//   4. カメラ（CenterEye/Main Camera）を m_headCamera にアサイン。未設定なら Camera.main を使用。
//
// 注意: OVRInput を使うため Oculus Integration / Meta XR Core が必要（本プロジェクトは導入済み）。
//       値は各Receiverのセッターに即反映。永続化はしない（実験中のその場調整用）。
//       一度決めた値をビルドに焼くには、確定値を各Inspectorの初期値へ手で書き写す。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CoThink
{
    public class SettingsPanel : MonoBehaviour
    {
        [Header("調整対象（未アサインは自動スキップ）")]
        [SerializeField] private BoardAnchorReceiver m_anchor;
        [SerializeField] private GoalLayerReceiver m_goal;
        [SerializeField] private PieceMeshReceiver m_pieceMesh;
        [SerializeField] private NextMoveAnimator m_nextMove;

        [Header("表示")]
        [Tooltip("パネルを追従させる頭のカメラ。未設定なら Camera.main。")]
        [SerializeField] private Camera m_headCamera;
        [Tooltip("カメラ前方どれだけの距離にパネルを出すか[m]")]
        [SerializeField] private float m_panelDistance = 0.6f;
        [Tooltip("起動時からパネルを開いておく")]
        [SerializeField] private bool m_startOpen = false;

        [Header("入力")]
        [Tooltip("値増減の連続変化レート[/秒]の係数（スティック倒し量に乗算）")]
        [SerializeField] private float m_repeatRate = 6f;
        [Tooltip("スティックのデッドゾーン")]
        [SerializeField] private float m_deadzone = 0.5f;

        // ---- 調整項目の定義 ----
        private class Item
        {
            public string label;
            public Func<float> get;
            public Action<float> set;
            public float step;      // 1操作あたりの増減量
            public float min, max;
            public string fmt;      // 表示フォーマット
        }

        private readonly List<Item> m_items = new List<Item>();
        private int m_cursor = 0;
        private bool m_open;

        // UI（自動生成）
        private Canvas m_canvas;
        private UnityEngine.UI.Text m_text;

        // 入力エッジ検出
        private bool m_prevUp, m_prevDown, m_prevB;
        private float m_holdTimer;

        private void Start()
        {
            if (m_headCamera == null) m_headCamera = Camera.main;
            BuildItems();
            BuildUI();
            m_open = m_startOpen;
            if (m_canvas != null) m_canvas.gameObject.SetActive(m_open);
        }

        private void BuildItems()
        {
            m_items.Clear();

            if (m_anchor != null)
            {
                // カメラ→目オフセット X/Y/Z（±0.005m ステップ）
                m_items.Add(new Item {
                    label = "Cam Offset X", step = 0.005f, min = -0.2f, max = 0.2f, fmt = "F3",
                    get = () => m_anchor.CamToEyeOffset.x,
                    set = v => { var o = m_anchor.CamToEyeOffset; o.x = v; m_anchor.CamToEyeOffset = o; } });
                m_items.Add(new Item {
                    label = "Cam Offset Y", step = 0.005f, min = -0.2f, max = 0.2f, fmt = "F3",
                    get = () => m_anchor.CamToEyeOffset.y,
                    set = v => { var o = m_anchor.CamToEyeOffset; o.y = v; m_anchor.CamToEyeOffset = o; } });
                m_items.Add(new Item {
                    label = "Cam Offset Z", step = 0.005f, min = -0.2f, max = 0.2f, fmt = "F3",
                    get = () => m_anchor.CamToEyeOffset.z,
                    set = v => { var o = m_anchor.CamToEyeOffset; o.z = v; m_anchor.CamToEyeOffset = o; } });
            }

            if (m_goal != null)
            {
                m_items.Add(new Item {
                    label = "Silhouette Ghost a", step = 0.05f, min = 0f, max = 1f, fmt = "F2",
                    get = () => m_goal.GhostAlpha, set = v => m_goal.GhostAlpha = v });
                m_items.Add(new Item {
                    label = "Silhouette Placed a", step = 0.05f, min = 0f, max = 1f, fmt = "F2",
                    get = () => m_goal.PlacedAlpha, set = v => m_goal.PlacedAlpha = v });
                m_items.Add(new Item {
                    label = "Silhouette Next a", step = 0.05f, min = 0f, max = 1f, fmt = "F2",
                    get = () => m_goal.NextAlphaMin, set = v => m_goal.NextAlphaMin = v });
            }

            if (m_pieceMesh != null)
            {
                m_items.Add(new Item {
                    label = "Piece STL a", step = 0.05f, min = 0f, max = 1f, fmt = "F2",
                    get = () => m_pieceMesh.Alpha, set = v => m_pieceMesh.Alpha = v });
            }

            if (m_nextMove != null)
            {
                m_items.Add(new Item {
                    label = "Anim Ghost a", step = 0.05f, min = 0f, max = 1f, fmt = "F2",
                    get = () => m_nextMove.Alpha, set = v => m_nextMove.Alpha = v });
            }
        }

        private void Update()
        {
            // --- Bボタンで開閉（エッジ検出） ---
            bool bNow = OVRInput.Get(OVRInput.Button.Two); // Two = B(右)/Y(左)。右手Bを想定。
            if (bNow && !m_prevB)
            {
                m_open = !m_open;
                if (m_canvas != null) m_canvas.gameObject.SetActive(m_open);
            }
            m_prevB = bNow;

            if (!m_open || m_items.Count == 0) { return; }

            Vector2 stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick); // 右スティック
            if (Mathf.Abs(stick.x) < m_deadzone && Mathf.Abs(stick.y) < m_deadzone)
                stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);       // フォールバック(左)

            // --- 上下: 項目選択（エッジ検出で1ステップずつ） ---
            bool up = stick.y > m_deadzone;
            bool down = stick.y < -m_deadzone;
            if (up && !m_prevUp) m_cursor = (m_cursor - 1 + m_items.Count) % m_items.Count;
            if (down && !m_prevDown) m_cursor = (m_cursor + 1) % m_items.Count;
            m_prevUp = up; m_prevDown = down;

            // --- 左右: 値増減（倒し続けで連続変化） ---
            if (Mathf.Abs(stick.x) > m_deadzone)
            {
                m_holdTimer += Time.deltaTime * m_repeatRate * Mathf.Abs(stick.x);
                if (m_holdTimer >= 1f)
                {
                    m_holdTimer = 0f;
                    var it = m_items[m_cursor];
                    float dir = Mathf.Sign(stick.x);
                    float v = Mathf.Clamp(it.get() + dir * it.step, it.min, it.max);
                    it.set(v);
                }
            }
            else m_holdTimer = 1f; // 次に倒した瞬間すぐ1回効かせる

            UpdateText();
            FollowHead();
        }

        private void UpdateText()
        {
            if (m_text == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("<b>SETTINGS</b>  (B:close  stick U/D:select  L/R:adjust)");
            sb.AppendLine("------------------------------");
            for (int i = 0; i < m_items.Count; i++)
            {
                var it = m_items[i];
                string cursor = (i == m_cursor) ? "> " : "  ";
                string val = it.get().ToString(it.fmt, System.Globalization.CultureInfo.InvariantCulture);
                if (i == m_cursor)
                    sb.AppendLine($"{cursor}<color=#FFD24A>{it.label}</color> : <color=#FFD24A>{val}</color>");
                else
                    sb.AppendLine($"{cursor}{it.label} : {val}");
            }
            m_text.text = sb.ToString();
        }

        private void FollowHead()
        {
            if (m_canvas == null || m_headCamera == null) return;
            var cam = m_headCamera.transform;
            m_canvas.transform.position = cam.position + cam.forward * m_panelDistance;
            m_canvas.transform.rotation = Quaternion.LookRotation(
                m_canvas.transform.position - cam.position, cam.up);
        }

        private void BuildUI()
        {
            // ワールド空間Canvasを自動生成
            var canvasGo = new GameObject("SettingsCanvas");
            canvasGo.transform.SetParent(transform, false);
            m_canvas = canvasGo.AddComponent<Canvas>();
            m_canvas.renderMode = RenderMode.WorldSpace;
            var scaler = canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;
            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            var rt = m_canvas.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(600, 420);
            rt.localScale = Vector3.one * 0.0006f; // ~0.36m x 0.25m の板

            // 背景
            var bgGo = new GameObject("BG");
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bg = bgGo.AddComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0f, 0f, 0f, 0.6f);
            var bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;

            // テキスト
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(canvasGo.transform, false);
            m_text = textGo.AddComponent<UnityEngine.UI.Text>();
            m_text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (m_text.font == null) m_text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            m_text.fontSize = 22;
            m_text.color = Color.white;
            m_text.supportRichText = true;
            m_text.alignment = TextAnchor.UpperLeft;
            var tRt = m_text.GetComponent<RectTransform>();
            tRt.anchorMin = Vector2.zero; tRt.anchorMax = Vector2.one;
            tRt.offsetMin = new Vector2(16, 16); tRt.offsetMax = new Vector2(-16, -16);

            UpdateText();
        }
    }
}
