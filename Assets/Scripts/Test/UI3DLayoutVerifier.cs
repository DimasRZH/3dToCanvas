using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UI3D;

namespace UI3DTest
{
    /// <summary>
    /// Shared bounds-comparison math for the 3D-to-UI stress test. The harness registers each
    /// (cell, character, reference Image) triple; RunAll() compares the character's on-screen
    /// content footprint against the cell rect the way a normal uGUI Image would lay out.
    ///
    /// Pass criteria are intentionally robust to the difference between the model's authored
    /// bounds (what the component fits) and the live silhouette AABB (what we measure):
    ///   - Center parity is the HARD assertion (this is what pivot/anchor/stretch/parent-scale
    ///     correctness actually affects, and what can regress).
    ///   - Size ratios are checked loosely (no gross overflow / no gross under-fill) and otherwise
    ///     reported as info. Rotated cells skip size checks (AABB of a rotated silhouette grows).
    /// Masking / alpha / fitter cells are registered as "findings" (manual/visual), not pass-fail.
    /// </summary>
    public class UI3DLayoutVerifier : MonoBehaviour
    {
        public class TestCell
        {
            public string category;
            public string name;
            public RectTransform cell;
            public UIBurstSkinnedCharacter character;
            public Image referenceImage;
            public UIBurstScaleMode mode;
            public bool checkSize = true;   // false for rotated parents
            public bool isFinding = false;  // masking/alpha/fitter: report, don't pass/fail
        }

        public struct CellResult
        {
            public string category;
            public string name;
            public bool passed;
            public bool isFinding;
            public float centerDeltaNorm;
            public float widthRatio;
            public float heightRatio;
            public string message;
        }

        [Tooltip("Normalized center offset (fraction of rect diagonal) allowed before FAIL. This is the precise check.")]
        public float m_CenterTolerance = 0.10f;
        [Tooltip("Gross-oversize guard: content larger than this multiple of the rect is a FAIL. Generous because Fill/MatchHeight legitimately overflow the free axis.")]
        public float m_MaxOversizeRatio = 2.5f;
        [Tooltip("Minimum footprint (content/rect, larger axis) to count as 'laid out'. Below this means the mesh was never skinned/positioned.")]
        public float m_MinLaidOutRatio = 0.05f;

        private readonly List<TestCell> m_Cells = new List<TestCell>();
        private Text m_ResultsText;
        private GameObject m_ResultsPanel;
        private static Font s_Font;

        private static Font GetFont()
        {
            if (s_Font != null) return s_Font;
            s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (s_Font == null) s_Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (s_Font == null) s_Font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "sans-serif" }, 16);
            return s_Font;
        }

        public void Register(TestCell c)
        {
            if (c != null && c.cell != null && c.character != null) m_Cells.Add(c);
        }

        public void ClearRegistrations() => m_Cells.Clear();

        // World-space AABB of the live skinned content across all parts of a character.
        public static bool TryGetCharacterWorldBounds(UIBurstSkinnedCharacter ch, out Bounds bounds)
        {
            bounds = default;
            if (ch == null) return false;
            var renderers = ch.GetComponentsInChildren<UIBurstSkinnedRenderer>(true);
            bool has = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!r.TryGetContentWorldBounds(out var b)) continue;
                if (!has) { min = b.min; max = b.max; has = true; }
                else { min = Vector3.Min(min, b.min); max = Vector3.Max(max, b.max); }
            }
            if (!has) return false;
            bounds = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }

        public CellResult Evaluate(TestCell c)
        {
            var res = new CellResult { category = c.category, name = c.name, isFinding = c.isFinding };

            if (!TryGetCharacterWorldBounds(c.character, out var content))
            {
                res.passed = false;
                res.message = "no content bounds (not initialized / zero verts)";
                return res;
            }

            // Cell rect in world space.
            var corners = new Vector3[4];
            c.cell.GetWorldCorners(corners); // 0=BL 1=TL 2=TR 3=BR
            Vector3 rectCenter = (corners[0] + corners[2]) * 0.5f;
            float rectW = Vector3.Distance(corners[0], corners[3]);
            float rectH = Vector3.Distance(corners[0], corners[1]);
            float rectDiag = Mathf.Max(1e-3f, new Vector2(rectW, rectH).magnitude);

            // Compare in the XY plane (Overlay canvas lies in world XY).
            Vector2 contentCenter = new Vector2(content.center.x, content.center.y);
            Vector2 rectCenter2 = new Vector2(rectCenter.x, rectCenter.y);
            res.centerDeltaNorm = Vector2.Distance(contentCenter, rectCenter2) / rectDiag;

            float cw = content.size.x, chgt = content.size.y;
            res.widthRatio = rectW > 1e-3f ? cw / rectW : 0f;
            res.heightRatio = rectH > 1e-3f ? chgt / rectH : 0f;

            if (c.isFinding)
            {
                res.passed = true; // not graded
                res.message = $"FINDING centerΔ={res.centerDeltaNorm:F3} w/h ratio={res.widthRatio:F2}/{res.heightRatio:F2} (judge visually)";
                return res;
            }

            // Center parity is the precise, mode-independent assertion (this is what anchors/pivot/
            // stretch/parent-scale/rotation correctness actually moves).
            bool centerOk = res.centerDeltaNorm <= m_CenterTolerance;

            float maxRatio = Mathf.Max(res.widthRatio, res.heightRatio);
            float minRatio = Mathf.Min(res.widthRatio, res.heightRatio);

            // Size is only a sanity guard: it must have been laid out (not a raw rest-pose mesh) and
            // not be wildly oversized. Per-mode fill ratios vary because the model's authored bounds
            // are wider than its silhouette, and Fill/MatchHeight legitimately overflow the free axis,
            // so exact fill is reported as info rather than graded.
            bool laidOut = maxRatio >= m_MinLaidOutRatio;
            bool notHuge = !c.checkSize || maxRatio <= m_MaxOversizeRatio;
            bool sizeOk = laidOut && notHuge;
            string sizeFlag = !laidOut ? "NOT-LAID-OUT" : (!notHuge ? "OVERSIZED" : "ok");

            res.passed = centerOk && sizeOk;
            res.message = $"center:{(centerOk ? "ok" : "OFF")}({res.centerDeltaNorm:F3}) size:{sizeFlag}({minRatio:F2}-{maxRatio:F2})";
            return res;
        }

        [HideInInspector] public string LastReport = "";

        // Only cells whose character is currently active+initialized (i.e. on the visible page and
        // skinned). Pages that are hidden never run LateUpdate, so their meshes are not laid out yet.
        public List<CellResult> EvaluateActive()
        {
            var results = new List<CellResult>();
            foreach (var c in m_Cells)
            {
                if (c.character == null || !c.character.isActiveAndEnabled) continue;
                results.Add(Evaluate(c));
            }
            return results;
        }

        // Evaluate just the active page and show/log it (used by the "Run Check" button).
        public string ReportActivePage()
        {
            var results = EvaluateActive();
            ShowResults(results);
            return BuildReportString(results);
        }

        // Called by the harness coroutine after it has walked every page.
        public string Report(List<CellResult> results)
        {
            ShowResults(results);
            string report = BuildReportString(results);
            LastReport = report;
            Debug.Log(report);
            return report;
        }

        public static string RunAllAndReport()
        {
            var v = FindAnyObjectByType<UI3DLayoutVerifier>();
            if (v == null) return "UI3DLayoutVerifier not found in scene.";
            var harness = FindAnyObjectByType<UI3DStressTestHarness>();
            if (harness != null)
            {
                v.LastReport = "";
                harness.BeginRunAllPages();
                return "Started full-page verification (walks every page). Read UI3DLayoutVerifier.LastReport in ~1s.";
            }
            // Fallback: just the active page.
            return v.ReportActivePage();
        }

        private static string BuildReportString(List<CellResult> results)
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0, findings = 0;
            string lastCat = null;
            foreach (var r in results)
            {
                if (r.category != lastCat) { sb.AppendLine($"== {r.category} =="); lastCat = r.category; }
                if (r.isFinding) { findings++; sb.AppendLine($"  [FINDING] {r.name}: {r.message}"); }
                else if (r.passed) { pass++; sb.AppendLine($"  [PASS] {r.name}: {r.message}"); }
                else { fail++; sb.AppendLine($"  [FAIL] {r.name}: {r.message}"); }
            }
            sb.Insert(0, $"UI3D Layout Verification: {pass} pass, {fail} fail, {findings} findings (n={results.Count})\n");
            return sb.ToString();
        }

        private void ShowResults(List<CellResult> results)
        {
            EnsureResultsPanel();
            var sb = new StringBuilder();
            int pass = 0, fail = 0, find = 0;
            foreach (var r in results)
            {
                if (r.isFinding) find++; else if (r.passed) pass++; else fail++;
            }
            sb.AppendLine($"<b>{pass} PASS  {fail} FAIL  {find} FINDING</b>");
            string lastCat = null;
            foreach (var r in results)
            {
                if (r.category != lastCat) { sb.AppendLine($"<b>{r.category}</b>"); lastCat = r.category; }
                string tag = r.isFinding ? "~" : (r.passed ? "<color=#5f5>P</color>" : "<color=#f55>F</color>");
                sb.AppendLine($" {tag} {r.name} {r.message}");
            }
            if (m_ResultsText != null) m_ResultsText.text = sb.ToString();
            if (m_ResultsPanel != null) m_ResultsPanel.SetActive(true);
        }

        public void ToggleResultsPanel()
        {
            if (m_ResultsPanel != null) m_ResultsPanel.SetActive(!m_ResultsPanel.activeSelf);
        }

        private void EnsureResultsPanel()
        {
            if (m_ResultsPanel != null) return;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) return;

            m_ResultsPanel = new GameObject("ResultsPanel", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)m_ResultsPanel.transform;
            rt.SetParent(canvas.transform, false);
            rt.anchorMin = new Vector2(0.62f, 0.05f);
            rt.anchorMax = new Vector2(0.98f, 0.85f);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            m_ResultsPanel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var trt = (RectTransform)textGo.transform;
            trt.SetParent(rt, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8, 8); trt.offsetMax = new Vector2(-8, -8);
            m_ResultsText = textGo.GetComponent<Text>();
            m_ResultsText.font = GetFont();
            m_ResultsText.fontSize = 14;
            m_ResultsText.color = Color.white;
            m_ResultsText.alignment = TextAnchor.UpperLeft;
            m_ResultsText.supportRichText = true;
            m_ResultsText.horizontalOverflow = HorizontalWrapMode.Wrap;
            m_ResultsText.verticalOverflow = VerticalWrapMode.Overflow;
        }
    }
}
