using System;
using System.Collections.Generic;
using System.Globalization;
using Mirror;
using UnityEngine;

namespace GwfLoanMod
{
    /// <summary>
    /// 所有 OnGUI 界面：F8 主面板 / 借款同意弹窗 / 劝诫弹幕 / Toast / 结算浮动详情。
    /// IMGUI 实现，字体跟随游戏默认 skin（最大程度贴近原生观感）。
    /// </summary>
    public class LoanUi : MonoBehaviour
    {
        internal LoanPlugin Plugin;

        private bool _panelOpen;
        private int _tab;              // 0 我的 1 借款 2 详情 3 团队贷款 4 房主设置
        private Vector2 _scroll;

        // 发起借款表单
        private string _loanAmountText = "100";
        private double _loanAmount = 100;
        private int _lenderIdx;

        // 房主设置表单
        private string _capText = "0";
        private double _cap = 0;

        // 同意弹窗
        private LoanPromptMsg? _prompt;
        private float _promptExpire;

        // 弹幕
        private string _danmaku;
        private float _danmakuStart;

        // Toast
        private readonly List<KeyValuePair<string, float>> _toasts = new List<KeyValuePair<string, float>>();

        // 结算浮动详情
        private string _lastEventsKey;
        private float _settleCardUntil;
        private Vector2 _settleScroll;

        // 面板/弹窗打开时接管光标与输入层（游戏默认每帧把光标锁回中心，导致按钮点不到）
        private bool _cursorTaken;
        private InputLayer _prevInputLayer;

        private Rect PanelRect => new Rect(80, 80, 560, 520);

        internal bool TogglePanel()
        {
            _panelOpen = !_panelOpen;
            return _panelOpen;
        }

        internal void ShowPrompt(LoanPromptMsg msg)
        {
            _prompt = msg;
            _promptExpire = Time.time + (float)msg.remainSec;
        }

        internal void ShowDanmaku(string text)
        {
            _danmaku = text;
            _danmakuStart = Time.time;
        }

        internal void ShowToast(string text)
        {
            _toasts.Add(new KeyValuePair<string, float>(text, Time.time + 4f));
            while (_toasts.Count > 6) _toasts.RemoveAt(0);
        }

        private void Update()
        {
            // 面板 / 弹窗打开时释放光标并锁定游戏输入，保证按钮可点击、点击不穿透到游戏世界
            bool wantUi = _panelOpen || _prompt.HasValue;
            if (wantUi && !_cursorTaken)
            {
                _cursorTaken = true;
                _prevInputLayer = InputEvents.ActiveLayer;
                InputEvents.ActiveLayer = InputLayer.Cutscene;
                try { UICursorSimple.Instance.ShowCursor(); } catch { }
            }
            else if (!wantUi && _cursorTaken)
            {
                _cursorTaken = false;
                InputEvents.ActiveLayer = _prevInputLayer;
                try { UICursorSimple.Instance.HideCursor(); } catch { }
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            if (wantUi)
            {
                // UICursorSimple 每帧用自绘光标对抗硬件光标，这里强制硬件光标可见且不锁定
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            // 弹窗倒计时自动关闭
            if (_prompt.HasValue && Time.time > _promptExpire)
            {
                SendDecision(false);
            }
        }

        private void OnGUI()
        {
            var snap = Plugin.UiSnap;
            if (snap == null) return;

            // IMGUI 键盘事件兜底（不依赖 Input 后端；与 legacy/新 InputSystem 路径帧内去重）
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F8)
            {
                Plugin.TogglePanelSafe("OnGUI事件");
                Event.current.Use();
            }

            // 左上角常驻小按钮：任何输入模式下都可以用鼠标打开面板
            if (GUI.Button(new Rect(10, 10, 118, 26), "借贷面板 (F8)"))
                Plugin.TogglePanelSafe("按钮");

            DrawToasts();

            // 团队贷款横幅：只要有一笔未还清的团队贷款，全员顶部常驻提示
            if (snap.enabled)
                DrawTeamLoanBanner(snap);

            if (!string.IsNullOrEmpty(_danmaku))
                DrawDanmaku();

            if (_prompt.HasValue)
                DrawPrompt();

            // 结算浮动详情：recentEvents 变化时弹 8 秒
            string key = snap.recentEvents != null ? string.Join("|", snap.recentEvents) : "";
            if (key != _lastEventsKey)
            {
                _lastEventsKey = key;
                if (!string.IsNullOrEmpty(key)) _settleCardUntil = Time.time + 8f;
            }
            if (snap.enabled && Time.time < _settleCardUntil && snap.recentEvents != null && snap.recentEvents.Count > 0)
                DrawSettleCard(snap);

            if (_panelOpen)
                DrawPanel(snap);
        }

        // ==================== 主面板 ====================

        private void DrawPanel(LedgerSnapshot snap)
        {
            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            GUILayout.Label($"<b>GWF 借贷系统</b>   （mod {(snap.enabled ? "<color=green>已开启</color>" : "<color=red>未开启</color>")}）");
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            string[] tabs = { "我的账户", "发起借款", "借款详情", "团队贷款", "房主设置" };
            _tab = GUILayout.Toolbar(_tab, tabs);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            switch (_tab)
            {
                case 0: DrawMyAccount(snap); break;
                case 1: DrawLoanForm(snap); break;
                case 2: DrawDetails(snap); break;
                case 3: DrawTeamLoan(snap); break;
                case 4: DrawHostSettings(snap); break;
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("关闭 (F8)")) _panelOpen = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private ulong MySteamId()
        {
            foreach (var p in FindObjectsOfType<PlayerProfile>())
                if (p.isLocalPlayer) return p.steamId;
            return 0;
        }

        private void DrawMyAccount(LedgerSnapshot snap)
        {
            ulong me = MySteamId();
            var acc = snap.GetAccount(me);
            if (acc == null)
            {
                GUILayout.Label("你还没有进入账本（等 mod 开启并分配，或你在 mod 开启后才加入）。");
                return;
            }
            GUILayout.Label($"玩家：<b>{acc.name}</b>");
            GUILayout.Label($"当前份额余额：{Fmt(acc.balance)}");
            GUILayout.Label($"未偿债务：{Fmt(acc.debtTotal)}");
            GUILayout.Label($"初始份额 / 单注上限：{Fmt(snap.perPlayerShare)}");
            GUILayout.Label($"自动还款线：余额 ≥ {Fmt(snap.settings.autoRepayMultiplier)} × 未偿总额 时触发");
            GUILayout.Space(8);
            if (acc.debtTotal > 0)
            {
                GUILayout.Label("<color=orange>未清借据：</color>");
                foreach (var l in snap.loans)
                {
                    if (l.borrower != me) continue;
                    var lender = snap.GetAccount(l.lender);
                    GUILayout.Label($"  · 欠 [{lender?.name}] {Fmt(l.remaining)} / {Fmt(l.principal)}" +
                                    (l.remaining > 0.5 ? "" : "（已结清）"));
                }
            }
            if (acc.balance <= 0)
                GUILayout.Label("<color=red>你已亏损，可到「发起借款」向队友借款。</color>");
        }

        private void DrawLoanForm(LedgerSnapshot snap)
        {
            if (!snap.enabled)
            {
                GUILayout.Label("借贷系统未开启（等待房主开启）。");
                return;
            }
            ulong me = MySteamId();
            var mine = new List<AccountState>();
            foreach (var a in snap.accounts)
                if (a.steamId != me) mine.Add(a);

            if (mine.Count == 0)
            {
                GUILayout.Label("房间里没有其他玩家可以借。");
                return;
            }
            if (_lenderIdx >= mine.Count) _lenderIdx = 0;

            GUILayout.BeginHorizontal();
            GUILayout.Label("向谁借：", GUILayout.Width(60));
            var names = mine.ConvertAll(a => a.name).ToArray();
            _lenderIdx = GUILayout.SelectionGrid(_lenderIdx, names, Math.Min(names.Length, 3));
            GUILayout.EndHorizontal();
            GUILayout.Label($"对方余额：{Fmt(mine[_lenderIdx].balance)}");

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("金额：", GUILayout.Width(60));
            // 数值输入规则：只允许半角数字，非法输入自动回退
            string newTxt = GUILayout.TextField(_loanAmountText, GUILayout.Width(140));
            double parsed;
            if (double.TryParse(NumericOnly(newTxt), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                _loanAmountText = NumericOnly(newTxt);
                _loanAmount = parsed;
            }
            else if (string.IsNullOrEmpty(newTxt))
            {
                _loanAmountText = "";
            }
            GUILayout.Label("元", GUILayout.Width(24));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            _loanAmount = Math.Max(1.0, Math.Min(_loanAmount, 1e15));

            GUILayout.Space(8);
            if (GUILayout.Button("发起借款请求", GUILayout.Height(34)))
            {
                LoanNet.SendToServer(new LoanRequestMsg
                {
                    borrower = me,
                    lender = mine[_lenderIdx].steamId,
                    amount = _loanAmount,
                });
                ShowToast("已发送借款请求…");
            }
            GUILayout.Space(4);
            GUILayout.Label("<i>对方同意后你会收到弹幕提示；还款按房主设定的顺序自动进行。</i>");
        }

        private void DrawDetails(LedgerSnapshot snap)
        {
            if (!snap.enabled)
            {
                GUILayout.Label("借贷系统未开启。");
                return;
            }
            GUILayout.Label("<b>所有玩家份额：</b>");
            foreach (var a in snap.accounts)
            {
                string debt = a.debtTotal > 0 ? $"  <color=orange>欠 {Fmt(a.debtTotal)}</color>" : "";
                GUILayout.Label($"  · {a.name}: {Fmt(a.balance)}{debt}");
            }
            GUILayout.Space(6);
            GUILayout.Label("<b>借据（全员可见）：</b>");
            if (snap.loans.Count == 0)
            {
                GUILayout.Label("  （暂无）");
            }
            else
            {
                foreach (var l in snap.loans)
                {
                    var b = snap.GetAccount(l.borrower);
                    var d = snap.GetAccount(l.lender);
                    GUILayout.Label($"  · #{l.id} [{b?.name}] → [{d?.name}] 剩 {Fmt(l.remaining)} / 本 {Fmt(l.principal)}" +
                                    (l.remaining <= 0.5 ? "  <color=green>已结清</color>" : ""));
                }
            }
            GUILayout.Space(6);
            GUILayout.Label("<b>最近动态：</b>");
            if (snap.recentEvents.Count == 0)
            {
                GUILayout.Label("  （暂无）");
            }
            else
            {
                for (int i = snap.recentEvents.Count - 1; i >= 0 && i >= snap.recentEvents.Count - 12; i--)
                    GUILayout.Label("  " + snap.recentEvents[i]);
            }
        }

        // ==================== 团队贷款（向贷款公司借，Host 权威） ====================

        private string _teamAmountText = "1000";
        private double _teamAmount = 1000;
        private string _teamRateText = "50";
        private double _teamRate = 50;

        private void DrawTeamLoan(LedgerSnapshot snap)
        {
            if (!snap.enabled)
            {
                GUILayout.Label("借贷系统未开启（等待房主开启）。");
                return;
            }
            var tl = snap.teamLoan;
            GUILayout.Label("<b>团队贷款（向贷款公司借款，立即进入团队资金池）</b>");
            GUILayout.Space(6);

            bool hasLoan = tl != null && tl.active && tl.remaining > 0.5;
            if (!hasLoan)
            {
                GUILayout.Label("当前没有未结清的团队贷款。");
            }
            else
            {
                GUILayout.Label($"本金：{Fmt(tl.principal)}    已还：{Fmt(tl.totalRepaid)}    <color=orange>剩余：{Fmt(tl.remaining)}</color>");
                GUILayout.Label($"还款机制：每局结算赢利 × {tl.repayRate:P0} 自动还款（多赚多还，少赚少还，没赚不还）");
                string due = tl.takenDuringDay ? "下一回合结束之前" : "本回合结束之前";
                GUILayout.Label($"还款期限：{due}全部还清");
                if (tl.overdue)
                    GUILayout.Label("<color=red>已逾期：未还清前，车辆倒计时不会开始（还清、达标本局金额或当局时间耗尽后恢复）</color>");
                try
                {
                    if (LoanCore.TeamLoanBlocksVehicle(snap))
                        GUILayout.Label("<color=red>拦截中：全员上车辆后发车倒计时不会开始，去继续赢钱还款即可。</color>");
                }
                catch { }
            }
            GUILayout.Space(10);

            bool isHost = Plugin.IsHost;
            if (!isHost)
                GUILayout.Label("<i>只有房主可以申请 / 免除团队贷款；你在这里只能查看状态。</i>");
            GUILayout.Space(4);

            GUI.enabled = isHost;
            GUILayout.BeginHorizontal();
            GUILayout.Label("贷款额度：", GUILayout.Width(70));
            // 数值输入规则：框内只允许半角数字
            string newAmt = GUILayout.TextField(_teamAmountText, GUILayout.Width(120));
            double amtParsed;
            if (double.TryParse(NumericOnly(newAmt), NumberStyles.Float, CultureInfo.InvariantCulture, out amtParsed))
            {
                _teamAmountText = NumericOnly(newAmt);
                _teamAmount = amtParsed;
            }
            else if (string.IsNullOrEmpty(newAmt))
            {
                _teamAmountText = "";
            }
            GUILayout.Label("元", GUILayout.Width(24));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("还款比例：", GUILayout.Width(70));
            string newRate = GUILayout.TextField(_teamRateText, GUILayout.Width(120));
            double rateParsed;
            if (double.TryParse(NumericOnly(newRate), NumberStyles.Float, CultureInfo.InvariantCulture, out rateParsed))
            {
                _teamRateText = NumericOnly(newRate);
                _teamRate = rateParsed;
            }
            else if (string.IsNullOrEmpty(newRate))
            {
                _teamRateText = "";
            }
            GUILayout.Label("%（每局赢利的该比例自动还贷，5~100）", GUILayout.Width(220));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            _teamAmount = Math.Max(1.0, Math.Min(_teamAmount, 1e15));
            _teamRate = Math.Max(5.0, Math.Min(_teamRate, 100.0));

            GUILayout.Space(8);
            if (GUILayout.Button("申请团队贷款（立即到账资金池）", GUILayout.Height(34)))
            {
                LoanNet.SendToServer(new TeamLoanMsg { action = 0, amount = _teamAmount, rate = _teamRate });
                ShowToast("已发送团队贷款申请…");
            }
            if (hasLoan)
            {
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("立即还清（从资金池扣款）", GUILayout.Height(30)))
                    LoanNet.SendToServer(new TeamLoanMsg { action = 2 });
                if (GUILayout.Button("免除未还部分", GUILayout.Height(30)))
                    LoanNet.SendToServer(new TeamLoanMsg { action = 1 });
                GUILayout.EndHorizontal();
            }
            GUI.enabled = true;
        }

        /// <summary>顶部常驻横幅：团队贷款未还清时对每个用户可见。</summary>
        private void DrawTeamLoanBanner(LedgerSnapshot snap)
        {
            var tl = snap.teamLoan;
            if (tl == null || !tl.active || tl.remaining <= 0.5)
                return;

            bool blocked = false;
            try { blocked = LoanCore.TeamLoanBlocksVehicle(snap); } catch { }

            string text;
            Color color;
            if (blocked)
            {
                text = $"⚠ 团队有一笔贷款未还完（剩 {Fmt(tl.remaining)}）：车辆倒计时不会开始 — 还清贷款或达到本局金额后恢复";
                color = new Color(1f, 0.32f, 0.28f);
            }
            else if (tl.overdue)
            {
                text = $"⚠ 团队有一笔贷款未还完（剩 {Fmt(tl.remaining)}）— 已逾期";
                color = new Color(1f, 0.6f, 0.15f);
            }
            else
            {
                text = $"⚠ 团队有一笔贷款未还完：剩 {Fmt(tl.remaining)} / 本金 {Fmt(tl.principal)} · 每局赢利 {tl.repayRate:P0} 自动还款";
                color = new Color(1f, 0.85f, 0.25f);
            }

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = color },
            };
            float w = Mathf.Min(Screen.width * 0.9f, 980f);
            var rect = new Rect((Screen.width - w) / 2f, 6f, w, 30f);
            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.Box(rect, "");
            GUI.color = prev;
            GUI.Label(rect, text, style);
        }

        private void DrawHostSettings(LedgerSnapshot snap)
        {
            bool isHost = Plugin.IsHost;
            if (!isHost)
            {
                GUILayout.Label("<color=red>只有房主能修改设置；你的修改不会生效。</color>");
                GUILayout.Label("<i>（当前你不是房主 / Host）</i>");
                GUILayout.Space(6);
            }

            GUILayout.Label($"总开关：{(snap.enabled ? "开" : "关")}");
            GUILayout.BeginHorizontal();
            GUI.enabled = isHost;
            if (GUILayout.Button(snap.enabled ? "关闭借贷系统" : "开启并平均分配", GUILayout.Height(32)))
            {
                LoanNet.SendToServer(new SettingsChangeMsg
                {
                    setEnabled = true,
                    newValue = !snap.enabled,
                    repayOrder = (int)snap.settings.repayOrder,
                    debtCap = snap.settings.debtCap,
                    danmakuEnabled = snap.settings.danmakuEnabled,
                    hardBetCap = snap.settings.hardBetCap,
                });
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("还款顺序（同一借款人有多笔债务时）：");
            GUI.enabled = isHost;
            GUILayout.BeginHorizontal();
            // 修复：Toggle 已选中时每帧返回 true，会在选中期间每帧向 Host 连发设置消息 → 只在"变为选中"时发送一次
            if (GUILayout.Toggle(snap.settings.repayOrder == RepayOrder.SmallFirst, "从小到大还") &&
                snap.settings.repayOrder != RepayOrder.SmallFirst)
                SendOrder(RepayOrder.SmallFirst, snap);
            if (GUILayout.Toggle(snap.settings.repayOrder == RepayOrder.LargeFirst, "从大到小还") &&
                snap.settings.repayOrder != RepayOrder.LargeFirst)
                SendOrder(RepayOrder.LargeFirst, snap);
            if (GUILayout.Toggle(snap.settings.repayOrder == RepayOrder.Chronological, "按借款顺序还") &&
                snap.settings.repayOrder != RepayOrder.Chronological)
                SendOrder(RepayOrder.Chronological, snap);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("债务上限：", GUILayout.Width(70));
            string newCap = GUILayout.TextField(_capText, GUILayout.Width(100));
            double capParsed;
            if (double.TryParse(NumericOnly(newCap), NumberStyles.Float, CultureInfo.InvariantCulture, out capParsed))
            {
                _capText = NumericOnly(newCap);
                _cap = capParsed;
            }
            GUILayout.Label("元（0 = 不限）", GUILayout.Width(80));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            bool newDanmaku = GUILayout.Toggle(snap.settings.danmakuEnabled, "借款时播放劝诫弹幕");
            bool newHardCap = GUILayout.Toggle(snap.settings.hardBetCap, "单注硬拦截（下注不能超过份额余额）");

            GUILayout.Space(8);
            if (GUILayout.Button("应用设置", GUILayout.Height(30)) && isHost)
            {
                LoanNet.SendToServer(new SettingsChangeMsg
                {
                    setEnabled = false,
                    newValue = snap.enabled,
                    repayOrder = (int)snap.settings.repayOrder,
                    debtCap = _cap,
                    danmakuEnabled = newDanmaku,
                    hardBetCap = newHardCap,
                });
            }
            if (GUILayout.Button("重新平均分配（清空借据）", GUILayout.Height(30)) && isHost)
            {
                LoanNet.SendToServer(new SettingsChangeMsg
                {
                    setEnabled = false,
                    newValue = snap.enabled,
                    repayOrder = (int)snap.settings.repayOrder,
                    debtCap = _cap,
                    danmakuEnabled = newDanmaku,
                    hardBetCap = newHardCap,
                    redistribute = true,
                });
            }
            GUI.enabled = true;
        }

        private void SendOrder(RepayOrder order, LedgerSnapshot snap)
        {
            if (!Plugin.IsHost) return; // 非房主点击无效果
            LoanNet.SendToServer(new SettingsChangeMsg
            {
                setEnabled = false,
                newValue = snap.enabled,
                repayOrder = (int)order,
                debtCap = snap.settings.debtCap,
                danmakuEnabled = snap.settings.danmakuEnabled,
                hardBetCap = snap.settings.hardBetCap,
            });
        }

        // ==================== 同意弹窗 ====================

        private void DrawPrompt()
        {
            var p = _prompt.Value;
            float remain = _promptExpire - Time.time;
            float w = 420, h = 170;
            var rect = new Rect((Screen.width - w) / 2f, Screen.height * 0.28f, w, h);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label("<b>借款请求</b>");
            GUILayout.Space(6);
            GUILayout.Label($"<b>{p.borrowerName}</b> 想向你借款 {Fmt(p.amount)}");
            GUILayout.Label($"（还款线：对方余额 ≥ 2 × 借款额时自动还款）");
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("同意", GUILayout.Height(34))) SendDecision(true);
            if (GUILayout.Button("拒绝", GUILayout.Height(34))) SendDecision(false);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.Label($"<i>{remain:F0} 秒后自动拒绝</i>");
            GUILayout.EndArea();
        }

        private void SendDecision(bool accept)
        {
            var p = _prompt.Value;
            LoanNet.SendToServer(new LoanDecisionMsg
            {
                requestId = p.requestId,
                lender = MySteamId(),
                accept = accept,
            });
            _prompt = null;
        }

        // ==================== 弹幕 ====================

        private void DrawDanmaku()
        {
            float age = Time.time - _danmakuStart;
            float life = 3.0f;
            if (age > life) { _danmaku = null; return; }
            float alpha = age < 0.3f ? age / 0.3f : (age > life - 0.8f ? (life - age) / 0.8f : 1f);
            float w = Screen.width * 0.7f;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.75f, 0.2f, alpha) },
            };
            var rect = new Rect((Screen.width - w) / 2f, Screen.height * 0.12f, w, 44);
            // 底衬
            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.45f * alpha);
            GUI.Box(rect, "");
            GUI.color = prev;
            GUI.Label(rect, "⚠ " + _danmaku + " ⚠", style);
        }

        // ==================== Toast ====================

        private void DrawToasts()
        {
            float y = Screen.height - 40f;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                float left = t.Value - Time.time;
                if (left <= 0) { _toasts.RemoveAt(i); continue; }
                float alpha = Mathf.Min(1f, left);
                var style = new GUIStyle(GUI.skin.label)
                {
                    normal = { textColor = new Color(1f, 1f, 1f, alpha) },
                };
                var rect = new Rect(16, y, 500, 24);
                GUI.Label(rect, "[借贷] " + t.Key, style);
                y -= 26f;
            }
        }

        // ==================== 结算浮动详情 ====================

        private void DrawSettleCard(LedgerSnapshot snap)
        {
            float w = 380, h = 210;
            var rect = new Rect(Screen.width - w - 24, Screen.height * 0.18f, w, h);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label("<b>借款详情（结算）</b>");
            GUILayout.Space(2);
            _settleScroll = GUILayout.BeginScrollView(_settleScroll, GUILayout.ExpandHeight(true));
            int start = Math.Max(0, snap.recentEvents.Count - 10);
            for (int i = start; i < snap.recentEvents.Count; i++)
                GUILayout.Label("· " + snap.recentEvents[i], new GUIStyle(GUI.skin.label) { wordWrap = true });
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ==================== 工具 ====================

        /// <summary>只保留半角数字与小数点（数值输入强制规则）。</summary>
        private static string NumericOnly(string s)
        {
            var sb = new System.Text.StringBuilder();
            bool dot = false;
            foreach (char c in s)
            {
                if (char.IsDigit(c)) sb.Append(c);
                else if (c == '.' && !dot) { sb.Append(c); dot = true; }
            }
            return sb.ToString();
        }

        private static string Fmt(double v)
        {
            return v.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
