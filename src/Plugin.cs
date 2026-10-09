using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Extensions;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace GwfLoanMod
{
    /// <summary>
    /// GWF 借贷系统 mod（共识型：房间内所有人安装，房主权威开关）。
    /// 零修改游戏文件：BepInEx 外挂式注入。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class LoanPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "zlwzk.gwfloansystem";
        public const string PluginName = "GWF Loan System";
        public const string PluginVersion = "0.2.1";

        internal static ManualLogSource Log;
        internal static LoanPlugin Instance;

        internal LoanCore Core = new LoanCore();
        private Harmony _harmony;
        private LoanUi _ui;
        private bool _registered;
        private bool _inputDiagDone;
        private int _lastToggleFrame = -1;
        private bool _gmStateSeen;
        private GameState _lastGmState;

        // 本地缓存的最新快照（客户端 UI 读它；Host 的 UI 走 UiSnap 读权威账本）
        internal LedgerSnapshot LocalSnap = new LedgerSnapshot();

        /// <summary>
        /// UI 显示用的快照：Host 直接读权威账本。
        /// Host 自己 SendToAll 的回环快照会被忽略（见 onSnapshot），LocalSnap 在 Host 端永不更新，
        /// 若 UI 读 LocalSnap，房主界面会永远显示「未开启」。
        /// </summary>
        internal LedgerSnapshot UiSnap => NetworkServer.active ? Core.Snap : LocalSnap;

        // BepInEx config（房主可扩展弹幕文案）
        internal ConfigEntry<string> CfgExtraDanmaku;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Log.LogInfo($"[LoanMod] {PluginName} v{PluginVersion} 加载中…");

            CfgExtraDanmaku = Config.Bind(
                "Danmaku", "ExtraLines",
                "",
                "额外的劝诫弹幕文案，用 | 分隔（例：文案1|文案2）");

            _harmony = new Harmony(PluginGuid + ".harmony");
            Patches.Core = Core;
            _harmony.PatchAll(typeof(Patches));
            Log.LogInfo("[LoanMod] Harmony patch 完成（TryChangeBalance / RegisterResult）");

            // mod 程序集未经 Mirror weaver，必须运行时手工注册消息序列化器（否则所有网络消息静默失败）
            LoanNet.EnsureSerializers();
            Log.LogInfo("[LoanMod] " + LoanNet.SerializerSelfTestResult);

            var go = new GameObject("GwfLoanModController");
            DontDestroyOnLoad(go);
            _ui = go.AddComponent<LoanUi>();
            _ui.Plugin = this;

            Log.LogInfo("[LoanMod] 就绪。进房后按 F8 打开借贷面板。");
        }

        private void Update()
        {
            // 网络激活后注册一次消息（进房时机不定，这里轮询）
            if ((NetworkServer.active || NetworkClient.active) && !_registered)
            {
                RegisterMessages();
                _registered = true;
                Log.LogInfo("[LoanMod] Mirror 消息已注册"
                            + (NetworkServer.active ? "（Host 模式：房主权威账本生效）" : "（客户端模式）"));
            }
            if (!NetworkServer.active && !NetworkClient.active && _registered)
            {
                _registered = false;
                _gmStateSeen = false;
                LocalSnap = new LedgerSnapshot();
                if (Core.Snap.enabled)
                {
                    Core.Snap = new LedgerSnapshot();
                    Log.LogInfo("[LoanMod] 离开房间，账本已清空");
                }
            }

            // 回合结束检测（state 从 Game 离开 = 一个回合真正结束）→ 团队贷款逾期判定
            try
            {
                var gm = NetworkSingleton<GameManager>.Instance;
                if (gm != null)
                {
                    var st = gm.state;
                    if (_gmStateSeen && _lastGmState == GameState.Game && st != GameState.Game)
                    {
                        Core.OnDayEnded();
                        LoanNet.BroadcastLedger(Core.Snap);
                        Log.LogInfo("[LoanMod] 检测到回合结束 → 团队贷款逾期检查完成");
                    }
                    _lastGmState = st;
                    _gmStateSeen = true;
                }
                else
                {
                    _gmStateSeen = false;
                }
            }
            catch { }

            if (F8Pressed())
                TogglePanelSafe("legacy/新InputSystem");
        }

        /// <summary>切换面板（帧内去重：legacy Input、新 InputSystem、OnGUI 事件三条路径可能同帧命中）。</summary>
        internal void TogglePanelSafe(string via)
        {
            if (Time.frameCount == _lastToggleFrame) return;
            _lastToggleFrame = Time.frameCount;
            bool open = _ui.TogglePanel();
            Log.LogInfo($"[LoanMod] 面板已{(open ? "打开" : "关闭")}（触发来源：{via}）");
        }

        /// <summary>F8 双保险检测：旧版 Input + 新 Input System（游戏若切了纯新模式，旧 API 静默失效）。</summary>
        private bool F8Pressed()
        {
            if (!_inputDiagDone)
            {
                _inputDiagDone = true;
                try { var _ = Input.anyKey; Log.LogInfo("[LoanMod] 输入诊断：旧版 Input API 可用"); }
                catch (Exception e) { Log.LogWarning("[LoanMod] 输入诊断：旧版 Input API 不可用（" + e.Message + "）→ 使用新 InputSystem/OnGUI 兜底"); }
                try
                {
                    var kb0 = UnityEngine.InputSystem.Keyboard.current;
                    Log.LogInfo("[LoanMod] 输入诊断：新 InputSystem Keyboard.current=" + (kb0 == null ? "null" : "可用"));
                }
                catch (Exception e) { Log.LogWarning("[LoanMod] 输入诊断：新 InputSystem 不可用（" + e.Message + "）"); }
            }

            try { if (Input.GetKeyDown(KeyCode.F8)) return true; } catch { }
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.f8Key != null && kb.f8Key.wasPressedThisFrame) return true;
            }
            catch { }
            return false;
        }

        private void RegisterMessages()
        {
            LoanNet.RegisterAll(
                onRequest: (conn, msg) =>
                {
                    try { Core.HandleRequest(conn, msg); LoanNet.BroadcastLedger(Core.Snap); }
                    catch (Exception e) { Log.LogInfo("[LoanMod] HandleRequest err: " + e.Message); }
                },
                onDecision: (conn, msg) =>
                {
                    try { Core.HandleDecision(msg); LoanNet.BroadcastLedger(Core.Snap); }
                    catch (Exception e) { Log.LogInfo("[LoanMod] HandleDecision err: " + e.Message); }
                },
                onSettings: (conn, msg) =>
                {
                    try { Core.HandleSettings(conn, msg); LoanNet.BroadcastLedger(Core.Snap); }
                    catch (Exception e) { Log.LogInfo("[LoanMod] HandleSettings err: " + e.Message); }
                },
                onPrompt: msg => _ui.ShowPrompt(msg),
                onSnapshot: msg =>
                {
                    var snap = LedgerSnapshot.FromJson(msg.json);
                    if (!NetworkServer.active || !IsLocalSnapshotStale(snap))
                        LocalSnap = snap;
                },
                onDanmaku: msg => _ui.ShowDanmaku(msg.text),
                onToast: msg => _ui.ShowToast(msg.text),
                onTeamLoan: (conn, msg) =>
                {
                    try { Core.HandleTeamLoan(conn, msg); LoanNet.BroadcastLedger(Core.Snap); }
                    catch (Exception e) { Log.LogInfo("[LoanMod] HandleTeamLoan err: " + e.Message); }
                });
        }

        /// <summary>Host 自己也会收到 SendToAll；Host 端以本地账本为准（快照仅作显示校验）。</summary>
        private bool IsLocalSnapshotStale(LedgerSnapshot incoming)
        {
            // Host 本地账本永远是权威，忽略回环
            return true;
        }

        /// <summary>是否房主（Host）。</summary>
        internal bool IsHost => NetworkServer.active;

        /// <summary>把弹幕额外文案合并进池子。</summary>
        internal void RefreshDanmakuPool()
        {
            var extra = CfgExtraDanmaku.Value;
            if (string.IsNullOrWhiteSpace(extra)) return;
            var baseList = new List<string>(DanmakuPool.Lines);
            foreach (var s in extra.Split('|'))
            {
                var t = s.Trim();
                if (t.Length > 0 && !baseList.Contains(t)) baseList.Add(t);
            }
            DanmakuPool.Lines = baseList.ToArray();
        }
    }
}
