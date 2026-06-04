using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UI3D;

namespace UI3DTest
{
    /// <summary>
    /// Builds the entire 3D-to-UI stress test UI at runtime (identical in editor and on Android).
    /// Pages cover: Anchors, Pivot, Stretch, ScaleModes, Parent scale/rotation, CanvasScaler+nested,
    /// LayoutGroups, Masking/alpha, and a Live page with sliders. Each cell holds a translucent
    /// reference uGUI Image (ground truth) plus a UIBurstSkinnedCharacter in the same rect, so the
    /// 3D should sit inside the outlined box. Registers cells with UI3DLayoutVerifier for PASS/FAIL.
    /// </summary>
    [RequireComponent(typeof(UI3DLayoutVerifier))]
    public class UI3DStressTestHarness : MonoBehaviour
    {
        [Header("Inputs")]
        [Tooltip("Inactive model prefab root with an Animator. Harness adds RectTransform + UIBurstSkinnedCharacter.")]
        public GameObject m_CharacterPrefab;
        public Shader m_UiShader;
        public Material m_CustomUiMaterial;
        [Tooltip("Shader used to clear depth after each character. Defaults to 'UI/DepthClear'; assign to guarantee it survives the build.")]
        public Shader m_DepthClearShader;
        public Font m_Font;

        [Header("Look")]
        [Tooltip("Extra Y rotation applied ON TOP of the prefab's own authored facing. The prefab itself should be oriented to face the camera; leave this at 0 unless you need a per-page tweak.")]
        public float m_ModelYRotation = 0f;
        public Color m_RefImageColor = new Color(0.25f, 0.7f, 1f, 0.22f);
        public Color m_BorderColor = new Color(1f, 1f, 0.2f, 0.9f);
        public Color m_CellLabelColor = Color.white;

        private Canvas m_Canvas;
        private CanvasScaler m_Scaler;
        private UI3DLayoutVerifier m_Verifier;
        private RectTransform m_PageArea;
        private Text m_TitleText;
        private bool m_Frozen;

        private readonly List<RectTransform> m_Pages = new List<RectTransform>();
        private readonly List<string> m_PageNames = new List<string>();
        private readonly List<Animator> m_Animators = new List<Animator>();
        private int m_Current = 0;

        private static Font s_Font;

        // ---------------------------------------------------------------- lifecycle

        private void Awake()
        {
            if (m_Font != null) s_Font = m_Font;
        }

        private void Start()
        {
            m_Verifier = GetComponent<UI3DLayoutVerifier>();
            m_Canvas = GetComponentInParent<Canvas>();
            if (m_Canvas == null) m_Canvas = FindAnyObjectByType<Canvas>();
            m_Scaler = m_Canvas != null ? m_Canvas.GetComponent<CanvasScaler>() : null;

            if (m_CharacterPrefab == null)
            {
                Debug.LogError("[UI3DStressTestHarness] No character prefab assigned.");
                return;
            }

            BuildTopBar();
            BuildPages();
            ShowPage(0);
        }

        public static Font GetUiFont()
        {
            if (s_Font != null) return s_Font;
            s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (s_Font == null) s_Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (s_Font == null) s_Font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "sans-serif" }, 16);
            return s_Font;
        }

        // ---------------------------------------------------------------- top bar

        private void BuildTopBar()
        {
            var bar = MakeRect("TopBar", m_Canvas.transform as RectTransform);
            bar.anchorMin = new Vector2(0, 1); bar.anchorMax = new Vector2(1, 1);
            bar.pivot = new Vector2(0.5f, 1f);
            bar.sizeDelta = new Vector2(0, 120);
            bar.anchoredPosition = Vector2.zero;
            AddImage(bar.gameObject, new Color(0, 0, 0, 0.55f));

            MakeButton(bar, "< Prev", new Vector2(0, 0.5f), new Vector2(10, 0), new Vector2(150, 90), () => Step(-1));
            MakeButton(bar, "Next >", new Vector2(0, 0.5f), new Vector2(170, 0), new Vector2(150, 90), () => Step(1));

            m_TitleText = MakeText(bar, "", 30, TextAnchor.MiddleCenter);
            var trt = (RectTransform)m_TitleText.transform;
            trt.anchorMin = new Vector2(0.5f, 0.5f); trt.anchorMax = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(520, 90); trt.anchoredPosition = new Vector2(40, 0);

            MakeButton(bar, "Freeze", new Vector2(1, 0.5f), new Vector2(-490, 0), new Vector2(150, 90), ToggleFreeze);
            MakeButton(bar, "Check Page", new Vector2(1, 0.5f), new Vector2(-330, 0), new Vector2(150, 90),
                () => m_Verifier.ReportActivePage());
            MakeButton(bar, "Run ALL", new Vector2(1, 0.5f), new Vector2(-170, 0), new Vector2(150, 90),
                BeginRunAllPages);
            MakeButton(bar, "Results", new Vector2(1, 0.5f), new Vector2(-10, 0), new Vector2(150, 90),
                () => m_Verifier.ToggleResultsPanel());

            // Page area = everything below the bar.
            m_PageArea = MakeRect("PageArea", m_Canvas.transform as RectTransform);
            m_PageArea.anchorMin = new Vector2(0, 0); m_PageArea.anchorMax = new Vector2(1, 1);
            m_PageArea.offsetMin = new Vector2(0, 0); m_PageArea.offsetMax = new Vector2(0, -120);
        }

        private void Step(int dir)
        {
            int next = Mathf.Clamp(m_Current + dir, 0, m_Pages.Count - 1);
            if (next != m_Current) ShowPage(next);
        }

        private void ShowPage(int idx)
        {
            m_Current = idx;
            for (int i = 0; i < m_Pages.Count; i++) m_Pages[i].gameObject.SetActive(i == idx);
            if (m_TitleText != null) m_TitleText.text = $"{idx + 1}/{m_Pages.Count}  {m_PageNames[idx]}";
            ApplyFreeze();
        }

        private void ToggleFreeze() { m_Frozen = !m_Frozen; ApplyFreeze(); }

        // Walk every page, let the skinning pipeline produce a frame, then evaluate that page's cells.
        // Aggregated results are logged and stored in UI3DLayoutVerifier.LastReport (read by MCP).
        public void BeginRunAllPages()
        {
            StopAllCoroutines();
            StartCoroutine(RunAllPagesRoutine());
        }

        private System.Collections.IEnumerator RunAllPagesRoutine()
        {
            int startPage = m_Current;
            var all = new List<UI3DLayoutVerifier.CellResult>();
            for (int i = 0; i < m_Pages.Count; i++)
            {
                ShowPage(i);
                // Wait for the pipeline: skinning is scheduled one frame, completed/uploaded the next.
                yield return null; yield return null; yield return null;
                all.AddRange(m_Verifier.EvaluateActive());
            }
            m_Verifier.Report(all);
            ShowPage(startPage);
        }

        private void ApplyFreeze()
        {
            foreach (var a in m_Animators) if (a != null) a.speed = m_Frozen ? 0f : 1f;
        }

        // ---------------------------------------------------------------- pages

        private RectTransform NewPage(string name)
        {
            var p = MakeRect("Page_" + name, m_PageArea);
            p.anchorMin = Vector2.zero; p.anchorMax = Vector2.one;
            p.offsetMin = Vector2.zero; p.offsetMax = Vector2.zero;
            m_Pages.Add(p); m_PageNames.Add(name);
            return p;
        }

        private void BuildPages()
        {
            BuildAnchorsPage();
            BuildPivotPage();
            BuildStretchPage();
            BuildScaleModesPage();
            BuildParentPage();
            BuildCanvasScalerPage();
            BuildLayoutGroupsPage();
            BuildMaskingPage();
            BuildScrollViewPage();
            BuildUiControlsPage();
            BuildDragMaskPage();
            BuildLivePage();
        }

        private void BuildAnchorsPage()
        {
            var page = NewPage("Anchors");
            (string n, Vector2 a)[] presets =
            {
                ("BL", new Vector2(0,0)), ("BC", new Vector2(0.5f,0)), ("BR", new Vector2(1,0)),
                ("ML", new Vector2(0,0.5f)), ("MC", new Vector2(0.5f,0.5f)), ("MR", new Vector2(1,0.5f)),
                ("TL", new Vector2(0,1)), ("TC", new Vector2(0.5f,1)), ("TR", new Vector2(1,1)),
            };
            foreach (var p in presets)
            {
                var cell = MakeCell(page, "anc_" + p.n);
                cell.anchorMin = p.a; cell.anchorMax = p.a; cell.pivot = p.a;
                cell.sizeDelta = new Vector2(230, 300);
                cell.anchoredPosition = OffsetForAnchor(p.a, 8);
                Populate(cell, "Anchor " + p.n, UIBurstScaleMode.Fit, "Anchors", true, true, false);
            }
        }

        private void BuildPivotPage()
        {
            var page = NewPage("Pivot");
            (string n, Vector2 piv)[] pivots =
            {
                ("0,0", new Vector2(0,0)), ("1,1", new Vector2(1,1)), ("0,1", new Vector2(0,1)),
                ("1,0", new Vector2(1,0)), ("0.5,0.5", new Vector2(0.5f,0.5f)),
            };
            float x = -880;
            foreach (var p in pivots)
            {
                var cell = MakeCell(page, "piv_" + p.n);
                cell.anchorMin = new Vector2(0.5f, 0.5f); cell.anchorMax = new Vector2(0.5f, 0.5f);
                cell.pivot = p.piv;
                cell.sizeDelta = new Vector2(200, 300);
                cell.anchoredPosition = new Vector2(x, 0);
                x += 450;
                Populate(cell, "Pivot " + p.n, UIBurstScaleMode.Fit, "Pivot", true, true, false);
            }
        }

        private void BuildStretchPage()
        {
            var page = NewPage("Stretch");
            // Horizontal stretch
            var h = MakeCell(page, "stretchH");
            h.anchorMin = new Vector2(0, 0.72f); h.anchorMax = new Vector2(1, 0.72f); h.pivot = new Vector2(0.5f, 0.5f);
            h.offsetMin = new Vector2(40, -130); h.offsetMax = new Vector2(-40, 130);
            Populate(h, "H-Stretch", UIBurstScaleMode.MatchHeight, "Stretch", true, true, false);
            // Vertical stretch
            var v = MakeCell(page, "stretchV");
            v.anchorMin = new Vector2(0.15f, 0.05f); v.anchorMax = new Vector2(0.15f, 0.55f); v.pivot = new Vector2(0.5f, 0.5f);
            v.offsetMin = new Vector2(-150, 10); v.offsetMax = new Vector2(150, -10);
            Populate(v, "V-Stretch", UIBurstScaleMode.MatchWidth, "Stretch", true, true, false);
            // Full stretch + animated resize
            var f = MakeCell(page, "stretchFull");
            f.anchorMin = new Vector2(0.55f, 0.05f); f.anchorMax = new Vector2(0.95f, 0.55f);
            f.offsetMin = Vector2.zero; f.offsetMax = Vector2.zero;
            var ch = Populate(f, "Full+LiveResize", UIBurstScaleMode.Stretch, "Stretch", true, true, false);
            var driver = f.gameObject.AddComponent<LiveResizeDriver>();
            driver.target = f;
        }

        private void BuildScaleModesPage()
        {
            var page = NewPage("ScaleModes");
            UIBurstScaleMode[] modes =
            {
                UIBurstScaleMode.None, UIBurstScaleMode.MatchWidth, UIBurstScaleMode.MatchHeight,
                UIBurstScaleMode.Fit, UIBurstScaleMode.Fill, UIBurstScaleMode.Stretch,
            };
            int cols = 3, rows = 2; int i = 0;
            foreach (var m in modes)
            {
                int cx = i % cols, cy = i / cols; i++;
                var cell = GridCell(page, "mode_" + m, cols, rows, cx, cy, 14);
                bool grade = m != UIBurstScaleMode.None;
                Populate(cell, m.ToString(), m, "ScaleModes", grade, grade, false);
            }
        }

        private void BuildParentPage()
        {
            var page = NewPage("ParentXform");
            // scale 0.5, scale 2, rotate 30, rotate -20 + scale 1.3
            BuildParentCell(page, "p_scale0.5", new Vector2(-700, 250), 0.5f, 0f, false);
            BuildParentCell(page, "p_scale2", new Vector2(0, 250), 2.0f, 0f, false);
            BuildParentCell(page, "p_rot30", new Vector2(-700, -350), 1.0f, 30f, true);
            BuildParentCell(page, "p_rot-20s", new Vector2(0, -350), 1.3f, -20f, true);
        }

        private void BuildParentCell(RectTransform page, string id, Vector2 pos, float scale, float zRot, bool rotated)
        {
            // A parent wrapper rect that we scale/rotate; the cell sits inside it.
            var wrapper = MakeRect(id + "_wrap", page);
            wrapper.anchorMin = new Vector2(0.5f, 0.5f); wrapper.anchorMax = new Vector2(0.5f, 0.5f);
            wrapper.pivot = new Vector2(0.5f, 0.5f);
            wrapper.sizeDelta = new Vector2(260, 340);
            wrapper.anchoredPosition = pos;
            wrapper.localScale = new Vector3(scale, scale, 1f);
            wrapper.localRotation = Quaternion.Euler(0, 0, zRot);

            var cell = MakeCell(wrapper, id);
            cell.anchorMin = Vector2.zero; cell.anchorMax = Vector2.one;
            cell.offsetMin = Vector2.zero; cell.offsetMax = Vector2.zero;
            string lbl = $"scale {scale}x rot {zRot}°";
            Populate(cell, lbl, UIBurstScaleMode.Fit, "ParentXform", true, !rotated, false);
        }

        private void BuildCanvasScalerPage()
        {
            var page = NewPage("CanvasScaler+Nested");
            // Toggle button for the root CanvasScaler mode.
            MakeButton(page, "Toggle CanvasScaler Mode", new Vector2(0.5f, 1f), new Vector2(0, -20),
                new Vector2(520, 80), ToggleScalerMode);

            // Nested child canvas under an offset parent rect.
            var offsetParent = MakeRect("offsetParent", page);
            offsetParent.anchorMin = new Vector2(0.5f, 0.5f); offsetParent.anchorMax = new Vector2(0.5f, 0.5f);
            offsetParent.pivot = new Vector2(0.5f, 0.5f);
            offsetParent.sizeDelta = new Vector2(500, 600);
            offsetParent.anchoredPosition = new Vector2(-260, -40);

            var nestedCanvasGo = new GameObject("NestedCanvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            var ncrt = (RectTransform)nestedCanvasGo.transform;
            ncrt.SetParent(offsetParent, false);
            ncrt.anchorMin = Vector2.zero; ncrt.anchorMax = Vector2.one;
            ncrt.offsetMin = new Vector2(20, 20); ncrt.offsetMax = new Vector2(-20, -20);

            var cell = MakeCell(ncrt, "nested_cell");
            cell.anchorMin = Vector2.zero; cell.anchorMax = Vector2.one;
            cell.offsetMin = Vector2.zero; cell.offsetMax = Vector2.zero;
            Populate(cell, "Nested Canvas", UIBurstScaleMode.Fit, "CanvasScaler", true, true, false);

            // A plain cell on the right that reacts to scaler-mode changes for comparison.
            var cmp = MakeCell(page, "scaler_cmp");
            cmp.anchorMin = new Vector2(0.78f, 0.5f); cmp.anchorMax = new Vector2(0.78f, 0.5f);
            cmp.pivot = new Vector2(0.5f, 0.5f); cmp.sizeDelta = new Vector2(360, 520);
            cmp.anchoredPosition = new Vector2(0, -40);
            Populate(cmp, "Root Canvas cell", UIBurstScaleMode.Fit, "CanvasScaler", true, true, false);
        }

        private void ToggleScalerMode()
        {
            if (m_Scaler == null) return;
            m_Scaler.uiScaleMode = m_Scaler.uiScaleMode == CanvasScaler.ScaleMode.ConstantPixelSize
                ? CanvasScaler.ScaleMode.ScaleWithScreenSize
                : CanvasScaler.ScaleMode.ConstantPixelSize;
        }

        private void BuildLayoutGroupsPage()
        {
            var page = NewPage("LayoutGroups");

            // Horizontal layout group
            var hRow = MakeRect("hgroup", page);
            hRow.anchorMin = new Vector2(0.05f, 0.7f); hRow.anchorMax = new Vector2(0.95f, 0.95f);
            hRow.offsetMin = Vector2.zero; hRow.offsetMax = Vector2.zero;
            var hg = hRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hg.childControlWidth = true; hg.childControlHeight = true; hg.childForceExpandWidth = true; hg.childForceExpandHeight = true;
            hg.spacing = 10; hg.padding = new RectOffset(10, 10, 10, 10);
            for (int i = 0; i < 3; i++)
            {
                var cell = MakeCell(hRow, "hg_" + i);
                Populate(cell, "H" + i, UIBurstScaleMode.Fit, "LayoutGroups", true, true, false);
            }

            // Vertical layout group
            var vCol = MakeRect("vgroup", page);
            vCol.anchorMin = new Vector2(0.05f, 0.05f); vCol.anchorMax = new Vector2(0.35f, 0.65f);
            vCol.offsetMin = Vector2.zero; vCol.offsetMax = Vector2.zero;
            var vg = vCol.gameObject.AddComponent<VerticalLayoutGroup>();
            vg.childControlWidth = true; vg.childControlHeight = true; vg.childForceExpandWidth = true; vg.childForceExpandHeight = true;
            vg.spacing = 10; vg.padding = new RectOffset(10, 10, 10, 10);
            for (int i = 0; i < 2; i++)
            {
                var cell = MakeCell(vCol, "vg_" + i);
                Populate(cell, "V" + i, UIBurstScaleMode.Fit, "LayoutGroups", true, true, false);
            }

            // Grid layout group
            var grid = MakeRect("grid", page);
            grid.anchorMin = new Vector2(0.4f, 0.05f); grid.anchorMax = new Vector2(0.95f, 0.65f);
            grid.offsetMin = Vector2.zero; grid.offsetMax = Vector2.zero;
            var gl = grid.gameObject.AddComponent<GridLayoutGroup>();
            gl.cellSize = new Vector2(180, 240); gl.spacing = new Vector2(10, 10); gl.padding = new RectOffset(10, 10, 10, 10);
            for (int i = 0; i < 4; i++)
            {
                var cell = MakeCell(grid, "gl_" + i);
                Populate(cell, "G" + i, UIBurstScaleMode.Fit, "LayoutGroups", true, true, false);
            }
        }

        private void BuildMaskingPage()
        {
            var page = NewPage("Masking+Alpha");

            // RectMask2D clipping a character that overflows it.
            var mask = MakeRect("rectmask", page);
            mask.anchorMin = new Vector2(0.05f, 0.45f); mask.anchorMax = new Vector2(0.45f, 0.95f);
            mask.offsetMin = Vector2.zero; mask.offsetMax = Vector2.zero;
            AddImage(mask.gameObject, new Color(0.3f, 0.1f, 0.1f, 0.35f));
            mask.gameObject.AddComponent<RectMask2D>();
            var inMask = MakeCell(mask, "masked");
            inMask.anchorMin = new Vector2(0.5f, 0.5f); inMask.anchorMax = new Vector2(0.5f, 0.5f);
            inMask.pivot = new Vector2(0.5f, 0.5f); inMask.sizeDelta = new Vector2(300, 600);
            inMask.anchoredPosition = new Vector2(0, 120); // pushed up so top overflows the mask
            Populate(inMask, "RectMask2D (clip?)", UIBurstScaleMode.MatchWidth, "Masking", false, true, true);

            // CanvasGroup alpha cell + slider.
            var alphaCell = MakeCell(page, "alpha");
            alphaCell.anchorMin = new Vector2(0.55f, 0.5f); alphaCell.anchorMax = new Vector2(0.55f, 0.5f);
            alphaCell.pivot = new Vector2(0.5f, 0.5f); alphaCell.sizeDelta = new Vector2(360, 520);
            alphaCell.anchoredPosition = new Vector2(0, 60);
            var cg = alphaCell.gameObject.AddComponent<CanvasGroup>();
            Populate(alphaCell, "CanvasGroup alpha (fade?)", UIBurstScaleMode.Fit, "Masking", false, true, true);
            MakeSlider(page, new Vector2(0.55f, 0.5f), new Vector2(0, -240), new Vector2(360, 50), 0f, 1f, 1f,
                v => cg.alpha = v);
        }

        private void BuildScrollViewPage()
        {
            var page = NewPage("ScrollView");

            // Vertical scrolling roster (left).
            var vHdr = MakeText(page, "Vertical roster — drag to scroll, watch clipping at edges", 20, TextAnchor.UpperLeft);
            var vh = (RectTransform)vHdr.transform;
            vh.anchorMin = new Vector2(0.04f, 0.95f); vh.anchorMax = new Vector2(0.5f, 0.99f);
            vh.offsetMin = Vector2.zero; vh.offsetMax = Vector2.zero;

            MakeScrollRect(page, new Vector2(0.04f, 0.06f), new Vector2(0.49f, 0.94f),
                Vector2.zero, Vector2.zero, false, out var vContent);
            string[] names = { "Aoi", "Haru", "Mei", "Ren", "Sora", "Yuki", "Kaze" };
            for (int i = 0; i < names.Length; i++)
                AddCharacterCard(vContent, names[i], new Vector2(0, 320), false);

            // Horizontal carousel (right).
            var hHdr = MakeText(page, "Horizontal carousel — swipe sideways", 20, TextAnchor.UpperLeft);
            var hh = (RectTransform)hHdr.transform;
            hh.anchorMin = new Vector2(0.51f, 0.95f); hh.anchorMax = new Vector2(0.96f, 0.99f);
            hh.offsetMin = Vector2.zero; hh.offsetMax = Vector2.zero;

            MakeScrollRect(page, new Vector2(0.51f, 0.5f), new Vector2(0.96f, 0.94f),
                Vector2.zero, Vector2.zero, true, out var hContent);
            for (int i = 0; i < 6; i++)
                AddCharacterCard(hContent, "Slot " + (i + 1), new Vector2(240, 0), true);

            // A note panel explaining what to look for.
            var note = MakeText(page,
                "If the 3D follows UI rules: cards clip cleanly at the scroll viewport edges,\n" +
                "scale to each card via layout, and move 1:1 with the scrolling content.",
                18, TextAnchor.UpperLeft);
            var nrt = (RectTransform)note.transform;
            nrt.anchorMin = new Vector2(0.51f, 0.06f); nrt.anchorMax = new Vector2(0.96f, 0.46f);
            nrt.offsetMin = Vector2.zero; nrt.offsetMax = Vector2.zero;
        }

        private ScrollRect MakeScrollRect(RectTransform parent, Vector2 aMin, Vector2 aMax,
            Vector2 offMin, Vector2 offMax, bool horizontal, out RectTransform content)
        {
            var root = MakeRect("Scroll", parent);
            root.anchorMin = aMin; root.anchorMax = aMax; root.offsetMin = offMin; root.offsetMax = offMax;
            AddImage(root.gameObject, new Color(0, 0, 0, 0.30f));
            var sr = root.gameObject.AddComponent<ScrollRect>();

            var viewport = MakeRect("Viewport", root);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero; viewport.offsetMax = Vector2.zero;
            viewport.pivot = new Vector2(0, 1);
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.color = new Color(1, 1, 1, 0.02f); // near-invisible graphic so the mask has something to clip against
            viewport.gameObject.AddComponent<RectMask2D>();

            content = MakeRect("Content", viewport);
            if (horizontal)
            {
                content.anchorMin = new Vector2(0, 0); content.anchorMax = new Vector2(0, 1); content.pivot = new Vector2(0, 0.5f);
                var hg = content.gameObject.AddComponent<HorizontalLayoutGroup>();
                hg.childControlWidth = true; hg.childControlHeight = true;
                hg.childForceExpandWidth = false; hg.childForceExpandHeight = true;
                hg.spacing = 12; hg.padding = new RectOffset(12, 12, 12, 12);
                var fit = content.gameObject.AddComponent<ContentSizeFitter>();
                fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            else
            {
                content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
                var vg = content.gameObject.AddComponent<VerticalLayoutGroup>();
                vg.childControlWidth = true; vg.childControlHeight = true;
                vg.childForceExpandWidth = true; vg.childForceExpandHeight = false;
                vg.spacing = 12; vg.padding = new RectOffset(12, 12, 12, 12);
                var fit = content.gameObject.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            sr.viewport = viewport; sr.content = content;
            sr.horizontal = horizontal; sr.vertical = !horizontal;
            sr.movementType = ScrollRect.MovementType.Elastic;
            sr.scrollSensitivity = 25f;

            // Real scrollbar on the side / bottom, with the viewport inset so it doesn't overlap content.
            const float barSize = 20f;
            if (!horizontal)
            {
                var bar = MakeScrollbar(root, true);
                var brt = (RectTransform)bar.transform;
                brt.anchorMin = new Vector2(1, 0); brt.anchorMax = new Vector2(1, 1); brt.pivot = new Vector2(1, 0.5f);
                brt.sizeDelta = new Vector2(barSize, 0); brt.anchoredPosition = Vector2.zero;
                sr.verticalScrollbar = bar;
                sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                viewport.offsetMax = new Vector2(-barSize, 0);
            }
            else
            {
                var bar = MakeScrollbar(root, false);
                var brt = (RectTransform)bar.transform;
                brt.anchorMin = new Vector2(0, 0); brt.anchorMax = new Vector2(1, 0); brt.pivot = new Vector2(0.5f, 0);
                brt.sizeDelta = new Vector2(0, barSize); brt.anchoredPosition = Vector2.zero;
                sr.horizontalScrollbar = bar;
                sr.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                viewport.offsetMin = new Vector2(0, barSize);
            }
            return sr;
        }

        private Scrollbar MakeScrollbar(RectTransform parent, bool vertical)
        {
            var go = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(1, 1, 1, 0.12f);

            var area = MakeRect("SlidingArea", rt);
            area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(2, 2); area.offsetMax = new Vector2(-2, -2);

            var handle = MakeRect("Handle", area);
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.color = new Color(0.6f, 0.7f, 0.9f, 0.9f);
            // Default fills; Scrollbar will resize via its size value.
            handle.anchorMin = Vector2.zero; handle.anchorMax = Vector2.one;
            handle.offsetMin = Vector2.zero; handle.offsetMax = Vector2.zero;

            var sb = go.GetComponent<Scrollbar>();
            sb.handleRect = handle; sb.targetGraphic = handleImg;
            sb.direction = vertical ? Scrollbar.Direction.BottomToTop : Scrollbar.Direction.LeftToRight;
            return sb;
        }

        private void AddCharacterCard(RectTransform parent, string name, Vector2 preferred, bool horizontal)
        {
            var card = MakeRect("Card_" + name, parent);
            var le = card.gameObject.AddComponent<LayoutElement>();
            if (horizontal) le.preferredWidth = preferred.x; else le.preferredHeight = preferred.y;
            AddImage(card.gameObject, new Color(0.15f, 0.17f, 0.22f, 0.92f));

            var refr = MakeRect("Ref", card);
            refr.anchorMin = Vector2.zero; refr.anchorMax = Vector2.one; refr.offsetMin = Vector2.zero; refr.offsetMax = Vector2.zero;
            refr.gameObject.AddComponent<Image>().color = m_RefImageColor;
            AddBorder(card, m_BorderColor, 2f);

            SpawnCharacter(card, UIBurstScaleMode.Fit);

            var lbl = MakeText(card, name, 20, TextAnchor.LowerCenter);
            var lrt = (RectTransform)lbl.transform;
            lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0); lrt.pivot = new Vector2(0.5f, 0);
            lrt.sizeDelta = new Vector2(0, 34); lrt.anchoredPosition = new Vector2(0, 4);
        }

        private void BuildUiControlsPage()
        {
            var page = NewPage("UI Controls");
            var res = default(DefaultControls.Resources); // all sprites null -> plain but functional

            float lx = 40f, w = 460f, y = -20f;
            System.Func<GameObject, float, float> place = (go, h) =>
            {
                var rt = (RectTransform)go.transform;
                rt.SetParent(page, false);
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(lx, y); rt.sizeDelta = new Vector2(w, h);
                SetFonts(go);
                y -= h + 24f;
                return y;
            };

            // Button (wired to Freeze so it visibly does something)
            var btn = DefaultControls.CreateButton(res);
            var btnText = btn.GetComponentInChildren<Text>(); if (btnText != null) { btnText.text = "Button (toggles Freeze)"; btnText.color = Color.black; }
            btn.GetComponent<Button>().onClick.AddListener(ToggleFreeze);
            place(btn, 60f);

            // Toggle
            var tog = DefaultControls.CreateToggle(res);
            var togText = tog.GetComponentInChildren<Text>(); if (togText != null) togText.text = "Toggle option";
            place(tog, 44f);

            // Toggle group (3 mutually-exclusive)
            var groupGo = new GameObject("ToggleGroup", typeof(RectTransform));
            var grpRt = (RectTransform)groupGo.transform; grpRt.SetParent(page, false);
            var grp = groupGo.AddComponent<ToggleGroup>();
            for (int i = 0; i < 3; i++)
            {
                var t = DefaultControls.CreateToggle(res);
                var tt = t.GetComponentInChildren<Text>(); if (tt != null) tt.text = "Choice " + (i + 1);
                t.GetComponent<Toggle>().group = grp;
                t.GetComponent<Toggle>().isOn = (i == 0);
                place(t, 40f);
            }

            // Slider
            place(DefaultControls.CreateSlider(res), 36f);
            // Scrollbar (standalone)
            place(DefaultControls.CreateScrollbar(res), 28f);

            // Dropdown with the scale modes
            var dd = DefaultControls.CreateDropdown(res);
            var dropdown = dd.GetComponent<Dropdown>();
            dropdown.options.Clear();
            foreach (var name in System.Enum.GetNames(typeof(UIBurstScaleMode)))
                dropdown.options.Add(new Dropdown.OptionData(name));
            dropdown.value = (int)UIBurstScaleMode.Fit; dropdown.RefreshShownValue();
            place(dd, 56f);

            // Input field
            var inp = DefaultControls.CreateInputField(res);
            var field = inp.GetComponent<InputField>();
            var ph = field.placeholder as Text; if (ph != null) ph.text = "Type here...";
            place(inp, 56f);

            // Right: a 3D character living among real controls (graded).
            var charCell = MakeRect("controls_char", page);
            charCell.anchorMin = new Vector2(0.72f, 0.64f); charCell.anchorMax = charCell.anchorMin; charCell.pivot = new Vector2(0.5f, 0.5f);
            charCell.sizeDelta = new Vector2(380, 520);
            Populate(charCell, "3D among controls", UIBurstScaleMode.Fit, "UIControls", true, true, false);

            // Right: circular stencil Mask (different from RectMask2D) clipping a character to a circle.
            var maskCell = MakeRect("circleMask", page);
            maskCell.anchorMin = new Vector2(0.72f, 0.2f); maskCell.anchorMax = maskCell.anchorMin; maskCell.pivot = new Vector2(0.5f, 0.5f);
            maskCell.sizeDelta = new Vector2(380, 380);
            var mimg = maskCell.gameObject.AddComponent<Image>();
            mimg.sprite = MakeCircleSprite(256);
            mimg.color = new Color(0.2f, 0.7f, 1f, 1f);
            var mask = maskCell.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;
            var inner = MakeRect("circle_inner", maskCell);
            inner.anchorMin = Vector2.zero; inner.anchorMax = Vector2.one; inner.offsetMin = Vector2.zero; inner.offsetMax = Vector2.zero;
            Populate(inner, "Circle Mask (stencil)", UIBurstScaleMode.Fit, "UIControls", false, true, true);

            var hdr = MakeText(page, "Standard uGUI controls + a 3D character living among them.\nCircle = stencil Mask (vs RectMask2D earlier).", 18, TextAnchor.UpperLeft);
            var hrt = (RectTransform)hdr.transform;
            hrt.anchorMin = new Vector2(0.5f, 0.95f); hrt.anchorMax = new Vector2(0.98f, 0.99f);
            hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
        }

        private void SetFonts(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Text>(true)) t.font = GetUiFont();
        }

        private Sprite MakeCircleSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float r = size * 0.5f;
            var px = new Color32[size * size];
            for (int yy = 0; yy < size; yy++)
                for (int xx = 0; xx < size; xx++)
                {
                    float dx = xx + 0.5f - r, dy = yy + 0.5f - r;
                    bool inside = dx * dx + dy * dy <= r * r;
                    px[yy * size + xx] = inside ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private void BuildDragMaskPage()
        {
            var page = NewPage("Drag Mask");

            var hdr = MakeText(page, "Drag the blue window to reveal / hide the character behind the mask.\n" +
                                     "The character stays put; the mask moves. Dashed box = full character footprint.",
                                     20, TextAnchor.UpperCenter);
            var hrt = (RectTransform)hdr.transform;
            hrt.anchorMin = new Vector2(0.08f, 0.9f); hrt.anchorMax = new Vector2(0.92f, 0.98f);
            hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;

            // Drag space.
            var container = MakeRect("dragContainer", page);
            container.anchorMin = new Vector2(0.5f, 0.5f); container.anchorMax = container.anchorMin; container.pivot = new Vector2(0.5f, 0.5f);
            container.sizeDelta = new Vector2(780, 1020);
            AddImage(container.gameObject, new Color(0, 0, 0, 0.25f));

            // Footprint guide: where the (fixed) full character sits, so you can see what's hidden.
            Vector2 holderSize = new Vector2(400, 600);
            var guide = MakeRect("footprint", container);
            guide.anchorMin = new Vector2(0.5f, 0.5f); guide.anchorMax = guide.anchorMin; guide.pivot = new Vector2(0.5f, 0.5f);
            guide.sizeDelta = holderSize; guide.anchoredPosition = Vector2.zero;
            AddBorder(guide, new Color(1, 1, 1, 0.30f), 2f);

            // The draggable mask window.
            Vector2 startPos = new Vector2(0, 130);
            var window = MakeRect("maskWindow", container);
            window.anchorMin = new Vector2(0.5f, 0.5f); window.anchorMax = window.anchorMin; window.pivot = new Vector2(0.5f, 0.5f);
            window.sizeDelta = new Vector2(300, 300);
            window.anchoredPosition = startPos;
            var winImg = window.gameObject.AddComponent<Image>();
            winImg.color = new Color(0.3f, 0.8f, 1f, 0.12f);
            winImg.raycastTarget = true; // needed to receive drag events
            window.gameObject.AddComponent<RectMask2D>();
            AddBorder(window, new Color(0.3f, 0.8f, 1f, 0.95f), 3f);

            // Character holder, child of the window, counter-positioned so the character appears fixed
            // in the container while only the mask window slides over it.
            var holder = MakeRect("charHolder", window);
            holder.anchorMin = new Vector2(0.5f, 0.5f); holder.anchorMax = holder.anchorMin; holder.pivot = new Vector2(0.5f, 0.5f);
            holder.sizeDelta = holderSize; holder.anchoredPosition = -startPos;
            SpawnCharacter(holder, UIBurstScaleMode.Fit);

            var drag = window.gameObject.AddComponent<DraggableMask>();
            drag.window = window; drag.container = container; drag.characterHolder = holder;

            MakeButton(page, "Reset", new Vector2(0.5f, 0.06f), Vector2.zero, new Vector2(200, 72),
                () => { window.anchoredPosition = startPos; holder.anchoredPosition = -startPos; });
        }

        private void BuildLivePage()
        {
            var page = NewPage("Live");

            var wrapper = MakeRect("live_wrap", page);
            wrapper.anchorMin = new Vector2(0.32f, 0.5f); wrapper.anchorMax = new Vector2(0.32f, 0.5f);
            wrapper.pivot = new Vector2(0.5f, 0.5f);
            wrapper.sizeDelta = new Vector2(360, 520);
            wrapper.anchoredPosition = Vector2.zero;

            var cell = MakeCell(wrapper, "live_cell");
            cell.anchorMin = Vector2.zero; cell.anchorMax = Vector2.one;
            cell.offsetMin = Vector2.zero; cell.offsetMax = Vector2.zero;
            var cg = cell.gameObject.AddComponent<CanvasGroup>();
            var ch = Populate(cell, "Live", UIBurstScaleMode.Fit, "Live", false, true, true);

            float x0 = 0.72f; // sliders on the right
            float y = 0.92f; float dy = -0.085f; int i = 0;
            Action<string, float, float, float, Action<float>> row = (label, mn, mx, val, cb) =>
            {
                var t = MakeText(page, label, 20, TextAnchor.MiddleLeft);
                var trt = (RectTransform)t.transform;
                trt.anchorMin = new Vector2(0.55f, y + i * dy); trt.anchorMax = trt.anchorMin;
                trt.pivot = new Vector2(0, 0.5f); trt.sizeDelta = new Vector2(220, 40); trt.anchoredPosition = Vector2.zero;
                MakeSlider(page, new Vector2(0.78f, y + i * dy), Vector2.zero, new Vector2(360, 36), mn, mx, val, cb);
                i++;
            };

            row("Pivot X", 0, 1, 0.5f, v => { var p = cell.pivot; cell.pivot = new Vector2(v, p.y); });
            row("Pivot Y", 0, 1, 0.5f, v => { var p = cell.pivot; cell.pivot = new Vector2(p.x, v); });
            row("Width", 80, 700, wrapper.sizeDelta.x, v => wrapper.sizeDelta = new Vector2(v, wrapper.sizeDelta.y));
            row("Height", 80, 900, wrapper.sizeDelta.y, v => wrapper.sizeDelta = new Vector2(wrapper.sizeDelta.x, v));
            row("Parent Scale", 0.3f, 2.5f, 1f, v => wrapper.localScale = new Vector3(v, v, 1));
            row("Parent ZRot", -180, 180, 0f, v => wrapper.localRotation = Quaternion.Euler(0, 0, v));
            row("Alpha", 0, 1, 1f, v => cg.alpha = v);
        }

        // ---------------------------------------------------------------- cell + character

        private RectTransform MakeCell(RectTransform parent, string name)
        {
            var cell = MakeRect("Cell_" + name, parent);
            cell.anchorMin = new Vector2(0.5f, 0.5f); cell.anchorMax = new Vector2(0.5f, 0.5f);
            cell.pivot = new Vector2(0.5f, 0.5f);
            cell.sizeDelta = new Vector2(220, 300);
            return cell;
        }

        /// <summary>Add reference Image + border + label + character to a cell, and register it.</summary>
        private UIBurstSkinnedCharacter Populate(RectTransform cell, string label, UIBurstScaleMode mode,
            string category, bool grade, bool checkSize, bool isFinding)
        {
            // Reference Image fills the cell (ground truth).
            var refGo = MakeRect("Ref", cell);
            refGo.anchorMin = Vector2.zero; refGo.anchorMax = Vector2.one;
            refGo.offsetMin = Vector2.zero; refGo.offsetMax = Vector2.zero;
            var refImg = refGo.gameObject.AddComponent<Image>();
            refImg.color = m_RefImageColor;
            AddBorder(cell, m_BorderColor, 3f);

            // Character fills the cell.
            var ch = SpawnCharacter(cell, mode);

            // Label.
            var lbl = MakeText(cell, label, 18, TextAnchor.UpperCenter);
            var lrt = (RectTransform)lbl.transform;
            lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(1, 1); lrt.pivot = new Vector2(0.5f, 0);
            lrt.sizeDelta = new Vector2(0, 36); lrt.anchoredPosition = new Vector2(0, 2);
            lbl.color = m_CellLabelColor;

            if (grade || isFinding)
            {
                m_Verifier.Register(new UI3DLayoutVerifier.TestCell
                {
                    category = category, name = label, cell = (RectTransform)ch.transform,
                    character = ch, referenceImage = refImg, mode = mode,
                    checkSize = checkSize, isFinding = isFinding
                });
            }
            return ch;
        }

        private UIBurstSkinnedCharacter SpawnCharacter(RectTransform parent, UIBurstScaleMode mode)
        {
            var go = Instantiate(m_CharacterPrefab);
            go.name = "[3D] " + mode;
            go.SetActive(false); // ensure inactive so UIBurstSkinnedCharacter.Awake defers until configured

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            // Respect the prefab's authored facing; m_ModelYRotation is only an extra offset.
            var baseRot = m_CharacterPrefab != null ? m_CharacterPrefab.transform.localRotation : Quaternion.identity;
            rt.localRotation = Quaternion.Euler(0, m_ModelYRotation, 0) * baseRot;

            var ch = go.GetComponent<UIBurstSkinnedCharacter>();
            if (ch == null) ch = go.AddComponent<UIBurstSkinnedCharacter>();
            ch.Configure(mode, true, new Vector2(2, 2), m_UiShader, m_CustomUiMaterial, false, m_DepthClearShader);

            var anim = go.GetComponent<Animator>();
            if (anim != null) m_Animators.Add(anim);

            go.SetActive(true); // page may be inactive; activation cascades when page is shown
            return ch;
        }

        // ---------------------------------------------------------------- uGUI builder helpers

        private RectTransform MakeRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        private Image AddImage(GameObject go, Color c)
        {
            var img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            img.color = c;
            return img;
        }

        private void AddBorder(RectTransform cell, Color color, float thickness)
        {
            (Vector2 mn, Vector2 mx)[] edges =
            {
                (new Vector2(0,0), new Vector2(1,0)), // bottom
                (new Vector2(0,1), new Vector2(1,1)), // top
                (new Vector2(0,0), new Vector2(0,1)), // left
                (new Vector2(1,0), new Vector2(1,1)), // right
            };
            bool[] horizontal = { true, true, false, false };
            for (int i = 0; i < 4; i++)
            {
                var e = MakeRect("border", cell);
                e.anchorMin = edges[i].mn; e.anchorMax = edges[i].mx;
                if (horizontal[i]) { e.sizeDelta = new Vector2(0, thickness); }
                else { e.sizeDelta = new Vector2(thickness, 0); }
                e.anchoredPosition = Vector2.zero;
                var img = e.gameObject.AddComponent<Image>();
                img.color = color; img.raycastTarget = false;
            }
        }

        private Text MakeText(RectTransform parent, string s, int size, TextAnchor anchor)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = GetUiFont(); t.fontSize = size; t.text = s; t.alignment = anchor;
            t.color = Color.white; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private Button MakeButton(RectTransform parent, string label, Vector2 anchor, Vector2 pos, Vector2 size, Action onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = new Vector2(anchor.x, anchor.y);
            rt.sizeDelta = size; rt.anchoredPosition = pos;
            go.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.9f, 0.9f);
            var btn = go.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick());
            var t = MakeText(rt, label, 20, TextAnchor.MiddleCenter);
            var trt = (RectTransform)t.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            return btn;
        }

        private Slider MakeSlider(RectTransform parent, Vector2 anchor, Vector2 pos, Vector2 size,
            float min, float max, float val, Action<float> onChange)
        {
            var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size; rt.anchoredPosition = pos;

            var bg = MakeRect("BG", rt);
            bg.anchorMin = new Vector2(0, 0.25f); bg.anchorMax = new Vector2(1, 0.75f); bg.offsetMin = Vector2.zero; bg.offsetMax = Vector2.zero;
            bg.gameObject.AddComponent<Image>().color = new Color(1, 1, 1, 0.3f);

            var fillArea = MakeRect("FillArea", rt);
            fillArea.anchorMin = new Vector2(0, 0.25f); fillArea.anchorMax = new Vector2(1, 0.75f); fillArea.offsetMin = Vector2.zero; fillArea.offsetMax = Vector2.zero;
            var fill = MakeRect("Fill", fillArea);
            fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0, 1); fill.sizeDelta = new Vector2(10, 0);
            fill.gameObject.AddComponent<Image>().color = new Color(0.3f, 0.8f, 0.4f, 0.9f);

            var handleArea = MakeRect("HandleArea", rt);
            handleArea.anchorMin = Vector2.zero; handleArea.anchorMax = Vector2.one; handleArea.offsetMin = Vector2.zero; handleArea.offsetMax = Vector2.zero;
            var handle = MakeRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(30, 0); handle.anchorMin = new Vector2(0, 0); handle.anchorMax = new Vector2(0, 1);
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.color = Color.white;

            var s = go.GetComponent<Slider>();
            s.fillRect = fill; s.handleRect = handle; s.targetGraphic = handleImg;
            s.direction = Slider.Direction.LeftToRight;
            s.minValue = min; s.maxValue = max; s.value = val;
            s.onValueChanged.AddListener(v => onChange(v));
            return s;
        }

        private RectTransform GridCell(RectTransform page, string name, int cols, int rows, int cx, int cy, float pad)
        {
            var cell = MakeRect("Cell_" + name, page);
            float w = 1f / cols, h = 1f / rows;
            cell.anchorMin = new Vector2(cx * w, 1f - (cy + 1) * h);
            cell.anchorMax = new Vector2((cx + 1) * w, 1f - cy * h);
            cell.offsetMin = new Vector2(pad, pad); cell.offsetMax = new Vector2(-pad, -pad);
            return cell;
        }

        private static Vector2 OffsetForAnchor(Vector2 a, float pad)
        {
            float x = a.x < 0.5f ? pad : (a.x > 0.5f ? -pad : 0);
            float y = a.y < 0.5f ? pad : (a.y > 0.5f ? -pad : 0);
            return new Vector2(x, y);
        }
    }

    /// <summary>
    /// Drag handler for the "Drag Mask" page: moves a RectMask2D window with the pointer while
    /// counter-translating the character holder so the character stays fixed in the container and
    /// only the clipping window slides over it (reveal / hide effect).
    /// </summary>
    public class DraggableMask : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public RectTransform window;
        public RectTransform container;
        public RectTransform characterHolder;
        private Vector2 m_Grab;

        public void OnBeginDrag(PointerEventData e)
        {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(container, e.position, e.pressEventCamera, out var lp))
                m_Grab = lp - window.anchoredPosition;
        }

        public void OnDrag(PointerEventData e)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(container, e.position, e.pressEventCamera, out var lp))
                return;
            window.anchoredPosition = lp - m_Grab;
            if (characterHolder != null) characterHolder.anchoredPosition = -window.anchoredPosition;
        }
    }

    /// <summary>Animates a RectTransform's size to demonstrate the 3D tracking live resizes.</summary>
    public class LiveResizeDriver : MonoBehaviour
    {
        public RectTransform target;
        private Vector2 m_Base;
        private void Start() { if (target != null) m_Base = target.sizeDelta; }
        private void Update()
        {
            if (target == null) return;
            float s = (Mathf.Sin(Time.time * 1.5f) + 1f) * 0.5f; // 0..1
            target.sizeDelta = new Vector2(Mathf.Lerp(160, 520, s), Mathf.Lerp(520, 200, s));
        }
    }
}
