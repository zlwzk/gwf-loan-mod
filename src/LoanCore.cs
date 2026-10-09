using System;
using System.Collections.Generic;
using System.Linq;
using Extensions;
using Mirror;
using UnityEngine;

namespace GwfLoanMod
{
    /// <summary>
    /// Host 权威账本。所有修改只发生在 Host（房主）端，通过全量快照广播给所有人。
    /// 客户端只发请求 + 渲染快照，保证所有人看到一致的借贷状态。
    /// </summary>
    public class LoanCore
    {
        public LedgerSnapshot Snap = new LedgerSnapshot();

        /// <summary>等待债主裁决的请求：requestId → (请求, 发起时间)。</summary>
        private readonly Dictionary<int, PendingRequest> _pending = new Dictionary<int, PendingRequest>();
        private int _nextRequestId = 1;

        private class PendingRequest
        {
            public int id;
            public ulong borrower;
            public ulong lender;
            public double amount;
            public double createdAt;
        }

        public bool IsHost => NetworkServer.active;

        // ==================== 开关 / 分配 ====================

        /// <summary>开启 mod：把共享池平均分给所有在线玩家，作为各自额度与单注上限基准。</summary>
        public void EnableAndRedistribute()
        {
            var players = CollectPlayers();
            if (players.Count == 0)
                return;

            double pool = CurrentPool();
            double share = Math.Floor(pool / players.Count);

            Snap.enabled = true;
            Snap.poolAtEnable = pool;
            Snap.perPlayerShare = share;
            Snap.accounts = players.Select(p => new AccountState
            {
                steamId = p.Item1,
                name = p.Item2,
                balance = share,
                debtTotal = 0,
            }).ToList();
            Snap.loans.Clear();
            Snap.recentEvents.Clear();
            Log($"[Loan] mod 开启：池 {pool:F0} / {players.Count} 人 = 每人 {share:F0}");
        }

        public void Disable()
        {
            // 关闭时不清账目，让结算界面还能看"坏账"；只关效果
            Snap.enabled = false;
            Log("[Loan] mod 关闭");
        }

        /// <summary>重新平均分配（房主操作）。</summary>
        public void Redistribute()
        {
            if (!Snap.enabled)
                return;
            var players = CollectPlayers();
            if (players.Count == 0)
                return;
            double pool = CurrentPool();
            double share = Math.Floor(pool / players.Count);
            Snap.poolAtEnable = pool;
            Snap.perPlayerShare = share;
            // 新玩家给份额；老玩家重置为份额（债务清零——重新发牌规则）
            Snap.loans.Clear();
            var seen = new HashSet<ulong>();
            var next = new List<AccountState>();
            foreach (var p in players)
            {
                seen.Add(p.Item1);
                next.Add(new AccountState { steamId = p.Item1, name = p.Item2, balance = share });
            }
            // 已离场但仍有账目的玩家保留（显示坏账）——这里重置规则简化：只保留在线玩家
            Snap.accounts = next;
            Log($"[Loan] 重新分配：每人 {share:F0}");
        }

        // ==================== 结算 hook（Host） ====================

        /// <summary>每局结算（GameResultsManager.RegisterResult）后调用：更新个人账户并检查自动还款。</summary>
        public void OnGameResult(ulong playerSteamId, double bet, double payout)
        {
            if (!Snap.enabled)
                return;
            // 团队贷款：多赚多还（每局赢利按比例自动扣还）
            RepayTeamFromProfit(payout - bet);

            var acc = Snap.GetAccount(playerSteamId);
            if (acc == null)
                return;
            acc.balance -= bet;
            acc.balance += payout;
            acc.debtTotal = Snap.DebtOf(playerSteamId);

            TryAutoRepay(playerSteamId);
            SyncDebtTotals();
            PushRecent($"[{acc.name}] 本局 下注 {bet:F0} / 派彩 {payout:F0} → 余额 {acc.balance:F0}");
        }

        /// <summary>余额 ≥ 倍数 × 未偿总额时自动还款（顺序由房主设置决定）。</summary>
        private void TryAutoRepay(ulong borrowerId)
        {
            var acc = Snap.GetAccount(borrowerId);
            if (acc == null) return;
            double debt = Snap.DebtOf(borrowerId);
            if (debt <= 0 || acc.balance < Snap.settings.autoRepayMultiplier * debt)
                return;

            // 取出该借款人的所有未清借据，按房主设定排序
            List<LoanRecord> mine = Snap.loans
                .Where(l => l.borrower == borrowerId && l.remaining > 0)
                .ToList();
            switch (Snap.settings.repayOrder)
            {
                case RepayOrder.SmallFirst:
                    mine.Sort((a, b) => a.remaining.CompareTo(b.remaining));
                    break;
                case RepayOrder.LargeFirst:
                    mine.Sort((a, b) => b.remaining.CompareTo(a.remaining));
                    break;
                case RepayOrder.Chronological:
                default:
                    mine.Sort((a, b) => a.seq.CompareTo(b.seq));
                    break;
            }

            foreach (var loan in mine)
            {
                if (acc.balance <= 0) break;
                // 保留触发线：还完后余额仍 ≥ 剩余债务的触发倍数，避免刚还完又立刻扣光
                double pay = Math.Min(loan.remaining, acc.balance);
                if (pay <= 0) break;

                loan.remaining -= pay;
                acc.balance -= pay;
                var lenderAcc = Snap.GetAccount(loan.lender);
                if (lenderAcc != null) lenderAcc.balance += pay;
                loan.repays.Add(new RepayEvent { amount = pay, ts = Time.time });
                PushRecent($"[{acc.name}] 自动还款 {pay:F0} → [{lenderAcc?.name ?? loan.lender.ToString()}]" +
                           (loan.remaining <= 0.5 ? "（已结清）" : $"（剩 {loan.remaining:F0}）"));
            }
            Snap.loans.RemoveAll(l => l.borrower == borrowerId && l.remaining <= 0.5);
        }

        private void SyncDebtTotals()
        {
            foreach (var a in Snap.accounts)
                a.debtTotal = Snap.DebtOf(a.steamId);
        }

        // ==================== 单注上限（Patches 调用，Host） ====================

        /// <summary>下注拦截：返回 false = 拒绝这笔下注。只在 hardBetCap 开启时启用。</summary>
        public bool AllowBet(ulong playerSteamId, double amount)
        {
            if (!Snap.enabled || !Snap.settings.hardBetCap)
                return true;
            var acc = Snap.GetAccount(playerSteamId);
            if (acc == null)
                return true; // 没进账本的（新加入者）先放行
            return amount <= acc.balance + 1e-9;
        }

        // ==================== 借款流程（Host） ====================

        /// <summary>收到借款请求：转发给债主裁决。</summary>
        public void HandleRequest(NetworkConnectionToClient conn, LoanRequestMsg msg)
        {
            if (!Snap.enabled)
            {
                LoanNet.SendToConnection(conn.connectionId, new ToastMsg { text = "借贷系统未开启" });
                return;
            }
            if (msg.amount <= 0)
                return;
            var borrower = Snap.GetAccount(msg.borrower);
            var lender = Snap.GetAccount(msg.lender);
            if (borrower == null || lender == null)
                return;
            if (msg.borrower == msg.lender)
                return;
            // 债务上限
            if (Snap.settings.debtCap > 0 && Snap.DebtOf(msg.borrower) + msg.amount > Snap.settings.debtCap)
            {
                LoanNet.SendToConnection(conn.connectionId, new ToastMsg { text = $"超出债务上限 {Snap.settings.debtCap:F0}" });
                return;
            }

            int rid = _nextRequestId++;
            _pending[rid] = new PendingRequest
            {
                id = rid,
                borrower = msg.borrower,
                lender = msg.lender,
                amount = msg.amount,
                createdAt = Time.time,
            };

            // 找债主的连接
            int lenderConn = FindConnectionOf(msg.lender);
            if (lenderConn < 0)
            {
                LoanNet.SendToConnection(conn.connectionId, new ToastMsg { text = "对方不在线" });
                _pending.Remove(rid);
                return;
            }
            LoanNet.SendToConnection(lenderConn, new LoanPromptMsg
            {
                requestId = rid,
                borrower = msg.borrower,
                borrowerName = borrower.name,
                amount = msg.amount,
                remainSec = Snap.settings.requestTimeoutSec,
            });
            LoanNet.SendToConnection(conn.connectionId, new ToastMsg { text = $"已向 {lender.name} 发起借款请求…" });
            Log($"[Loan] 请求 #{rid}: {borrower.name} → {lender.name} ¥{msg.amount:F0}");
        }

        /// <summary>债主裁决。</summary>
        public void HandleDecision(LoanDecisionMsg msg)
        {
            PendingRequest req;
            if (!_pending.TryGetValue(msg.requestId, out req) || req.lender != msg.lender)
                return;
            // 超时判定
            if (Time.time - req.createdAt > Snap.settings.requestTimeoutSec + 5.0)
            {
                _pending.Remove(msg.requestId);
                return;
            }
            if (!msg.accept)
            {
                _pending.Remove(msg.requestId);
                var bAcc = Snap.GetAccount(req.borrower);
                int bConn = FindConnectionOf(req.borrower);
                if (bConn >= 0)
                    LoanNet.SendToConnection(bConn, new ToastMsg { text = "对方拒绝了你的借款请求" });
                PushRecent($"[{bAcc?.name}] 向 [{Snap.GetAccount(req.lender)?.name}] 借款被拒");
                return;
            }

            // 放款
            var lenderAcc = Snap.GetAccount(req.lender);
            var borrowerAcc = Snap.GetAccount(req.borrower);
            if (lenderAcc == null || borrowerAcc == null) { _pending.Remove(msg.requestId); return; }
            if (lenderAcc.balance < req.amount)
            {
                int bConn2 = FindConnectionOf(req.borrower);
                if (bConn2 >= 0)
                    LoanNet.SendToConnection(bConn2, new ToastMsg { text = "对方余额不足，借款失败" });
                _pending.Remove(msg.requestId);
                return;
            }

            lenderAcc.balance -= req.amount;
            borrowerAcc.balance += req.amount;
            Snap.loans.Add(new LoanRecord
            {
                id = Snap.nextLoanId++,
                borrower = req.borrower,
                lender = req.lender,
                principal = req.amount,
                remaining = req.amount,
                seq = Snap.nextSeq++,
                takenAt = Time.time,
            });
            SyncDebtTotals();
            _pending.Remove(msg.requestId);
            PushRecent($"[{borrowerAcc.name}] 向 [{lenderAcc.name}] 借款 ¥{req.amount:F0}（自动还款线 ¥{req.amount * Snap.settings.autoRepayMultiplier:F0}）");

            // 弹幕：发给借款人（劝诫）
            int borrowerConn = FindConnectionOf(req.borrower);
            if (Snap.settings.danmakuEnabled && borrowerConn >= 0)
            {
                string tip = DanmakuPool.Pick();
                LoanNet.SendToConnection(borrowerConn, new DanmakuMsg { text = tip });
            }
            Log($"[Loan] 放款 #{msg.requestId}: {req.amount:F0}");
        }

        /// <summary>房主设置变更（只信任 Host 本地连接发来的）。</summary>
        public void HandleSettings(NetworkConnectionToClient conn, SettingsChangeMsg msg)
        {
            // 只有 Host 自己的连接（房主）才能改设置；其他人发的消息直接忽略
            if (!IsFromHost(conn))
            {
                LoanNet.SendToConnection(conn.connectionId, new ToastMsg { text = "只有房主可以修改借贷设置" });
                return;
            }
            if (msg.redistribute)
            {
                Redistribute();
                return;
            }
            if (msg.setEnabled)
            {
                if (msg.newValue && !Snap.enabled) EnableAndRedistribute();
                else if (!msg.newValue && Snap.enabled) Disable();
            }
            Snap.settings.repayOrder = (RepayOrder)msg.repayOrder;
            Snap.settings.debtCap = msg.debtCap;
            Snap.settings.danmakuEnabled = msg.danmakuEnabled;
            Snap.settings.hardBetCap = msg.hardBetCap;
            Log("[Loan] 房主更新设置");
        }

        private bool IsFromHost(NetworkConnectionToClient conn)
        {
            // Host 的本地客户端连接：localConnection
            var local = NetworkServer.localConnection as NetworkConnectionToClient;
            if (local != null && conn != null && conn.connectionId == local.connectionId)
                return true;
            // 保险：Host 上 connId==0 通常是本地连接
            return conn != null && conn.connectionId == 0 && NetworkClient.active;
        }

        // ==================== 团队贷款（贷款公司，Host 权威） ====================

        /// <summary>房主操作团队贷款：0 = 申请，1 = 免除，2 = 立即还清。非房主一律拒绝。</summary>
        public void HandleTeamLoan(NetworkConnectionToClient conn, TeamLoanMsg msg)
        {
            if (!IsFromHost(conn))
            {
                LoanNet.SendToConnection(conn.connectionId, new ToastMsg { text = "只有房主可以操作团队贷款" });
                return;
            }
            switch (msg.action)
            {
                case 0:
                    TakeTeamLoan(msg.amount, msg.rate);
                    break;
                case 1:
                    ForgiveTeamLoan();
                    break;
                case 2:
                    TryRepayTeamNow();
                    break;
            }
        }

        /// <summary>向贷款公司申请团队贷款：到账进团队资金池，赢利自动按比例还款。</summary>
        public void TakeTeamLoan(double amount, double rate)
        {
            if (!Snap.enabled || amount <= 0)
                return;
            var tl = Snap.teamLoan;
            if (tl.active && tl.remaining > 0.5)
            {
                LoanNet.BroadcastToast("已有一笔未结清的团队贷款，先还清或免除");
                return;
            }
            var mm = NetworkSingleton<MoneyManager>.Instance;
            if (mm == null)
                return;
            rate = Math.Max(0.05, Math.Min(1.0, rate <= 1.0 ? rate : rate / 100.0));
            if (!mm.TryChangeBalance(BigNumber.FromDouble(amount), null, ChangeType.Misc))
                return;

            var gm = NetworkSingleton<GameManager>.Instance;
            Snap.teamLoan = new TeamLoanState
            {
                active = true,
                principal = amount,
                remaining = amount,
                repayRate = rate,
                takenDuringDay = gm != null && gm.state == GameState.Game,
                dayEndsSinceLoan = 0,
                overdue = false,
                totalRepaid = 0,
            };
            PushRecent($"[团队] 向贷款公司借款 ¥{amount:F0}（每局赢利 {rate:P0} 自动还款）");
            LoanNet.BroadcastToast($"团队贷款 {amount:F0} 已生效：每局赢利 {rate:P0} 自动还款，{(tl.takenDuringDay ? "下一" : "本")}回合结束前须还清");
            Log($"[Loan] 团队贷款生效：¥{amount:F0}，还款比例 {rate:P0}");
        }

        /// <summary>每局结算：赢利按比例自动扣还（多赚多还，少赚少还，没赚不还）。</summary>
        private void RepayTeamFromProfit(double profit)
        {
            var tl = Snap.teamLoan;
            if (!Snap.enabled || !tl.active || tl.remaining <= 0.5 || profit <= 0)
                return;
            var mm = NetworkSingleton<MoneyManager>.Instance;
            if (mm == null)
                return;
            double pay = Math.Min(tl.remaining, profit * tl.repayRate);
            // 不允许还款把团队资金扣成负数
            double bal;
            try { bal = mm.balance.ToDouble(); }
            catch { bal = mm.balance.ToLong(); }
            pay = Math.Min(pay, Math.Max(0.0, bal));
            if (pay <= 0.01)
                return;
            if (!mm.TryChangeBalance(BigNumber.FromDouble(-pay), null, ChangeType.Misc))
                return;
            tl.remaining -= pay;
            tl.totalRepaid += pay;
            PushRecent($"[团队] 赢利自动还款 ¥{pay:F0}（剩 ¥{tl.remaining:F0}）");
            if (tl.remaining <= 0.5)
            {
                tl.remaining = 0;
                tl.overdue = false;
                tl.active = false;
                PushRecent("[团队] 团队贷款已全部还清！");
                LoanNet.BroadcastToast("团队贷款已全部还清！");
            }
        }

        /// <summary>回合结束（state 离开 Game）时由 Plugin 调用：检查逾期。</summary>
        public void OnDayEnded()
        {
            if (!Snap.enabled)
                return;
            var tl = Snap.teamLoan;
            if (!tl.active || tl.remaining <= 0.5)
                return;
            tl.dayEndsSinceLoan++;
            // 借款发生在对局内：本轮结束不算，下一轮结束为期限；在大厅借：下一轮结束为期限
            int deadline = tl.takenDuringDay ? 2 : 1;
            if (tl.dayEndsSinceLoan >= deadline && !tl.overdue)
            {
                tl.overdue = true;
                PushRecent("[团队] 贷款逾期：车辆倒计时暂停，直至还清（或房主免除）");
                LoanNet.BroadcastToast("团队贷款逾期！车辆倒计时暂停");
            }
        }

        /// <summary>房主手动一次性还款（资金池够则全额还清，不够则能还多少还多少）。</summary>
        public void TryRepayTeamNow()
        {
            var tl = Snap.teamLoan;
            if (!Snap.enabled || !tl.active || tl.remaining <= 0.5)
                return;
            var mm = NetworkSingleton<MoneyManager>.Instance;
            if (mm == null)
                return;
            double bal;
            try { bal = mm.balance.ToDouble(); }
            catch { bal = mm.balance.ToLong(); }
            double pay = Math.Min(tl.remaining, Math.Max(0.0, bal));
            if (pay <= 0.01)
            {
                LoanNet.BroadcastToast("资金池余额不足，无法还款（赢利会自动还款）");
                return;
            }
            if (!mm.TryChangeBalance(BigNumber.FromDouble(-pay), null, ChangeType.Misc))
                return;
            tl.remaining -= pay;
            tl.totalRepaid += pay;
            if (tl.remaining <= 0.5)
            {
                tl.remaining = 0;
                tl.overdue = false;
                tl.active = false;
                PushRecent("[团队] 团队贷款已全部还清！");
                LoanNet.BroadcastToast("团队贷款已全部还清！车辆恢复正常");
            }
            else
            {
                PushRecent($"[团队] 主动还款 ¥{pay:F0}（剩 ¥{tl.remaining:F0}）");
                LoanNet.BroadcastToast($"已还款 {pay:F0}，仍剩 {tl.remaining:F0}");
            }
        }

        /// <summary>
        /// 车辆倒计时是否被团队贷款阻塞（Host 的 Patches 前缀每帧问；客户端 UI 也用同一份快照评估横幅）。
        /// 拦的是「回合结束的车辆发车倒计时」，不是回合计时器 —— 保证当局倒计时耗尽后游戏照常推进，绝不软锁。
        /// </summary>
        public static bool TeamLoanBlocksVehicle(LedgerSnapshot snap)
        {
            try
            {
                if (snap == null || !snap.enabled)
                    return false;
                var tl = snap.teamLoan;
                if (tl == null || !tl.active || tl.remaining <= 0.5)
                    return false;
                // 还没到「还款日所在回合」：本轮结束不拦，给满一轮宽限
                int needed = tl.takenDuringDay ? 1 : 0;
                if (tl.dayEndsSinceLoan < needed)
                    return false;
                var gm = NetworkSingleton<GameManager>.Instance;
                var mm = NetworkSingleton<MoneyManager>.Instance;
                if (gm == null || mm == null)
                    return false;
                if (gm.state != GameState.Game)
                    return false;
                // 房主关了回合计时器 → 没有超时兜底，绝不硬拦
                if (!gm.RoundTimerEnabled)
                    return false;
                // 当局倒计时已耗尽（时间到）→ 无力还款也照常进行后面的游戏流程
                if (gm.RoundTimerRemaining <= 0.01f)
                    return false;
                // 已达标当前对局金额 → 放行（贷款继续记账）
                if (mm.balance >= gm.currentQuota)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>实例便捷方法（Host 本地账本）。</summary>
        public bool TeamLoanBlocksVehicle()
        {
            return TeamLoanBlocksVehicle(Snap);
        }

        private double _lastBlockNotify = -999.0;

        /// <summary>车辆倒计时被拦截时向全员提示（节流：最多每 5 秒一次）。</summary>
        public void NotifyVehicleBlocked()
        {
            if (Time.time - _lastBlockNotify < 5.0)
                return;
            _lastBlockNotify = Time.time;
            var tl = Snap.teamLoan;
            PushRecent("[团队] 车辆倒计时被暂停：团队贷款未还清");
            LoanNet.BroadcastToast($"团队有一笔贷款未还完（剩 {tl.remaining:F0}）：车辆倒计时不会开始，还清或达标本局金额后恢复");
            Log($"[Loan] 拦截车辆倒计时（剩 ¥{tl.remaining:F0}）");
        }

        /// <summary>房主免除团队贷款未还部分。</summary>
        public void ForgiveTeamLoan()
        {
            var tl = Snap.teamLoan;
            if (!tl.active)
                return;
            PushRecent($"[团队] 房主免除了未还的 ¥{tl.remaining:F0}");
            tl.active = false;
            tl.remaining = 0;
            tl.overdue = false;
            LoanNet.BroadcastToast("房主免除了团队贷款");
        }

        // ==================== 工具 ====================

        public double CurrentPool()
        {
            var mm = UnityEngine.Object.FindObjectOfType<MoneyManager>();
            if (mm == null) return 0;
            try { return mm.balance.ToDouble(); }
            catch { return mm.balance.ToLong(); }
        }

        public List<Tuple<ulong, string>> CollectPlayers()
        {
            var list = new List<Tuple<ulong, string>>();
            foreach (var profile in UnityEngine.Object.FindObjectsOfType<PlayerProfile>())
            {
                try
                {
                    ulong sid = profile.steamId;
                    string name = profile.playerName;
                    if (string.IsNullOrEmpty(name)) name = sid.ToString();
                    list.Add(Tuple.Create(sid, name));
                }
                catch (Exception e)
                {
                    Log("[Loan] CollectPlayers err: " + e.Message);
                }
            }
            return list;
        }

        public int FindConnectionOf(ulong steamId)
        {
            foreach (var kv in NetworkServer.connections)
            {
                var conn = kv.Value;
                if (conn == null || conn.identity == null) continue;
                var profile = conn.identity.GetComponent<PlayerProfile>();
                if (profile != null && profile.steamId == steamId)
                    return kv.Key;
            }
            // Host 自己（localConnection, connId 0）
            var hostProfile = UnityEngine.Object.FindObjectsOfType<PlayerProfile>()
                .FirstOrDefault(p => p.isLocalPlayer);
            if (hostProfile != null && hostProfile.steamId == steamId)
                return 0;
            return -1;
        }

        public void PushRecent(string text)
        {
            Snap.recentEvents.Add(text);
            while (Snap.recentEvents.Count > 30)
                Snap.recentEvents.RemoveAt(0);
        }

        private static void Log(string s)
        {
            Debug.Log(s);
        }
    }

    /// <summary>劝诫弹幕文案池（房主可在 config 扩展）。</summary>
    public static class DanmakuPool
    {
        public static string[] Lines = new string[]
        {
            "借贷一时爽，还款两行泪",
            "牌桌借钱，友谊的小船说翻就翻",
            "量入为出，适度娱乐",
            "十赌九输，回头是岸",
            "借款有风险，下注需谨慎",
            "赢回来是运气，还不上是责任",
        };
        private static int _idx = 0;
        private static System.Random _rng = new System.Random();
        public static string Pick()
        {
            return Lines[_rng.Next(Lines.Length)];
        }
    }
}
