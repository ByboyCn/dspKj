using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace DSPBlueprintTools
{
    /// <summary>
    /// 蓝图工具模组 (DSPBlueprintTools)
    /// 功能：
    ///  1. 坐标偏移：按设定的 dx/dy/dz（精确到小数点后 4 位）和数量，批量偏移蓝图内所有建筑坐标
    ///  2. 垂直叠加：按设定层高（精确到小数点后 4 位）把蓝图叠加成多层
    ///  3. 线性变换：缩放（三轴，精确到小数点后 4 位）、绕竖直轴旋转，以及批量替换建筑型号
    ///  4. 从游戏内剪贴板直接读取蓝图数据（也可点蓝图详情窗口旁的"读取"按钮）
    ///  5. 默认快捷键 ALT+4，可在 BepInEx 配置文件中修改
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class BlueprintToolsPlugin : BaseUnityPlugin
    {
        public const string GUID = "dsp.byboy.blueprinttools";
        public const string NAME = "DSPBlueprintTools";
        public const string VERSION = "1.3.6";

        private const int MaxBlueprintBuildings = 15000; // 游戏蓝图建筑上限
        private const int BeltItemId = 2001;             // 传送带物品 ID（2002 是分拣器），仅作快速路径

        /// <summary>用游戏原型库判断建筑是否为传送带（比硬编码 ID 可靠）。</summary>
        private static bool IsBelt(BlueprintBuilding b)
        {
            if (b.itemId == BeltItemId) return true;
            try
            {
                var model = LDB.models.Select(b.modelIndex);
                return model != null && model.prefabDesc != null && model.prefabDesc.isBelt;
            }
            catch
            {
                return false;
            }
        }

        // ---------- 配置 ----------
        private ConfigEntry<string> _hotkeyEntry;
        private ConfigEntry<float> _maxBeltLengthEntry;
        private ConfigEntry<bool> _autoBeltLatEntry;
        private KeyCode _mainKey = KeyCode.Alpha4;
        private KeyCode[] _modifierKeys = { KeyCode.LeftAlt };

        // ---------- 窗口状态 ----------
        private bool _showWindow;
        private Rect _winRect = new Rect(120f, 100f, 430f, 560f);
        private Vector2 _scroll;
        private string _status = "按快捷键打开/关闭本窗口";
        private BlueprintData _bp;               // 当前载入的蓝图
        private BlueprintData _bpOriginal;       // 最初读取的原始蓝图（还原用）
        private string _bpDesc = "(未载入蓝图)";
        private string _beltStat = "";           // 传送带自动探测结果

        // 输入缓存（字符串形式，便于编辑，应用时按 4 位小数处理）
        private string _ox = "0.0000", _oy = "0.0000", _oz = "0.0000", _offCount = "1";
        private string _layers = "2", _layerHeight = "10.0000";
        private string _sx = "1.0000", _sy = "1.0000", _sz = "1.0000", _rotDeg = "0";
        private string _replaceFrom = "", _replaceTo = "";

        // 蓝图详情窗口（UIBlueprintInspector）的引用缓存，用于注入"读取"按钮
        private UIBlueprintInspector _inspector;
        private float _nextInjectTry;
        private bool _readBtnCreated;
        private FieldInfo _fiCopyBtnTrans;
        private FieldInfo _fiCopyBtnText;
        private FieldInfo _fiPasteTool;
        private bool _uiRefsResolved;

        private void Awake()
        {
            _hotkeyEntry = Config.Bind("通用", "快捷键", "LeftAlt + Alpha4",
                "打开/关闭蓝图工具窗口的快捷键。格式：修饰键 + 主键，用 + 分隔。" +
                "键名使用 UnityEngine.KeyCode 枚举名，例如：LeftAlt + Alpha4、LeftControl + F8、None + F9（无修饰键）。");
            _hotkeyEntry.SettingChanged += (_, __) => ParseHotkey();
            ParseHotkey();
            _maxBeltLengthEntry = Config.Bind("传送带", "单段最大长度", 1.8f,
                "传送带合并最长化时允许的单段最大长度（小数点后 4 位）。" +
                "实测原版可放置上限随纬度变化：赤道约 1.8，极地约 1.6，超过则无法摆放。默认 1.8。");
            _autoBeltLatEntry = Config.Bind("传送带", "按纬度自动适配上限", true,
                "开启后按蓝图区域纬度自动收紧上限（赤道 1.8 → 极地 1.6，线性过渡），" +
                "关闭则始终使用上面配置的固定上限。");
            _status = $"快捷键：{_hotkeyEntry.Value}";
        }

        private void ParseHotkey()
        {
            var mods = new List<KeyCode>();
            KeyCode main = KeyCode.None;
            var parts = _hotkeyEntry.Value.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                var s = parts[i].Trim();
                if (s.Length == 0) continue;
                if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Enum.TryParse(s, true, out KeyCode k)) continue;
                if (i == parts.Length - 1) main = k;
                else mods.Add(k);
            }
            if (main == KeyCode.None) main = KeyCode.Alpha4;
            _mainKey = main;
            _modifierKeys = mods.ToArray();
        }

        private static bool ParseF4(string text, out float v)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                v = (float)Math.Round(v, 4); // 精确到小数点后 4 位
                return true;
            }
            v = 0f;
            return false;
        }

        private void Update()
        {
            bool modsOk = true;
            foreach (var m in _modifierKeys)
            {
                if (!Input.GetKey(m)) { modsOk = false; break; }
            }
            if (modsOk && Input.GetKeyDown(_mainKey))
            {
                _showWindow = !_showWindow;
            }
            // 低频（每 2 秒）尝试向蓝图详情窗口注入"读取"按钮；成功一次后不再执行
            if (!_readBtnCreated && Time.unscaledTime >= _nextInjectTry)
            {
                _nextInjectTry = Time.unscaledTime + 2f;
                EnsureInspectorReadButton();
            }
        }

        // ===================== UI =====================

        private void OnGUI()
        {
            // 窗口未打开时不做任何 IMGUI 工作，避免每帧性能开销
            if (!_showWindow) return;
            _winRect = GUILayout.Window(0x42B0, _winRect, DrawWindow, "蓝图工具 DSPBlueprintTools (v" + VERSION + ")");
        }

        /// <summary>
        /// 把"读取"按钮一次性注入蓝图详情窗口（克隆原版"复制"按钮的样式），
        /// 放在原按钮左侧，不遮挡任何原版按钮；只创建一次，零每帧开销。
        /// </summary>
        private void EnsureInspectorReadButton()
        {
            if (!_uiRefsResolved)
            {
                _fiCopyBtnTrans = typeof(UIBlueprintInspector).GetField("copyBtnTrans",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _fiCopyBtnText = typeof(UIBlueprintInspector).GetField("copyBtnText",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _fiPasteTool = typeof(UIBlueprintInspector).GetField("pasteBuildTool",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _uiRefsResolved = true;
            }
            if (_fiCopyBtnTrans == null || _fiCopyBtnText == null) return;

            try
            {
                _inspector = FindObjectOfType<UIBlueprintInspector>();
            }
            catch { _inspector = null; }
            if (_inspector == null) return;
            var copyRt = _fiCopyBtnTrans.GetValue(_inspector) as RectTransform;
            var srcText = _fiCopyBtnText.GetValue(_inspector) as Text;
            if (copyRt == null || srcText == null || !_inspector.gameObject.activeInHierarchy) return;

            var copyBtn = copyRt.GetComponent<Button>();
            var copyImg = copyRt.GetComponent<Image>();
            if (copyBtn == null || copyImg == null) return;

            // 创建按钮
            var go = new GameObject("DSPBlueprintTools_ReadBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(copyRt.parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = copyRt.anchorMin;
            rt.anchorMax = copyRt.anchorMax;
            rt.pivot = copyRt.pivot;
            rt.sizeDelta = copyRt.sizeDelta;
            // 放到"复制"按钮左侧，留 6px 间距，不遮挡任何原版按钮
            rt.anchoredPosition = copyRt.anchoredPosition - new Vector2(copyRt.sizeDelta.x + 6f, 0f);

            var img = go.GetComponent<Image>();
            img.sprite = copyImg.sprite;
            img.type = copyImg.type;
            img.color = new Color(0.65f, 0.92f, 1f, copyImg.color.a); // 淡蓝色区分

            // 克隆原版按钮的文字样式
            var txtGo = UnityEngine.Object.Instantiate(srcText.gameObject, rt, false);
            txtGo.name = "Text";
            var txt = txtGo.GetComponent<Text>();
            if (txt != null)
            {
                txt.text = "读取";
                txt.rectTransform.anchorMin = Vector2.zero;
                txt.rectTransform.anchorMax = Vector2.one;
                txt.rectTransform.offsetMin = Vector2.zero;
                txt.rectTransform.offsetMax = Vector2.zero;
            }

            var btn = go.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent(); // 丢弃克隆来源的持久化监听
            btn.onClick.AddListener(() =>
            {
                ReadFromClipboard();
                _showWindow = true;
            });

            _readBtnCreated = true;
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            // ---------- 蓝图信息 ----------
            GUILayout.Label(_bpDesc, GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("从剪贴板读取蓝图", GUILayout.Height(26)))
                ReadFromClipboard();
            if (GUILayout.Button("写回剪贴板", GUILayout.Height(26)))
                WriteToClipboard();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("还原原始蓝图（放回剪贴板）", GUILayout.Height(26)))
                RestoreOriginal();
            if (GUILayout.Button("应用/预览", GUILayout.Height(26), GUILayout.Width(80)))
                ApplyToPasteTool();
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);

            // ---------- 功能 1：坐标偏移 ----------
            GUILayout.Label("── 1. 坐标偏移（小数点后 4 位）──", GUI.skin.box);
            DrawGridRow("X 偏移", ref _ox, "Y 偏移", ref _oy);
            DrawGridRow("Z 偏移(高度)", ref _oz, "重复数量", ref _offCount);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("偏移（净偏移 = 偏移量 × 数量）", GUILayout.Height(24)))
                DoOffset();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // ---------- 功能 2：垂直叠加 ----------
            GUILayout.Label("── 2. 垂直叠加（层高精确到小数点后 4 位）──", GUI.skin.box);
            DrawGridRow("叠加层数", ref _layers, "每层高度", ref _layerHeight);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("垂直叠加建筑层数", GUILayout.Height(24)))
                DoStack();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // ---------- 功能 3：线性变换 ----------
            GUILayout.Label("── 3. 线性变换（批量设置建筑模型）──", GUI.skin.box);
            DrawGridRow("X 缩放", ref _sx, "Y 缩放", ref _sy);
            DrawGridRow("Z 缩放", ref _sz, "旋转(度)", ref _rotDeg);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("应用缩放/旋转", GUILayout.Height(24)))
                DoLinearTransform();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            DrawGridRow("原建筑ID", ref _replaceFrom, "替换为ID", ref _replaceTo);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("批量替换建筑型号", GUILayout.Height(24)))
                DoReplace();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // ---------- 功能 4：传送带最长化 ----------
            GUILayout.Label("── 4. 传送带合并最长化（省带子）──", GUI.skin.box);
            GUILayout.Label(string.IsNullOrEmpty(_beltStat)
                ? "读取蓝图后自动探测传送带长度（合并以此为参考）"
                : _beltStat);
            GUILayout.Label("合并上限按纬度自动适配（赤道 1.8 / 极地 1.6，端点 0.5 格步进，配置文件可调）");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("传送带最长化", GUILayout.Height(24)))
                DoBeltMerge();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("提示：处理结果自动写回游戏剪贴板，可在蓝图粘贴界面直接使用；" +
                            "\"应用/预览\"会直接替换当前蓝图粘贴工具中的蓝图。", GUI.skin.label);

            GUILayout.EndScrollView();

            GUILayout.Label(_status);
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private static void DrawGridRow(string l1, ref string v1, string l2, ref string v2)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(l1, GUILayout.Width(80));
            v1 = GUILayout.TextField(v1);
            GUILayout.Label(l2, GUILayout.Width(80));
            v2 = GUILayout.TextField(v2);
            GUILayout.EndHorizontal();
        }

        // ===================== 蓝图操作 =====================

        private void ReadFromClipboard()
        {
            try
            {
                string code = GUIUtility.systemCopyBuffer;
                if (string.IsNullOrEmpty(code))
                {
                    _status = "剪贴板为空";
                    return;
                }
                var bp = BlueprintData.CreateNew();
                var err = bp.FromBase64String(code.Trim());
                if (err != BlueprintDataIOError.OK || bp.buildings == null)
                {
                    _status = "读取失败：" + err;
                    _bp = null;
                    _bpDesc = "(未载入蓝图)";
                    return;
                }
                _bp = bp;
                _bpOriginal = bp.Clone(); // 保留原始蓝图，供"还原"使用
                _bpDesc = $"已载入：{bp.shortDesc} | 建筑 {bp.buildings.Length} 个";
                AnalyzeBelts();
                _status = "从剪贴板读取成功";
            }
            catch (Exception ex)
            {
                _status = "读取异常：" + ex.Message;
            }
        }

        private void WriteToClipboard()
        {
            if (_bp == null) { _status = "没有蓝图可写回"; return; }
            try
            {
                _bp.CheckBuildingData();
                GUIUtility.systemCopyBuffer = _bp.ToBase64String();
                _status = $"已写回剪贴板（建筑 {_bp.buildings.Length} 个）";
            }
            catch (Exception ex)
            {
                _status = "写回异常：" + ex.Message;
            }
        }

        private void DoOffset()
        {
            if (!RequireBlueprint()) return;
            if (!ParseF4(_ox, out float dx) | !ParseF4(_oy, out float dy) |
                !ParseF4(_oz, out float dz) | !int.TryParse(_offCount, out int count))
            {
                _status = "输入格式错误（数值需为合法数字，数量需为整数）";
                return;
            }
            if (count < 1) count = 1;
            try
            {
                var bp = _bp.Clone();
                foreach (var b in bp.buildings)
                {
                    b.localOffset_x = R4(b.localOffset_x + dx * count);
                    b.localOffset_y = R4(b.localOffset_y + dy * count);
                    b.localOffset_z = R4(b.localOffset_z + dz * count);
                    // 分拣器/传送带有尾端点，必须一起移动
                    b.localOffset_x2 = R4(b.localOffset_x2 + dx * count);
                    b.localOffset_y2 = R4(b.localOffset_y2 + dy * count);
                    b.localOffset_z2 = R4(b.localOffset_z2 + dz * count);
                }
                Commit(bp, $"坐标偏移完成：({dx:F4}, {dy:F4}, {dz:F4}) × {count}");
            }
            catch (Exception ex)
            {
                _status = "偏移异常：" + ex.Message;
            }
        }

        private void DoStack()
        {
            if (!RequireBlueprint()) return;
            if (!int.TryParse(_layers, out int layers) | !ParseF4(_layerHeight, out float h))
            {
                _status = "输入格式错误（层数需为整数，层高需为数字）";
                return;
            }
            if (layers < 1) layers = 1;
            try
            {
                int n = _bp.buildings.Length;
                if ((long)n * layers > MaxBlueprintBuildings)
                {
                    _status = $"叠加后建筑数 {n * layers} 超过上限 {MaxBlueprintBuildings}";
                    return;
                }
                var result = _bp.Clone();
                var list = new List<BlueprintBuilding>(result.buildings);
                for (int i = 1; i < layers; i++)
                {
                    var layer = _bp.Clone();
                    foreach (var b in layer.buildings)
                    {
                        b.localOffset_z = R4(b.localOffset_z + h * i);
                        b.localOffset_z2 = R4(b.localOffset_z2 + h * i); // 尾端点一起移动
                    }
                    list.AddRange(layer.buildings);
                }
                var arr = list.ToArray();
                for (int i = 0; i < arr.Length; i++) arr[i].index = i;
                result.buildings = arr;
                Commit(result, $"垂直叠加完成：{layers} 层 × 层高 {h:F4}，共 {arr.Length} 个建筑");
            }
            catch (Exception ex)
            {
                _status = "叠加异常：" + ex.Message;
            }
        }

        private void DoLinearTransform()
        {
            if (!RequireBlueprint()) return;
            if (!ParseF4(_sx, out float sx) | !ParseF4(_sy, out float sy) |
                !ParseF4(_sz, out float sz) | !float.TryParse(_rotDeg, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float rotDeg))
            {
                _status = "输入格式错误";
                return;
            }
            try
            {
                var bp = _bp.Clone();
            // 蓝图局部坐标：X/Y 为水平面，Z 为高度方向；旋转绕 Z 轴（竖直轴）在 X/Y 平面进行
                double rad = rotDeg * Math.PI / 180.0;
                double c = Math.Cos(rad), s = Math.Sin(rad);
                foreach (var b in bp.buildings)
                {
                    float x = (float)Math.Round(b.localOffset_x * sx, 4);
                    float y = (float)Math.Round(b.localOffset_y * sy, 4);
                    float z = (float)Math.Round(b.localOffset_z * sz, 4);
                    float x2 = (float)Math.Round(b.localOffset_x2 * sx, 4);
                    float y2 = (float)Math.Round(b.localOffset_y2 * sy, 4);
                    float z2 = (float)Math.Round(b.localOffset_z2 * sz, 4);
                    if (Math.Abs(rotDeg) > 0.0001f)
                    {
                        float rx = (float)Math.Round(x * c + y * s, 4);
                        float ry = (float)Math.Round(-x * s + y * c, 4);
                        x = rx; y = ry;
                        float rx2 = (float)Math.Round(x2 * c + y2 * s, 4);
                        float ry2 = (float)Math.Round(-x2 * s + y2 * c, 4);
                        x2 = rx2; y2 = ry2;
                        b.yaw = (float)Math.Round(b.yaw + rotDeg, 4);
                    }
                    b.localOffset_x = x;
                    b.localOffset_y = y;
                    b.localOffset_z = z;
                    b.localOffset_x2 = x2; // 尾端点同步变换（分拣器/传送带）
                    b.localOffset_y2 = y2;
                    b.localOffset_z2 = z2;
                }
                Commit(bp, $"线性变换完成：缩放({sx:F4}, {sy:F4}, {sz:F4})，旋转 {rotDeg:F4}°");
            }
            catch (Exception ex)
            {
                _status = "变换异常：" + ex.Message;
            }
        }

        private void DoReplace()
        {
            if (!RequireBlueprint()) return;
            if (!short.TryParse(_replaceFrom.Trim(), out short from) ||
                !short.TryParse(_replaceTo.Trim(), out short to))
            {
                _status = "输入格式错误（建筑 ID 需为整数，如 2001 分拣器）";
                return;
            }
            try
            {
                var bp = _bp.Clone();
                int cnt = 0;
                foreach (var b in bp.buildings)
                {
                    if (b.itemId == from)
                    {
                        b.itemId = to;
                        b.modelIndex = (short)LDB.items.Select(to).ModelIndex;
                        cnt++;
                    }
                }
                Commit(bp, $"批量替换完成：{cnt} 个建筑从 ID {from} 替换为 {to}");
            }
            catch (Exception ex)
            {
                _status = "替换异常：" + ex.Message;
            }
        }

        /// <summary>
        /// 传送带合并最长化：把蓝图里逐格（逐段）铺设的同一直线传送带
        /// 合并为不超过单段最大长度的最长段（端点按 0.5 格步进），大幅减少传送带数量。
        /// 上限随纬度自动适配（赤道约 1.8，极地约 1.6）。
        /// </summary>
        private void DoBeltMerge()
        {
            if (!RequireBlueprint()) return;
            try
            {
                float fixedMax = (float)Math.Round(_maxBeltLengthEntry.Value, 4);
                if (fixedMax < 1f) fixedMax = 1f;

                var bp = _bp.Clone();

                var others = new List<BlueprintBuilding>();
                var belts = new List<BlueprintBuilding>();
                foreach (var b in bp.buildings)
                {
                    if (IsBelt(b)) belts.Add(b);
                    else others.Add(b);
                }
                if (belts.Count == 0)
                {
                    _status = "蓝图中没有传送带（ itemId=2001 ）";
                    return;
                }

                var used = new HashSet<BlueprintBuilding>();
                var merged = new List<BlueprintBuilding>();

                // 游戏粘贴校验（反编译确认）：传送带两端点世界距离 > √5.3 ≈ 2.3022 米判为 TooFar。
                // 世界半径 = 行星半径(200) + 0.2 + 1.3333×Z，每格世界尺寸随纬度(E-W)和高度变化，
                // 因此按每条链的纬度/方向/高度精确计算允许的格数上限。
                const float MaxWorldLen = 2.3022f;
                const int SegmentCnt = 200;
                const float PlanetRadius = 200f;
                float latRadPerGrid = BlueprintUtils.GetLatitudeRadPerGrid(SegmentCnt); // 2π/1000

                // 区域 → 纬度（弧度）
                var areaLatRad = new Dictionary<int, float>();
                if (bp.areas != null)
                {
                    foreach (var area in bp.areas)
                    {
                        areaLatRad[area.index] = area.tropicAnchor * latRadPerGrid;
                    }
                }

                // 蓝图里已存在的最长段是实际通过校验的经验值，作为额外保险
                float detectedMax = 0f;
                foreach (var b in belts)
                {
                    float len = Vector3.Distance(
                        new Vector3(b.localOffset_x, b.localOffset_y, b.localOffset_z),
                        new Vector3(b.localOffset_x2, b.localOffset_y2, b.localOffset_z2));
                    if (len > detectedMax) detectedMax = len;
                }
                bool useDetected = detectedMax > 1.05f;

                // 逐格传送带的坐标索引（同高度才算相邻）
                var byPos = new Dictionary<long, BlueprintBuilding>();
                foreach (var b in belts)
                {
                    if (BeltLen(b) >= 1e-4f) continue;
                    byPos[BeltCellKey(b)] = b;
                }

                foreach (var b in belts)
                {
                    if (used.Contains(b)) continue;
                    float limit = ComputeBeltLimit(b, areaLatRad, latRadPerGrid, MaxWorldLen, SegmentCnt, PlanetRadius);
                    if (!_autoBeltLatEntry.Value) limit = fixedMax; // 关闭自动适配时用固定值
                    if (useDetected) limit = Math.Min(limit, detectedMax);
                    MergeBeltChain(b, belts, byPos, used, limit, merged);
                }

                var arr = new BlueprintBuilding[others.Count + merged.Count];
                others.CopyTo(arr, 0);
                merged.CopyTo(arr, others.Count);
                for (int i = 0; i < arr.Length; i++) arr[i].index = i;
                bp.buildings = arr;

                int saved = belts.Count - merged.Count;
                Commit(bp, $"传送带最长化完成：{belts.Count} 段 → {merged.Count} 段，省下 {saved} 段带子" +
                           (useDetected ? $"（按探测上限 {detectedMax:F4}）" : "（按纬度/高度自适应）"));
            }
            catch (Exception ex)
            {
                _status = "传送带合并异常：" + ex.Message;
            }
        }

        /// <summary>
        /// 按传送带所在纬度、方向、高度精确计算允许的段长上限（蓝图格数）。
        /// 游戏规则：端点世界距离 ≤ √5.3 ≈ 2.3022 米。
        /// 世界半径 = 200 + 0.2 + 1.3333×Z（Z 即蓝图 localOffset_z 高度层），
        /// E-W 每格世界尺寸随纬度收缩，N-S 每格恒定，故上限随高度升高而变短。
        /// </summary>
        private float ComputeBeltLimit(BlueprintBuilding b, Dictionary<int, float> areaLatRad,
            float latRadPerGrid, float maxWorldLen, int segmentCnt, float planetRadius)
        {
            try
            {
                float dx = b.localOffset_x2 - b.localOffset_x;
                float dy = b.localOffset_y2 - b.localOffset_y;
                float mag = (float)Math.Sqrt(dx * dx + dy * dy);
                if (mag < 1e-4f) return 1.8f;

                areaLatRad.TryGetValue(b.areaIndex, out float latRad);
                latRad += b.localOffset_y * latRadPerGrid; // 链条实际纬度
                latRad = Mathf.Clamp(latRad, -MathF.PI / 2f, MathF.PI / 2f);

                float height = 0.2f + 1.3333333f * Math.Max(0f, b.localOffset_z);
                float radius = planetRadius + height;

                float cellNS = radius * latRadPerGrid; // N-S（纬向）每格世界尺寸，恒定
                float cellEW = radius * BlueprintUtils.GetLongitudeRadPerGrid(latRad, segmentCnt); // E-W 随纬度收缩

                // 链方向的单位格子世界长度
                float ux = dx / mag, uy = dy / mag;
                float cellWorld = (float)Math.Sqrt(
                    ux * ux * cellEW * cellEW + uy * uy * cellNS * cellNS);
                if (cellWorld < 1e-4f) return 1.8f;

                float limit = maxWorldLen / cellWorld;
                if (float.IsNaN(limit) || limit < 1f) limit = 1f;
                return (float)Math.Round(limit, 4);
            }
            catch
            {
                return 1.8f;
            }
        }

        /// <summary>
        /// 从一条传送带出发构建同一直线链并稀疏化——保留的节点间隔 ≤ 上限，减少传送带数量。
        /// 兼容两种存储格式：
        ///  A 段式（localOffset2 = 远端点）：相邻段首尾相接；
        ///  B 逐格式（localOffset2 == localOffset）：按格子几何邻接（8 邻域、同朝向、同高度）串链。
        /// </summary>
        private void MergeBeltChain(BlueprintBuilding start, List<BlueprintBuilding> belts,
            Dictionary<long, BlueprintBuilding> byPos, HashSet<BlueprintBuilding> used, float maxLen,
            List<BlueprintBuilding> output)
        {
            bool perCell = BeltLen(start) < 1e-4f;
            List<BlueprintBuilding> chain;

            if (perCell)
            {
                chain = BuildPerCellRun(start, byPos, used);
            }
            else
            {
                chain = new List<BlueprintBuilding> { start };
                used.Add(start);
                var cur = start;
                while (true)
                {
                    var nxt = cur.outputObj;
                    if (nxt == null || !IsBelt(nxt) || used.Contains(nxt)) break;
                    if ((P(nxt) - P2(cur)).sqrMagnitude > 0.0004f) break;
                    chain.Add(nxt); used.Add(nxt); cur = nxt;
                }
                cur = start;
                while (true)
                {
                    var prv = cur.inputObj;
                    if (prv == null || !IsBelt(prv) || used.Contains(prv)) break;
                    if ((P(cur) - P2(prv)).sqrMagnitude > 0.0004f) break;
                    chain.Insert(0, prv); used.Add(prv); cur = prv;
                }
            }

            if (perCell)
            {
                var run = BuildPerCellRun(start, byPos, used);
                MergePerCellChain(run, maxLen, output);
                return;
            }

            // ---------- 格式A：段式，把相邻长段重切分为 ≤ maxLen ----------
            var origin = P(start);
            var dirVec = P2(start) - origin;
            float chainLen = 0f;
            foreach (var c in chain) chainLen += BeltLen(c);
            if (dirVec.sqrMagnitude < 1e-8f || chainLen <= maxLen)
            {
                output.AddRange(chain);
                return;
            }
            const double Snap = 0.5;
            int totalUnits = (int)Math.Round(chainLen / Snap);
            int maxUnitsPerSeg = (int)Math.Floor(maxLen / Snap);
            if (maxUnitsPerSeg < 1 || totalUnits < 2) { output.AddRange(chain); return; }
            int k = (int)Math.Ceiling((double)totalUnits / maxUnitsPerSeg);
            int baseUnits = totalUnits / k, extra = totalUnits % k;
            var unit = dirVec.normalized * (float)Snap;

            var kept = new List<BlueprintBuilding>();
            int consumed = 0;
            for (int i = 0; i < k; i++)
            {
                int units = baseUnits + (i < extra ? 1 : 0);
                var proto = chain[Math.Min((int)((double)consumed / totalUnits * chain.Count), chain.Count - 1)];
                kept.Add(new BlueprintBuilding
                {
                    areaIndex = proto.areaIndex,
                    localOffset_x = R4(origin.x + unit.x * consumed),
                    localOffset_y = R4(origin.y + unit.y * consumed),
                    localOffset_z = R4(origin.z + unit.z * consumed),
                    localOffset_x2 = R4(origin.x + unit.x * (consumed + units)),
                    localOffset_y2 = R4(origin.y + unit.y * (consumed + units)),
                    localOffset_z2 = R4(origin.z + unit.z * (consumed + units)),
                    pitch = proto.pitch, yaw = proto.yaw, tilt = proto.tilt,
                    itemId = proto.itemId, modelIndex = proto.modelIndex, filterId = proto.filterId
                });
                consumed += units;
            }
            Rechain(kept, chain);
            output.AddRange(kept);
        }

        /// <summary>逐格式传送带链：每格一个实体，稀疏化为每 0.5 格步进、间隔 ≤ 上限的节点。</summary>
        private void MergePerCellChain(List<BlueprintBuilding> chain, float maxLen, List<BlueprintBuilding> output)
        {
            int n = chain.Count;
            if (n < 3) { output.AddRange(chain); return; }

            // 方向 = 相邻两格的差（链保证同向）
            var d = P(chain[1]) - P(chain[0]);
            if (d.sqrMagnitude < 1e-6f && chain.Count > 2) d = P(chain[2]) - P(chain[1]);
            if (d.sqrMagnitude < 1e-6f) { output.AddRange(chain); return; }
            float cellLen = d.magnitude;
            var dirUnit = d.normalized;

            // 强制保留的锚点：端点 + 连接到链外建筑（如机器接口）的格子
            var anchorIdx = new HashSet<int> { 0, n - 1 };
            var inRun = new HashSet<BlueprintBuilding>(chain);
            for (int i = 0; i < n; i++)
            {
                var b = chain[i];
                if ((b.inputObj != null && !inRun.Contains(b.inputObj)) ||
                    (b.outputObj != null && !inRun.Contains(b.outputObj)))
                {
                    anchorIdx.Add(i);
                }
            }
            var anchors = anchorIdx.OrderBy(x => x).ToList();
            const double Snap = 0.5;

            var kept = new List<BlueprintBuilding>();
            for (int a = 0; a < anchors.Count - 1; a++)
            {
                int i0 = anchors[a], i1 = anchors[a + 1];
                double span = (i1 - i0) * cellLen;
                kept.Add(chain[i0]);
                if (span <= maxLen) continue;

                double spacing = Math.Max(Snap, Math.Floor(Math.Min(maxLen, span) * 2) / 2.0);
                double nodes = Math.Ceiling(span / spacing);
                double step = span / nodes;
                for (int s = 1; s < (int)nodes; s++)
                {
                    double dist = step * s;
                    var src = chain[i0 + (int)Math.Round(dist / cellLen)];
                    var nb = CloneBelt(src);
                    var p = P(chain[i0]) + dirUnit * (float)dist;
                    SetP(nb, p); SetP2(nb, p);
                    kept.Add(nb);
                }
            }
            kept.Add(chain[n - 1]);
            Rechain(kept, chain);
            output.AddRange(kept);
        }

        /// <summary>
        /// 逐格传送带：按几何邻接（8 邻域、同 yaw、同高度）沿直线方向收集整条链。
        /// </summary>
        private List<BlueprintBuilding> BuildPerCellRun(BlueprintBuilding start,
            Dictionary<long, BlueprintBuilding> byPos, HashSet<BlueprintBuilding> used)
        {
            var run = new List<BlueprintBuilding> { start };
            used.Add(start);
            float z = start.localOffset_z;
            float yaw = start.yaw;

            BlueprintBuilding Neighbor(BlueprintBuilding b, int dx, int dy)
            {
                long k = BeltCellKeyOf(Mathf.RoundToInt(b.localOffset_x * 2f) + dx,
                                       Mathf.RoundToInt(b.localOffset_y * 2f) + dy, z);
                return byPos.TryGetValue(k, out var n) && !used.Contains(n) && n.yaw == yaw ? n : null;
            }

            int[][] deltas =
            {
                new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 },
                new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { -1, -1 }
            };

            foreach (var d in deltas)
            {
                if (Neighbor(start, d[0], d[1]) == null) continue;
                var cur = start;
                while (true)
                {
                    var n = Neighbor(cur, d[0], d[1]);
                    if (n == null) break;
                    run.Add(n); used.Add(n); cur = n;
                }
                cur = start;
                while (true)
                {
                    var n = Neighbor(cur, -d[0], -d[1]);
                    if (n == null) break;
                    run.Insert(0, n); used.Add(n); cur = n;
                }
                break;
            }
            return run;
        }

        private static long BeltCellKey(BlueprintBuilding b) =>
            BeltCellKeyOf(Mathf.RoundToInt(b.localOffset_x * 2f), Mathf.RoundToInt(b.localOffset_y * 2f), b.localOffset_z);

        private static long BeltCellKeyOf(int x2, int y2, float z)
        {
            // x2/y2 为 0.5 格步进的两倍值；高度按 1.3333 米层量化
            int zi = (int)Mathf.Round(z * 0.75f);
            return ((long)(x2 + 40000) << 42) | ((long)(y2 + 40000) << 20) | (uint)(zi + 5000);
        }

        /// <summary>重新链接稀疏化后节点的输入/输出引用，并接回链条两端的原有连接。</summary>
        private static void Rechain(List<BlueprintBuilding> kept, List<BlueprintBuilding> chain)
        {
            if (kept.Count == 0) return;
            for (int i = 0; i < kept.Count; i++)
            {
                kept[i].outputObj = (i + 1 < kept.Count) ? kept[i + 1] : chain[chain.Count - 1].outputObj;
                kept[i].inputObj = (i > 0) ? kept[i - 1] : chain[0].inputObj;
            }
        }

        private static Vector3 P(BlueprintBuilding b) =>
            new Vector3(b.localOffset_x, b.localOffset_y, b.localOffset_z);

        private static Vector3 P2(BlueprintBuilding b) =>
            new Vector3(b.localOffset_x2, b.localOffset_y2, b.localOffset_z2);

        private static void SetP(BlueprintBuilding b, Vector3 v)
        {
            b.localOffset_x = R4(v.x); b.localOffset_y = R4(v.y); b.localOffset_z = R4(v.z);
        }

        private static void SetP2(BlueprintBuilding b, Vector3 v)
        {
            b.localOffset_x2 = R4(v.x); b.localOffset_y2 = R4(v.y); b.localOffset_z2 = R4(v.z);
        }

        private static float BeltLen(BlueprintBuilding b) => Vector3.Distance(P(b), P2(b));

        private static float CellDist(BlueprintBuilding a, BlueprintBuilding b) => Vector3.Distance(P(a), P(b));

        private static float CellDistOf(BlueprintBuilding a, BlueprintBuilding b) => Vector3.Distance(P(a), P(b));

        private static BlueprintBuilding CloneBelt(BlueprintBuilding src) => new BlueprintBuilding
        {
            areaIndex = src.areaIndex,
            pitch = src.pitch, yaw = src.yaw, tilt = src.tilt,
            itemId = src.itemId, modelIndex = src.modelIndex,
            filterId = src.filterId,
            parameters = src.parameters
        };

        private void Commit(BlueprintData bp, string message)
        {
            bp.CheckBuildingData();
            _bp = bp;
            _bpDesc = $"当前蓝图 | 建筑 {bp.buildings.Length} 个";
            AnalyzeBelts();
            // 不再自动覆盖剪贴板：确认无误后点"写回剪贴板"，填错了点"还原原始蓝图"
            _status = message + "；确认无误后请点\"写回剪贴板\"";
        }

        /// <summary>把最初读取的原始蓝图放回剪贴板（撤销所有处理，重新开始）。</summary>
        private void RestoreOriginal()
        {
            if (_bpOriginal == null) { _status = "没有可还原的原始蓝图（请先读取）"; return; }
            try
            {
                _bp = _bpOriginal.Clone();
                _bp.CheckBuildingData();
                _bpDesc = $"已还原原始蓝图 | 建筑 {_bp.buildings.Length} 个";
                AnalyzeBelts();
                GUIUtility.systemCopyBuffer = _bp.ToBase64String();
                _status = "已还原原始蓝图并放回剪贴板";
            }
            catch (Exception ex)
            {
                _status = "还原异常：" + ex.Message;
            }
        }

        /// <summary>
        /// 自动探测蓝图中传送带的段数与长度范围，并把物品 ID 诊断信息写入 BepInEx 日志。
        /// </summary>
        private void AnalyzeBelts()
        {
            _beltStat = "";
            if (_bp?.buildings == null) return;
            int cnt = 0, zeroLen = 0;
            float min = float.MaxValue, max = 0f;
            var idHistogram = new Dictionary<int, int>();
            var modelSamples = new Dictionary<int, int>(); // itemId -> 第一个样本的 modelIndex
            foreach (var b in _bp.buildings)
            {
                if (!idHistogram.ContainsKey(b.itemId)) idHistogram[b.itemId] = 0;
                idHistogram[b.itemId]++;
                if (!modelSamples.ContainsKey(b.itemId)) modelSamples[b.itemId] = b.modelIndex;
                if (!IsBelt(b)) continue;
                cnt++;
                float len = Vector3.Distance(
                    new Vector3(b.localOffset_x, b.localOffset_y, b.localOffset_z),
                    new Vector3(b.localOffset_x2, b.localOffset_y2, b.localOffset_z2));
                if (len < 1e-4f) zeroLen++;
                if (len < min) min = len;
                if (len > max) max = len;
            }

            // 诊断日志：每种物品的数量、modelIndex 与游戏的 isBelt 判定结果
            try
            {
                foreach (var kv in idHistogram.OrderBy(p => p.Key))
                {
                    bool belt = IsBeltByModel(modelSamples[kv.Key]);
                    Logger.Log(LogLevel.Info, $"[蓝图诊断] itemId={kv.Key} × {kv.Value}，modelIndex={modelSamples[kv.Key]}，isBelt={belt}");
                }
            }
            catch { }

            var parts = idHistogram.OrderBy(p => p.Key).Select(p => p.Key + "×" + p.Value);
            if (cnt > 0)
            {
                _beltStat = zeroLen == cnt
                    ? $"探测到传送带 {cnt} 段（逐格存储，方向由朝向决定）"
                    : $"探测到传送带 {cnt} 段，长度 {min:F4} ~ {max:F4}（其中首尾重合 {zeroLen} 段）";
            }
            else
            {
                _beltStat = "未探测到传送带；蓝图物品ID分布: " + string.Join(", ", parts);
            }
        }

        /// <summary>仅按模型原型判断 isBelt（不带 itemId 快速路径）。</summary>
        private static bool IsBeltByModel(int modelIndex)
        {
            try
            {
                var model = LDB.models.Select(modelIndex);
                return model?.prefabDesc != null && model.prefabDesc.isBelt;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把处理后的蓝图直接替换进当前蓝图粘贴工具（可立即摆放）。</summary>
        private void ApplyToPasteTool()
        {
            if (!RequireBlueprint()) return;
            try
            {
                var inspector = _inspector != null ? _inspector : FindObjectOfType<UIBlueprintInspector>();
                if (inspector == null || _fiPasteTool == null)
                {
                    _status = "未找到蓝图粘贴工具（请先打开蓝图界面）";
                    return;
                }
                var pasteTool = _fiPasteTool.GetValue(inspector) as BuildTool_BlueprintPaste;
                if (pasteTool == null)
                {
                    _status = "粘贴工具不可用";
                    return;
                }
                var bp = _bp.Clone();
                bp.CheckBuildingData();
                pasteTool.blueprint = bp;
                try { pasteTool.ForceDetermineBP(); } catch { /* 预览下一帧会自动重建 */ }
                try { pasteTool.RefreshBlueprintUI(); } catch { }
                _status = $"已替换粘贴工具中的蓝图（建筑 {bp.buildings.Length} 个）";
            }
            catch (Exception ex)
            {
                _status = "应用异常：" + ex.Message;
            }
        }

        private bool RequireBlueprint()
        {
            if (_bp != null && _bp.buildings != null) return true;
            _status = "请先从剪贴板读取蓝图";
            return false;
        }

        private static float R4(float v) => (float)Math.Round(v, 4);
    }
}
