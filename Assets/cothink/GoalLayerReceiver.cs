// GoalLayerReceiver.cs
// Draws the static goal silhouette: one semi-transparent flat slab per solution
// cell, as a child of the board anchor. Step 1 of Week 3 (positioning check).
// Set m_flipY to the SAME value as BlockMarkerReceiver.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoThink
{
    [Serializable] internal class SolCell { public int r, c, bid; public string name; public float x, y, w, h; }
    [Serializable] internal class SolMsg  { public string type; public int gw, gh; public SolCell[] cells; public int[] order; }

    public class GoalLayerReceiver : MonoBehaviour
    {
        [SerializeField] private BoardAnchorReceiver m_anchor;
        [Tooltip("Match BlockMarkerReceiver's Flip Y.")]
        [SerializeField] private bool m_flipY = true;
        [SerializeField] private Color m_color = new Color(0.6f, 0.6f, 0.6f, 0.4f);
        [SerializeField] private float m_thickness = 0.002f;
        [SerializeField] private float m_zOffset = 0.0f;

        private readonly List<GameObject> m_pool = new List<GameObject>();

        private void OnEnable()  { if (m_anchor != null) m_anchor.OnSolutionJson += OnSolution; }
        private void OnDisable() { if (m_anchor != null) m_anchor.OnSolutionJson -= OnSolution; }

        private void OnSolution(string json)
        {
            var root = (m_anchor != null) ? m_anchor.BoardRoot : null;
            if (root == null) return;
            SolMsg msg;
            try { msg = JsonUtility.FromJson<SolMsg>(json); } catch { return; }
            if (msg == null || msg.cells == null) return;

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
                SetTransparent(go, m_color);
                go.SetActive(true);
            }
            Hide(n);
            Debug.Log($"GoalLayer: drew {n} cells (gw={msg.gw}, gh={msg.gh})");
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

        private static void SetTransparent(GameObject go, Color color)
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
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
        }
    }
}
