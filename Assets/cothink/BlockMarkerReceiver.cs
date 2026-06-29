// BlockMarkerReceiver.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoThink
{
    [Serializable] internal class DetItem { public string n; public float x; public float y; }
    [Serializable] internal class DetMsg  { public string type; public int frameId; public DetItem[] items; }

    public class BlockMarkerReceiver : MonoBehaviour
    {
        [SerializeField] private BoardAnchorReceiver m_anchor;
        [SerializeField] private bool m_flipY = true;
        [SerializeField] private float m_markerSize = 0.02f;

        private readonly List<GameObject> m_pool = new List<GameObject>();

        private static readonly Dictionary<string, Color> COLORS = new Dictionary<string, Color>
        {
            { "I", Color.red }, { "O", Color.green }, { "T", new Color(0.2f,0.4f,1f) },
            { "S", Color.yellow }, { "Z", Color.magenta }, { "J", new Color(1f,0.6f,0f) },
            { "L", Color.cyan },
        };

        private void OnEnable()
        {
            if (m_anchor != null) m_anchor.OnDetectionsJson += OnDetections;
        }

        private void OnDisable()
        {
            if (m_anchor != null) m_anchor.OnDetectionsJson -= OnDetections;
        }

        private void OnDetections(string json)
        {
            var root = (m_anchor != null) ? m_anchor.BoardRoot : null;
            if (root == null) return;

            DetMsg msg;
            try { msg = JsonUtility.FromJson<DetMsg>(json); }
            catch { return; }
            if (msg == null || msg.items == null) { Hide(0); return; }

            int n = msg.items.Length;
            EnsurePool(n);

            for (int i = 0; i < n; i++)
            {
                var it = msg.items[i];
                var go = m_pool[i];
                go.transform.SetParent(root, false);
                float y = m_flipY ? -it.y : it.y;
                go.transform.localPosition = new Vector3(it.x, y, 0f);
                go.transform.localScale = Vector3.one * m_markerSize;
                Color c = COLORS.TryGetValue(it.n ?? "", out var col) ? col : Color.white;
                BoardAnchorReceiver.Paint(go, c);
                go.SetActive(true);
            }
            Hide(n);
        }

        private void EnsurePool(int n)
        {
            while (m_pool.Count < n)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                go.name = "BlockMarker_" + m_pool.Count;
                go.SetActive(false);
                m_pool.Add(go);
            }
        }

        private void Hide(int from)
        {
            for (int i = from; i < m_pool.Count; i++)
                if (m_pool[i].activeSelf) m_pool[i].SetActive(false);
        }
    }
}