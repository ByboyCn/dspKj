using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Bootstrap;
using HarmonyLib;
using NebulaAPI;
using NebulaAPI.GameState;
using NebulaAPI.Interfaces;
using NebulaAPI.Networking;
using NebulaAPI.Packets;
using UnityEngine;

namespace DSPMinerMultiplier
{
    /// <summary>
    /// DSP 采矿机倍率模组
    /// - 采矿倍率可配置，范围 1 ~ 1000 倍
    /// - 耗电随采矿倍率线性增长：1x => 1x，1000x => 256x（上限 256）
    ///   公式：耗电倍率 = 1 + (采矿倍率 - 1) * 255 / 999
    /// - 游戏内按 Alt+F1 打开倍率调节面板，实时生效并保存到配置
    ///
    /// 实现基于反编译核实（Assembly-CSharp，netstandard2.1 版本）：
    /// - MinerComponent.InternalUpdate(factory, veinPool, power, miningRate, miningSpeed, productRegister)
    ///   中的 miningSpeed 参数控制采矿/输出速率。
    /// - MinerComponent.SetPCState(pcPool) 中
    ///   pcPool[pcId].SetRequiredEnergy((double)(speedDamper * speed * speed) / 1e8) 决定耗电。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class MinerMultiplierPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.byboy.dsp.minermultiplier";
        public const string PluginName = "DSP Miner Multiplier";
        public const string PluginVersion = "1.3.0";

        internal static ManualLogSource Log;

        // 采矿倍率（1 ~ 1000）
        public static float MiningMultiplier = 10f;
        // 耗电倍率（由采矿倍率线性映射，最大 256）
        public static float PowerMultiplier = 1f;

        internal static ConfigEntry<float> MiningMultiplierEntry;

        private ConfigEntry<float> _miningMultiplier;
        private ConfigEntry<KeyCode> _modifierKey;
        private ConfigEntry<KeyCode> _toggleKey;

        private MultiplierUI _ui;

        private void Awake()
        {
            Log = Logger;

            _miningMultiplier = Config.Bind(
                "General",
                "采矿倍率 MiningMultiplier",
                10f,
                new ConfigDescription(
                    "采矿机的采矿速度倍率，范围 1 ~ 1000。耗电按 1~256 线性同步增长。",
                    new AcceptableValueRange<float>(1f, 1000f)));

            _modifierKey = Config.Bind("Hotkey", "修饰键 ModifierKey", KeyCode.LeftAlt,
                "与切换键组合使用的修饰键");
            _toggleKey = Config.Bind("Hotkey", "切换键 ToggleKey", KeyCode.F1,
                "与修饰键组合打开/关闭倍率面板");

            ApplyMiningMultiplier(_miningMultiplier.Value);
            MiningMultiplierEntry = _miningMultiplier;
            _miningMultiplier.SettingChanged += (s, e) =>
            {
                // 用户手动改 cfg 或其他插件改配置时同步
                if (Math.Abs(_miningMultiplier.Value - MiningMultiplier) > 0.001f)
                {
                    ApplyMiningMultiplier(_miningMultiplier.Value);
                }
            };

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(MinerPatches));
            harmony.PatchAll(typeof(DisplayPatches));

            _ui = gameObject.AddComponent<MultiplierUI>();
            _ui.Init(_miningMultiplier, _modifierKey, _toggleKey);

            InitNebula();

            // 强制写出配置文件，确保首次启动后 BepInEx\config\ 下就有 cfg
            Config.Save();

            Logger.LogInfo($"{PluginName} {PluginVersion} 已加载，采矿 {MiningMultiplier}x，耗电 {PowerMultiplier:F2}x。" +
                           $"按 {_modifierKey.Value}+{_toggleKey.Value} 打开倍率面板。" +
                           (NebulaSync.Available ? "Nebula 联机同步已启用。" : ""));
        }

        /// <summary>Nebula 联机同步：主机修改倍率后广播，客户端自动跟随。</summary>
        private void InitNebula()
        {
            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(NebulaModAPI.API_GUID))
                {
                    return; // 未安装 Nebula API，纯单机运行
                }

                NebulaSync.Available = true;

                // 注册本模组的包与处理器
                NebulaModAPI.RegisterPackets(Assembly.GetExecutingAssembly());

                NebulaModAPI.OnMultiplayerGameStarted += () =>
                {
                    Log.LogInfo($"联机游戏已开始 (主机={NebulaModAPI.MultiplayerSession.IsServer})");
                    if (NebulaModAPI.MultiplayerSession.IsServer)
                    {
                        // 主机在联机开始时以主机当前倍率为准并广播
                        NebulaSync.Broadcast();
                    }
                    else
                    {
                        // 客户端清零等待主机下发（避免短暂不一致）
                        Log.LogInfo("等待主机同步采矿倍率…");
                    }
                };

                NebulaModAPI.OnPlayerJoinedGame += _ =>
                {
                    // 新玩家加入时主机重发当前倍率
                    if (NebulaModAPI.MultiplayerSession != null && NebulaModAPI.MultiplayerSession.IsServer)
                    {
                        NebulaSync.Broadcast();
                    }
                };

                NebulaModAPI.OnMultiplayerGameEnded += () =>
                {
                    Log.LogInfo("联机游戏已结束，倍率恢复为本机配置。");
                    ApplyMiningMultiplier(_miningMultiplier.Value);
                };

                Log.LogInfo("Nebula API 已接入，联机倍率同步可用。");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"初始化 Nebula 同步失败（将以单机模式运行）: {ex.Message}");
            }
        }

        /// <summary>根据采矿倍率线性计算耗电倍率（上限 256）。</summary>
        public static void ApplyMiningMultiplier(float value)
        {
            value = value < 1f ? 1f : (value > 1000f ? 1000f : value);
            MiningMultiplier = value;
            PowerMultiplier = 1f + (value - 1f) * (256f - 1f) / (1000f - 1f);
        }

        /// <summary>设置倍率并写回配置文件（不触发循环同步）。</summary>
        public static void SetMultiplier(ConfigEntry<float> entry, float value)
        {
            ApplyMiningMultiplier(value);
            entry.Value = MiningMultiplier; // 会写回 cfg 文件
            NebulaSync.Broadcast(); // 联机时主机向客户端广播
        }
    }

    // ==================== Nebula 联机同步 ====================

    /// <summary>倍率同步包：仅一个 float。</summary>
    public class MinerMultiplierPacket : INetSerializable
    {
        public float MiningMultiplier { get; set; }

        public void Serialize(INetDataWriter writer)
        {
            writer.Put(MiningMultiplier);
        }

        public void Deserialize(INetDataReader reader)
        {
            MiningMultiplier = reader.GetFloat();
        }
    }

    /// <summary>包处理器：客户端收到主机的倍率后应用（客户端的修改不会反向广播）。</summary>
    [RegisterPacketProcessor]
    public class MinerMultiplierPacketProcessor : BasePacketProcessor<MinerMultiplierPacket>
    {
        public override void ProcessPacket(MinerMultiplierPacket packet, INebulaConnection conn)
        {
            MinerMultiplierPlugin.Log.LogInfo($"收到主机同步的采矿倍率: {packet.MiningMultiplier}x");
            MinerMultiplierPlugin.ApplyMiningMultiplier(packet.MiningMultiplier);
        }
    }

    /// <summary>联机同步辅助：主机广播当前倍率。</summary>
    public static class NebulaSync
    {
        public static bool Available { get; set; }

        /// <summary>主机把当前倍率广播给所有客户端。</summary>
        public static void Broadcast()
        {
            if (!Available || !NebulaModAPI.IsMultiplayerActive)
            {
                return;
            }
            try
            {
                var session = NebulaModAPI.MultiplayerSession;
                if (session == null || !session.IsServer)
                {
                    return; // 只有主机广播
                }
                session.Network.SendPacket(new MinerMultiplierPacket
                {
                    MiningMultiplier = MinerMultiplierPlugin.MiningMultiplier
                });
                MinerMultiplierPlugin.Log.LogInfo($"已向客户端广播采矿倍率: {MinerMultiplierPlugin.MiningMultiplier}x");
            }
            catch (Exception ex)
            {
                MinerMultiplierPlugin.Log.LogWarning($"广播倍率失败: {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    public static class MinerPatches
    {
        /// <summary>
        /// 采矿提速：InternalUpdate 的 miningSpeed 参数控制时间累计与产出速率，
        /// 直接乘上配置倍率。不改存档字段，移除模组即恢复原版行为。
        /// </summary>
        [HarmonyPatch(typeof(MinerComponent), "InternalUpdate")]
        [HarmonyPrefix]
        public static void InternalUpdate_Prefix(ref float miningSpeed)
        {
            miningSpeed *= MinerMultiplierPlugin.MiningMultiplier;
        }

        /// <summary>
        /// 耗电线性增长：SetPCState 里游戏按
        ///   ratio = speedDamper * speed * speed / 1e8
        /// 设置需求能量；此处在其后按耗电倍率重设，保持 idle/work 混合比例不变。
        /// </summary>
        [HarmonyPatch(typeof(MinerComponent), "SetPCState")]
        [HarmonyPostfix]
        public static void SetPCState_Postfix(MinerComponent __instance, PowerConsumerComponent[] pcPool)
        {
            if (__instance.pcId <= 0 || pcPool == null || __instance.pcId >= pcPool.Length)
            {
                return;
            }

            double ratio = (double)(__instance.speedDamper * __instance.speed * __instance.speed)
                           / 100000000.0 * MinerMultiplierPlugin.PowerMultiplier;
            pcPool[__instance.pcId].SetRequiredEnergy(ratio);
        }
    }

    /// <summary>
    /// 显示修正补丁：游戏各 UI 用 GameMain.history.miningSpeedScale / workEnergyPerTick
    /// 计算显示值，不包含模组倍率。此处对相关显示位置打 Postfix，
    /// 把显示数值乘上对应倍率，使显示与实际一致。
    /// </summary>
    [HarmonyPatch]
    public static class DisplayPatches
    {
        private static readonly System.Text.RegularExpressions.Regex LeadingNumber =
            new System.Text.RegularExpressions.Regex(@"^(\d+(?:\.\d+)?)");

        private static readonly System.Text.RegularExpressions.Regex NumberBeforeSlashMin =
            new System.Text.RegularExpressions.Regex(@"(\d+(?:\.\d+)?)( ?)/min");

        /// <summary>
        /// 星球/恒星详情面板的水泵产量条目：格式为整数 + " /min"
        /// （气态采集率是 "0.0" 小数或 k/M 后缀，不会匹配）。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex IntegerPerMin =
            new System.Text.RegularExpressions.Regex(@"^(\d+) /min$");

        private static int _propIndex;

        /// <summary>把文本开头的数字乘以 factor（返回 null 表示文本未变）。</summary>
        public static string ScaleLeadingNumber(string text, float factor)
        {
            if (string.IsNullOrEmpty(text) || factor == 1f)
            {
                return null;
            }
            var m = LeadingNumber.Match(text);
            if (!m.Success || !float.TryParse(m.Groups[1].Value, out float v))
            {
                return null;
            }
            float scaled = v * factor;
            // 保留 1 位小数以内，避免长尾
            string s = scaled >= 100f ? scaled.ToString("0") : scaled.ToString("0.#");
            return text.Substring(0, m.Groups[1].Index) + s + text.Substring(m.Groups[1].Index + m.Groups[1].Length);
        }

        /// <summary>把文本中 "N/min" 的 N 乘以 factor（用于建造预览 "x/min" 文本）。</summary>
        public static string ScalePerMinNumber(string text, float factor)
        {
            if (string.IsNullOrEmpty(text) || factor == 1f)
            {
                return null;
            }
            return NumberBeforeSlashMin.Replace(text, m =>
            {
                if (!float.TryParse(m.Groups[1].Value, out float v))
                {
                    return m.Value;
                }
                float scaled = v * factor;
                string s = scaled >= 100f ? scaled.ToString("0.#") : scaled.ToString("0.##");
                return s + m.Groups[2].Value + "/min";
            });
        }

        // ---- 采矿机窗口：产量/分 ----
        [HarmonyPatch(typeof(UIMinerWindow), "_OnUpdate")]
        [HarmonyPostfix]
        public static void UIMinerWindow_OnUpdate_Postfix(UIMinerWindow __instance)
        {
            var t = __instance.speedText;
            if (t == null)
            {
                return;
            }
            string newText = ScaleLeadingNumber(t.text, MinerMultiplierPlugin.MiningMultiplier);
            if (newText != null)
            {
                t.text = newText;
            }
        }

        // ---- 建造预览 gizmo：N x XX/min ----
        [HarmonyPatch(typeof(BuildingGizmo), "SetGizmoDesc")]
        [HarmonyPostfix]
        public static void BuildingGizmo_SetGizmoDesc_Postfix(BuildingGizmo __instance)
        {
            var t = __instance.buildText;
            if (t == null)
            {
                return;
            }
            string newText = ScalePerMinNumber(t.text, MinerMultiplierPlugin.MiningMultiplier);
            if (newText != null)
            {
                t.text = newText;
            }
        }

        // ---- 物品详情属性（index 19 = 矿机产量描述） ----
        [HarmonyPatch(typeof(ItemProto), "GetPropValue")]
        [HarmonyPrefix]
        public static void ItemProto_GetPropValue_Prefix(int index)
        {
            _propIndex = index;
        }

        [HarmonyPatch(typeof(ItemProto), "GetPropValue")]
        [HarmonyPostfix]
        public static void ItemProto_GetPropValue_Postfix(ItemProto __instance, ref string __result)
        {
            if (_propIndex != 19 || __result == null || __instance.prefabDesc == null
                || __instance.prefabDesc.minerType == EMinerType.None)
            {
                return;
            }
            string newText = ScaleLeadingNumber(__result, MinerMultiplierPlugin.MiningMultiplier);
            if (newText != null)
            {
                __result = newText;
            }
        }

        // ---- 采集器面板（矿脉采集器/海洋采集器等 MinerComponent 实体） ----
        [HarmonyPatch(typeof(UIVeinCollectorPanel), "_OnUpdate")]
        [HarmonyPostfix]
        public static void UIVeinCollectorPanel_OnUpdate_Postfix(UIVeinCollectorPanel __instance)
        {
            var t = AccessTools.Field(typeof(UIVeinCollectorPanel), "speedText")?.GetValue(__instance) as UnityEngine.UI.Text;
            string newText = ScaleLeadingNumber(t?.text, MinerMultiplierPlugin.MiningMultiplier);
            if (newText != null && t != null)
            {
                t.text = newText;
            }
        }

        [HarmonyPatch(typeof(UIControlPanelVeinCollectorPanel), "_OnUpdate")]
        [HarmonyPostfix]
        public static void UIControlPanelVeinCollectorPanel_OnUpdate_Postfix(UIControlPanelVeinCollectorPanel __instance)
        {
            var t = AccessTools.Field(typeof(UIControlPanelVeinCollectorPanel), "speedText")?.GetValue(__instance) as UnityEngine.UI.Text;
            string newText = ScaleLeadingNumber(t?.text, MinerMultiplierPlugin.MiningMultiplier);
            if (newText != null && t != null)
            {
                t.text = newText;
            }
        }

        // ---- 星球/恒星详情面板：水泵产量条目（"N /min"） ----
        // 直接改 valueText 而不走 valueString，避免触发 setter 递归
        [HarmonyPatch(typeof(UIResAmountEntry), "valueString", MethodType.Setter)]
        [HarmonyPostfix]
        public static void ResAmountEntry_ValueString_Postfix(UIResAmountEntry __instance, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            var m = IntegerPerMin.Match(value);
            if (!m.Success || !float.TryParse(m.Groups[1].Value, out float v))
            {
                return;
            }
            float scaled = v * MinerMultiplierPlugin.MiningMultiplier;
            __instance.valueText.text = $"{scaled:0} /min";
        }

        // ---- 配电面板：巨型采矿机条目的额定功率（workEnergyPerTick 未含倍率） ----
        [HarmonyPatch(typeof(UIControlPanelAdvancedMinerEntry), "_OnUpdate")]
        [HarmonyPostfix]
        public static void AdvancedMinerEntry_OnUpdate_Postfix(UIControlPanelAdvancedMinerEntry __instance)
        {
            var t = __instance.maxChargePowerValue;
            if (t == null)
            {
                return;
            }
            string newText = ScaleLeadingNumber(t.text, MinerMultiplierPlugin.PowerMultiplier);
            if (newText != null)
            {
                t.text = newText;
            }
        }
    }

    /// <summary>
    /// 游戏内倍率调节面板：按 修饰键+切换键（默认 Alt+F1）开关。
    /// </summary>
    public class MultiplierUI : MonoBehaviour
    {
        private ConfigEntry<float> _miningMultiplier;
        private ConfigEntry<KeyCode> _modifierKey;
        private ConfigEntry<KeyCode> _toggleKey;

        private bool _open;
        private Rect _windowRect = new Rect(80f, 80f, 340f, 200f);
        private string _input = "10";
        private bool _inputDirty = true;

        public void Init(ConfigEntry<float> miningMultiplier, ConfigEntry<KeyCode> modifierKey, ConfigEntry<KeyCode> toggleKey)
        {
            _miningMultiplier = miningMultiplier;
            _modifierKey = modifierKey;
            _toggleKey = toggleKey;
            _input = miningMultiplier.Value.ToString("0.#");
        }

        private void Update()
        {
            bool mod = Input.GetKey(_modifierKey.Value);
            bool key = Input.GetKeyDown(_toggleKey.Value);
            if (mod && key)
            {
                _open = !_open;
                if (_open)
                {
                    _input = MinerMultiplierPlugin.MiningMultiplier.ToString("0.#");
                    _inputDirty = true;
                }
            }
        }

        private void OnGUI()
        {
            if (!_open)
            {
                return;
            }
            _windowRect = GUILayout.Window(0x4D50, _windowRect, DrawWindow, "DSP 采矿倍率 (Alt+F1)");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(6f);

            // 当前值
            GUILayout.Label(string.Format("当前采矿倍率：{0:0.#}x    耗电：{1:0.##}x",
                MinerMultiplierPlugin.MiningMultiplier, MinerMultiplierPlugin.PowerMultiplier));

            // 滑条
            float sliderVal = GUILayout.HorizontalSlider(
                MinerMultiplierPlugin.MiningMultiplier, 1f, 1000f);
            if (Math.Abs(sliderVal - MinerMultiplierPlugin.MiningMultiplier) > 0.001f)
            {
                MinerMultiplierPlugin.SetMultiplier(_miningMultiplier, sliderVal);
                _input = MinerMultiplierPlugin.MiningMultiplier.ToString("0.#");
                _inputDirty = true;
            }

            GUILayout.Space(4f);

            // 快捷按钮
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("1x")) Apply(1f);
            if (GUILayout.Button("10x")) Apply(10f);
            if (GUILayout.Button("100x")) Apply(100f);
            if (GUILayout.Button("500x")) Apply(500f);
            if (GUILayout.Button("1000x")) Apply(1000f);
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            // 手动输入
            GUILayout.BeginHorizontal();
            GUILayout.Label("手动输入：", GUILayout.Width(70f));
            string newInput = GUILayout.TextField(_inputDirty ? _input : _input, GUILayout.Width(90f));
            if (!string.Equals(newInput, _input))
            {
                _input = newInput;
                _inputDirty = false;
            }
            if (GUILayout.Button("应用", GUILayout.Width(60f)))
            {
                if (float.TryParse(_input, out float v))
                {
                    Apply(v);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            GUILayout.Label("拖动滑条或点按钮即时生效，并自动保存到配置文件。");
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void Apply(float v)
        {
            MinerMultiplierPlugin.SetMultiplier(_miningMultiplier, v);
            _input = MinerMultiplierPlugin.MiningMultiplier.ToString("0.#");
            _inputDirty = true;
        }
    }
}
